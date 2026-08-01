using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// CLR heap-size timeline. Each point is attributed to an exact process
// lifetime so snapshots from a reused PID cannot form a false growth trend.
public static class GcHeapStatsAnalysis
{
    public static GcHeapStatsResponse Analyze(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(
            trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(startUs ?? 0, endUs ?? traceEndUs);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ResolveTrendScope(
            window, pid, processStartUs, identities);

        var events = new List<GcHeapStatsEvent>();
        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCHeapStats += data => events.Add(new GcHeapStatsEvent(
                Pid: data.ProcessID,
                TimeUs: TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                TotalHeapBytes: data.TotalHeapSize,
                Gen0Bytes: data.GenerationSize0,
                Gen1Bytes: data.GenerationSize1,
                Gen2Bytes: data.GenerationSize2,
                LohBytes: data.GenerationSize3,
                PohBytes: data.GenerationSize4,
                PinnedObjectCount: data.PinnedObjectCount,
                GcHandleCount: data.GCHandleCount,
                FinalizationPromotedBytes: data.FinalizationPromotedSize,
                FinalizationPromotedCount: data.FinalizationPromotedCount));
        });

        return AnalyzeResolved(identities, events, pid, scope);
    }

    internal static GcHeapStatsResponse AnalyzeEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<GcHeapStatsEvent> events,
        int? pid,
        TimeWindow window,
        long? processStartUs)
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs,
            processLifetimes,
            Array.Empty<ThreadLifecycleEvent>());
        var scope = ResolveTrendScope(
            window, pid, processStartUs, identities);
        return AnalyzeResolved(identities, events, pid, scope);
    }

    private static GcHeapStatsResponse AnalyzeResolved(
        TraceIdentityIndex identities,
        IReadOnlyList<GcHeapStatsEvent> events,
        int? pid,
        ProcessAnalysisScope scope)
    {
        var rows = new List<GcHeapStatsRow>();
        long traceIdentityUnresolvedEventCount = 0;
        long scopedIdentityUnresolvedEventCount = 0;
        foreach (var observation in events)
        {
            var traceResolution = identities.Processes.Resolve(
                observation.Pid,
                observation.TimeUs,
                processStartUs: null);
            if (traceResolution.Status != InstanceResolutionStatus.Resolved ||
                !traceResolution.Value.HasValue)
            {
                traceIdentityUnresolvedEventCount++;
            }

            if (!scope.IsResolved ||
                !scope.Window.ContainsPoint(observation.TimeUs) ||
                (pid.HasValue && observation.Pid != pid.Value))
            {
                continue;
            }

            var scopedResolution = identities.Processes.Resolve(
                observation.Pid,
                observation.TimeUs,
                scope.SelectedProcess?.StartUs);
            if (scopedResolution.Status != InstanceResolutionStatus.Resolved ||
                !scopedResolution.Value.HasValue)
            {
                if (scope.MatchesRawUnresolvedCandidate(
                        identities,
                        observation.Pid,
                        observation.TimeUs))
                {
                    scopedIdentityUnresolvedEventCount++;
                }
                continue;
            }
            var process = scopedResolution.Value.Value;
            if (!scope.IncludedProcesses.Contains(process))
                continue;

            rows.Add(new GcHeapStatsRow(
                TimeUs: observation.TimeUs,
                Pid: observation.Pid,
                TotalHeapBytes: observation.TotalHeapBytes,
                Gen0Bytes: observation.Gen0Bytes,
                Gen1Bytes: observation.Gen1Bytes,
                Gen2Bytes: observation.Gen2Bytes,
                LohBytes: observation.LohBytes,
                PohBytes: observation.PohBytes,
                PinnedObjectCount: observation.PinnedObjectCount,
                GcHandleCount: observation.GcHandleCount,
                FinalizationPromotedBytes: observation.FinalizationPromotedBytes,
                FinalizationPromotedCount: observation.FinalizationPromotedCount,
                ProcessStartUs: process.StartUs));
        }

        rows.Sort((left, right) =>
        {
            var timeComparison = left.TimeUs.CompareTo(right.TimeUs);
            if (timeComparison != 0)
                return timeComparison;
            var pidComparison = left.Pid.CompareTo(right.Pid);
            return pidComparison != 0
                ? pidComparison
                : left.ProcessStartUs.CompareTo(right.ProcessStartUs);
        });

        var warnings = new List<string>();
        if (!scope.IsResolved)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(
                scope.ScopeStatus));
        }
        else if (events.Count == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "GCHeapStats",
                "GC",
                "or no GC fired in the trace"));
        }
        else if (rows.Count == 0)
        {
            warnings.Add(
                "no_matching_gc_heap_stats: GCHeapStats events were present, but none matched the selected process instance and half-open window.");
        }
        if (scopedIdentityUnresolvedEventCount > 0)
        {
            warnings.Add(
                $"source_events_unattributed: {scopedIdentityUnresolvedEventCount:N0} GCHeapStats event(s) matched the raw PID/window selector but could not be tied safely to an included process lifetime.");
        }

        return new GcHeapStatsResponse(
            Pid: pid,
            Rows: rows,
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: scope.IsResolved
                ? rows.Count > 0
                    ? "observed"
                    : events.Count == 0
                        ? "not_observed"
                        : "unknown"
                : "unknown",
            MatchedEventCount: rows.Count,
            NoDataReason: !scope.IsResolved
                ? scope.ScopeStatus
                : events.Count == 0
                    ? "event_class_not_observed"
                    : rows.Count == 0
                        ? scopedIdentityUnresolvedEventCount > 0
                            ? "source_events_unattributed"
                            : "no_events_in_scope"
                        : null,
            TraceIdentityUnresolvedEventCount: traceIdentityUnresolvedEventCount,
            ScopedIdentityUnresolvedEventCount: scopedIdentityUnresolvedEventCount);
    }

    private static ProcessAnalysisScope ResolveTrendScope(
        TimeWindow window,
        int? pid,
        long? processStartUs,
        TraceIdentityIndex identities)
    {
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        return pid.HasValue ? scope.RequireSingleProcess() : scope;
    }
}

internal readonly record struct GcHeapStatsEvent(
    int Pid,
    long TimeUs,
    long TotalHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LohBytes,
    long PohBytes,
    int PinnedObjectCount,
    int GcHandleCount,
    long FinalizationPromotedBytes,
    long FinalizationPromotedCount);
