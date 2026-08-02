using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Core;
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
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ResolveScope(window, pid, processStartUs, identities);
        var instances = new InstanceAccumulator(process => ProcessName(trace, process));
        var systemRows = new List<MemoryResourceSystemRow>();
        long processSampleCount = 0;
        long handleEventCount = 0;
        long poolEventCount = 0;
        long rawPoolEventCount = 0;
        long traceIdentityUnresolvedEventCount = 0;
        long scopedIdentityUnresolvedEventCount = 0;
        long globalProcessSampleCount = 0;
        long globalSystemSampleCount = 0;
        long globalHandleEventCount = 0;
        long globalPoolEventCount = 0;

        foreach (var ev in AnalysisEvents.Enumerate(trace))
        {
            if (!IsPoolEventName(ev.EventName)) continue;
            if (!TryReadPoolEvent(ev, out var poolEvent, out var rawPoolEvent)) continue;

            globalPoolEventCount++;
            var nowUs = ToUs(ev);
            var isAllocation = IsPoolAllocationEvent(ev.EventName);
            var processResolution = identities.Processes.Resolve(
                ev.ProcessID,
                nowUs,
                processStartUs: null);
            ProcessInstanceKey? process = processResolution.Status == InstanceResolutionStatus.Resolved
                ? processResolution.Value
                : null;
            if (!process.HasValue && isAllocation)
            {
                traceIdentityUnresolvedEventCount++;
                if (RawSelectorMatches(scope, identities, ev.ProcessID, nowUs))
                    scopedIdentityUnresolvedEventCount++;
            }

            instances.AddPoolObservation(
                process,
                nowUs,
                isAllocation,
                poolEvent.Entry,
                poolEvent.Bytes,
                poolEvent.Tag,
                poolEvent.PoolKind,
                rawPoolEvent,
                rawPid: ev.ProcessID);
        }

        var poolProjection = instances.ProjectPoolObservations(
            window, scope, identities);
        poolEventCount = poolProjection.EventCount;
        rawPoolEventCount = poolProjection.RawEventCount;
        traceIdentityUnresolvedEventCount +=
            poolProjection.TraceIdentityUnresolvedFreeCount;
        scopedIdentityUnresolvedEventCount +=
            poolProjection.ScopedIdentityUnresolvedFreeCount;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.MemoryProcessMemInfo += data =>
            {
                globalProcessSampleCount += data.Count;
                var nowUs = ToUs(data);

                for (var i = 0; i < data.Count; i++)
                {
                    var values = data.Values(i);
                    if (!TryResolveScopedEventProcess(
                            scope,
                            identities,
                            values.ProcessID,
                            nowUs,
                            ref traceIdentityUnresolvedEventCount,
                            ref scopedIdentityUnresolvedEventCount,
                            out var process))
                        continue;

                    processSampleCount++;
                    instances.AddSnapshot(process, nowUs, values);
                }
            };

            kernel.MemorySystemMemInfo += data =>
            {
                globalSystemSampleCount++;
                if (!scope.IsResolved) return;
                var nowUs = ToUs(data);
                if (!window.ContainsPoint(nowUs)) return;

                var row = new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePages),
                    ZeroBytes: null,
                    ModifiedBytes: null,
                    ModifiedNoWriteBytes: null,
                    BadBytes: null);
                systemRows.Add(row);
                instances.AddSystem(row);
            };

            kernel.MemoryMemInfo += data =>
            {
                globalSystemSampleCount++;
                if (!scope.IsResolved) return;
                var nowUs = ToUs(data);
                if (!window.ContainsPoint(nowUs)) return;

                var row = new MemoryResourceSystemRow(
                    TimeUs: nowUs,
                    FreeBytes: PagesToBytes(data.FreePageCount),
                    ZeroBytes: PagesToBytes(data.ZeroPageCount),
                    ModifiedBytes: PagesToBytes(data.ModifiedPageCount),
                    ModifiedNoWriteBytes: PagesToBytes(data.ModifiedNoWritePageCount),
                    BadBytes: PagesToBytes(data.BadPageCount));
                systemRows.Add(row);
                instances.AddSystem(row);
            };

            kernel.ObjectCreateHandle += data =>
            {
                globalHandleEventCount++;
                var nowUs = ToUs(data);
                if (!TryResolveScopedEventProcess(
                        scope,
                        identities,
                        data.ProcessID,
                        nowUs,
                        ref traceIdentityUnresolvedEventCount,
                        ref scopedIdentityUnresolvedEventCount,
                        out var process))
                    return;

                handleEventCount++;
                instances.AddHandle(process, HandleEventKind.Create);
            };

            kernel.ObjectCloseHandle += data =>
            {
                globalHandleEventCount++;
                var nowUs = ToUs(data);
                if (!TryResolveScopedEventProcess(
                        scope,
                        identities,
                        data.ProcessID,
                        nowUs,
                        ref traceIdentityUnresolvedEventCount,
                        ref scopedIdentityUnresolvedEventCount,
                        out var process))
                    return;

                handleEventCount++;
                instances.AddHandle(process, HandleEventKind.Close);
            };

            kernel.ObjectDuplicateHandle += data =>
            {
                globalHandleEventCount++;
                var nowUs = ToUs(data);

                var counted = false;
                if (TryResolveScopedEventProcess(
                        scope,
                        identities,
                        data.TargetProcessID,
                        nowUs,
                        ref traceIdentityUnresolvedEventCount,
                        ref scopedIdentityUnresolvedEventCount,
                        out var targetProcess))
                {
                    counted = true;
                    instances.AddHandle(targetProcess, HandleEventKind.DuplicateIn);
                }

                if (TryResolveScopedEventProcess(
                        scope,
                        identities,
                        data.SourceProcessID,
                        nowUs,
                        ref traceIdentityUnresolvedEventCount,
                        ref scopedIdentityUnresolvedEventCount,
                        out var sourceProcess))
                {
                    counted = true;
                    instances.AddHandle(sourceProcess, HandleEventKind.DuplicateOut);
                }

                if (counted) handleEventCount++;
            };
        });

        var processRows = instances.ProcessRows(top);
        var handleRows = instances.HandleRows(top);
        var poolRows = instances.PoolProcessRows(top);
        var poolTagRows = instances.PoolTagRows(top);
        var pressureSummary = instances.PressureSummary(top);
        var matchedEventCount = checked(processSampleCount + handleEventCount + poolEventCount);
        var eventClassObserved =
            globalProcessSampleCount > 0 || globalSystemSampleCount > 0 ||
            globalHandleEventCount > 0 || globalPoolEventCount > 0;
        var contract = ClassifyDataContract(
            scope,
            eventClassObserved,
            matchedEventCount,
            scopedIdentityUnresolvedEventCount,
            hasWindowGlobalSystemEvidence: systemRows.Count > 0);
        var warnings = BuildWarnings(
            processSampleCount,
            handleEventCount,
            poolEventCount,
            globalProcessSampleCount,
            globalSystemSampleCount,
            globalHandleEventCount,
            globalPoolEventCount,
            rawPoolEventCount,
            pressureSummary,
            pid,
            processStartUs,
            contract.NoDataReason,
            scope.ScopeStatus);
        warnings.Add(scope.IsResolved
            ? "SystemMemory and the system-pressure fields in Pressure are window-global and are not changed by pid/processStartUs filtering; sampled-process totals and ranked process rows use the selected process scope."
            : "SystemMemoryScope remains window_global as a field contract, but SystemMemory and system-pressure evidence are empty because the requested process scope did not resolve safely.");
        if (scope.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                "pid_aggregate: pid-only memory scope explicitly aggregates multiple process lifetimes; rows remain separated by ProcessStartUs.");
        }
        if (traceIdentityUnresolvedEventCount > 0)
        {
            warnings.Add(
                $"process_instance_unresolved: {traceIdentityUnresolvedEventCount} source event-side identity observation(s) were unresolved trace-wide; {scopedIdentityUnresolvedEventCount} matched the lifetime-aware raw process selector and half-open query window but were not attributed.");
        }
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
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            SystemMemoryScope: "window_global",
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: contract.CapabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: contract.NoDataReason,
            TraceIdentityUnresolvedEventCount: traceIdentityUnresolvedEventCount,
            ScopedIdentityUnresolvedEventCount: scopedIdentityUnresolvedEventCount);
    }

    private static List<string> BuildWarnings(
        long processSampleCount,
        long handleEventCount,
        long poolEventCount,
        long globalProcessSampleCount,
        long globalSystemSampleCount,
        long globalHandleEventCount,
        long globalPoolEventCount,
        long rawPoolEventCount,
        MemoryPressureSummary pressure,
        int? pid,
        long? processStartUs,
        string? noDataReason,
        string scopeStatus)
    {
        var warnings = new List<string>();

        if (noDataReason is ProcessAnalysisScope.NotFoundStatus or
            ProcessAnalysisScope.AmbiguousStatus)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(scopeStatus));
            AddMemorySemanticsWarnings(warnings);
            return warnings;
        }

        if (noDataReason == "source_events_unattributed")
        {
            warnings.Add(
                "source_events_unattributed: supported memory/handle/pool source evidence matched the lifetime-aware raw process selector and half-open query window, but required process identity was unresolved; no process lifetime attribution was guessed.");
            AddMemorySemanticsWarnings(warnings);
            return warnings;
        }

        if (processSampleCount == 0)
        {
            warnings.Add(globalProcessSampleCount > 0
                ? "no_events_in_scope: Memory/ProcessMemInfo entries were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window."
                : "event_class_not_observed: " +
                  WarningBuilder.MissingKeyword("Memory/ProcessMemInfo", "MemoryInfoWS"));
        }

        if (pressure.SystemSampleCount == 0)
        {
            warnings.Add(globalSystemSampleCount > 0
                ? "no_events_in_scope: Memory/SystemMemInfo or Memory/MemInfo snapshots were observed elsewhere in the trace, but none matched the requested half-open window."
                : "event_class_not_observed: " +
                  WarningBuilder.MissingKeyword(
                      "Memory/SystemMemInfo or Memory/MemInfo",
                      "MemoryInfo"));
        }

        if (handleEventCount == 0)
        {
            warnings.Add(globalHandleEventCount > 0
                ? "no_events_in_scope: Object handle events were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window."
                : "event_class_not_observed: " +
                  WarningBuilder.MissingKeyword("Object handle", "Handle"));
        }

        if (poolEventCount == 0)
        {
            warnings.Add(globalPoolEventCount > 0
                ? "no_events_in_scope: PoolAllocation/PoolFree events were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window."
                : "event_class_not_observed: " +
                  WarningBuilder.MissingKeyword("PoolAllocation/PoolFree", "Pool"));
        }
        else
        {
            warnings.Add(
                "Pool rows are observed allocation/free endpoint deltas within the captured window, not absolute current paged/nonpaged pool counters. Entry pairing is trace-global and paired frees are attributed to the allocation process; UnknownFreeCount tracks frees with no resolvable allocation anywhere in the trace.");
            if (rawPoolEventCount > 0)
            {
                warnings.Add(
                    "Some Pool events were parsed from classic raw Pool task GUID/opcode payloads because clean TraceEvent conversion did not name them as Pool/... events.");
            }
        }

        AddMemorySemanticsWarnings(warnings);

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

    private static void AddMemorySemanticsWarnings(List<string> warnings)
    {
        warnings.Add(
            $"Page-count memory metrics are converted using {PageSizeBytes}-byte pages; this response does not currently expose trace-specific page size metadata.");
        warnings.Add(
            "Memory-pressure process totals are observed ETW sample-batch totals, not complete whole-system memory accounting. " +
            "MinAvailableBytes is free+zero memory when zero-page data is present and excludes standby pages.");
    }

    private static long ToUs(TraceEvent data) => (long)(data.TimeStampRelativeMSec * 1000);

    internal static ProcessAnalysisScope ResolveScope(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        TraceIdentityIndex identities)
        => ProcessAnalysisScope.Resolve(window, pid, processStartUs, identities);

    internal static (string CapabilityStatus, string? NoDataReason) ClassifyDataContract(
        ProcessAnalysisScope scope,
        bool eventClassObserved,
        long matchedEventCount,
        long scopedIdentityUnresolvedEventCount = 0,
        bool hasWindowGlobalSystemEvidence = false)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (!scope.IsResolved)
            return ("unknown", scope.ScopeStatus);
        if (matchedEventCount > 0)
            return ("observed", null);
        if (hasWindowGlobalSystemEvidence)
            return ("partial", null);
        if (scopedIdentityUnresolvedEventCount > 0)
            return ("unknown", "source_events_unattributed");
        return eventClassObserved
            ? ("unknown", "no_events_in_scope")
            : ("not_observed", "event_class_not_observed");
    }

    private static bool TryResolveScopedEventProcess(
        ProcessAnalysisScope scope,
        TraceIdentityIndex identities,
        int pid,
        long timestampUs,
        ref long traceIdentityUnresolvedEventCount,
        ref long scopedIdentityUnresolvedEventCount,
        out ProcessInstanceKey process)
    {
        process = default;
        var resolution = identities.Processes.Resolve(
            pid, timestampUs, processStartUs: null);
        if (resolution.Status != InstanceResolutionStatus.Resolved ||
            !resolution.Value.HasValue)
        {
            traceIdentityUnresolvedEventCount++;
            if (RawSelectorMatches(scope, identities, pid, timestampUs))
                scopedIdentityUnresolvedEventCount++;
            return false;
        }

        if (!scope.IsResolved ||
            !scope.Window.ContainsPoint(timestampUs) ||
            (scope.Pid.HasValue && scope.Pid.Value != pid) ||
            !scope.IncludedProcesses.Contains(resolution.Value.Value))
        {
            return false;
        }

        process = resolution.Value.Value;
        return true;
    }

    private static bool RawSelectorMatches(
        ProcessAnalysisScope scope,
        TraceIdentityIndex identities,
        int pid,
        long timestampUs,
        bool atEndpoint = false) =>
        scope.MatchesRawUnresolvedCandidate(
            identities, pid, timestampUs, atEndpoint);

    private static string ProcessName(TraceLog trace, ProcessInstanceKey process)
        => AnalysisEvents.Enumerate(trace.Processes)
               .Where(candidate => candidate.ProcessID == process.Pid)
               .FirstOrDefault(candidate =>
                   TraceTime.FromMilliseconds(candidate.StartTimeRelativeMsec) == process.StartUs)
               ?.Name ?? $"Process({process.Pid})";

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

    internal enum HandleEventKind
    {
        Create,
        Close,
        DuplicateIn,
        DuplicateOut,
    }

    internal sealed class InstanceAccumulator
    {
        private readonly Func<ProcessInstanceKey, string> _processName;
        private readonly Dictionary<ProcessInstanceKey, ProcessAccumulator> _processes = new();
        private readonly Dictionary<ProcessInstanceKey, HandleAccumulator> _handles = new();
        private readonly PoolTracker _pools;
        private readonly List<PoolObservation> _poolObservations = new();
        private readonly MemoryPressureAccumulator _pressure = new();

        public InstanceAccumulator(Func<ProcessInstanceKey, string> processName)
        {
            _processName = processName ?? throw new ArgumentNullException(nameof(processName));
            _pools = new PoolTracker(processName);
        }

        public void AddSnapshot(
            ProcessInstanceKey process,
            long timeUs,
            MemoryProcessMemInfoValues values)
        {
            var accumulator = GetProcess(process);
            accumulator.Add(timeUs, values);
            _pressure.AddProcessSnapshot(
                timeUs,
                process,
                PagesToBytes(values.WorkingSetPageCount),
                PagesToBytes(values.CommitPageCount),
                PagesToBytes(Math.Max(0, values.CommitPageCount - values.SharedCommitInPages)));
        }

        internal void AddSnapshot(
            ProcessInstanceKey process,
            long timeUs,
            long workingSetBytes,
            long commitBytes,
            long privateBytes)
        {
            var accumulator = GetProcess(process);
            accumulator.Add(
                timeUs,
                workingSetBytes,
                privateWorkingSetBytes: 0,
                commitBytes,
                privateBytes,
                sharedCommitBytes: Math.Max(0, commitBytes - privateBytes),
                virtualSizeBytes: 0,
                commitDebtBytes: 0,
                storeBytes: 0);
            _pressure.AddProcessSnapshot(
                timeUs, process, workingSetBytes, commitBytes, privateBytes);
        }

        public void AddSystem(MemoryResourceSystemRow row) => _pressure.AddSystem(row);

        public void AddHandle(ProcessInstanceKey process, HandleEventKind kind)
        {
            var accumulator = GetHandle(process);
            switch (kind)
            {
                case HandleEventKind.Create:
                    accumulator.Created++;
                    break;
                case HandleEventKind.Close:
                    accumulator.Closed++;
                    break;
                case HandleEventKind.DuplicateIn:
                    accumulator.DuplicatedIn++;
                    break;
                case HandleEventKind.DuplicateOut:
                    accumulator.DuplicatedOut++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        public void AddPool(
            ProcessInstanceKey process,
            bool isAllocation,
            ulong entry,
            long bytes,
            string tag,
            string poolKind) =>
            _pools.Add(
                process,
                isAllocation,
                new PoolEvent(entry, bytes, RawTag: 0, tag, poolKind));

        internal void AddPoolObservation(
            ProcessInstanceKey? process,
            long timeUs,
            bool isAllocation,
            ulong entry,
            long bytes,
            string tag,
            string poolKind,
            bool rawPoolEvent,
            int? rawPid = null) =>
            _poolObservations.Add(new PoolObservation(
                process,
                rawPid ?? process?.Pid ?? -1,
                timeUs,
                isAllocation,
                new PoolEvent(entry, bytes, RawTag: 0, tag, poolKind),
                rawPoolEvent));

        internal (
            long EventCount,
            long RawEventCount,
            long TraceIdentityUnresolvedFreeCount,
            long ScopedIdentityUnresolvedFreeCount) ProjectPoolObservations(
            TimeWindow window,
            ProcessAnalysisScope scope,
            TraceIdentityIndex? identities = null)
        {
            ArgumentNullException.ThrowIfNull(scope);

            var includedProcesses = scope.IncludedProcesses.ToHashSet();
            var live = new Dictionary<ulong, PoolObservation>();
            long eventCount = 0;
            long rawEventCount = 0;
            long traceIdentityUnresolvedFreeCount = 0;
            long scopedIdentityUnresolvedFreeCount = 0;

            foreach (var observation in AnalysisEvents.Enumerate(_poolObservations)
                         .OrderBy(item => item.TimeUs))
            {
                if (observation.IsAllocation)
                {
                    live[observation.Event.Entry] = observation;
                    if (!scope.IsResolved ||
                        !observation.Process.HasValue ||
                        !window.ContainsPoint(observation.TimeUs) ||
                        !includedProcesses.Contains(observation.Process.Value))
                    {
                        continue;
                    }

                    _pools.AddProjectedAllocation(
                        observation.Process.Value,
                        observation.Event);
                    eventCount++;
                    if (observation.RawPoolEvent)
                        rawEventCount++;
                    continue;
                }

                var paired = live.Remove(observation.Event.Entry, out var allocation);
                var canAttribute = (paired && allocation.Process.HasValue) ||
                                   observation.Process.HasValue;
                if (!canAttribute)
                {
                    traceIdentityUnresolvedFreeCount++;
                    if ((identities is not null && RawSelectorMatches(
                             scope,
                             identities,
                             observation.RawPid,
                             observation.TimeUs)) ||
                        (identities is null &&
                         scope.IsResolved &&
                         scope.Window.ContainsPoint(observation.TimeUs) &&
                         (!scope.Pid.HasValue || scope.Pid.Value == observation.RawPid)))
                    {
                        scopedIdentityUnresolvedFreeCount++;
                    }
                }
                if (!scope.IsResolved ||
                    !window.ContainsPoint(observation.TimeUs))
                    continue;

                if (paired && allocation.Process.HasValue)
                {
                    if (scope.Pid.HasValue &&
                        !includedProcesses.Contains(allocation.Process.Value))
                        continue;

                    _pools.AddProjectedFree(
                        allocation.Process.Value,
                        allocation.Event,
                        unknown: false);
                }
                else if (observation.Process.HasValue &&
                         includedProcesses.Contains(observation.Process.Value))
                {
                    _pools.AddProjectedFree(
                        observation.Process.Value,
                        observation.Event,
                        unknown: true);
                }
                else
                {
                    continue;
                }

                eventCount++;
                if (observation.RawPoolEvent)
                    rawEventCount++;
            }

            return (
                eventCount,
                rawEventCount,
                traceIdentityUnresolvedFreeCount,
                scopedIdentityUnresolvedFreeCount);
        }

        public IReadOnlyList<MemoryResourceProcessRow> ProcessRows(int top) =>
            _processes.Values
                .Select(process => process.ToRow())
                .OrderByDescending(row => row.WorkingSetBytes)
                .ThenByDescending(row => row.CommitBytes)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.ProcessStartUs)
                .Take(top)
                .ToList();

        public IReadOnlyList<MemoryHandleProcessRow> HandleRows(int top) =>
            _handles.Values
                .Select(handle => handle.ToRow())
                .OrderByDescending(row => Math.Abs(row.NetDelta))
                .ThenByDescending(row => row.Created + row.Closed + row.DuplicatedIn + row.DuplicatedOut)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.ProcessStartUs)
                .Take(top)
                .ToList();

        public IReadOnlyList<MemoryPoolProcessRow> PoolProcessRows(int top) =>
            _pools.ProcessRows(top);

        public IReadOnlyList<MemoryPoolTagRow> PoolTagRows(int top) =>
            _pools.TagRows(top);

        public MemoryPressureSummary PressureSummary(int top) =>
            _pressure.ToSummary(_processes.Values, top);

        private ProcessAccumulator GetProcess(ProcessInstanceKey process)
        {
            if (!_processes.TryGetValue(process, out var accumulator))
            {
                accumulator = new ProcessAccumulator(process, _processName(process));
                _processes.Add(process, accumulator);
            }

            return accumulator;
        }

        private HandleAccumulator GetHandle(ProcessInstanceKey process)
        {
            if (!_handles.TryGetValue(process, out var accumulator))
            {
                accumulator = new HandleAccumulator(process, _processName(process));
                _handles.Add(process, accumulator);
            }

            return accumulator;
        }
    }

    private sealed class ProcessAccumulator(ProcessInstanceKey process, string processName)
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
            => Add(
                nowUs,
                PagesToBytes(values.WorkingSetPageCount),
                PagesToBytes(values.PrivateWorkingSetPageCount),
                PagesToBytes(values.CommitPageCount),
                PagesToBytes(Math.Max(0, values.CommitPageCount - values.SharedCommitInPages)),
                PagesToBytes(values.SharedCommitInPages),
                PagesToBytes(values.VirtualSizeInPages),
                PagesToBytes(values.CommitDebtInPages),
                PagesToBytes(values.StoredPageCount + values.StoreSizePageCount));

        public void Add(
            long nowUs,
            long workingSetBytes,
            long privateWorkingSetBytes,
            long commitBytes,
            long privateBytes,
            long sharedCommitBytes,
            long virtualSizeBytes,
            long commitDebtBytes,
            long storeBytes)
        {
            SampleCount++;
            _firstSampleUs = Math.Min(_firstSampleUs, nowUs);
            _lastSampleUs = Math.Max(_lastSampleUs, nowUs);

            _workingSetBytes = workingSetBytes;
            _privateWorkingSetBytes = privateWorkingSetBytes;
            _commitBytes = commitBytes;
            _sharedCommitBytes = sharedCommitBytes;
            _privateBytes = privateBytes;
            _virtualSizeBytes = virtualSizeBytes;
            _commitDebtBytes = commitDebtBytes;
            _storeBytes = storeBytes;

            PeakWorkingSetBytes = Math.Max(PeakWorkingSetBytes, _workingSetBytes);
            PeakPrivateWorkingSetBytes = Math.Max(PeakPrivateWorkingSetBytes, _privateWorkingSetBytes);
            PeakCommitBytes = Math.Max(PeakCommitBytes, _commitBytes);
            PeakPrivateBytes = Math.Max(PeakPrivateBytes, _privateBytes);
        }

        public MemoryResourceProcessRow ToRow() =>
            new(
                Pid: process.Pid,
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
                StoreBytes: _storeBytes,
                ProcessStartUs: process.StartUs);

        public MemoryPressureProcessRow ToPressureRow() =>
            new(
                Pid: process.Pid,
                ProcessName: processName,
                PeakWorkingSetBytes: PeakWorkingSetBytes,
                PeakCommitBytes: PeakCommitBytes,
                PeakPrivateBytes: PeakPrivateBytes,
                ProcessStartUs: process.StartUs);
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
            ProcessInstanceKey process,
            long workingSetBytes,
            long commitBytes,
            long privateBytes)
        {
            if (!_processBatches.TryGetValue(timeUs, out var batch))
                batch = new ProcessSnapshotBatch();

            // ProcessMemInfo can repeat the same process instance within one timestamped batch.
            // Keep its last row so aggregate pressure totals are not inflated.
            batch.Processes[process] = new ProcessSnapshot(
                WorkingSetBytes: workingSetBytes,
                CommitBytes: commitBytes,
                PrivateBytes: privateBytes);
            _processBatches[timeUs] = batch;
        }

        public MemoryPressureSummary ToSummary(IEnumerable<ProcessAccumulator> processes, int top)
        {
            foreach (var (timeUs, batch) in AnalysisEvents.Enumerate(_processBatches))
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
            public readonly Dictionary<ProcessInstanceKey, ProcessSnapshot> Processes = new();

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

    private sealed class HandleAccumulator(ProcessInstanceKey process, string processName)
    {
        public long Created { get; set; }
        public long Closed { get; set; }
        public long DuplicatedIn { get; set; }
        public long DuplicatedOut { get; set; }

        public MemoryHandleProcessRow ToRow() =>
            new(
                Pid: process.Pid,
                ProcessName: processName,
                Created: Created,
                Closed: Closed,
                DuplicatedIn: DuplicatedIn,
                DuplicatedOut: DuplicatedOut,
                NetDelta: CalculateHandleNetDelta(Created, Closed, DuplicatedIn, DuplicatedOut),
                ProcessStartUs: process.StartUs);
    }

    private readonly record struct PoolEvent(
        ulong Entry,
        long Bytes,
        ulong RawTag,
        string Tag,
        string PoolKind);

    private readonly record struct PoolObservation(
        ProcessInstanceKey? Process,
        int RawPid,
        long TimeUs,
        bool IsAllocation,
        PoolEvent Event,
        bool RawPoolEvent);

    private sealed class PoolTracker(Func<ProcessInstanceKey, string> processName)
    {
        private readonly Dictionary<ulong, PoolAllocation> _live = new();
        private readonly Dictionary<ProcessInstanceKey, PoolProcessAccumulator> _processes = new();
        private readonly Dictionary<(string Tag, string PoolKind), PoolTagAccumulator> _tags = new();

        public void Add(ProcessInstanceKey process, bool isAllocation, PoolEvent poolEvent)
        {
            if (isAllocation)
            {
                _live[poolEvent.Entry] = new PoolAllocation(process, poolEvent);
                AddProjectedAllocation(process, poolEvent);
                return;
            }

            if (_live.Remove(poolEvent.Entry, out var allocation))
            {
                AddProjectedFree(allocation.Process, allocation.Event, unknown: false);
            }
            else
            {
                AddProjectedFree(process, poolEvent, unknown: true);
            }
        }

        public void AddProjectedAllocation(ProcessInstanceKey process, PoolEvent poolEvent)
        {
            var resolvedProcessName = processName(process);
            GetProcess(process, resolvedProcessName).AddAllocation(poolEvent);
            GetTag(poolEvent.Tag, poolEvent.PoolKind).AddAllocation(poolEvent.Bytes);
        }

        public void AddProjectedFree(
            ProcessInstanceKey process,
            PoolEvent poolEvent,
            bool unknown)
        {
            var resolvedProcessName = processName(process);
            GetProcess(process, resolvedProcessName).AddFree(poolEvent, unknown);
            GetTag(poolEvent.Tag, poolEvent.PoolKind).AddFree(poolEvent.Bytes, unknown);
        }

        public IReadOnlyList<MemoryPoolProcessRow> ProcessRows(int top)
            => _processes.Values
                .Select(process => process.ToRow())
                .OrderByDescending(row => row.PagedOutstandingBytes + row.NonPagedOutstandingBytes)
                .ThenByDescending(row => row.PagedAllocatedBytes + row.NonPagedAllocatedBytes)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.ProcessStartUs)
                .Take(top)
                .ToList();

        public IReadOnlyList<MemoryPoolTagRow> TagRows(int top)
            => _tags.Values
                .Select(tag => tag.ToRow())
                .OrderByDescending(row => row.OutstandingBytes)
                .ThenByDescending(row => row.AllocatedBytes)
                .ThenBy(row => row.Tag, StringComparer.Ordinal)
                .ThenBy(row => row.PoolKind, StringComparer.Ordinal)
                .Take(top)
                .ToList();

        private PoolProcessAccumulator GetProcess(ProcessInstanceKey process, string resolvedProcessName)
        {
            if (!_processes.TryGetValue(process, out var accumulator))
            {
                accumulator = new PoolProcessAccumulator(process, resolvedProcessName);
                _processes.Add(process, accumulator);
            }

            return accumulator;
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

    private sealed record PoolAllocation(
        ProcessInstanceKey Process,
        PoolEvent Event);

    private sealed class PoolProcessAccumulator(ProcessInstanceKey process, string processName)
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
                Pid: process.Pid,
                ProcessName: processName,
                PagedOutstandingBytes: PagedOutstandingBytes,
                NonPagedOutstandingBytes: NonPagedOutstandingBytes,
                PagedAllocatedBytes: PagedAllocatedBytes,
                NonPagedAllocatedBytes: NonPagedAllocatedBytes,
                PagedFreedBytes: PagedFreedBytes,
                NonPagedFreedBytes: NonPagedFreedBytes,
                AllocationCount: AllocationCount,
                FreeCount: FreeCount,
                UnknownFreeCount: UnknownFreeCount,
                ProcessStartUs: process.StartUs);

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
