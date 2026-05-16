using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

public static class MemoryResourceAnalysis
{
    // TraceEvent exposes Memory/ProcessMemInfo values as page counts. We currently convert
    // with the common Windows 4 KB page size and surface that assumption in response warnings.
    private const long PageSizeBytes = 4096;
    // Keep bounded output useful for the default ~500 ms ProcessMemInfo cadence: 100 samples
    // covers roughly 50 seconds and longer traces get an explicit truncation warning.
    private const int MaxSystemSamples = 100;

    public static MemoryResourceResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var processes = new Dictionary<int, ProcessAccumulator>();
        var handles = new Dictionary<int, HandleAccumulator>();
        var systemRows = new List<MemoryResourceSystemRow>();
        long processSampleCount = 0;
        long handleEventCount = 0;

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
                    GetProcess(processes, values.ProcessID, trace).Add(nowUs, values);
                }
            };

            kernel.MemorySystemMemInfo += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                systemRows.Add(new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePages),
                    ZeroBytes: null,
                    ModifiedBytes: null,
                    ModifiedNoWriteBytes: null,
                    BadBytes: null));
            };

            kernel.MemoryMemInfo += data =>
            {
                var nowUs = ToUs(data);
                if (!PassesTimeWindow(nowUs, startUs, endUs)) return;

                systemRows.Add(new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePageCount),
                    ZeroBytes: PagesToBytes(data.ZeroPageCount),
                    ModifiedBytes: PagesToBytes(data.ModifiedPageCount),
                    ModifiedNoWriteBytes: PagesToBytes(data.ModifiedNoWritePageCount),
                    BadBytes: PagesToBytes(data.BadPageCount)));
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

        var warnings = BuildWarnings(processSampleCount, handleEventCount);
        var boundedSystemRows = systemRows
            .OrderBy(row => row.TimeUs)
            .TakeLast(MaxSystemSamples)
            .ToList();

        if (systemRows.Count > MaxSystemSamples)
            warnings.Add($"System memory samples truncated to the last {MaxSystemSamples} rows.");

        return new MemoryResourceResponse(
            Processes: processRows,
            Handles: handleRows,
            SystemMemory: boundedSystemRows,
            ProcessSampleCount: processSampleCount,
            HandleEventCount: handleEventCount,
            Warnings: warnings);
    }

    private static List<string> BuildWarnings(long processSampleCount, long handleEventCount)
    {
        var warnings = new List<string>();
        if (processSampleCount == 0)
        {
            warnings.Add(
                "No Memory/ProcessMemInfo events matched. Capture with the MemoryInfoWS keyword " +
                "(for example tests/WprMcp.Tests/fixtures/MemoryCapture.wprp) to get working set, commit, and private bytes.");
        }

        if (handleEventCount == 0)
        {
            warnings.Add(
                "No Object handle events matched. Capture with the Handle keyword to estimate handle-create/close deltas.");
        }

        warnings.Add(
            "Paged/nonpaged pool current counters are not emitted by this response yet. Capture with the Pool keyword for future pool views; do not treat missing pool rows as proof pool usage is healthy.");
        warnings.Add(
            $"Page-count memory metrics are converted using {PageSizeBytes}-byte pages; this response does not currently expose trace-specific page size metadata.");
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

    internal static long CalculateHandleNetDelta(long created, long closed, long duplicatedIn, long duplicatedOut)
    {
        _ = duplicatedOut; // Duplicating a handle out leaves the source process's handle count unchanged.
        return created + duplicatedIn - closed;
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
}
