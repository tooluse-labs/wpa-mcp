using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Stacks;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Top stacks ranked by blocked microseconds. A blocked stack is the old thread's
// CSwitch blocking stack captured at switch-out. The ordinary stack on the later
// switch-in belongs to the resume event and is never substituted for it.
public static class BlockedTimeStackAnalysis
{
    public static WaitTopStacksResponse TopBlockedStacks(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        TextWriter symbolLog,
        int whenBuckets = 0)
    {
        var scope = ResolveLegacyScope(trace, pid, startUs, endUs);
        var when = StackSourceTopN.WhenHistogram.ForWindow(scope.Window, whenBuckets);
        var request = new StackAnalysisRequest(pid, startUs, endUs, symbolLog, when)
        {
            ThreadScope = scope,
        };
        return TopBlockedStacks(trace, top, request);
    }

    internal static WaitTopStacksResponse TopBlockedStacks(
        TraceLog trace,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        int whenBuckets = 0,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(scope.Window, whenBuckets);
        var request = new StackAnalysisRequest(
            scope.Pid, scope.Window.StartUs, scope.Window.EndUs, symbolLog, when)
        {
            ThreadScope = scope,
            FilterSpecified = filterSpecified,
        };
        return TopBlockedStacks(trace, top, request);
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
        return CallerCallee(trace, focusFunction, top, request);
    }

    internal static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        ThreadAnalysisScope scope,
        TextWriter symbolLog,
        bool? filterSpecified = null)
    {
        var when = StackSourceTopN.WhenHistogram.ForWindow(scope.Window, bucketCount: 0);
        var request = new StackAnalysisRequest(
            scope.Pid, scope.Window.StartUs, scope.Window.EndUs, symbolLog, when)
        {
            ThreadScope = scope,
            FilterSpecified = filterSpecified,
        };
        return CallerCallee(trace, focusFunction, top, request);
    }

    internal static SyntheticBlockedProjection ProjectSynthetic(
        long switchOutUs,
        long resumeUs,
        TimeWindow window,
        CallStackIndex blockingStack,
        CallStackIndex ordinaryResumeStack)
    {
        var thread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 1, StartUs: 0), Tid: 1, Generation: 1);
        var scheduler = new SchedulerIntervalAccumulator();
        scheduler.ProcessSwitch(
            oldThread: thread,
            newThread: null,
            timestampUs: switchOutUs,
            waitReason: "Synthetic",
            core: 0,
            oldThreadBlockingStack: blockingStack);
        var closed = scheduler.ProcessSwitch(
            oldThread: null,
            newThread: thread,
            timestampUs: resumeUs,
            waitReason: "Synthetic",
            core: 0);

        if (!closed.Blocked.HasValue)
            return new SyntheticBlockedProjection(0, Array.Empty<SyntheticBlockedSample>());

        var interval = closed.Blocked.Value;
        var scope = new ThreadAnalysisScope(
            window,
            Pid: null,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var accountedUs = scope.AccountInterval(
            interval.Thread, interval.StartUs, interval.EndUs);
        if (accountedUs <= 0)
            return new SyntheticBlockedProjection(0, Array.Empty<SyntheticBlockedSample>());

        return new SyntheticBlockedProjection(
            accountedUs,
            [new SyntheticBlockedSample(
                interval.BlockingStack,
                ordinaryResumeStack,
                accountedUs)]);
    }

    internal sealed record SyntheticBlockedProjection(
        long TotalBlockedUs,
        IReadOnlyList<SyntheticBlockedSample> Samples);

    internal readonly record struct SyntheticBlockedSample(
        CallStackIndex SourceStack,
        CallStackIndex OrdinaryResumeStack,
        long MetricUs);

    private static WaitTopStacksResponse TopBlockedStacks(
        TraceLog trace,
        int top,
        StackAnalysisRequest request)
    {
        var context = BuildNormalized(trace, request);
        var exact = StackSourceTopN.ComputeExactFrameMetrics(context.Normalized);
        var totalMetric = Math.Max(1L, exact.TotalMetric);

        var rows = StackSourceTopN.RankExactFrames(exact)
            .Where(_ => context.StackCoverage.TotalEventCount > 0)
            .Take(top)
            .Select(node => new WaitStackRow(
                Function: node.Function,
                ExclusiveBlockedUs: node.ExclusiveMetric,
                InclusiveBlockedUs: node.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, node.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, node.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter, context.TraceTotalBlockedUs, node.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter, context.TraceTotalBlockedUs, node.InclusiveMetric)))
            .ToList();

        var contract = BuildEndpointContract(
            request.ThreadScope,
            request.HasFilter,
            context.StackCoverage,
            context.TraceSourceEndpointCount,
            context.ScopedSourceEndpointCount,
            context.ScopedIdentityUnresolvedCSwitchSideCount);
        contract.AddWarning(context.Warnings);

        return new WaitTopStacksResponse(
            Rows: rows,
            TotalBlockedUs: context.TotalBlockedUs,
            SampleCount: context.SampleCount,
            Stats: context.Stats,
            Warnings: context.Warnings,
            When: request.When.Build("microseconds"),
            UnmatchedBlockedIntervalCount: context.TraceUnmatchedBlockedIntervalCount,
            SelectedProcess: request.ThreadScope?.Process?.Key,
            SelectedThread: request.ThreadScope?.Thread?.Key,
            HasContextSwitches: context.HasContextSwitches,
            HasContextSwitchBlockingStacks: context.HasContextSwitchBlockingStacks,
            SymbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                request.ResolveSymbols,
                context.Stats,
                context.StackCoverage.StackedEventCount > 0),
            StackCoverage: context.StackCoverage,
            ScopeMode: contract.ScopeMode,
            PidReuseObserved: contract.PidReuseObserved,
            IncludedProcesses: contract.IncludedProcesses,
            ScopeStatus: contract.ScopeStatus,
            CapabilityStatus: contract.CapabilityStatus,
            MatchedEventCount: contract.MatchedEventCount,
            NoDataReason: contract.NoDataReason,
            IncludedThreads: contract.IncludedThreads,
            TraceUnmatchedBlockedIntervalCount:
                context.TraceUnmatchedBlockedIntervalCount,
            ScopedUnmatchedBlockedIntervalCount:
                context.ScopedUnmatchedBlockedIntervalCount,
            TraceHasContextSwitches: context.TraceHasContextSwitches,
            ScopedCSwitches: context.ScopedCSwitches,
            ScopedStackedSwitches: context.ScopedStackedSwitches,
            ScopedStackCoveragePct: context.ScopedStackCoveragePct,
            TraceCSwitches: context.TraceSourceEndpointCount,
            MatchedIntervalCount: context.MatchedIntervalCount,
            TraceIdentityUnresolvedCSwitchSideCount:
                context.TraceIdentityUnresolvedCSwitchSideCount,
            ScopedIdentityUnresolvedCSwitchSideCount:
                context.ScopedIdentityUnresolvedCSwitchSideCount);
    }

    private static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        StackAnalysisRequest request)
    {
        var context = BuildNormalized(trace, request);
        var contract = BuildEndpointContract(
            request.ThreadScope,
            request.HasFilter,
            context.StackCoverage,
            context.TraceSourceEndpointCount,
            context.ScopedSourceEndpointCount,
            context.ScopedIdentityUnresolvedCSwitchSideCount);
        var response = StackSourceTopN.ComputeCallerCallee(
            context.Normalized,
            focusFunction,
            top,
            metricName: "blockedUs",
            context.Stats,
            context.Warnings,
            sourceTotalMetric: context.TotalBlockedUs,
            unmatchedIntervalCount: context.TraceUnmatchedBlockedIntervalCount,
            selectedProcess: request.ThreadScope?.Process?.Key,
            selectedThread: request.ThreadScope?.Thread?.Key,
            hasContextSwitches: context.HasContextSwitches,
            hasContextSwitchBlockingStacks: context.HasContextSwitchBlockingStacks,
            symbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                request.ResolveSymbols,
                context.Stats,
                context.StackCoverage.StackedEventCount > 0),
            stackCoverage: context.StackCoverage,
            resultContract: contract);
        return response with
        {
            TraceUnmatchedIntervalCount = context.TraceUnmatchedBlockedIntervalCount,
            ScopedUnmatchedIntervalCount = context.ScopedUnmatchedBlockedIntervalCount,
            TraceHasContextSwitches = context.TraceHasContextSwitches,
            ScopedCSwitches = context.ScopedCSwitches,
            ScopedStackedSwitches = context.ScopedStackedSwitches,
            ScopedStackCoveragePct = context.ScopedStackCoveragePct,
            TraceSourceEndpointCount = context.TraceSourceEndpointCount,
            ScopedSourceEndpointCount = context.ScopedSourceEndpointCount,
            MatchedIntervalCount = context.MatchedIntervalCount,
            TraceIdentityUnresolvedEndpointCount =
                context.TraceIdentityUnresolvedCSwitchSideCount,
            ScopedIdentityUnresolvedEndpointCount =
                context.ScopedIdentityUnresolvedCSwitchSideCount,
        };
    }

    private sealed record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBlockedUs,
        long TotalBlockedUs,
        long SampleCount,
        int TraceUnmatchedBlockedIntervalCount,
        int ScopedUnmatchedBlockedIntervalCount,
        bool HasContextSwitches,
        bool TraceHasContextSwitches,
        bool HasContextSwitchBlockingStacks,
        long ScopedCSwitches,
        long ScopedStackedSwitches,
        double? ScopedStackCoveragePct,
        long TraceSourceEndpointCount,
        long ScopedSourceEndpointCount,
        long MatchedIntervalCount,
        long TraceIdentityUnresolvedCSwitchSideCount,
        long ScopedIdentityUnresolvedCSwitchSideCount,
        DomainStackCoverage StackCoverage,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest request)
    {
        var scope = request.ThreadScope ??
            throw new InvalidOperationException("Wait stacks require a resolved thread analysis scope.");
        var identities = TraceIdentityIndex.For(trace);
        var scheduler = new SchedulerIntervalAccumulator(identities.Threads.EndUsFor);
        using var symbolReader = StackSourceTopN.OpenSymbolReader(trace, request.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(
            trace, "wait", "us", stackSemantics: "switch_out_blocking_stack");
        long traceTotalBlockedUs = 0;
        long totalBlockedUs = 0;
        long sampleCount = 0;
        long totalContextSwitches = 0;
        long scopedContextSwitches = 0;
        long scopedStackedSwitches = 0;
        long unresolvedIdentityCount = 0;
        long traceIdentityUnresolvedCSwitchSideCount = 0;
        long scopedIdentityUnresolvedCSwitchSideCount = 0;

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
            {
                totalContextSwitches++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var switchResolution = identities.Threads.ResolveSwitch(
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewThreadID,
                    timestampUs);
                var oldResolution = switchResolution.OldThread;
                var newResolution = switchResolution.NewThread;
                var oldThread = ResolvedValue(oldResolution);
                var newThread = ResolvedValue(newResolution);
                var oldIdentityUnresolved = CountUnresolvedSide(
                    data.OldProcessID, data.OldThreadID, oldResolution);
                var newIdentityUnresolved = CountUnresolvedSide(
                    data.NewProcessID, data.NewThreadID, newResolution);
                unresolvedIdentityCount += oldIdentityUnresolved;
                unresolvedIdentityCount += newIdentityUnresolved;
                traceIdentityUnresolvedCSwitchSideCount = checked(
                    traceIdentityUnresolvedCSwitchSideCount +
                    oldIdentityUnresolved + newIdentityUnresolved);
                if (oldIdentityUnresolved > 0 && IsScopedUnresolvedSide(
                        scope,
                        identities,
                        data.OldProcessID,
                        data.OldThreadID,
                        timestampUs))
                {
                    scopedIdentityUnresolvedCSwitchSideCount = checked(
                        scopedIdentityUnresolvedCSwitchSideCount + 1);
                }
                if (newIdentityUnresolved > 0 && IsScopedUnresolvedSide(
                        scope,
                        identities,
                        data.NewProcessID,
                        data.NewThreadID,
                        timestampUs))
                {
                    scopedIdentityUnresolvedCSwitchSideCount = checked(
                        scopedIdentityUnresolvedCSwitchSideCount + 1);
                }

                var blockingStack = oldThread.HasValue
                    ? data.BlockingStack()
                    : CallStackIndex.Invalid;
                if (oldThread.HasValue &&
                    scope.MatchesPoint(oldThread.Value, timestampUs))
                {
                    scopedContextSwitches++;
                    if (blockingStack != CallStackIndex.Invalid)
                        scopedStackedSwitches++;
                }
                var closed = scheduler.ProcessSwitch(
                    oldThread,
                    newThread,
                    timestampUs,
                    WaitAnalysis.WaitReasonName(data.OldThreadWaitReason),
                    data.ProcessorNumber,
                    blockingStack);
                if (!closed.Blocked.HasValue)
                    return;

                var interval = closed.Blocked.Value;
                var fullDurationUs = checked(interval.EndUs - interval.StartUs);
                traceTotalBlockedUs = checked(traceTotalBlockedUs + fullDurationUs);

                var accountedUs = scope.AccountInterval(
                    interval.Thread, interval.StartUs, interval.EndUs);
                if (accountedUs <= 0)
                    return;

                totalBlockedUs = checked(totalBlockedUs + accountedUs);
                sampleCount++;
                raw.AddSample(interval.BlockingStack, data, accountedUs);
                request.When.AddDurationInterval(interval.StartUs, interval.EndUs);
            };

            kernel.ThreadStop += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAtEndpoint(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs,
                    preferredEndObserved: true);
                var thread = ResolvedValue(resolution);
                if (thread.HasValue)
                {
                    scheduler.Stop(thread.Value, timestampUs);
                }
                else
                {
                    unresolvedIdentityCount += CountUnresolvedSide(
                        data.ProcessID, data.ThreadID, resolution);
                }
            };
        });

        var completion = scheduler.Complete(identities.TraceEndUs);
        raw.Source.DoneAddingSamples();

        var lookupAttempt = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, request.ResolveSymbols, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw, lookupAttempt);
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source, trace, excludeEtwSelfOverhead: false);
        var coverage = raw.Coverage.Snapshot();
        var warnings = BuildWarnings(
            totalContextSwitches,
            sampleCount,
            unresolvedIdentityCount,
            completion,
            request.ResolveSymbols,
            stats);
        if (scope.ScopeMode == "pid_aggregate" && scope.PidReuseObserved)
        {
            warnings.Add(
                "pid_aggregate: pid-only scope aggregates multiple process lifetimes.");
        }
        StackSourceTopN.AddCoverageWarning(warnings, coverage);
        StackSourceTopN.AddSymbolLookupWarning(warnings, stats);

        return new BuildContext(
            normalized,
            stats,
            traceTotalBlockedUs,
            totalBlockedUs,
            sampleCount,
            completion.UnmatchedBlockedIntervalCount,
            completion.CountScopedUnmatchedBlockedIntervals(scope),
            scopedContextSwitches > 0,
            totalContextSwitches > 0,
            HasScopedBlockingStacks(scopedStackedSwitches),
            scopedContextSwitches,
            scopedStackedSwitches,
            scopedContextSwitches > 0
                ? 100.0 * scopedStackedSwitches / scopedContextSwitches
                : null,
            totalContextSwitches,
            scopedContextSwitches,
            sampleCount,
            traceIdentityUnresolvedCSwitchSideCount,
            scopedIdentityUnresolvedCSwitchSideCount,
            coverage,
            warnings);
    }

    internal static bool IsScopedUnresolvedSide(
        ThreadAnalysisScope scope,
        TraceIdentityIndex identities,
        int pid,
        int tid,
        long timestampUs) =>
        scope.MatchesRawUnresolvedCandidate(
            identities, pid, tid, timestampUs);

    internal static StackResultContract BuildEndpointContract(
        ThreadAnalysisScope? scope,
        bool filterSpecified,
        DomainStackCoverage coverage,
        long traceSourceEndpointCount,
        long scopedSourceEndpointCount,
        long scopedIdentityUnresolvedEndpointCount) =>
        StackResultContract.FromIntervalEndpoints(
            processScope: null,
            threadScope: scope,
            filterSpecified: filterSpecified,
            coverage: coverage,
            traceSourceEndpointCount: traceSourceEndpointCount,
            scopedSourceEndpointCount: scopedSourceEndpointCount,
            scopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount);

    internal static bool HasScopedBlockingStacks(long scopedStackedSwitches) =>
        scopedStackedSwitches > 0;

    private static ThreadAnalysisScope ResolveLegacyScope(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs)
    {
        var window = Validation.RequireWindowInput(startUs, endUs).Resolve(
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
                $"Unable to resolve wait stack scope: {resolution.Status}.");
    }

    private static ThreadInstanceKey? ResolvedValue(
        InstanceResolution<ThreadInstanceKey> resolution) =>
        resolution.Status == InstanceResolutionStatus.Resolved && resolution.Value.HasValue
            ? resolution.Value.Value
            : null;

    private static int CountUnresolvedSide(
        int pid,
        int tid,
        InstanceResolution<ThreadInstanceKey> resolution) =>
        pid > 0 && tid > 0 && resolution.Status != InstanceResolutionStatus.Resolved
            ? 1
            : 0;

    private static List<string> BuildWarnings(
        long totalContextSwitches,
        long sampleCount,
        long unresolvedIdentityCount,
        SchedulerIntervalResult completion,
        bool resolveSymbols,
        SymbolStats stats)
    {
        var warnings = new List<string>();
        if (totalContextSwitches == 0)
        {
            warnings.Add(
                "event_class_not_observed: no CSwitch events were observed in the trace. " +
                "This does not prove the CSwitch keyword was disabled; no qualifying switches may have occurred or the materialized trace may not expose that event class.");
        }
        else if (sampleCount == 0)
        {
            warnings.Add(
                "CSwitch events present but no closed blocked-time interval overlapped the requested scope.");
        }

        if (unresolvedIdentityCount > 0)
        {
            warnings.Add(
                $"identity_unresolved: scheduler_identity_unresolved; {unresolvedIdentityCount:N0} event-side identity resolution(s) were unavailable or ambiguous.");
        }
        if (completion.IdentityMismatchCount > 0)
        {
            warnings.Add(
                $"scheduler_identity_mismatch: {completion.IdentityMismatchCount:N0} scheduler state transition(s) did not match the resolved thread instance.");
        }
        if (!resolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate is { } resolutionRate && resolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(resolutionRate));

        return warnings;
    }
}
