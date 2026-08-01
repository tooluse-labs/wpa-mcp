using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// CLR GC walls and stop-the-world pauses are independent interval streams. Pair both over
// the full trace, associate each completed pause once, then project through the query window.
public static class GcAnalysis
{
    public static GcAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var accumulator = new GcIntervalAccumulator();
        long sourceEventCount = 0;
        long matchedSourceEventCount = 0;
        long scopedUsableSourceEventCount = 0;
        long traceIdentityUnresolvedEndpointCount = 0;
        long scopedIdentityUnresolvedEndpointCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCStart += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: false);
                if (!process.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(scope, data.ProcessID, timestampUs))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                var clrInstanceId = TryReadClrInstanceId(data);
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                }
                else if (matchesScope)
                {
                    scopedUsableSourceEventCount++;
                }

                accumulator.AddGcStart(
                    process.Value,
                    clrInstanceId,
                    data.Count,
                    timestampUs,
                    data.Depth,
                    data.Reason.ToString());
            };

            clr.GCStop += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: true);
                if (!process.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(scope, data.ProcessID, timestampUs))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                var clrInstanceId = TryReadClrInstanceId(data);
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                }
                else if (matchesScope)
                {
                    scopedUsableSourceEventCount++;
                }

                accumulator.AddGcStop(
                    process.Value,
                    clrInstanceId,
                    data.Count,
                    timestampUs);
            };

            clr.GCSuspendEEStart += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: false);
                if (!process.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(scope, data.ProcessID, timestampUs))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                var clrInstanceId = TryReadClrInstanceId(data);
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                }
                else if (matchesScope)
                {
                    scopedUsableSourceEventCount++;
                }

                accumulator.AddSuspendStart(
                    process.Value,
                    clrInstanceId,
                    timestampUs);
            };

            clr.GCRestartEEStop += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: true);
                if (!process.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(scope, data.ProcessID, timestampUs))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                var clrInstanceId = TryReadClrInstanceId(data);
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                }
                else if (matchesScope)
                {
                    scopedUsableSourceEventCount++;
                }

                accumulator.AddRestartStop(
                    process.Value,
                    clrInstanceId,
                    timestampUs);
            };
        });

        return Project(
            accumulator.Complete(), window, scope, sourceEventCount,
            matchedSourceEventCount,
            scopedUsableSourceEventCount,
            traceIdentityUnresolvedEndpointCount,
            scopedIdentityUnresolvedEndpointCount);
    }

    internal static GcAnalysisResponse Project(
        GcIntervalSet intervals,
        TimeWindow window,
        int? pid)
    {
        var inferredSourceEventCount = CountAllSourceEndpoints(intervals);
        bool MatchesProcess(ProcessInstanceKey process) =>
            !pid.HasValue || process.Pid == pid.Value;
        return ProjectCore(
            intervals,
            window,
            pid,
            MatchesProcess,
            selectedProcess: null,
            scopeMode: pid.HasValue ? "pid_aggregate" : "all_processes",
            pidReuseObserved: false,
            includedProcesses: Array.Empty<ProcessInstanceKey>(),
            scopeStatus: ProcessAnalysisScope.ResolvedStatus,
            sourceEventCount: inferredSourceEventCount,
            matchedSourceEventCount: CountMatchedSourceEndpoints(
                intervals, window, MatchesProcess),
            scopedUsableSourceEventCount: CountMatchedUsableSourceEndpoints(
                intervals, window, MatchesProcess),
            traceIdentityUnresolvedEndpointCount:
                intervals.IncompleteEvidence.Count,
            scopedIdentityUnresolvedEndpointCount: CountScopedIncompleteIdentity(
                intervals, window, MatchesProcess));
    }

    internal static GcAnalysisResponse Project(
        GcIntervalSet intervals,
        TimeWindow window,
        ProcessAnalysisScope scope,
        long sourceEventCount,
        long? matchedSourceEventCount = null,
        long? scopedUsableSourceEventCount = null,
        long? traceIdentityUnresolvedEndpointCount = null,
        long? scopedIdentityUnresolvedEndpointCount = null)
    {
        bool MatchesProcess(ProcessInstanceKey process) =>
            scope.IsResolved && scope.IncludedProcesses.Contains(process);
        var matched = matchedSourceEventCount ?? CountMatchedSourceEndpoints(
            intervals, window, MatchesProcess);
        var scopedIdentity = scopedIdentityUnresolvedEndpointCount ??
            CountScopedIncompleteIdentity(intervals, window, MatchesProcess);
        return ProjectCore(
            intervals,
            window,
            scope.Pid,
            MatchesProcess,
            scope.SelectedProcess,
            scope.ScopeMode,
            scope.PidReuseObserved,
            scope.IncludedProcesses,
            scope.ScopeStatus,
            sourceEventCount,
            matched,
            scopedUsableSourceEventCount ?? Math.Max(0, matched - scopedIdentity),
            traceIdentityUnresolvedEndpointCount ??
                intervals.IncompleteEvidence.Count,
            scopedIdentity);
    }

    private static GcAnalysisResponse ProjectCore(
        GcIntervalSet intervals,
        TimeWindow window,
        int? pid,
        Func<ProcessInstanceKey, bool> matchesProcess,
        ProcessInstanceKey? selectedProcess,
        string scopeMode,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey> includedProcesses,
        string scopeStatus,
        long sourceEventCount,
        long matchedSourceEventCount,
        long scopedUsableSourceEventCount,
        long traceIdentityUnresolvedEndpointCount,
        long scopedIdentityUnresolvedEndpointCount)
    {
        ArgumentNullException.ThrowIfNull(intervals);

        var rows = new List<GcEventRow>();
        long totalFullGcUs = 0;
        long totalAccountedGcUs = 0;
        long totalFullPauseUs = 0;
        long totalAccountedPauseUs = 0;
        var gen0 = 0;
        var gen1 = 0;
        var gen2 = 0;

        foreach (var gc in intervals.Gcs)
        {
            if (!matchesProcess(gc.Key.Process))
                continue;

            var wall = DurationAccounting.Project(
                new PairedInterval<ClrGcKey, GcStartData, GcStopData>(
                    gc.Key,
                    gc.StartUs,
                    gc.EndUs,
                    new GcStartData(gc.Generation, gc.Reason),
                    new GcStopData()),
                window);

            var pauses = gc.Pauses
                .Select(pause => DurationAccounting.Project(
                    new PairedInterval<ClrPauseKey, SuspendStartData, RestartStopData>(
                        pause.Key,
                        pause.StartUs,
                        pause.EndUs,
                        new SuspendStartData(),
                        new RestartStopData()),
                    window))
                .Where(projected => projected.HasValue)
                .Select(projected => projected!.Value)
                .ToArray();

            if (!wall.HasValue && pauses.Length == 0)
                continue;

            if (!wall.HasValue)
            {
                foreach (var pause in pauses)
                {
                    totalFullPauseUs += pause.FullDurationUs;
                    totalAccountedPauseUs += pause.AccountedDurationUs;
                    rows.Add(new GcEventRow(
                        StartUs: pause.StartUs,
                        DurationUs: pause.AccountedDurationUs,
                        Generation: -1,
                        Reason: "(pause associated with GC wall outside query window)",
                        Pid: gc.Key.Process.Pid,
                        PauseUs: pause.AccountedDurationUs,
                        EndUs: pause.EndUs,
                        FullDurationUs: pause.FullDurationUs,
                        AccountedDurationUs: pause.AccountedDurationUs,
                        FullPauseUs: pause.FullDurationUs,
                        AccountedPauseUs: pause.AccountedDurationUs,
                        AccountingMode: DurationAccounting.ClippedOverlapMode,
                        ProcessStartUs: gc.Key.Process.StartUs,
                        ClrInstanceId: gc.Key.ClrInstanceId,
                        GcCount: gc.Key.GcCount,
                        IsOrphanPause: false,
                        IntervalKind: "pause_only_associated_gc_wall_outside_window"));
                }
                continue;
            }

            var fullPauseUs = pauses.Length == 0
                ? (long?)null
                : pauses.Sum(pause => pause.FullDurationUs);
            var accountedPauseUs = pauses.Length == 0
                ? (long?)null
                : pauses.Sum(pause => pause.AccountedDurationUs);
            var accountedGcUs = wall.Value.AccountedDurationUs;

            totalFullGcUs = checked(totalFullGcUs + gc.FullDurationUs);
            totalAccountedGcUs += accountedGcUs;
            totalFullPauseUs += fullPauseUs ?? 0;
            totalAccountedPauseUs += accountedPauseUs ?? 0;

            if (gc.Generation == 0)
                gen0++;
            else if (gc.Generation == 1)
                gen1++;
            else
                gen2++;

            rows.Add(new GcEventRow(
                StartUs: gc.StartUs,
                DurationUs: accountedGcUs,
                Generation: gc.Generation,
                Reason: gc.Reason,
                Pid: gc.Key.Process.Pid,
                PauseUs: accountedPauseUs,
                EndUs: gc.EndUs,
                FullDurationUs: gc.FullDurationUs,
                AccountedDurationUs: accountedGcUs,
                FullPauseUs: fullPauseUs,
                AccountedPauseUs: accountedPauseUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: gc.Key.Process.StartUs,
                ClrInstanceId: gc.Key.ClrInstanceId,
                GcCount: gc.Key.GcCount,
                IsOrphanPause: false,
                IntervalKind: "gc_wall"));
        }

        foreach (var pause in intervals.OrphanPauses)
        {
            if (!matchesProcess(pause.Key.Process))
                continue;

            var projected = DurationAccounting.Project(
                new PairedInterval<ClrPauseKey, SuspendStartData, RestartStopData>(
                    pause.Key,
                    pause.StartUs,
                    pause.EndUs,
                    new SuspendStartData(),
                    new RestartStopData()),
                window);
            if (!projected.HasValue)
                continue;

            totalFullPauseUs += projected.Value.FullDurationUs;
            totalAccountedPauseUs += projected.Value.AccountedDurationUs;
            rows.Add(new GcEventRow(
                StartUs: pause.StartUs,
                DurationUs: projected.Value.AccountedDurationUs,
                Generation: -1,
                Reason: "(pause without compatible GC interval)",
                Pid: pause.Key.Process.Pid,
                PauseUs: projected.Value.AccountedDurationUs,
                EndUs: pause.EndUs,
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                FullPauseUs: projected.Value.FullDurationUs,
                AccountedPauseUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: pause.Key.Process.StartUs,
                ClrInstanceId: pause.Key.ClrInstanceId,
                GcCount: null,
                IsOrphanPause: true,
                IntervalKind: "orphan_pause"));
        }

        rows.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));

        var warnings = new List<string>();
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : scopedUsableSourceEventCount > 0 || rows.Count > 0
                ? "observed"
                : sourceEventCount == 0
                    ? "not_observed"
                    : "unknown";
        if (scopeStatus != ProcessAnalysisScope.ResolvedStatus)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(scopeStatus));
        }
        else if (rows.Count == 0 && sourceEventCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "GC", "GC", "or no GC occurred in the filter window"));
        }
        else if (rows.Count == 0 &&
                 scopedIdentityUnresolvedEndpointCount > 0 &&
                 scopedUsableSourceEventCount == 0)
        {
            warnings.Add(
                "source_events_unattributed: GC/pause endpoints with matching raw PID/time were observed, but process or CLR instance identity was unresolved; no interval attribution was guessed.");
        }
        else if (rows.Count == 0)
        {
            warnings.Add(
                "no_matching_gc_intervals: GC endpoint events were observed, but no completed GC or pause interval matched the selected scope and window.");
        }
        if (scopeMode == "pid_aggregate")
        {
            warnings.Add(
                "pid_aggregate: pid-only scope explicitly aggregates multiple process lifetimes; rows remain separated by ProcessStartUs.");
        }
        if (traceIdentityUnresolvedEndpointCount > 0)
        {
            warnings.Add(
                $"identity_unresolved: {traceIdentityUnresolvedEndpointCount} GC/pause endpoint event(s) were dropped because process or CLR instance identity was unresolved or ambiguous.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        var noDataReason = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? scopeStatus
            : sourceEventCount == 0
                ? "event_class_not_observed"
                : rows.Count == 0
                    ? scopedUsableSourceEventCount > 0
                        ? "no_completed_intervals_in_scope"
                        : scopedIdentityUnresolvedEndpointCount > 0
                            ? "source_events_unattributed"
                            : "no_events_in_scope"
                    : null;

        var traceUnmatchedGcIntervalCount =
            intervals.UnmatchedGcStartCount + intervals.UnmatchedGcStopCount;
        var traceUnmatchedPauseIntervalCount =
            intervals.UnmatchedSuspendStartCount + intervals.UnmatchedRestartStopCount;
        var scopedUnmatchedGcIntervalCount = intervals.UnmatchedGcIntervals is null
            ? traceUnmatchedGcIntervalCount
            : CountScopedAnomalies(
                intervals.UnmatchedGcIntervals, window, matchesProcess);
        var scopedUnmatchedPauseIntervalCount = intervals.UnmatchedPauseIntervals is null
            ? traceUnmatchedPauseIntervalCount
            : CountScopedAnomalies(
                intervals.UnmatchedPauseIntervals, window, matchesProcess);
        var scopedInvalidIntervalCount = intervals.InvalidIntervals is null
            ? intervals.InvalidIntervalCount
            : CountScopedAnomalies(
                intervals.InvalidIntervals, window, matchesProcess);

        return new GcAnalysisResponse(
            Pid: pid,
            TotalGcCount: gen0 + gen1 + gen2,
            Gen0Count: gen0,
            Gen1Count: gen1,
            Gen2Count: gen2,
            TotalGcUs: totalAccountedGcUs,
            TotalPauseUs: totalAccountedPauseUs,
            Events: rows,
            Warnings: warnings,
            TotalFullGcUs: totalFullGcUs,
            TotalAccountedGcUs: totalAccountedGcUs,
            TotalFullPauseUs: totalFullPauseUs,
            TotalAccountedPauseUs: totalAccountedPauseUs,
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            IncompleteClrIdentityCount: intervals.IncompleteEvidence.Count(
                row => row.Code == "missing_clr_instance" &&
                       matchesProcess(row.Process) &&
                       window.ContainsPoint(row.TimestampUs)),
            UnmatchedGcIntervalCount: traceUnmatchedGcIntervalCount,
            UnmatchedPauseIntervalCount: traceUnmatchedPauseIntervalCount,
            InvalidIntervalCount: intervals.InvalidIntervalCount,
            SelectedProcess: selectedProcess,
            ScopeMode: scopeMode,
            PidReuseObserved: pidReuseObserved,
            IncludedProcesses: includedProcesses,
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedSourceEventCount,
            NoDataReason: noDataReason,
            MatchedIntervalCount: rows.Count,
            TraceUnmatchedGcIntervalCount: traceUnmatchedGcIntervalCount,
            ScopedUnmatchedGcIntervalCount: scopedUnmatchedGcIntervalCount,
            TraceUnmatchedPauseIntervalCount: traceUnmatchedPauseIntervalCount,
            ScopedUnmatchedPauseIntervalCount: scopedUnmatchedPauseIntervalCount,
            TraceInvalidIntervalCount: intervals.InvalidIntervalCount,
            ScopedInvalidIntervalCount: scopedInvalidIntervalCount,
            TraceIdentityUnresolvedEndpointCount:
                traceIdentityUnresolvedEndpointCount,
            ScopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount,
            TraceUnmatchedGcStartCount: intervals.UnmatchedGcStartCount,
            TraceUnmatchedGcStopCount: intervals.UnmatchedGcStopCount,
            TraceUnmatchedPauseStartCount:
                intervals.UnmatchedSuspendStartCount,
            TraceUnmatchedPauseStopCount:
                intervals.UnmatchedRestartStopCount);
    }

    private static int CountScopedAnomalies(
        IReadOnlyList<GcIntervalAnomaly> anomalies,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess) =>
        anomalies.Count(item =>
            matchesProcess(item.Process) &&
            (window.ContainsPoint(item.StartUs) ||
             window.ContainsPoint(item.EndUs)));

    private static long CountAllSourceEndpoints(GcIntervalSet intervals) =>
        checked(
            (long)intervals.Gcs.Count * 2 +
            intervals.Gcs.Sum(gc => (long)gc.Pauses.Count * 2) +
            (long)intervals.OrphanPauses.Count * 2 +
            intervals.UnmatchedGcStartCount +
            intervals.UnmatchedGcStopCount +
            intervals.UnmatchedSuspendStartCount +
            intervals.UnmatchedRestartStopCount +
            intervals.IncompleteEvidence.Count +
            (long)intervals.InvalidIntervalCount * 2);

    private static long CountMatchedSourceEndpoints(
        GcIntervalSet intervals,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess)
    {
        long count = 0;
        foreach (var gc in intervals.Gcs)
        {
            if (!matchesProcess(gc.Key.Process))
                continue;
            if (window.ContainsPoint(gc.StartUs)) count++;
            if (window.ContainsPoint(gc.EndUs)) count++;
            foreach (var pause in gc.Pauses)
            {
                if (window.ContainsPoint(pause.StartUs)) count++;
                if (window.ContainsPoint(pause.EndUs)) count++;
            }
        }
        foreach (var pause in intervals.OrphanPauses)
        {
            if (!matchesProcess(pause.Key.Process))
                continue;
            if (window.ContainsPoint(pause.StartUs)) count++;
            if (window.ContainsPoint(pause.EndUs)) count++;
        }
        if (intervals.UnmatchedGcIntervals is not null)
        {
            count += CountAnomalyEndpoints(
                intervals.UnmatchedGcIntervals,
                window,
                matchesProcess,
                singlePointEvidence: true);
        }
        if (intervals.UnmatchedPauseIntervals is not null)
        {
            count += CountAnomalyEndpoints(
                intervals.UnmatchedPauseIntervals,
                window,
                matchesProcess,
                singlePointEvidence: true);
        }
        if (intervals.InvalidIntervals is not null)
        {
            count += CountAnomalyEndpoints(
                intervals.InvalidIntervals,
                window,
                matchesProcess,
                singlePointEvidence: false);
        }
        count += CountScopedIncompleteIdentity(intervals, window, matchesProcess);
        return count;
    }

    private static long CountMatchedUsableSourceEndpoints(
        GcIntervalSet intervals,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess) =>
        CountMatchedSourceEndpoints(intervals, window, matchesProcess) -
        CountScopedIncompleteIdentity(intervals, window, matchesProcess);

    private static int CountScopedIncompleteIdentity(
        GcIntervalSet intervals,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess) =>
        intervals.IncompleteEvidence.Count(item =>
            matchesProcess(item.Process) &&
            window.ContainsPoint(item.TimestampUs));

    private static long CountAnomalyEndpoints(
        IReadOnlyList<GcIntervalAnomaly> anomalies,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess,
        bool singlePointEvidence)
    {
        long count = 0;
        foreach (var anomaly in anomalies)
        {
            if (!matchesProcess(anomaly.Process))
                continue;
            if (window.ContainsPoint(anomaly.StartUs))
                count++;
            if ((!singlePointEvidence || anomaly.EndUs != anomaly.StartUs) &&
                window.ContainsPoint(anomaly.EndUs))
            {
                count++;
            }
        }
        return count;
    }

    private static bool MatchesRawScope(
        ProcessAnalysisScope scope,
        int pid,
        long timestampUs) =>
        scope.IsResolved &&
        scope.Window.ContainsPoint(timestampUs) &&
        (!scope.Pid.HasValue || scope.Pid.Value == pid);

    internal static ushort? TryReadClrInstanceId(TraceEvent data)
    {
        for (var index = 0; index < data.PayloadNames.Length; index++)
        {
            if (!string.Equals(
                    data.PayloadNames[index],
                    "ClrInstanceID",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = data.PayloadValue(index);
            return value is null
                ? null
                : Convert.ToUInt16(
                    value,
                    System.Globalization.CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static ProcessInstanceKey? ResolveProcess(
        ProcessInstanceResolver resolver,
        int pid,
        long timestampUs,
        bool atEndpoint)
    {
        var resolution = atEndpoint
            ? resolver.ResolveAtEndpoint(pid, timestampUs)
            : resolver.Resolve(pid, timestampUs, processStartUs: null);
        return resolution.Status == InstanceResolutionStatus.Resolved
            ? resolution.Value
            : null;
    }
}
