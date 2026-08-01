using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

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
        bool? filterSpecified = null,
        long? processStartUs = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, whenBuckets);
        var request = StackAnalysisRequest.ForProcess(
            trace, pid, processStartUs, startUs, endUs, symbolLog, when, filterSpecified);
        var scope = ResolveThreadScope(trace, request.ProcessScope!, pid, processStartUs);
        request = request with
        {
            ThreadScope = scope,
        };
        var context = BuildNormalized(trace, request);
        var contract = BuildEndpointContract(
            request.ProcessScope,
            request.HasFilter,
            context.StackCoverage,
            context.TraceSourceEndpointCount,
            context.ScopedSourceEndpointCount,
            context.ScopedIdentityUnresolvedEndpointCount);
        contract.AddWarning(context.Warnings);

        var callTree = new CallTree(ScalingPolicyKind.ScaleToData)
        {
            StackSource = context.Normalized,
        };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);
        var completeNodes = callTree.ByID
            .Where(_ => context.StackCoverage.TotalEventCount > 0)
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
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            StackCoverage: context.StackCoverage,
            SelectedProcess: contract.SelectedProcess,
            ScopeMode: contract.ScopeMode,
            PidReuseObserved: contract.PidReuseObserved,
            IncludedProcesses: contract.IncludedProcesses,
            ScopeStatus: contract.ScopeStatus,
            CapabilityStatus: contract.CapabilityStatus,
            MatchedEventCount: contract.MatchedEventCount,
            NoDataReason: contract.NoDataReason,
            TraceSourceEndpointCount: context.TraceSourceEndpointCount,
            ScopedSourceEndpointCount: context.ScopedSourceEndpointCount,
            MatchedIntervalCount: context.Projection.Samples.Count,
            TraceIdentityUnresolvedEndpointCount:
                context.TraceIdentityUnresolvedEndpointCount,
            ScopedIdentityUnresolvedEndpointCount:
                context.ScopedIdentityUnresolvedEndpointCount,
            TraceUnmatchedIntervalCount: context.TraceUnmatchedIntervalCount,
            ScopedUnmatchedIntervalCount: context.ScopedUnmatchedIntervalCount);
    }

    public static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        long? processStartUs = null,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs, endUs, trace, bucketCount: 0);
        var request = StackAnalysisRequest.ForProcess(
            trace, pid, processStartUs, startUs, endUs, symbolLog, when, filterSpecified);
        var scope = ResolveThreadScope(trace, request.ProcessScope!, pid, processStartUs);
        request = request with
        {
            ThreadScope = scope,
        };
        var context = BuildNormalized(trace, request);
        var contract = BuildEndpointContract(
            request.ProcessScope,
            request.HasFilter,
            context.StackCoverage,
            context.TraceSourceEndpointCount,
            context.ScopedSourceEndpointCount,
            context.ScopedIdentityUnresolvedEndpointCount);
        var response = StackSourceTopN.ComputeCallerCallee(
            context.Normalized,
            focusFunction,
            top,
            metricName: "contentionUs",
            context.Stats,
            context.Warnings,
            sourceTotalMetric: context.Projection.TotalAccountedDurationUs,
            unmatchedIntervalCount:
                context.ScopedUnmatchedIntervalCount +
                checked((int)context.ScopedIdentityUnresolvedEndpointCount),
            stackCoverage: context.StackCoverage,
            resultContract: contract);
        return response with
        {
            TraceUnmatchedIntervalCount = context.TraceUnmatchedIntervalCount,
            ScopedUnmatchedIntervalCount = context.ScopedUnmatchedIntervalCount,
            TraceSourceEndpointCount = context.TraceSourceEndpointCount,
            ScopedSourceEndpointCount = context.ScopedSourceEndpointCount,
            MatchedIntervalCount = context.Projection.Samples.Count,
            TraceIdentityUnresolvedEndpointCount =
                context.TraceIdentityUnresolvedEndpointCount,
            ScopedIdentityUnresolvedEndpointCount =
                context.ScopedIdentityUnresolvedEndpointCount,
        };
    }

    internal static ContentionDurationProjection ProjectIntervals(
        IReadOnlyList<PairedInterval<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>> pairs,
        ThreadAnalysisScope scope,
        int unmatchedIntervalCount,
        int invalidIntervalCount,
        ProcessAnalysisScope? processScope = null)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var samples = new List<ContentionDurationSample>();
        long totalFullDurationUs = 0;
        long totalAccountedDurationUs = 0;

        foreach (var pair in pairs)
        {
            if (!MatchesProcess(processScope, pair.Key.Process))
                continue;

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
        long TraceSourceEndpointCount,
        long ScopedSourceEndpointCount,
        long TraceIdentityUnresolvedEndpointCount,
        long ScopedIdentityUnresolvedEndpointCount,
        int TraceUnmatchedIntervalCount,
        int ScopedUnmatchedIntervalCount,
        DomainStackCoverage StackCoverage,
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
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, request.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace, "clr_contention", "us");
        var stackByStart = new Dictionary<
            ContentionStackKey,
            StackSourceCallStackIndex>();
        long traceSourceEndpointCount = 0;
        long scopedSourceEndpointCount = 0;
        long traceIdentityUnresolvedEndpointCount = 0;
        long scopedIdentityUnresolvedEndpointCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.ContentionStart += data =>
            {
                if (data.ContentionFlags != ContentionFlags.Managed)
                    return;

                traceSourceEndpointCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAt(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs);
                if (resolution.Status != InstanceResolutionStatus.Resolved ||
                    !resolution.Value.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (scope.MatchesPoint(
                            data.ProcessID, data.ThreadID, timestampUs))
                    {
                        scopedIdentityUnresolvedEndpointCount++;
                    }
                    return;
                }

                var thread = resolution.Value.Value;
                if (MatchesEvidence(
                        scope, request.ProcessScope, thread, timestampUs))
                {
                    scopedSourceEndpointCount++;
                }
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

                traceSourceEndpointCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAtEndpoint(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs);
                if (resolution.Status != InstanceResolutionStatus.Resolved ||
                    !resolution.Value.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (scope.MatchesPoint(
                            data.ProcessID, data.ThreadID, timestampUs))
                    {
                        scopedIdentityUnresolvedEndpointCount++;
                    }
                    return;
                }

                if (MatchesEvidence(
                        scope,
                        request.ProcessScope,
                        resolution.Value.Value,
                        timestampUs))
                {
                    scopedSourceEndpointCount++;
                }
                pairer.AddStop(
                    resolution.Value.Value,
                    timestampUs,
                    new ContentionStopData());
            };
        });

        var result = pairer.Complete();
        var scopedUnmatchedIntervalCount =
            result.UnmatchedStarts.Count(endpoint =>
                MatchesEvidence(scope, request.ProcessScope, endpoint.Key, endpoint.TimeUs)) +
            result.UnmatchedStops.Count(endpoint =>
                MatchesEvidence(scope, request.ProcessScope, endpoint.Key, endpoint.TimeUs));
        var traceUnmatchedIntervalCount =
            result.UnmatchedStarts.Count + result.UnmatchedStops.Count;
        var compatibilityUnmatchedIntervalCount = checked(
            scopedUnmatchedIntervalCount +
            (int)scopedIdentityUnresolvedEndpointCount);
        var invalidIntervalCount = result.InvalidIntervals.Count(interval =>
            scope.MatchesThread(interval.Key) &&
            MatchesProcess(request.ProcessScope, interval.Key.Process) &&
            (scope.Window.ContainsPoint(interval.StartUs) ||
             scope.Window.ContainsPoint(interval.EndUs)));
        var projection = ProjectIntervals(
            result.Pairs,
            scope,
            compatibilityUnmatchedIntervalCount,
            invalidIntervalCount,
            request.ProcessScope);

        long traceTotalUs = 0;
        foreach (var pair in result.Pairs)
        {
            traceTotalUs = checked(traceTotalUs + pair.FullDurationUs);
        }

        foreach (var sample in projection.Samples)
        {
            var stackKey = new ContentionStackKey(
                sample.Thread,
                sample.StartUs,
                sample.Stack);
            var stack = raw.NoStackCallStack;
            var hasStack = sample.Stack != CallStackIndex.Invalid &&
                           stackByStart.TryGetValue(stackKey, out stack);
            raw.AddSample(
                hasStack ? stack : raw.NoStackCallStack,
                hasStack,
                sample.EndUs / 1_000d,
                sample.AccountedDurationUs);
            request.When.AddDurationInterval(sample.StartUs, sample.EndUs);
        }
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, request.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();

        var warnings = new List<string>();
        if (traceSourceEndpointCount == 0 && !request.HasFilter)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "contention",
                "Contention",
                "or no managed lock contention occurred in the filter window"));
        }
        if (!request.ResolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);
        if (traceIdentityUnresolvedEndpointCount > 0)
        {
            warnings.Add(
                $"identity_unresolved: skipped {traceIdentityUnresolvedEndpointCount} contention endpoint events because their thread instance was unresolved or ambiguous.");
        }
        if (request.ProcessScope?.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                "pid_aggregate: the PID-only scope aggregates multiple process lifetimes; inspect IncludedProcesses.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        return new BuildContext(
            normalized,
            stats,
            traceTotalUs,
            projection,
            traceSourceEndpointCount,
            scopedSourceEndpointCount,
            traceIdentityUnresolvedEndpointCount,
            scopedIdentityUnresolvedEndpointCount,
            traceUnmatchedIntervalCount,
            scopedUnmatchedIntervalCount,
            coverage,
            warnings);
    }

    internal static StackResultContract BuildEndpointContract(
        ProcessAnalysisScope? processScope,
        bool filterSpecified,
        DomainStackCoverage coverage,
        long traceSourceEndpointCount,
        long scopedSourceEndpointCount,
        long scopedIdentityUnresolvedEndpointCount) =>
        StackResultContract.FromIntervalEndpoints(
            processScope: processScope,
            threadScope: null,
            filterSpecified: filterSpecified,
            coverage: coverage,
            traceSourceEndpointCount: traceSourceEndpointCount,
            scopedSourceEndpointCount: scopedSourceEndpointCount,
            scopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount);

    private static ThreadAnalysisScope ResolveThreadScope(
        TraceLog trace,
        ProcessAnalysisScope processScope,
        int? pid,
        long? processStartUs)
    {
        var resolution = ThreadAnalysisScope.Resolve(
            processScope.Window,
            pid,
            tid: null,
            processStartUs,
            threadStartUs: null,
            TraceIdentityIndex.For(trace));
        return resolution.Status == InstanceResolutionStatus.Resolved &&
               resolution.Value.HasValue
            ? resolution.Value.Value
            : new ThreadAnalysisScope(
                processScope.Window,
                pid,
                Process: null,
                Thread: null,
                AggregatesPidLifetimes: false,
                PidReuseObserved: processScope.PidReuseObserved);
    }

    private static bool MatchesEvidence(
        ThreadAnalysisScope scope,
        ProcessAnalysisScope? processScope,
        ThreadInstanceKey thread,
        long timestampUs) =>
        scope.MatchesThread(thread) &&
        MatchesProcess(processScope, thread.Process) &&
        scope.Window.ContainsPoint(timestampUs);

    private static bool MatchesProcess(
        ProcessAnalysisScope? processScope,
        ProcessInstanceKey process) =>
        processScope is null ||
        (processScope.IsResolved && processScope.IncludedProcesses.Contains(process));

    private readonly record struct ContentionStackKey(
        ThreadInstanceKey Thread,
        long StartUs,
        CallStackIndex Stack);
}
