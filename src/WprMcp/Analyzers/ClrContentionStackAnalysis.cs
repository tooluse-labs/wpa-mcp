using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

internal readonly record struct ContentionStartData(CallStackIndex Stack);

internal readonly record struct ContentionStopData;

internal sealed record ContentionDurationSample(
    ThreadInstanceKey Thread,
    CallStackIndex Stack,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode);

internal sealed record ContentionDurationProjection(
    IReadOnlyList<ContentionDurationSample> Samples,
    long TotalFullDurationUs,
    long TotalAccountedDurationUs,
    int UnmatchedIntervalCount,
    int InvalidIntervalCount);

// Pair managed contention by thread instance over the complete trace. The stop payload's
// DurationNs is intentionally ignored so full and accounted values share normalized endpoints.
public static class ClrContentionStackAnalysis
{
    public static ClrContentionStacksResponse TopStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        var when = StackSourceTopN.WhenHistogram.ForWindow(scope.Window, whenBuckets);
        var request = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            FilterSpecified = filterSpecified,
            ThreadScope = scope,
        };
        var context = BuildNormalized(trace, request);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData)
        {
            StackSource = context.Normalized,
        };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);
        var completeNodes = callTree.ByID
            .OrderByDescending(node => node.ExclusiveMetric)
            .ToArray();
        var rows = completeNodes
            .Take(top)
            .Select(node => new ClrContentionStackRow(
                Function: node.Name,
                ExclusiveBlockedUs: (long)node.ExclusiveMetric,
                InclusiveBlockedUs: (long)node.InclusiveMetric,
                ExclusiveCount: (long)node.ExclusiveCount,
                InclusiveCount: (long)node.InclusiveCount,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, node.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, node.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter,
                    context.TraceTotalUs,
                    node.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter,
                    context.TraceTotalUs,
                    node.InclusiveMetric),
                ExclusiveAccountedBlockedUs: (long)node.ExclusiveMetric,
                InclusiveAccountedBlockedUs: (long)node.InclusiveMetric,
                AccountingMode: DurationAccounting.ClippedOverlapMode))
            .ToArray();

        return new ClrContentionStacksResponse(
            Rows: rows,
            TotalBlockedUs: context.Projection.TotalAccountedDurationUs,
            TotalEventCount: context.Projection.Samples.Count,
            Stats: context.Stats,
            Warnings: context.Warnings,
            When: when.Build(),
            TotalFullBlockedUs: context.Projection.TotalFullDurationUs,
            TotalAccountedBlockedUs: context.Projection.TotalAccountedDurationUs,
            UnmatchedIntervalCount: context.Projection.UnmatchedIntervalCount,
            InvalidIntervalCount: context.Projection.InvalidIntervalCount,
            HasMore: completeNodes.Length > rows.Length,
            AccountingMode: DurationAccounting.ClippedOverlapMode);
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog)
    {
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        var when = StackSourceTopN.WhenHistogram.ForWindow(scope.Window, bucketCount: 0);
        var request = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            ThreadScope = scope,
        };
        var context = BuildNormalized(trace, request);
        return StackSourceTopN.ComputeCallerCallee(
            context.Normalized,
            focusFunction,
            top,
            metricName: "contentionUs",
            context.Stats,
            context.Warnings);
    }

    internal static ContentionDurationProjection ProjectIntervals(
        IReadOnlyList<PairedInterval<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>> pairs,
        ThreadAnalysisScope scope,
        int unmatchedIntervalCount,
        int invalidIntervalCount)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var samples = new List<ContentionDurationSample>();
        long totalFullDurationUs = 0;
        long totalAccountedDurationUs = 0;

        foreach (var pair in pairs)
        {
            var accountedDurationUs = scope.AccountInterval(
                pair.Key,
                pair.StartUs,
                pair.EndUs);
            if (accountedDurationUs <= 0)
                continue;

            totalFullDurationUs = checked(totalFullDurationUs + pair.FullDurationUs);
            totalAccountedDurationUs = checked(
                totalAccountedDurationUs + accountedDurationUs);
            samples.Add(new ContentionDurationSample(
                pair.Key,
                pair.StartData.Stack,
                pair.StartUs,
                pair.EndUs,
                pair.FullDurationUs,
                accountedDurationUs,
                DurationAccounting.ClippedOverlapMode));
        }

        return new ContentionDurationProjection(
            samples,
            totalFullDurationUs,
            totalAccountedDurationUs,
            unmatchedIntervalCount,
            invalidIntervalCount);
    }

    private sealed record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalUs,
        ContentionDurationProjection Projection,
        List<string> Warnings);

    private static BuildContext BuildNormalized(
        TraceLog trace,
        StackAnalysisRequest request)
    {
        var scope = request.ThreadScope ??
            throw new InvalidOperationException(
                "Contention stacks require a resolved thread analysis scope.");
        var identities = TraceIdentityIndex.For(trace);
        var pairer = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();
        using var symbolReader = StackSourceTopN.OpenSymbolReader(request.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        var stackByStart = new Dictionary<
            ContentionStackKey,
            StackSourceCallStackIndex>();
        var unresolvedIdentityCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.ContentionStart += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed)
                    return;

                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAt(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs);
                if (resolution.Status != InstanceResolutionStatus.Resolved ||
                    !resolution.Value.HasValue)
                {
                    if (scope.MatchesPoint(data.ProcessID, data.ThreadID, timestampUs))
                        unresolvedIdentityCount++;
                    return;
                }

                var thread = resolution.Value.Value;
                var stack = data.CallStackIndex();
                pairer.AddStart(
                    thread,
                    timestampUs,
                    new ContentionStartData(stack));
                stackByStart[new ContentionStackKey(thread, timestampUs, stack)] =
                    stack == CallStackIndex.Invalid
                        ? raw.NoStackCallStack
                        : raw.Source.GetCallStack(stack, data);
            };

            clr.ContentionStop += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed)
                    return;

                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAtEndpoint(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs);
                if (resolution.Status != InstanceResolutionStatus.Resolved ||
                    !resolution.Value.HasValue)
                {
                    if (scope.MatchesPoint(data.ProcessID, data.ThreadID, timestampUs))
                        unresolvedIdentityCount++;
                    return;
                }

                pairer.AddStop(
                    resolution.Value.Value,
                    timestampUs,
                    new ContentionStopData());
            };
        });

        var result = pairer.Complete();
        var unmatchedIntervalCount =
            result.UnmatchedStarts.Count(endpoint =>
                MatchesEvidence(scope, endpoint.Key, endpoint.TimeUs)) +
            result.UnmatchedStops.Count(endpoint =>
                MatchesEvidence(scope, endpoint.Key, endpoint.TimeUs)) +
            unresolvedIdentityCount;
        var invalidIntervalCount = result.InvalidIntervals.Count(interval =>
            scope.MatchesThread(interval.Key) &&
            (scope.Window.ContainsPoint(interval.StartUs) ||
             scope.Window.ContainsPoint(interval.EndUs)));
        var projection = ProjectIntervals(
            result.Pairs,
            scope,
            unmatchedIntervalCount,
            invalidIntervalCount);

        long traceTotalUs = 0;
        foreach (var pair in result.Pairs)
            traceTotalUs = checked(traceTotalUs + pair.FullDurationUs);

        foreach (var sample in projection.Samples)
        {
            var stackKey = new ContentionStackKey(
                sample.Thread,
                sample.StartUs,
                sample.Stack);
            raw.Sample.StackIndex = stackByStart.TryGetValue(stackKey, out var stack)
                ? stack
                : raw.NoStackCallStack;
            raw.Sample.TimeRelativeMSec = sample.EndUs / 1_000d;
            raw.Sample.Metric = sample.AccountedDurationUs;
            raw.Source.AddSample(raw.Sample);
            request.When.AddDurationInterval(sample.StartUs, sample.EndUs);
        }
        raw.Source.DoneAddingSamples();

        if (request.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);

        var warnings = new List<string>();
        if (projection.Samples.Count == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "contention",
                "Contention",
                "or no managed lock contention occurred in the filter window"));
        }
        if (!request.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));
        if (unresolvedIdentityCount > 0)
        {
            warnings.Add(
                $"identity_incomplete: skipped {unresolvedIdentityCount} contention endpoint events because their thread instance was unresolved or ambiguous.");
        }
        if (scope.PidReuseObserved)
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        return new BuildContext(
            normalized,
            stats,
            traceTotalUs,
            projection,
            warnings);
    }

    private static ThreadAnalysisScope ResolveLegacyScope(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(
                TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
                maxDurationUs: null);
        var resolution = ThreadAnalysisScope.Resolve(
            window,
            pid,
            tid: null,
            processStartUs: null,
            threadStartUs: null,
            TraceIdentityIndex.For(trace));
        return resolution.Status == InstanceResolutionStatus.Resolved &&
               resolution.Value.HasValue
            ? resolution.Value.Value
            : throw new InvalidOperationException(
                $"Unable to resolve contention stack scope: {resolution.Status}.");
    }

    private static bool MatchesEvidence(
        ThreadAnalysisScope scope,
        ThreadInstanceKey thread,
        long timestampUs) =>
        scope.MatchesThread(thread) && scope.Window.ContainsPoint(timestampUs);

    private readonly record struct ContentionStackKey(
        ThreadInstanceKey Thread,
        long StartUs,
        CallStackIndex Stack);
}
