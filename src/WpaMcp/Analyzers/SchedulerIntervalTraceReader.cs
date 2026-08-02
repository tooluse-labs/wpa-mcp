using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal interface ISchedulerIntervalSink
{
    void OnRunning(in RunningInterval interval);

    void OnBlocked(in BlockedInterval interval);
}

internal readonly record struct SchedulerSwitchObservation(
    ThreadInstanceKey? OldThread,
    string OldProcessName,
    ThreadInstanceKey? NewThread,
    string NewProcessName,
    long TimestampUs,
    CallStackIndex BlockingStack,
    int OldPid = 0,
    int OldTid = 0,
    bool OldIdentityUnresolved = false,
    int NewPid = 0,
    int NewTid = 0,
    bool NewIdentityUnresolved = false);

internal interface ISchedulerEventSink
{
    void OnContextSwitch(in SchedulerSwitchObservation observation);
}

internal sealed record SchedulerStreamSummary(
    SchedulerIntervalResult Completion,
    int IdentityDiagnosticCount,
    IReadOnlyList<IdentityDiagnostic> DiagnosticSample);

internal static class SchedulerIntervalTraceReader
{
    public const int MaxDiagnosticSample = 32;

    public static SchedulerStreamSummary Read(
        TraceLog trace,
        TraceIdentityIndex identities,
        IReadOnlyList<ISchedulerIntervalSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(identities);
        ArgumentNullException.ThrowIfNull(sinks);
        if (sinks.Any(sink => sink is null))
            throw new ArgumentException("Scheduler interval sinks cannot contain null.", nameof(sinks));

        var scheduler = new SchedulerIntervalAccumulator(identities.Threads.EndUsFor);
        var diagnostics = new List<IdentityDiagnostic>(MaxDiagnosticSample);
        var diagnosticCount = 0;
        var eventSinks = sinks.OfType<ISchedulerEventSink>().ToArray();

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var switchResolution = identities.Threads.ResolveSwitch(
                    data.OldProcessID,
                    data.OldThreadID,
                    data.NewProcessID,
                    data.NewThreadID,
                    timestampUs);
                var oldResolution = switchResolution.OldThread;
                var newResolution = switchResolution.NewThread;
                RecordDiagnostic(
                    data.OldProcessID,
                    data.OldThreadID,
                    timestampUs,
                    oldResolution,
                    diagnostics,
                    ref diagnosticCount);
                RecordDiagnostic(
                    data.NewProcessID,
                    data.NewThreadID,
                    timestampUs,
                    newResolution,
                    diagnostics,
                    ref diagnosticCount);

                var oldThread = ResolvedValue(oldResolution);
                var newThread = ResolvedValue(newResolution);
                var blockingStack = oldThread.HasValue
                    ? data.BlockingStack()
                    : CallStackIndex.Invalid;
                var observation = new SchedulerSwitchObservation(
                    oldThread,
                    data.OldProcessName ?? string.Empty,
                    newThread,
                    data.NewProcessName ?? string.Empty,
                    timestampUs,
                    blockingStack,
                    data.OldProcessID,
                    data.OldThreadID,
                    IsIdentityUnresolved(
                        data.OldProcessID, data.OldThreadID, oldResolution),
                    data.NewProcessID,
                    data.NewThreadID,
                    IsIdentityUnresolved(
                        data.NewProcessID, data.NewThreadID, newResolution));
                foreach (var eventSink in eventSinks)
                    eventSink.OnContextSwitch(observation);

                Publish(
                    scheduler.ProcessSwitch(
                        oldThread,
                        newThread,
                        timestampUs,
                        WaitAnalysis.WaitReasonName(data.OldThreadWaitReason),
                        data.ProcessorNumber,
                        blockingStack),
                    sinks);
            };

            kernel.ThreadStop += data =>
            {
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var resolution = identities.Threads.ResolveAtEndpoint(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs,
                    preferredEndObserved: true);
                RecordDiagnostic(
                    data.ProcessID,
                    data.ThreadID,
                    timestampUs,
                    resolution,
                    diagnostics,
                    ref diagnosticCount);
                var thread = ResolvedValue(resolution);
                if (thread.HasValue)
                    Publish(scheduler.Stop(thread.Value, timestampUs), sinks);
            };
        });

        var completion = scheduler.Complete(identities.TraceEndUs);
        foreach (var interval in AnalysisEvents.Enumerate(completion.ClosedAtTraceEnd))
            PublishRunning(interval, sinks);
        return new SchedulerStreamSummary(
            completion,
            diagnosticCount,
            diagnostics.ToArray());
    }

    private static void Publish(
        ClosedSchedulerIntervals closed,
        IReadOnlyList<ISchedulerIntervalSink> sinks)
    {
        if (closed.Running.HasValue)
            PublishRunning(closed.Running.Value, sinks);
        if (closed.Blocked.HasValue)
        {
            var interval = closed.Blocked.Value;
            foreach (var sink in sinks)
                sink.OnBlocked(interval);
        }
    }

    private static void PublishRunning(
        RunningInterval interval,
        IReadOnlyList<ISchedulerIntervalSink> sinks)
    {
        foreach (var sink in sinks)
            sink.OnRunning(interval);
    }

    private static ThreadInstanceKey? ResolvedValue(
        InstanceResolution<ThreadInstanceKey> resolution) =>
        resolution.Status == InstanceResolutionStatus.Resolved && resolution.Value.HasValue
            ? resolution.Value.Value
            : null;

    private static bool IsIdentityUnresolved(
        int pid,
        int tid,
        InstanceResolution<ThreadInstanceKey> resolution) =>
        pid > 0 && tid > 0 &&
        resolution.Status != InstanceResolutionStatus.Resolved;

    private static void RecordDiagnostic(
        int pid,
        int tid,
        long timestampUs,
        InstanceResolution<ThreadInstanceKey> resolution,
        List<IdentityDiagnostic> sample,
        ref int totalCount)
    {
        if (pid <= 0 || tid <= 0 ||
            resolution.Status == InstanceResolutionStatus.Resolved)
        {
            return;
        }

        totalCount = checked(totalCount + 1);
        if (sample.Count >= MaxDiagnosticSample)
            return;
        sample.Add(new IdentityDiagnostic(
            resolution.Status == InstanceResolutionStatus.Ambiguous
                ? "scheduler_thread_ambiguous"
                : "scheduler_thread_unresolved",
            pid,
            tid,
            timestampUs,
            resolution.Status,
            resolution.Candidates.Count));
    }
}
