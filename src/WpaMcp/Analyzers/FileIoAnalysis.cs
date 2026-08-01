using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Aggregates FileIORead/FileIOWrite events into per-file byte/count totals.
//
// File mappings and I/O are processed in one trace-ordered pass. The event's own
// FileName wins; otherwise the resolver uses only FileKey/FileObject names observed
// no later than that event. Future rundown or pointer reuse cannot rename earlier I/O.
public static class FileIoAnalysis
{
    public static FileIoResponse TopFiles(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs = null,
        long? endUs = null,
        long? processStartUs = null)
    {
        ArgumentNullException.ThrowIfNull(trace);
        if (processStartUs.HasValue && !pid.HasValue)
            throw new ArgumentException("processStartUs requires pid.", nameof(processStartUs));

        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(window, pid, processStartUs, identities);
        var resolver = new FileObjectResolver();
        var agg = new Dictionary<string, (long ReadBytes, long ReadCount, long WriteBytes, long WriteCount)>();
        long globalEventCount = 0;
        long matchedEventCount = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            resolver.Subscribe(kernel);
            kernel.FileIORead += data =>
            {
                globalEventCount++;
                var timestampUs = ToUs(data.TimeStampRelativeMSec);
                if (!scope.MatchesEvent(identities, data.ProcessID, timestampUs)) return;
                matchedEventCount++;
                var name = ResolveFileName(
                    resolver,
                    data.FileName,
                    data.FileObject,
                    data.FileKey,
                    timestampUs,
                    data.EventIndex);
                var cur = agg.GetValueOrDefault(name);
                agg[name] = (cur.ReadBytes + data.IoSize, cur.ReadCount + 1, cur.WriteBytes, cur.WriteCount);
            };
            kernel.FileIOWrite += data =>
            {
                globalEventCount++;
                var timestampUs = ToUs(data.TimeStampRelativeMSec);
                if (!scope.MatchesEvent(identities, data.ProcessID, timestampUs)) return;
                matchedEventCount++;
                var name = ResolveFileName(
                    resolver,
                    data.FileName,
                    data.FileObject,
                    data.FileKey,
                    timestampUs,
                    data.EventIndex);
                var cur = agg.GetValueOrDefault(name);
                agg[name] = (cur.ReadBytes, cur.ReadCount, cur.WriteBytes + data.IoSize, cur.WriteCount + 1);
            };
        });

        var rows = agg
            .Select(kv => new FileIoRow(kv.Key, kv.Value.ReadBytes, kv.Value.ReadCount, kv.Value.WriteBytes, kv.Value.WriteCount))
            .OrderByDescending(r => r.ReadBytes + r.WriteBytes)
            .Take(top)
            .ToList();

        // CapabilityStatus is scoped evidence across process-level tools: it is
        // observed only when this selector matched source events. A trace-wide
        // event class seen only outside the requested scope remains unknown here;
        // NoDataReason preserves the global-vs-scoped distinction.
        var capabilityStatus = !scope.IsResolved
            ? "unknown"
            : matchedEventCount > 0
                ? "observed"
                : globalEventCount == 0 ? "not_observed" : "unknown";
        var noDataReason = ClassifyNoData(scope, globalEventCount, matchedEventCount);
        var warnings = BuildWarnings(scope, pid, processStartUs, noDataReason);
        return new FileIoResponse(
            Rows: rows,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedEventCount,
            NoDataReason: noDataReason,
            Warnings: warnings);
    }

    internal static string ResolveFileName(
        FileObjectResolver resolver,
        string? eventFileName,
        ulong fileObject,
        ulong fileKey,
        long timestampUs) =>
        !string.IsNullOrEmpty(eventFileName)
            ? eventFileName
            : resolver.ResolveAt(fileObject, fileKey, timestampUs);

    private static string ResolveFileName(
        FileObjectResolver resolver,
        string? eventFileName,
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        Microsoft.Diagnostics.Tracing.EventIndex eventIndex) =>
        !string.IsNullOrEmpty(eventFileName)
            ? eventFileName
            : resolver.ResolveAt(fileObject, fileKey, timestampUs, eventIndex);

    internal static string? ClassifyNoData(
        ProcessAnalysisScope scope,
        long globalEventCount,
        long matchedEventCount)
    {
        if (!scope.IsResolved) return scope.ScopeStatus;
        if (globalEventCount == 0) return "event_class_not_observed";
        return matchedEventCount == 0 ? "no_events_in_scope" : null;
    }

    private static IReadOnlyList<string> BuildWarnings(
        ProcessAnalysisScope scope,
        int? pid,
        long? processStartUs,
        string? noDataReason)
    {
        var warnings = new List<string>();
        if (scope.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                $"pid_aggregate: PID {pid} matched {scope.IncludedProcesses.Count} process lifetimes in the requested window; totals combine those instances. Specify processStartUs for one lifetime.");
        }

        switch (noDataReason)
        {
            case ProcessAnalysisScope.AmbiguousStatus:
                warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(
                    ProcessAnalysisScope.AmbiguousStatus));
                break;
            case "scope_not_found":
                warnings.Add(
                    $"scope_not_found: no process lifetime for PID {pid}" +
                    (processStartUs.HasValue ? $" with processStartUs={processStartUs.Value}" : string.Empty) +
                    " intersects the requested window.");
                break;
            case "event_class_not_observed":
                warnings.Add(
                    "event_class_not_observed: " +
                    WarningBuilder.MissingKeyword("FileIO Read/Write", "FileIO"));
                break;
            case "no_events_in_scope":
                warnings.Add(
                    "no_events_in_scope: FileIO Read/Write events were observed elsewhere in the trace, but none matched the selected process lifetimes and half-open window.");
                break;
        }

        return warnings;
    }

    private static long ToUs(double timeStampRelativeMSec) =>
        TraceTime.FromMilliseconds(timeStampRelativeMSec);
}
