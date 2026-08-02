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
// Simultaneous valid key/object mappings that disagree are reported as ambiguous;
// neither candidate name is selected.
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
        var agg = new Dictionary<string, FileIoAggregate>();
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
                var resolution = ResolveFileMapping(
                    resolver,
                    data.FileName,
                    data.FileObject,
                    data.FileKey,
                    timestampUs,
                    data.EventIndex);
                GetAggregate(agg, resolution.File).AddRead(data.IoSize, resolution.MappingState);
            };
            kernel.FileIOWrite += data =>
            {
                globalEventCount++;
                var timestampUs = ToUs(data.TimeStampRelativeMSec);
                if (!scope.MatchesEvent(identities, data.ProcessID, timestampUs)) return;
                matchedEventCount++;
                var resolution = ResolveFileMapping(
                    resolver,
                    data.FileName,
                    data.FileObject,
                    data.FileKey,
                    timestampUs,
                    data.EventIndex);
                GetAggregate(agg, resolution.File).AddWrite(data.IoSize, resolution.MappingState);
            };
        });

        var rows = RankRows(agg, top);

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

    internal static IReadOnlyList<FileIoRow> RankRows(
        IEnumerable<KeyValuePair<string, FileIoAggregate>> aggregates,
        int top)
    {
        ArgumentNullException.ThrowIfNull(aggregates);
        return aggregates
            // Rank with the same checked Int64 semantics used by the byte
            // accumulators. Silent wraparound here would make the largest file
            // appear small while the returned component totals still look exact.
            .OrderByDescending(kv => kv.Value.TotalBytes)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(top)
            .Select(kv => kv.Value.ToRow(kv.Key))
            .ToList();
    }

    internal static string ResolveFileName(
        FileObjectResolver resolver,
        string? eventFileName,
        ulong fileObject,
        ulong fileKey,
        long timestampUs) =>
        ResolveFileMapping(
            resolver,
            eventFileName,
            fileObject,
            fileKey,
            timestampUs).File;

    internal static FileMappingResolution ResolveFileMapping(
        FileObjectResolver resolver,
        string? eventFileName,
        ulong fileObject,
        ulong fileKey,
        long timestampUs) =>
        !string.IsNullOrEmpty(eventFileName)
            ? FileMappingResolution.FromEventName(eventFileName)
            : resolver.ResolveDetailedAt(fileObject, fileKey, timestampUs);

    private static FileMappingResolution ResolveFileMapping(
        FileObjectResolver resolver,
        string? eventFileName,
        ulong fileObject,
        ulong fileKey,
        long timestampUs,
        Microsoft.Diagnostics.Tracing.EventIndex eventIndex) =>
        !string.IsNullOrEmpty(eventFileName)
            ? FileMappingResolution.FromEventName(eventFileName)
            : resolver.ResolveDetailedAt(fileObject, fileKey, timestampUs, eventIndex);

    private static FileIoAggregate GetAggregate(
        IDictionary<string, FileIoAggregate> aggregates,
        string file)
    {
        if (!aggregates.TryGetValue(file, out var aggregate))
        {
            aggregate = new FileIoAggregate();
            aggregates.Add(file, aggregate);
        }

        return aggregate;
    }

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

    internal sealed class FileIoAggregate
    {
        private readonly FileMappingStateAccumulator _mappingStates = new();

        public long ReadBytes { get; private set; }
        public long ReadCount { get; private set; }
        public long WriteBytes { get; private set; }
        public long WriteCount { get; private set; }
        public long TotalBytes => checked(ReadBytes + WriteBytes);

        public void AddRead(long bytes, string mappingState)
        {
            checked
            {
                ReadBytes += bytes;
                ReadCount++;
            }
            _mappingStates.Add(mappingState);
        }

        public void AddWrite(long bytes, string mappingState)
        {
            checked
            {
                WriteBytes += bytes;
                WriteCount++;
            }
            _mappingStates.Add(mappingState);
        }

        public FileIoRow ToRow(string file) => new(
            file,
            ReadBytes,
            ReadCount,
            WriteBytes,
            WriteCount,
            _mappingStates.AggregateState,
            _mappingStates.Snapshot());
    }
}
