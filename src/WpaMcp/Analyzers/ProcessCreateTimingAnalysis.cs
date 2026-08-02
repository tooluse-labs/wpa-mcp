using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// "How long did each fork take" view. Given a parent PID, returns each child process the
// kernel reported as having that parent, with two key timing signals per child:
//
//   FirstImageLoadOffsetUs — the observed interval from ProcessStart to the first DLL mapped
//     into the new address space. Process callbacks, scanning, suspension, scheduling, and
//     other work can fall in this interval; the measurement alone does not identify which
//     mechanism consumed it or prove that no user-mode work occurred.
//
//   GapFromPreviousSpawnUs — wall time between consecutive sibling spawns. Useful for
//     spotting burst patterns versus steady-state creation. It does not by itself establish
//     contention or identify a component handling the creates.
//
// Aggregate stats (median / p95 / max) across all kernel gaps surface the worst-case in a
// single number for tooling. PerfView/WPA don't have a dedicated "fork timing" view; this
// analyzer is original to wpa-mcp.
public static class ProcessCreateTimingAnalysis
{
    internal const long SlowFirstImageLoadGapUs = 1_000_000;
    internal const long VerySlowFirstImageLoadGapUs = 5_000_000;

    public static ProcessCreateTimingResponse Analyze(
        TraceLog trace,
        int parentPid,
        int top,
        long? processStartUs = null)
    {
        var query = TimelinePagination.CanonicalQuery(
            TimelinePagination.ProcessCreateTimingTool,
            ("parentPid", TimelinePagination.Number(parentPid)),
            ("processStartUs", TimelinePagination.OptionalNumber(processStartUs)),
            ("pageSize", TimelinePagination.Number(top)));
        return AnalyzePage(
            trace,
            parentPid,
            top,
            processStartUs,
            new QueryResultCursorPosition(TimelinePagination.Phase, 0, null),
            TimelinePagination.DirectContext(
                TimelinePagination.ProcessCreateTimingTool,
                query,
                TimelinePagination.ProcessCreateTimingOrdering));
    }

    internal static ProcessCreateTimingResponse AnalyzePage(
        TraceLog trace,
        int parentPid,
        int pageSize,
        long? processStartUs,
        QueryResultCursorPosition pagePosition,
        TimelineQueryContext queryContext)
    {
        if (pageSize <= 0) throw new ArgumentOutOfRangeException(nameof(pageSize));

        var warnings = new List<string>();
        var identities = TraceIdentityIndex.For(trace);
        var globalProcessStartEventCount = identities.Processes.Lifetimes
            .Count(lifetime => lifetime.StartObserved);
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, identities.TraceEndUs),
            parentPid,
            processStartUs,
            identities).RequireSingleProcess();
        if (!scope.IsResolved)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(
                scope.ScopeStatus));
            warnings.Add(
                $"No children were attributed to ParentID={parentPid} because the selected parent process scope did not resolve safely.");
            return Empty(
                parentPid,
                parentName: null,
                processStartUs: null,
                warnings,
                scope,
                capabilityStatus: "unknown",
                noDataReason: scope.ScopeStatus,
                pageSize,
                pagePosition,
                queryContext);
        }

        var parentLifetime = identities.Processes
            .FindExact(scope.SelectedProcess!.Value)
            .OrderByDescending(candidate => candidate.EndUs)
            .First();

        var parent = AnalysisEvents.Enumerate(trace.Processes).FirstOrDefault(process =>
            process.ProcessID == parentPid &&
            TraceTime.FromMilliseconds(process.StartTimeRelativeMsec) ==
                parentLifetime.Key.StartUs);

        var childInventory = AnalysisEvents.Enumerate(trace.Processes)
            .Select((process, sourceOrdinal) => new
            {
                Process = process,
                SourceOrdinal = (long)sourceOrdinal,
                StartTimeUs = TraceTime.FromMilliseconds(process.StartTimeRelativeMsec),
            })
            .Where(item => ChildBelongsToParentInstance(
                parentLifetime,
                item.Process.ParentID,
                item.StartTimeUs))
            .OrderBy(item => item.StartTimeUs)
            .ThenBy(item => item.Process.ProcessID)
            .ThenBy(item => item.SourceOrdinal)
            .ToList();
        var children = childInventory
            .Where(item => identities.Processes
                .FindExact(new ProcessInstanceKey(
                    item.Process.ProcessID,
                    item.StartTimeUs))
                .Any(lifetime => lifetime.StartObserved))
            .ToList();
        var backfilledChildrenExcluded = childInventory.Count - children.Count;
        if (backfilledChildrenExcluded > 0)
        {
            warnings.Add(
                $"Excluded {backfilledChildrenExcluded} child process inventory row(s) because no observed ProcessStart record established an exact spawn time; no spawn gap or startup-relative timing was inferred from backfill.");
        }

        if (children.Count == 0)
        {
            if (globalProcessStartEventCount == 0)
            {
                warnings.Add(
                    "event_class_not_observed: no observed ProcessStart records were materialized in the trace. This does not prove that process lifecycle capture was disabled.");
            }
            warnings.Add(
                $"No children found with ParentID={parentPid}. Either the parent didn't fork " +
                "anything in the captured window, or trace.Processes hasn't seen the ProcessStart " +
                "events for the children. Check list_processes output for known PIDs and their ParentPid.");
            return Empty(
                parentPid,
                parent?.Name,
                parentLifetime.Key.StartUs,
                warnings,
                scope,
                capabilityStatus: globalProcessStartEventCount == 0
                    ? "not_observed"
                    : "unknown",
                noDataReason: globalProcessStartEventCount == 0
                    ? "event_class_not_observed"
                    : "no_events_in_scope",
                pageSize,
                pagePosition,
                queryContext,
                backfilledChildrenExcluded);
        }

        // Preserve the child process-instance key so a reused child PID cannot borrow the
        // earlier/later lifetime's first ImageLoad event.
        var childKeys = children
            .Select(child => new ProcessInstanceKey(
                child.Process.ProcessID,
                child.StartTimeUs))
            .ToList();
        var matchedProcessStartEventCount = childKeys.Count(key =>
            identities.Processes.FindExact(key).Any(lifetime => lifetime.StartObserved));
        var imageLoadsByProcess = ImageLoadAnalysis.ForProcesses(trace, childKeys);

        var rows = new List<ChildSpawnTiming>(children.Count);
        long? prevStartUs = null;
        foreach (var child in AnalysisEvents.Enumerate(children))
        {
            var c = child.Process;
            var startUs = child.StartTimeUs;
            var childKey = new ProcessInstanceKey(c.ProcessID, startUs);
            long? firstLoadOffset = imageLoadsByProcess.TryGetValue(childKey, out var loads) && loads.Count > 0
                ? loads[0].TimeUs - startUs
                : (long?)null;

            rows.Add(new ChildSpawnTiming(
                Pid: c.ProcessID,
                Name: c.Name ?? string.Empty,
                StartTimeUs: startUs,
                FirstImageLoadOffsetUs: firstLoadOffset,
                ImageLoadCount: loads?.Count ?? 0,
                GapFromPreviousSpawnUs: prevStartUs.HasValue ? startUs - prevStartUs.Value : (long?)null,
                SourceOrdinal: child.SourceOrdinal));
            prevStartUs = startUs;
        }

        // Aggregate kernel-gap distribution across children that actually loaded a DLL.
        // Children with FirstImageLoadOffsetUs=null had no ImageLoad events (extremely
        // short-lived OR the trace cut them off pre-load) and would skew percentiles, so
        // we exclude them.
        var gaps = rows.Select(r => r.FirstImageLoadOffsetUs)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .OrderBy(g => g)
            .ToList();

        long? median = MedianRounded(gaps);
        long? p95 = NearestRank(gaps, numerator: 95, denominator: 100);
        long? max = gaps.Count > 0 ? gaps[^1] : (long?)null;

        var spawnGaps = rows.Select(r => r.GapFromPreviousSpawnUs)
            .Where(g => g.HasValue)
            .Select(g => g!.Value)
            .ToList();
        long? avgSpawnGap = RoundedMean(spawnGaps);

        var page = TimelinePagination.Slice(
            rows,
            pagePosition,
            pageSize,
            TimelinePagination.ProcessCreateKey);
        AddKernelGapWarnings(rows, warnings);

        return new ProcessCreateTimingResponse(
            ParentPid: parentPid,
            ParentName: parent?.Name,
            SpawnCount: rows.Count,
            FirstSpawnTimeUs: rows[0].StartTimeUs,
            LastSpawnTimeUs: rows[^1].StartTimeUs,
            AvgSpawnGapUs: avgSpawnGap,
            MedianKernelGapUs: median,
            P95KernelGapUs: p95,
            MaxKernelGapUs: max,
            Children: page.Rows,
            Warnings: warnings,
            ParentProcessStartUs: parentLifetime.Key.StartUs,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: matchedProcessStartEventCount > 0
                ? "observed"
                : globalProcessStartEventCount == 0
                    ? "not_observed"
                    : "unknown",
            MatchedEventCount: matchedProcessStartEventCount,
            NoDataReason: null,
            PageContext: queryContext.PageContext(
                page.StartIndex,
                pageSize,
                page.TotalCount,
                page.Rows.Count),
            ReturnedCount: page.Rows.Count,
            HasMore: page.HasMore,
            NextCursor: page.HasMore
                ? QueryResultCursorRegistry.PendingDeliveryToken
                : null,
            BackfilledChildrenExcluded: backfilledChildrenExcluded);
    }

    internal static void AddKernelGapWarnings(IReadOnlyList<ChildSpawnTiming> rows, IList<string> warnings)
    {
        var slowRows = rows
            .Where(row => row.FirstImageLoadOffsetUs >= SlowFirstImageLoadGapUs)
            .OrderByDescending(row => row.FirstImageLoadOffsetUs)
            .ToList();
        if (slowRows.Count == 0)
            return;

        var max = slowRows[0].FirstImageLoadOffsetUs!.Value;
        var thresholdMs = SlowFirstImageLoadGapUs / 1000;
        var severity = max >= VerySlowFirstImageLoadGapUs ? "very slow" : "slow";
        var examples = string.Join(", ", slowRows.Take(5).Select(row =>
            $"{row.Name}({row.Pid})={row.FirstImageLoadOffsetUs!.Value / 1000}ms"));
        warnings.Add(
            $"Detected {slowRows.Count} {severity} child process first-image-load gap(s) >= {thresholdMs}ms " +
            $"(max {max / 1000}ms; first {Math.Min(5, slowRows.Count)} of {slowRows.Count} examples: {examples}). " +
            "This pre-user-mode boundary is consistent with delay somewhere in the process-creation path, " +
            "including process callbacks, AV/EDR inspection, or minifilter work; the gap alone does not " +
            "identify which mechanism caused it. Corroborate with provider events, stacks, or controlled comparison.");
    }

    internal static bool ChildBelongsToParentInstance(
        ProcessLifetime parent,
        int observedParentPid,
        long childStartUs) =>
        observedParentPid == parent.Key.Pid && parent.Contains(childStartUs);

    internal static long? RoundedMean(IReadOnlyList<long> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0)
            return null;
        decimal sum = 0;
        foreach (var value in values)
            sum += value;
        return checked((long)Math.Round(
            sum / values.Count,
            decimals: 0,
            MidpointRounding.AwayFromZero));
    }

    internal static long? MedianRounded(IReadOnlyList<long> sortedValues)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);
        if (sortedValues.Count == 0)
            return null;
        var middle = sortedValues.Count / 2;
        return sortedValues.Count % 2 == 1
            ? sortedValues[middle]
            : RoundedMean([sortedValues[middle - 1], sortedValues[middle]]);
    }

    internal static long? NearestRank(
        IReadOnlyList<long> sortedValues,
        int numerator,
        int denominator)
    {
        ArgumentNullException.ThrowIfNull(sortedValues);
        if (sortedValues.Count == 0)
            return null;
        if (numerator <= 0 || numerator > denominator || denominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(numerator));
        var rank = checked(
            ((long)numerator * sortedValues.Count + denominator - 1) /
            denominator);
        return sortedValues[checked((int)rank - 1)];
    }

    private static ProcessCreateTimingResponse Empty(
        int parentPid,
        string? parentName,
        long? processStartUs,
        List<string> warnings,
        ProcessAnalysisScope scope,
        string capabilityStatus,
        string noDataReason,
        int pageSize,
        QueryResultCursorPosition pagePosition,
        TimelineQueryContext queryContext,
        int backfilledChildrenExcluded = 0)
    {
        var page = TimelinePagination.Slice(
            Array.Empty<ChildSpawnTiming>(),
            pagePosition,
            pageSize,
            TimelinePagination.ProcessCreateKey);
        return new(
            ParentPid: parentPid,
            ParentName: parentName,
            SpawnCount: 0,
            FirstSpawnTimeUs: null,
            LastSpawnTimeUs: null,
            AvgSpawnGapUs: null,
            MedianKernelGapUs: null,
            P95KernelGapUs: null,
            MaxKernelGapUs: null,
            Children: new List<ChildSpawnTiming>(),
            Warnings: warnings,
            ParentProcessStartUs: processStartUs,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: 0,
            NoDataReason: noDataReason,
            PageContext: queryContext.PageContext(
                page.StartIndex,
                pageSize,
                page.TotalCount,
                page.Rows.Count),
            ReturnedCount: page.Rows.Count,
            HasMore: false,
            NextCursor: null,
            BackfilledChildrenExcluded: backfilledChildrenExcluded);
    }
}
