using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

public static class MemoryResourceAnalysis
{
    // TraceEvent exposes Memory/ProcessMemInfo values as page counts. We currently convert
    // with the common Windows 4 KB page size and surface that assumption in response warnings.
    private const long PageSizeBytes = 4096;
    private const string RawPoolTaskGuid = "0268a8b6-74fd-4302-9dd0-6e8f1795c0cf";
    // Keep bounded output useful for the default ~500 ms ProcessMemInfo cadence: 100 samples
    // covers roughly 50 seconds and longer traces get an explicit truncation warning.
    private const int MaxSystemSamples = 100;
    private const long LowFreeMemoryWarningThresholdBytes = 128L * 1024 * 1024;

    public static MemoryResourceResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var processes = new Dictionary<int, ProcessAccumulator>();
        var handles = new Dictionary<int, HandleAccumulator>();
        var pools = new PoolTracker(trace);
        var systemRows = new List<MemoryResourceSystemRow>();
        var pressure = new MemoryPressureAccumulator();
        long processSampleCount = 0;
        long handleEventCount = 0;
        long poolEventCount = 0;
        long rawPoolEventCount = 0;

        foreach (var ev in trace.Events)
        {
            var nowUs = ToUs(ev);
            if (!PassesTimeWindow(nowUs, startUs, endUs)) continue;
            if (!IsPoolEventName(ev.EventName)) continue;
            if (pid.HasValue && ev.ProcessID != pid.Value) continue;
            if (!TryReadPoolEvent(ev, out var poolEvent, out var rawPoolEvent)) continue;

            poolEventCount++;
            if (rawPoolEvent) rawPoolEventCount++;
            pools.Add(ev.ProcessID, ev.ProcessName ?? string.Empty, ev.EventName, poolEvent);
        }

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.MemoryProcessMemInfo += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                for (var i = 0; i < data.Count; i++)
                {
                    var values = data.Values(i);
                    if (pid.HasValue && values.ProcessID != pid.Value) continue;

                    processSampleCount++;
                    pressure.AddProcessSnapshot(
                        nowUs,
                        values.ProcessID,
                        workingSetBytes: PagesToBytes(values.WorkingSetPageCount),
                        commitBytes: PagesToBytes(values.CommitPageCount),
                        privateBytes: PagesToBytes(Math.Max(0, values.CommitPageCount - values.SharedCommitInPages)));
                    GetProcess(processes, values.ProcessID, trace).Add(nowUs, values);
                }
            };

            kernel.MemorySystemMemInfo += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                var row = new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePages),
                    ZeroBytes: null,
                    ModifiedBytes: null,
                    ModifiedNoWriteBytes: null,
                    BadBytes: null);
                systemRows.Add(row);
                pressure.AddSystem(row);
            };

            kernel.MemoryMemInfo += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                var row = new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePageCount),
                    ZeroBytes: PagesToBytes(data.ZeroPageCount),
                    ModifiedBytes: PagesToBytes(data.ModifiedPageCount),
                    ModifiedNoWriteBytes: PagesToBytes(data.ModifiedNoWritePageCount),
                    BadBytes: PagesToBytes(data.BadPageCount));
                systemRows.Add(row);
                pressure.AddSystem(row);
            };

            kernel.ObjectCreateHandle += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesEvent(data, nowUs, pid, startUs, endUs)) return;

                handleEventCount++;
                GetHandle(handles, data.ProcessID, trace).Created++;
            };

            kernel.ObjectCloseHandle += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesEvent(data, nowUs, pid, startUs, endUs)) return;

                handleEventCount++;
                GetHandle(handles, data.ProcessID, trace).Closed++;
            };

            kernel.ObjectDuplicateHandle += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                var counted = false;
                if (!pid.HasValue || data.TargetProcessID == pid.Value)
                {
                    counted = true;
                    GetHandle(handles, data.TargetProcessID, trace).DuplicatedIn++;
                }

                if (!pid.HasValue || data.SourceProcessID == pid.Value)
                {
                    counted = true;
                    GetHandle(handles, data.SourceProcessID, trace).DuplicatedOut++;
                }

                if (counted) handleEventCount++;
            };
        });

        var processRows = processes.Values
            .Select(process => process.ToRow())
            .OrderByDescending(row => row.WorkingSetBytes)
            .ThenByDescending(row => row.CommitBytes)
            .Take(top)
            .ToList();

        var handleRows = handles.Values
            .Select(handle => handle.ToRow())
            .OrderByDescending(row => Math.Abs(row.NetDelta))
            .ThenByDescending(row => row.Created + row.Closed + row.DuplicatedIn + row.DuplicatedOut)
            .Take(top)
            .ToList();

        var poolRows = pools.ProcessRows(top);
        var poolTagRows = pools.TagRows(top);
        var pressureSummary = pressure.ToSummary(processes.Values, top);
        var warnings = BuildWarnings(processSampleCount, handleEventCount, poolEventCount, rawPoolEventCount, pressureSummary);
        var boundedSystemRows = systemRows
            .OrderBy(row => row.TimeUs)
            .TakeLast(MaxSystemSamples)
            .ToList();

        if (systemRows.Count > MaxSystemSamples)
            warnings.Add($"System memory samples truncated to the last {MaxSystemSamples} rows.");

        return new MemoryResourceResponse(
            Processes: processRows,
            Handles: handleRows,
            PoolProcesses: poolRows,
            PoolTags: poolTagRows,
            Pressure: pressureSummary,
            SystemMemory: boundedSystemRows,
            ProcessSampleCount: processSampleCount,
            HandleEventCount: handleEventCount,
            PoolEventCount: poolEventCount,
            Warnings: warnings);
    }

    private static List<string> BuildWarnings(
        long processSampleCount,
        long handleEventCount,
        long poolEventCount,
        long rawPoolEventCount,
        MemoryPressureSummary pressure)
    {
        var warnings = new List<string>();
        if (processSampleCount == 0)
        {
            warnings.Add(
                "No Memory/ProcessMemInfo events matched. Capture with the MemoryInfoWS keyword " +
                "(for example tests/WpaMcp.Tests/fixtures/MemoryCapture.wprp) to get working set, commit, and private bytes.");
        }

        if (handleEventCount == 0)
        {
            warnings.Add(
                "No Object handle events matched. Capture with the Handle keyword to estimate handle-create/close deltas.");
        }

        if (poolEventCount == 0)
        {
            warnings.Add(
                "No PoolAllocation/PoolFree events matched. Capture with the Pool keyword to estimate observed paged/nonpaged pool deltas.");
        }
        else
        {
            warnings.Add(
                "Pool rows are observed allocation/free deltas within the captured window, not absolute current paged/nonpaged pool counters. UnknownFreeCount tracks frees whose allocations predate or fall outside the window.");
            if (rawPoolEventCount > 0)
            {
                warnings.Add(
                    "Some Pool events were parsed from classic raw Pool task GUID/opcode payloads because clean TraceEvent conversion did not name them as Pool/... events.");
            }
        }

        warnings.Add(
            $"Page-count memory metrics are converted using {PageSizeBytes}-byte pages; this response does not currently expose trace-specific page size metadata.");
        warnings.Add(
            "Memory-pressure process totals are observed ETW sample-batch totals, not complete whole-system memory accounting. " +
            "MinAvailableBytes is free+zero memory when zero-page data is present and excludes standby pages.");

        if (pressure.MinFreeBytes == 0)
        {
            warnings.Add("System free memory reached 0 bytes in the selected window; cross-check hard faults and top working-set processes for memory-pressure stalls.");
        }
        else if (pressure.MinFreeBytes is > 0 and < LowFreeMemoryWarningThresholdBytes)
        {
            warnings.Add($"System free memory fell below {LowFreeMemoryWarningThresholdBytes} bytes in the selected window; hard faults and cold-load stalls are more likely under memory pressure.");
        }

        return warnings;
    }

    private static bool PassesEvent(TraceEvent data, long nowUs, int? pid, long? startUs, long? endUs)
        => (!pid.HasValue || data.ProcessID == pid.Value) && PassesTimeWindow(nowUs, startUs, endUs);

    private static bool PassesTimeWindow(long nowUs, long? startUs, long? endUs)
        => (!startUs.HasValue || nowUs >= startUs.Value) &&
           (!endUs.HasValue || nowUs < endUs.Value);

    private static long ToUs(TraceEvent data) => (long)(data.TimeStampRelativeMSec * 1000);

    private static ProcessAccumulator GetProcess(Dictionary<int, ProcessAccumulator> processes, int pid, TraceLog trace)
    {
        if (!processes.TryGetValue(pid, out var process))
        {
            process = new ProcessAccumulator(pid, ProcessName(trace, pid));
            processes.Add(pid, process);
        }

        return process;
    }

    private static HandleAccumulator GetHandle(Dictionary<int, HandleAccumulator> handles, int pid, TraceLog trace)
    {
        if (!handles.TryGetValue(pid, out var handle))
        {
            handle = new HandleAccumulator(pid, ProcessName(trace, pid));
            handles.Add(pid, handle);
        }

        return handle;
    }

    private static string ProcessName(TraceLog trace, int pid)
        => trace.Processes.LastOrDefault(process => process.ProcessID == pid)?.Name ?? $"Process({pid})";

    private static long PagesToBytes(long pages) => pages <= 0 ? 0 : pages * PageSizeBytes;

    private static void TrackMin(
        long? value,
        long timeUs,
        ref long? minValue,
        ref long? minTimeUs)
    {
        if (!value.HasValue)
            return;

        if (!minValue.HasValue || value.Value < minValue.Value)
        {
            minValue = value.Value;
            minTimeUs = timeUs;
        }
    }

    private static void TrackMax(
        long? value,
        long timeUs,
        ref long? maxValue,
        ref long? maxTimeUs)
    {
        if (!value.HasValue)
            return;

        if (!maxValue.HasValue || value.Value > maxValue.Value)
        {
            maxValue = value.Value;
            maxTimeUs = timeUs;
        }
    }

    internal static long CalculateHandleNetDelta(long created, long closed, long duplicatedIn, long duplicatedOut)
    {
        _ = duplicatedOut; // Duplicating a handle out leaves the source process's handle count unchanged.
        return created + duplicatedIn - closed;
    }

    internal static string ClassifyPoolKind(long poolType)
        => (poolType & 1) == 1 ? "paged" : "nonpaged";

    internal static string DecodePoolTag(ulong rawTag)
    {
        Span<char> chars = stackalloc char[4];
        for (var i = 0; i < chars.Length; i++)
        {
            var b = (byte)((rawTag >> (i * 8)) & 0xFF);
            chars[i] = b is >= 32 and <= 126 ? (char)b : '.';
        }

        var tag = new string(chars);
        return tag == "...." ? $"0x{rawTag:X8}" : tag;
    }

    internal static bool IsPoolEventName(string eventName)
        => eventName is "Pool/PoolAllocation" or "Pool/SessionPoolAllocation" or
           "Pool/PoolFree" or "Pool/SessionPoolFree" ||
           IsRawPoolEvent(eventName);

    private static bool IsPoolAllocationEvent(string eventName)
        => eventName is "Pool/PoolAllocation" or "Pool/SessionPoolAllocation" ||
           (TryGetRawPoolOpcode(eventName, out var opcode) && opcode is 32 or 33);

    private static bool TryReadPoolEvent(TraceEvent ev, out PoolEvent poolEvent, out bool rawPoolEvent)
    {
        rawPoolEvent = false;
        poolEvent = default;
        if (!TryPayloadLong(ev, "Type", out var type) ||
            !TryPayloadUlong(ev, "Tag", out var rawTag) ||
            !TryPayloadLong(ev, "NumberOfBytes", out var bytes) ||
            !TryPayloadUlong(ev, "Entry", out var entry))
        {
            rawPoolEvent = TryReadRawPoolEvent(ev, out type, out rawTag, out bytes, out entry);
            if (!rawPoolEvent) return false;
        }

        if (bytes <= 0) return false;

        poolEvent = new PoolEvent(
            Entry: entry,
            Bytes: bytes,
            RawTag: rawTag,
            Tag: DecodePoolTag(rawTag),
            PoolKind: ClassifyPoolKind(type));
        return true;
    }

    private static bool IsRawPoolEvent(string eventName)
        => TryGetRawPoolOpcode(eventName, out var opcode) && opcode is 32 or 33 or 34 or 35;

    private static bool TryGetRawPoolOpcode(string eventName, out int opcode)
    {
        opcode = 0;
        if (!eventName.Contains(RawPoolTaskGuid, StringComparison.OrdinalIgnoreCase)) return false;

        var marker = eventName.LastIndexOf("Opcode(", StringComparison.Ordinal);
        if (marker < 0) return false;

        var start = marker + "Opcode(".Length;
        var end = eventName.IndexOf(')', start);
        if (end <= start) return false;

        return int.TryParse(eventName.AsSpan(start, end - start), out opcode);
    }

    private static bool TryReadRawPoolEvent(
        TraceEvent ev,
        out long type,
        out ulong rawTag,
        out long bytes,
        out ulong entry)
    {
        type = 0;
        rawTag = 0;
        bytes = 0;
        entry = 0;
        if (!IsRawPoolEvent(ev.EventName)) return false;
        if (ev.EventDataLength < 24) return false;

        var data = ev.DataStart;
        type = unchecked((uint)Marshal.ReadInt32(data, 0));
        rawTag = unchecked((uint)Marshal.ReadInt32(data, 4));
        bytes = unchecked((uint)Marshal.ReadInt32(data, 8));
        entry = unchecked((ulong)Marshal.ReadInt64(data, 16));
        return bytes > 0;
    }

    private static bool TryPayloadLong(TraceEvent ev, string name, out long value)
    {
        value = 0;
        var raw = TryPayload(ev, name);
        switch (raw)
        {
            case null:
                return false;
            case long l:
                value = l;
                return true;
            case int i:
                value = i;
                return true;
            case short s:
                value = s;
                return true;
            case sbyte b:
                value = b;
                return true;
            case ulong u when u <= long.MaxValue:
                value = (long)u;
                return true;
            case uint u:
                value = u;
                return true;
            case ushort u:
                value = u;
                return true;
            case byte b:
                value = b;
                return true;
            default:
                return long.TryParse(raw.ToString(), out value);
        }
    }

    private static bool TryPayloadUlong(TraceEvent ev, string name, out ulong value)
    {
        value = 0;
        var raw = TryPayload(ev, name);
        switch (raw)
        {
            case null:
                return false;
            case ulong u:
                value = u;
                return true;
            case long l:
                value = unchecked((ulong)l);
                return true;
            case uint u:
                value = u;
                return true;
            case int i:
                value = unchecked((uint)i);
                return true;
            case ushort u:
                value = u;
                return true;
            case short s:
                value = unchecked((ushort)s);
                return true;
            case byte b:
                value = b;
                return true;
            case sbyte b:
                value = unchecked((byte)b);
                return true;
            default:
            {
                var text = raw.ToString();
                if (ulong.TryParse(text, out value)) return true;
                if (long.TryParse(text, out var signed))
                {
                    value = unchecked((ulong)signed);
                    return true;
                }

                return false;
            }
        }
    }

    private static object? TryPayload(TraceEvent ev, string name)
    {
        for (var i = 0; i < ev.PayloadNames.Length; i++)
        {
            if (string.Equals(ev.PayloadNames[i], name, StringComparison.Ordinal))
                return ev.PayloadValue(i);
        }

        return null;
    }

    private sealed class ProcessAccumulator(int pid, string processName)
    {
        private long _firstSampleUs = long.MaxValue;
        private long _lastSampleUs = long.MinValue;
        private long _workingSetBytes;
        private long _privateWorkingSetBytes;
        private long _commitBytes;
        private long _privateBytes;
        private long _sharedCommitBytes;
        private long _virtualSizeBytes;
        private long _commitDebtBytes;
        private long _storeBytes;

        public int SampleCount { get; private set; }
        public long PeakWorkingSetBytes { get; private set; }
        public long PeakPrivateWorkingSetBytes { get; private set; }
        public long PeakCommitBytes { get; private set; }
        public long PeakPrivateBytes { get; private set; }

        public void Add(long nowUs, MemoryProcessMemInfoValues values)
        {
            SampleCount++;
            _firstSampleUs = Math.Min(_firstSampleUs, nowUs);
            _lastSampleUs = Math.Max(_lastSampleUs, nowUs);

            _workingSetBytes = PagesToBytes(values.WorkingSetPageCount);
            _privateWorkingSetBytes = PagesToBytes(values.PrivateWorkingSetPageCount);
            _commitBytes = PagesToBytes(values.CommitPageCount);
            _sharedCommitBytes = PagesToBytes(values.SharedCommitInPages);
            _privateBytes = PagesToBytes(Math.Max(0, values.CommitPageCount - values.SharedCommitInPages));
            _virtualSizeBytes = PagesToBytes(values.VirtualSizeInPages);
            _commitDebtBytes = PagesToBytes(values.CommitDebtInPages);
            _storeBytes = PagesToBytes(values.StoredPageCount + values.StoreSizePageCount);

            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, _workingSetBytes);
            PeakPrivateWorkingSetBytes = Math.Max(PeakPrivateWorkingSetBytes, _privateWorkingSetBytes);
            PeakCommitBytes = Math.Max(PeakCommitBytes, _commitBytes);
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, _privateBytes);
        }

        public MemoryResourceProcessRow ToRow() =>
            new(
                Pid: pid,
                ProcessName: processName,
                FirstSampleUs: _firstSampleUs == long.MaxValue ? 0 : _firstSampleUs,
                LastSampleUs: _lastSampleUs == long.MinValue ? 0 : _lastSampleUs,
                SampleCount: SampleCount,
                WorkingSetBytes: _workingSetBytes,
                PeakWorkingSetBytes: PeakWorkingSetBytes,
                PrivateWorkingSetBytes: _privateWorkingSetBytes,
                PeakPrivateWorkingSetBytes: PeakPrivateWorkingSetBytes,
                CommitBytes: _commitBytes,
                PeakCommitBytes: PeakCommitBytes,
                PrivateBytes: _privateBytes,
                PeakPrivateBytes: PeakPrivateBytes,
                SharedCommitBytes: _sharedCommitBytes,
                VirtualSizeBytes: _virtualSizeBytes,
                CommitDebtBytes: _commitDebtBytes,
                StoreBytes: _storeBytes);

        public MemoryPressureProcessRow ToPressureRow() =>
            new(
                Pid: pid,
                ProcessName: processName,
                PeakWorkingSetBytes: PeakWorkingSetBytes,
                PeakCommitBytes: PeakCommitBytes,
                PeakPrivateBytes: PeakPrivateBytes);
    }

    private sealed class MemoryPressureAccumulator
    {
        private long? _minFreeBytes;
        private long? _minFreeTimeUs;
        private long? _minAvailableBytes;
        private long? _minAvailableTimeUs;
        private long? _maxModifiedBytes;
        private long? _maxModifiedTimeUs;
        private long? _maxTotalWorkingSetBytes;
        private long? _maxTotalWorkingSetTimeUs;
        private long? _maxTotalCommitBytes;
        private long? _maxTotalCommitTimeUs;
        private long? _maxTotalPrivateBytes;
        private long? _maxTotalPrivateTimeUs;
        private readonly Dictionary<long, ProcessSnapshotBatch> _processBatches = new();

        public long SystemSampleCount { get; private set; }

        public void AddSystem(MemoryResourceSystemRow row)
        {
            SystemSampleCount++;
            TrackMin(row.FreeBytes, row.TimeUs, ref _minFreeBytes, ref _minFreeTimeUs);

            long? availableBytes = row.FreeBytes.HasValue && row.ZeroBytes.HasValue
                ? row.FreeBytes.Value + row.ZeroBytes.Value
                : null;
            TrackMin(availableBytes, row.TimeUs, ref _minAvailableBytes, ref _minAvailableTimeUs);
            TrackMax(row.ModifiedBytes, row.TimeUs, ref _maxModifiedBytes, ref _maxModifiedTimeUs);
        }

        public void AddProcessSnapshot(
            long timeUs,
            int pid,
            long workingSetBytes,
            long commitBytes,
            long privateBytes)
        {
            if (!_processBatches.TryGetValue(timeUs, out var batch))
                batch = new ProcessSnapshotBatch();

            // ProcessMemInfo can repeat the same PID within one timestamped batch.
            // Keep the last row for that PID so aggregate pressure totals are not inflated.
            batch.Processes[pid] = new ProcessSnapshot(
                WorkingSetBytes: workingSetBytes,
                CommitBytes: commitBytes,
                PrivateBytes: privateBytes);
            _processBatches[timeUs] = batch;
        }

        public MemoryPressureSummary ToSummary(IEnumerable<ProcessAccumulator> processes, int top)
        {
            foreach (var (timeUs, batch) in _processBatches)
            {
                var totals = batch.Totals();
                TrackMax(totals.WorkingSetBytes, timeUs, ref _maxTotalWorkingSetBytes, ref _maxTotalWorkingSetTimeUs);
                TrackMax(totals.CommitBytes, timeUs, ref _maxTotalCommitBytes, ref _maxTotalCommitTimeUs);
                TrackMax(totals.PrivateBytes, timeUs, ref _maxTotalPrivateBytes, ref _maxTotalPrivateTimeUs);
            }

            var pressureRows = processes
                .Select(process => process.ToPressureRow())
                .ToList();

            return new MemoryPressureSummary(
                SystemSampleCount: SystemSampleCount,
                ProcessSnapshotBatchCount: _processBatches.Count,
                MinFreeBytes: _minFreeBytes,
                MinFreeTimeUs: _minFreeTimeUs,
                MinAvailableBytes: _minAvailableBytes,
                MinAvailableTimeUs: _minAvailableTimeUs,
                MaxModifiedBytes: _maxModifiedBytes,
                MaxModifiedTimeUs: _maxModifiedTimeUs,
                MaxObservedTotalWorkingSetBytes: _maxTotalWorkingSetBytes,
                MaxObservedTotalWorkingSetTimeUs: _maxTotalWorkingSetTimeUs,
                MaxObservedTotalCommitBytes: _maxTotalCommitBytes,
                MaxObservedTotalCommitTimeUs: _maxTotalCommitTimeUs,
                MaxObservedTotalPrivateBytes: _maxTotalPrivateBytes,
                MaxObservedTotalPrivateTimeUs: _maxTotalPrivateTimeUs,
                TopPeakWorkingSetProcesses: pressureRows
                    .OrderByDescending(row => row.PeakWorkingSetBytes)
                    .ThenByDescending(row => row.PeakCommitBytes)
                    .Take(top)
                    .ToList(),
                TopPeakCommitProcesses: pressureRows
                    .OrderByDescending(row => row.PeakCommitBytes)
                    .ThenByDescending(row => row.PeakWorkingSetBytes)
                    .Take(top)
                    .ToList());
        }

        private sealed class ProcessSnapshotBatch
        {
            public readonly Dictionary<int, ProcessSnapshot> Processes = new();

            public ProcessSnapshot Totals() =>
                new(
                    WorkingSetBytes: Processes.Values.Sum(process => process.WorkingSetBytes),
                    CommitBytes: Processes.Values.Sum(process => process.CommitBytes),
                    PrivateBytes: Processes.Values.Sum(process => process.PrivateBytes));
        }

        private readonly record struct ProcessSnapshot(
            long WorkingSetBytes,
            long CommitBytes,
            long PrivateBytes);
    }

    private sealed class HandleAccumulator(int pid, string processName)
    {
        public long Created { get; set; }
        public long Closed { get; set; }
        public long DuplicatedIn { get; set; }
        public long DuplicatedOut { get; set; }

        public MemoryHandleProcessRow ToRow() =>
            new(
                Pid: pid,
                ProcessName: processName,
                Created: Created,
                Closed: Closed,
                DuplicatedIn: DuplicatedIn,
                DuplicatedOut: DuplicatedOut,
                NetDelta: CalculateHandleNetDelta(Created, Closed, DuplicatedIn, DuplicatedOut));
    }

    private readonly record struct PoolEvent(
        ulong Entry,
        long Bytes,
        ulong RawTag,
        string Tag,
        string PoolKind);

    private sealed class PoolTracker(TraceLog trace)
    {
        private readonly Dictionary<ulong, PoolAllocation> _live = new();
        private readonly Dictionary<int, PoolProcessAccumulator> _processes = new();
        private readonly Dictionary<(string Tag, string PoolKind), PoolTagAccumulator> _tags = new();

        public void Add(int pid, string processName, string eventName, PoolEvent poolEvent)
        {
            var resolvedProcessName = string.IsNullOrEmpty(processName) ? ProcessName(trace, pid) : processName;
            if (IsPoolAllocationEvent(eventName))
            {
                _live[poolEvent.Entry] = new PoolAllocation(pid, resolvedProcessName, poolEvent);
                GetProcess(pid, resolvedProcessName).AddAllocation(poolEvent);
                GetTag(poolEvent.Tag, poolEvent.PoolKind).AddAllocation(poolEvent.Bytes);
                return;
            }

            if (_live.Remove(poolEvent.Entry, out var allocation))
            {
                GetProcess(allocation.Pid, allocation.ProcessName).AddFree(allocation.Event, unknown: false);
                GetTag(allocation.Event.Tag, allocation.Event.PoolKind).AddFree(allocation.Event.Bytes, unknown: false);
            }
            else
            {
                GetProcess(pid, resolvedProcessName).AddFree(poolEvent, unknown: true);
                GetTag(poolEvent.Tag, poolEvent.PoolKind).AddFree(poolEvent.Bytes, unknown: true);
            }
        }

        public IReadOnlyList<MemoryPoolProcessRow> ProcessRows(int top)
            => _processes.Values
                .Select(process => process.ToRow())
                .OrderByDescending(row => row.PagedOutstandingBytes + row.NonPagedOutstandingBytes)
                .ThenByDescending(row => row.PagedAllocatedBytes + row.NonPagedAllocatedBytes)
                .Take(top)
                .ToList();

        public IReadOnlyList<MemoryPoolTagRow> TagRows(int top)
            => _tags.Values
                .Select(tag => tag.ToRow())
                .OrderByDescending(row => row.OutstandingBytes)
                .ThenByDescending(row => row.AllocatedBytes)
                .Take(top)
                .ToList();

        private PoolProcessAccumulator GetProcess(int pid, string processName)
        {
            if (!_processes.TryGetValue(pid, out var process))
            {
                var name = string.IsNullOrEmpty(processName) ? ProcessName(trace, pid) : processName;
                process = new PoolProcessAccumulator(pid, name);
                _processes.Add(pid, process);
            }

            return process;
        }

        private PoolTagAccumulator GetTag(string tag, string poolKind)
        {
            var key = (tag, poolKind);
            if (!_tags.TryGetValue(key, out var accumulator))
            {
                accumulator = new PoolTagAccumulator(tag, poolKind);
                _tags.Add(key, accumulator);
            }

            return accumulator;
        }
    }

    private sealed record PoolAllocation(int Pid, string ProcessName, PoolEvent Event);

    private sealed class PoolProcessAccumulator(int pid, string processName)
    {
        public long PagedOutstandingBytes { get; private set; }
        public long NonPagedOutstandingBytes { get; private set; }
        public long PagedAllocatedBytes { get; private set; }
        public long NonPagedAllocatedBytes { get; private set; }
        public long PagedFreedBytes { get; private set; }
        public long NonPagedFreedBytes { get; private set; }
        public long AllocationCount { get; private set; }
        public long FreeCount { get; private set; }
        public long UnknownFreeCount { get; private set; }

        public void AddAllocation(PoolEvent ev)
        {
            AllocationCount++;
            AddAllocated(ev.PoolKind, ev.Bytes);
            AddOutstanding(ev.PoolKind, ev.Bytes);
        }

        public void AddFree(PoolEvent ev, bool unknown)
        {
            FreeCount++;
            if (unknown) UnknownFreeCount++;
            AddFreed(ev.PoolKind, ev.Bytes);
            if (!unknown) AddOutstanding(ev.PoolKind, -ev.Bytes);
        }

        public MemoryPoolProcessRow ToRow() =>
            new(
                Pid: pid,
                ProcessName: processName,
                PagedOutstandingBytes: PagedOutstandingBytes,
                NonPagedOutstandingBytes: NonPagedOutstandingBytes,
                PagedAllocatedBytes: PagedAllocatedBytes,
                NonPagedAllocatedBytes: NonPagedAllocatedBytes,
                PagedFreedBytes: PagedFreedBytes,
                NonPagedFreedBytes: NonPagedFreedBytes,
                AllocationCount: AllocationCount,
                FreeCount: FreeCount,
                UnknownFreeCount: UnknownFreeCount);

        private void AddAllocated(string kind, long bytes)
        {
            if (kind == "paged") PagedAllocatedBytes += bytes;
            else NonPagedAllocatedBytes += bytes;
        }

        private void AddFreed(string kind, long bytes)
        {
            if (kind == "paged") PagedFreedBytes += bytes;
            else NonPagedFreedBytes += bytes;
        }

        private void AddOutstanding(string kind, long bytes)
        {
            if (kind == "paged") PagedOutstandingBytes += bytes;
            else NonPagedOutstandingBytes += bytes;
        }
    }

    private sealed class PoolTagAccumulator(string tag, string poolKind)
    {
        public long OutstandingBytes { get; private set; }
        public long AllocatedBytes { get; private set; }
        public long FreedBytes { get; private set; }
        public long AllocationCount { get; private set; }
        public long FreeCount { get; private set; }
        public long UnknownFreeCount { get; private set; }

        public void AddAllocation(long bytes)
        {
            AllocationCount++;
            AllocatedBytes += bytes;
            OutstandingBytes += bytes;
        }

        public void AddFree(long bytes, bool unknown)
        {
            FreeCount++;
            FreedBytes += bytes;
            if (unknown) UnknownFreeCount++;
            else OutstandingBytes -= bytes;
        }

        public MemoryPoolTagRow ToRow() =>
            new(
                Tag: tag,
                PoolKind: poolKind,
                OutstandingBytes: OutstandingBytes,
                AllocatedBytes: AllocatedBytes,
                FreedBytes: FreedBytes,
                AllocationCount: AllocationCount,
                FreeCount: FreeCount,
                UnknownFreeCount: UnknownFreeCount);
    }
}
