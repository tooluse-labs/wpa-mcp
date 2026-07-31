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
        var callTree = new CallTree(ScalingPolicyKind.ScaleToData)
        {
            StackSource = context.Normalized,
        };
        var totalMetric = Math.Max(1.0, callTree.Root.InclusiveMetric);

        var rows = callTree.ByID
            .OrderByDescending(node => node.ExclusiveMetric)
            .Take(top)
            .Select(node => new WaitStackRow(
                Function: node.Name,
                ExclusiveBlockedUs: (long)node.ExclusiveMetric,
                InclusiveBlockedUs: (long)node.InclusiveMetric,
                ExclusivePct: StackSourceTopN.Pct(totalMetric, node.ExclusiveMetric),
                InclusivePct: StackSourceTopN.Pct(totalMetric, node.InclusiveMetric),
                ExclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter, context.TraceTotalBlockedUs, node.ExclusiveMetric),
                InclusivePctOfTrace: StackSourceTopN.PctOfTrace(
                    request.HasFilter, context.TraceTotalBlockedUs, node.InclusiveMetric)))
            .ToList();

        return new WaitTopStacksResponse(
            Rows: rows,
            TotalBlockedUs: context.TotalBlockedUs,
            SampleCount: context.SampleCount,
            Stats: context.Stats,
            Warnings: context.Warnings,
            When: request.When.Build(),
            UnmatchedBlockedIntervalCount: context.UnmatchedBlockedIntervalCount,
            SelectedProcess: request.ThreadScope?.Process?.Key,
            SelectedThread: request.ThreadScope?.Thread?.Key,
            HasContextSwitches: context.HasContextSwitches,
            HasContextSwitchBlockingStacks: context.HasContextSwitchBlockingStacks,
            SymbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                request.ResolveSymbols, context.Stats, context.HasContextSwitchBlockingStacks));
    }

    private static CallerCalleeResponse CallerCallee(
        TraceLog trace,
        string focusFunction,
        int top,
        StackAnalysisRequest request)
    {
        var context = BuildNormalized(trace, request);
        return StackSourceTopN.ComputeCallerCallee(
            context.Normalized,
            focusFunction,
            top,
            metricName: "blockedUs",
            context.Stats,
            context.Warnings,
            sourceTotalMetric: context.TotalBlockedUs,
            unmatchedIntervalCount: context.UnmatchedBlockedIntervalCount,
            selectedProcess: request.ThreadScope?.Process?.Key,
            selectedThread: request.ThreadScope?.Thread?.Key,
            hasContextSwitches: context.HasContextSwitches,
            hasContextSwitchBlockingStacks: context.HasContextSwitchBlockingStacks,
            symbolResolutionState: StackSourceTopN.GetSymbolResolutionState(
                request.ResolveSymbols, context.Stats, context.HasContextSwitchBlockingStacks));
    }

    private sealed record BuildContext(
        MutableTraceEventStackSource Normalized,
        SymbolStats Stats,
        long TraceTotalBlockedUs,
        long TotalBlockedUs,
        long SampleCount,
        int UnmatchedBlockedIntervalCount,
        bool HasContextSwitches,
        bool HasContextSwitchBlockingStacks,
        List<string> Warnings);

    private static BuildContext BuildNormalized(TraceLog trace, StackAnalysisRequest request)
    {
        var scope = request.ThreadScope ??
            throw new InvalidOperationException("Wait stacks require a resolved thread analysis scope.");
        var identities = TraceIdentityIndex.For(trace);
        var scheduler = new SchedulerIntervalAccumulator(identities.Threads.EndUsFor);
        using var symbolReader = StackSourceTopN.OpenSymbolReader(request.SymbolLog);
        var raw = StackSourceTopN.CreateRawSource(trace);
        long traceTotalBlockedUs = 0;
        long totalBlockedUs = 0;
        long sampleCount = 0;
        long totalContextSwitches = 0;
        long unresolvedIdentityCount = 0;
        var hasContextSwitchBlockingStacks = false;

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
                unresolvedIdentityCount += CountUnresolvedSide(
                    data.OldProcessID, data.OldThreadID, oldResolution);
                unresolvedIdentityCount += CountUnresolvedSide(
                    data.NewProcessID, data.NewThreadID, newResolution);

                var blockingStack = oldThread.HasValue
                    ? data.BlockingStack()
                    : CallStackIndex.Invalid;
                if (blockingStack != CallStackIndex.Invalid)
                    hasContextSwitchBlockingStacks = true;
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

        if (request.ResolveSymbols)
            raw.Source.LookupWarmSymbols(StackSourceTopN.WarmSymbolThreshold, symbolReader);
        var stats = StackSourceTopN.ComputeSymbolStats(raw.Source);
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source, trace, excludeEtwSelfOverhead: false);
        var warnings = BuildWarnings(
            totalContextSwitches,
            sampleCount,
            unresolvedIdentityCount,
            completion,
            request.ResolveSymbols,
            stats);
        if (scope.PidReuseObserved)
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope aggregates multiple process lifetimes.");
        }

        return new BuildContext(
            normalized,
            stats,
            traceTotalBlockedUs,
            totalBlockedUs,
            sampleCount,
            completion.UnmatchedBlockedIntervalCount,
            totalContextSwitches > 0,
            hasContextSwitchBlockingStacks,
            warnings);
    }

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
                "No CSwitch events found. The capture profile must include the CSwitch keyword. " +
                "Default WPR 'CPU' / 'CPU.light' profiles include it; some custom .wprp files may not.");
        }
        else if (sampleCount == 0)
        {
            warnings.Add(
                "CSwitch events present but no closed blocked-time interval overlapped the requested scope.");
        }

        if (unresolvedIdentityCount > 0)
        {
            warnings.Add(
                $"scheduler_identity_unresolved: {unresolvedIdentityCount:N0} event-side identity resolution(s) were unavailable or ambiguous.");
        }
        if (completion.IdentityMismatchCount > 0)
        {
            warnings.Add(
                $"scheduler_identity_mismatch: {completion.IdentityMismatchCount:N0} scheduler state transition(s) did not match the resolved thread instance.");
        }
        if (!resolveSymbols)
            warnings.Add(WarningBuilder.SymbolResolutionSkipped("stack analysis"));
        else if (stats.ResolutionRate < 0.8)
            warnings.Add(WarningBuilder.SymbolResolution(stats.ResolutionRate));

        return warnings;
    }
}
