using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Aggregates MemoryHardFault events into per-file page-in totals.  PerfView equivalent:
// "Memory Hard Fault → ByFile" view.  Most hard faults are mmap'd files being touched for
// the first time; the rest come from paged-out heap/stack pages and the page file.
//
// Probe findings (TraceEvent 3.2.2, KernelTraceEventParser):
//   * Event: MemoryHardFault (NOT PageFaultHardFault as the plan template guessed).
//   * Data type: MemoryHardFaultTraceData with these properties:
//       Double ElapsedTimeMSec
//       Int64 ReadOffset
//       UInt64 VirtualAddress
//       UInt64 FileKey         -- Section-object key, NOT a user-mode FileObject handle.
//       String FileName        -- Sometimes populated directly; can be empty for files
//                                 mapped before the trace started.
//       Int32 ByteCount        -- Bytes paged in for this fault.
//       Int32 ProcessID
//
// Because hard faults reference FileKey (not FileObject), FileObjectResolver — which
// keys on FileObject — does not apply here. Per task instructions we keep the change
// contained: this file builds its own FileKey -> FileName map by subscribing to the
// kernel events whose data type is FileIONameTraceData (FileIOName, FileIOFileCreate,
// FileIOFileDelete, FileIOFileRundown — confirmed in Task 11). We also fold in any
// FileName the hard-fault event itself supplies, since it can be present.
//
// FileKey names and hard faults are processed in trace order. A fault never receives a
// name first observed later in the trace, and FileDelete terminates that key's mapping.
public static class HardFaultByFileAnalysis
{
    public static HardFaultByFileResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        string orderBy = "bytes",
        long? startUs = null,
        long? endUs = null,
        long? processStartUs = null,
        bool? filterSpecified = null)
    {
        var normalizedOrderBy = NormalizeOrderBy(orderBy);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(window, pid, processStartUs, identities);

        var fileNames = new TemporalFileNameMap<ulong>();
        long globalEventCount = 0;
        long matchedEventCount = 0;
        var agg = new Dictionary<string, (long bytes, long count, long maxLatencyUs, long maxLatencyTimeUs)>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            void Capture(Microsoft.Diagnostics.Tracing.Parsers.Kernel.FileIONameTraceData data)
            {
                if (data.FileKey != 0 && !string.IsNullOrEmpty(data.FileName))
                    fileNames.Add(data.FileKey, ToUs(data), data.EventIndex, data.FileName);
            }

            kernel.FileIOName += Capture;
            kernel.FileIOFileCreate += Capture;
            kernel.FileIOFileDelete += data =>
            {
                Capture(data);
                if (data.FileKey != 0)
                    fileNames.End(data.FileKey, ToUs(data), data.EventIndex);
            };
            kernel.FileIOFileRundown += Capture;
            kernel.MemoryHardFault += data =>
            {
                globalEventCount++;
                var nowUs = ToUs(data);
                if (!scope.MatchesEvent(identities, data.ProcessID, nowUs)) return;
                matchedEventCount++;

                // Prefer the FileName the event carries; otherwise fall back to the FileKey map.
                var name = ResolveFileName(
                    data.FileName,
                    data.FileKey,
                    nowUs,
                    data.EventIndex,
                    fileNames);

                var cur = agg.GetValueOrDefault(name);
                var latencyUs = (long)(data.ElapsedTimeMSec * 1000);
                var maxLatencyUs = cur.maxLatencyUs;
                var maxLatencyTimeUs = cur.maxLatencyTimeUs;
                if (cur.count == 0 || latencyUs > cur.maxLatencyUs)
                {
                    maxLatencyUs = latencyUs;
                    maxLatencyTimeUs = nowUs;
                }

                agg[name] = (cur.bytes + data.ByteCount,
                             cur.count + 1,
                             maxLatencyUs,
                             maxLatencyTimeUs);
            };
        });

        var rows = agg
            .Select(kv => new HardFaultFileRow(
                kv.Key,
                kv.Value.bytes,
                kv.Value.count,
                kv.Value.maxLatencyUs,
                kv.Value.maxLatencyTimeUs))
            .OrderByDescending(r => SortMetric(r, normalizedOrderBy))
            .ThenByDescending(r => r.PageInBytes)
            .ThenByDescending(r => r.PageInCount)
            .Take(top)
            .ToList();

        var warnings = new List<string> { WarningBuilder.HardFaultKeywordHint };
        var scopeMissing = !scope.IsResolved;
        var capabilityStatus = scopeMissing
            ? "unknown"
            : matchedEventCount > 0
                ? "observed"
                : globalEventCount == 0
                    ? "not_observed"
                    : "unknown";
        var noDataReason = scopeMissing
            ? "scope_not_found"
            : matchedEventCount > 0
                ? null
                : globalEventCount == 0
                    ? "event_class_not_observed"
                    : "no_events_in_scope";
        AddNoDataWarning(warnings, noDataReason);
        return new HardFaultByFileResponse(
            rows,
            warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.Pid.HasValue
                ? scope.IncludedProcesses
                : Array.Empty<ProcessInstanceKey>(),
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: noDataReason);
    }

    private static void AddNoDataWarning(
        ICollection<string> warnings,
        string? noDataReason)
    {
        switch (noDataReason)
        {
            case "scope_not_found":
                warnings.Add(
                    "scope_not_found: the requested process lifetime does not intersect the analysis window.");
                break;
            case "event_class_not_observed":
                warnings.Add(
                    "event_class_not_observed: " +
                    WarningBuilder.MissingKeyword(
                        "MemoryHardFault",
                        "MemoryHardFaults"));
                break;
            case "no_events_in_scope":
                warnings.Add(
                    "no_events_in_scope: MemoryHardFault events were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window.");
                break;
        }
    }

    private static long ToUs(Microsoft.Diagnostics.Tracing.TraceEvent data) =>
        (long)(data.TimeStampRelativeMSec * 1000);

    internal static string NormalizeOrderBy(string? orderBy)
    {
        var normalized = string.IsNullOrWhiteSpace(orderBy)
            ? "bytes"
            : orderBy.Trim().ToLowerInvariant().Replace("-", "_");

        return normalized switch
        {
            "bytes" or "page_in_bytes" => "bytes",
            "count" or "faults" or "page_in_count" => "count",
            "latency" or "max_latency" or "max_latency_us" => "max_latency",
            _ => throw new ArgumentException(
                "orderBy must be one of: bytes, count, max_latency.",
                nameof(orderBy))
        };
    }

    private static long SortMetric(HardFaultFileRow row, string orderBy) =>
        orderBy switch
        {
            "count" => row.PageInCount,
            "max_latency" => row.MaxLatencyUs,
            _ => row.PageInBytes
        };

    internal static string ResolveFileName(
        string? eventFileName,
        ulong fileKey,
        long timestampUs,
        TemporalFileNameMap<ulong> fileNames)
    {
        if (!string.IsNullOrEmpty(eventFileName)) return eventFileName;
        return fileNames.TryResolveAt(fileKey, timestampUs, out var mapped)
            ? mapped
            : $"<unmapped:0x{fileKey:X}>";
    }

    private static string ResolveFileName(
        string? eventFileName,
        ulong fileKey,
        long timestampUs,
        Microsoft.Diagnostics.Tracing.EventIndex eventIndex,
        TemporalFileNameMap<ulong> fileNames)
    {
        if (!string.IsNullOrEmpty(eventFileName)) return eventFileName;
        return fileNames.TryResolveAt(fileKey, timestampUs, eventIndex, out var mapped)
            ? mapped
            : $"<unmapped:0x{fileKey:X}>";
    }

}
