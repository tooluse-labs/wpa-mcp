using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

internal readonly record struct FinalizerPairKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId);

internal readonly record struct FinalizerStartData;

internal readonly record struct FinalizerStopData(int FinalizersRun);

// Finalizer batches are intervals; finalized objects are point events. Pair all batch
// endpoints first, project completed batches, and independently point-filter object counts.
public static class FinalizerAnalysis
{
    public static FinalizerAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var pairer = new IntervalPairAccumulator<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>();
        var incompleteEndpoints = new List<IncompleteFinalizerEndpoint>();
        var countByType = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalObjects = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCFinalizeObject += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                if (!window.ContainsPoint(timestampUs) ||
                    (pid.HasValue && data.ProcessID != pid.Value))
                {
                    return;
                }

                totalObjects++;
                var typeName = string.IsNullOrEmpty(data.TypeName)
                    ? "(unknown)"
                    : data.TypeName;
                countByType[typeName] = countByType.GetValueOrDefault(typeName) + 1;
            };

            clr.GCFinalizersStart += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.Resolve(
                    data.ProcessID,
                    timestampUs,
                    processStartUs: null);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue ||
                    !clrInstanceId.HasValue)
                {
                    incompleteEndpoints.Add(new IncompleteFinalizerEndpoint(
                        data.ProcessID,
                        timestampUs));
                    return;
                }

                pairer.AddStart(
                    new FinalizerPairKey(process.Value.Value, clrInstanceId.Value),
                    timestampUs,
                    new FinalizerStartData());
            };

            clr.GCFinalizersStop += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.ResolveAtEndpoint(
                    data.ProcessID,
                    timestampUs);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue ||
                    !clrInstanceId.HasValue)
                {
                    incompleteEndpoints.Add(new IncompleteFinalizerEndpoint(
                        data.ProcessID,
                        timestampUs));
                    return;
                }

                pairer.AddStop(
                    new FinalizerPairKey(process.Value.Value, clrInstanceId.Value),
                    timestampUs,
                    new FinalizerStopData(data.Count));
            };
        });

        var result = pairer.Complete();
        var unmatchedIntervalCount =
            result.UnmatchedStarts.Count(endpoint =>
                MatchesEvidence(endpoint.Key.Process, endpoint.TimeUs, window, pid)) +
            result.UnmatchedStops.Count(endpoint =>
                MatchesEvidence(endpoint.Key.Process, endpoint.TimeUs, window, pid));
        var invalidIntervalCount = result.InvalidIntervals.Count(interval =>
            MatchesProcess(interval.Key.Process, pid) &&
            (window.ContainsPoint(interval.StartUs) || window.ContainsPoint(interval.EndUs)));
        var incompleteIdentityCount = incompleteEndpoints.Count(endpoint =>
            (!pid.HasValue || endpoint.Pid == pid.Value) &&
            window.ContainsPoint(endpoint.TimeUs));

        var response = ProjectBatches(
            result.Pairs,
            window,
            pid,
            unmatchedIntervalCount + incompleteIdentityCount,
            invalidIntervalCount);
        var topTypes = StackSourceTopN.TopByValue(
            countByType,
            20,
            (typeName, count) => new FinalizedTypeRow(typeName, count));

        return response with
        {
            TotalObjectsFinalized = totalObjects,
            TopTypes = topTypes,
            Warnings = BuildWarnings(
                totalObjects,
                response.Batches.Count,
                incompleteIdentityCount),
        };
    }

    internal static FinalizerAnalysisResponse ProjectBatches(
        IReadOnlyList<PairedInterval<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>> pairs,
        TimeWindow window,
        int? pid,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var rows = new List<FinalizerBatchRow>();
        long totalFullBatchUs = 0;
        long totalAccountedBatchUs = 0;

        foreach (var pair in pairs)
        {
            if (!MatchesProcess(pair.Key.Process, pid))
                continue;

            var projected = DurationAccounting.Project(pair, window);
            if (!projected.HasValue)
                continue;

            totalFullBatchUs += projected.Value.FullDurationUs;
            totalAccountedBatchUs += projected.Value.AccountedDurationUs;
            rows.Add(new FinalizerBatchRow(
                Pid: pair.Key.Process.Pid,
                StartUs: pair.StartUs,
                DurationUs: projected.Value.AccountedDurationUs,
                FinalizersRun: pair.StopData.FinalizersRun,
                EndUs: pair.EndUs,
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: pair.Key.Process.StartUs));
        }

        rows.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));

        return new FinalizerAnalysisResponse(
            Pid: pid,
            TotalObjectsFinalized: 0,
            TotalBatchUs: totalAccountedBatchUs,
            Batches: rows,
            TopTypes: [],
            Warnings: BuildWarnings(0, rows.Count, incompleteIdentityCount: 0),
            TotalFullBatchUs: totalFullBatchUs,
            TotalAccountedBatchUs: totalAccountedBatchUs,
            UnmatchedIntervalCount: unmatchedIntervalCount,
            InvalidIntervalCount: invalidIntervalCount,
            AccountingMode: DurationAccounting.ClippedOverlapMode);
    }

    private static bool MatchesEvidence(
        ProcessInstanceKey process,
        long timestampUs,
        TimeWindow window,
        int? pid) =>
        MatchesProcess(process, pid) && window.ContainsPoint(timestampUs);

    private static bool MatchesProcess(ProcessInstanceKey process, int? pid) =>
        !pid.HasValue || process.Pid == pid.Value;

    private static IReadOnlyList<string> BuildWarnings(
        long totalObjects,
        int batchCount,
        int incompleteIdentityCount)
    {
        var warnings = new List<string>();
        if (totalObjects == 0 && batchCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "finalizer",
                "GC",
                "or no finalizers ran in the filter window for the given PID"));
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);
        if (incompleteIdentityCount > 0)
        {
            warnings.Add(
                $"identity_incomplete: skipped {incompleteIdentityCount} finalizer batch endpoint events because their process or CLR instance identity was unresolved or ambiguous.");
        }
        return warnings;
    }

    private readonly record struct IncompleteFinalizerEndpoint(int Pid, long TimeUs);
}
