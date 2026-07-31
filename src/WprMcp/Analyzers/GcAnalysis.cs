using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// CLR GC walls and stop-the-world pauses are independent interval streams. Pair both over
// the full trace, associate each completed pause once, then project through the query window.
public static class GcAnalysis
{
    public static GcAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var accumulator = new GcIntervalAccumulator();
        var unresolvedProcessEventCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCStart += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: false);
                if (!process.HasValue)
                {
                    unresolvedProcessEventCount++;
                    return;
                }

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
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: true);
                if (!process.HasValue)
                {
                    unresolvedProcessEventCount++;
                    return;
                }

                accumulator.AddGcStop(
                    process.Value,
                    TryReadClrInstanceId(data),
                    data.Count,
                    timestampUs);
            };

            clr.GCSuspendEEStart += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: false);
                if (!process.HasValue)
                {
                    unresolvedProcessEventCount++;
                    return;
                }

                accumulator.AddSuspendStart(
                    process.Value,
                    TryReadClrInstanceId(data),
                    timestampUs);
            };

            clr.GCRestartEEStop += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = ResolveProcess(
                    identities.Processes, data.ProcessID, timestampUs, atEndpoint: true);
                if (!process.HasValue)
                {
                    unresolvedProcessEventCount++;
                    return;
                }

                accumulator.AddRestartStop(
                    process.Value,
                    TryReadClrInstanceId(data),
                    timestampUs);
            };
        });

        var response = Project(accumulator.Complete(), window, pid);
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
            if (pid.HasValue && gc.Key.Process.Pid != pid.Value)
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
            if (pid.HasValue && pause.Key.Process.Pid != pid.Value)
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
        if (rows.Count == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "GC", "GC", "or no GC occurred in the filter window"));
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

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
                row => row.Code == "missing_clr_instance"),
            UnmatchedGcIntervalCount:
                intervals.UnmatchedGcStartCount + intervals.UnmatchedGcStopCount,
            UnmatchedPauseIntervalCount:
                intervals.UnmatchedSuspendStartCount + intervals.UnmatchedRestartStopCount,
            InvalidIntervalCount: intervals.InvalidIntervalCount);
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
