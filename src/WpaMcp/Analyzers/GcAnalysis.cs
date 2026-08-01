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
        var unresolvedProcessEventCount = 0;
        long sourceEventCount = 0;
        long matchedSourceEventCount = 0;

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
                    unresolvedProcessEventCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;

                accumulator.AddGcStart(
                    process.Value,
                    TryReadClrInstanceId(data),
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
                    unresolvedProcessEventCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;

                accumulator.AddGcStop(
                    process.Value,
                    TryReadClrInstanceId(data),
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
                    unresolvedProcessEventCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;

                accumulator.AddSuspendStart(
                    process.Value,
                    TryReadClrInstanceId(data),
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
                    unresolvedProcessEventCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;

                accumulator.AddRestartStop(
                    process.Value,
                    TryReadClrInstanceId(data),
                    timestampUs);
            };
        });

        var response = Project(
            accumulator.Complete(), window, scope, sourceEventCount,
            matchedSourceEventCount);
        if (unresolvedProcessEventCount == 0)
            return response;

        return response with
        {
            Warnings = response.Warnings
                .Concat(
                [
                    $"identity_incomplete: skipped {unresolvedProcessEventCount} GC/pause endpoint events because their process instance was unresolved or ambiguous.",
                ])
                .ToArray(),
        };
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
                intervals, window, MatchesProcess));
    }

    internal static GcAnalysisResponse Project(
        GcIntervalSet intervals,
        TimeWindow window,
        ProcessAnalysisScope scope,
        long sourceEventCount,
        long? matchedSourceEventCount = null) =>
        ProjectCore(
            intervals,
            window,
            scope.Pid,
            process => scope.IncludedProcesses.Contains(process),
            scope.SelectedProcess,
            scope.ScopeMode,
            scope.PidReuseObserved,
            scope.IncludedProcesses,
            scope.ScopeStatus,
            sourceEventCount,
            matchedSourceEventCount ?? CountMatchedSourceEndpoints(
                intervals,
                window,
                process => scope.IncludedProcesses.Contains(process)));

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
        long matchedSourceEventCount)
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

            var fullPauseUs = pauses.Length == 0
                ? (long?)null
                : pauses.Sum(pause => pause.FullDurationUs);
            var accountedPauseUs = pauses.Length == 0
                ? (long?)null
                : pauses.Sum(pause => pause.AccountedDurationUs);
            var accountedGcUs = wall?.AccountedDurationUs ?? 0;

            if (wall.HasValue)
            {
                totalFullGcUs += wall.Value.FullDurationUs;
                totalAccountedGcUs += accountedGcUs;
            }
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
                IsOrphanPause: false));
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
                IsOrphanPause: true));
        }

        rows.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));

        var warnings = new List<string>();
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : matchedSourceEventCount > 0 || rows.Count > 0
                ? "observed"
                : sourceEventCount == 0
                    ? "not_observed"
                    : "unknown";
        if (rows.Count == 0 && sourceEventCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "GC", "GC", "or no GC occurred in the filter window"));
        }
        else if (rows.Count == 0)
        {
            warnings.Add(
                "no_matching_gc_intervals: GC endpoint events were observed, but no completed GC or pause interval matched the selected scope and window.");
        }
        if (scopeStatus != ProcessAnalysisScope.ResolvedStatus)
            warnings.Add("scope_not_found: no process lifetime matched the requested selector and window.");
        if (scopeMode == "pid_aggregate")
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope explicitly aggregates multiple process lifetimes; rows remain separated by ProcessStartUs.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        var noDataReason = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "scope_not_found"
            : sourceEventCount == 0
                ? "event_class_not_observed"
                : rows.Count == 0
                    ? matchedSourceEventCount > 0
                        ? "no_completed_intervals_in_scope"
                        : "no_events_in_scope"
                    : null;

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
            UnmatchedGcIntervalCount:
                intervals.UnmatchedGcStartCount + intervals.UnmatchedGcStopCount,
            UnmatchedPauseIntervalCount:
                intervals.UnmatchedSuspendStartCount + intervals.UnmatchedRestartStopCount,
            InvalidIntervalCount: intervals.InvalidIntervalCount,
            SelectedProcess: selectedProcess,
            ScopeMode: scopeMode,
            PidReuseObserved: pidReuseObserved,
            IncludedProcesses: includedProcesses,
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedSourceEventCount,
            NoDataReason: noDataReason,
            MatchedIntervalCount: rows.Count);
    }

    private static long CountAllSourceEndpoints(GcIntervalSet intervals) =>
        checked(
            (long)intervals.Gcs.Count * 2 +
            intervals.Gcs.Sum(gc => (long)gc.Pauses.Count * 2) +
            (long)intervals.OrphanPauses.Count * 2);

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
        return count;
    }

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
