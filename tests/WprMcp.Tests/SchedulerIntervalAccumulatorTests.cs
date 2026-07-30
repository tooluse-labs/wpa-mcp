using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class SchedulerIntervalAccumulatorTests
{
    [Fact]
    public void SwitchOutBeforeWindow_ResumeAfterWindow_ProducesOneClippableInterval()
    {
        var thread = Thread(10, processStartUs: 0, tid: 5, generation: 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(
            oldThread: thread, newThread: null, timestampUs: 90,
            waitReason: "UserRequest", core: 0,
            oldThreadBlockingStack: (CallStackIndex)11);
        var closed = accumulator.ProcessSwitch(
            oldThread: null, newThread: thread, timestampUs: 210,
            waitReason: "Unknown", core: 0);

        Assert.Equal(
            new BlockedInterval(thread, 90, 210, "UserRequest", (CallStackIndex)11),
            closed.Blocked);
        Assert.Equal(
            100,
            new TimeWindow(100, 200).IntersectDurationUs(
                closed.Blocked!.Value.StartUs, closed.Blocked.Value.EndUs));
    }

    [Fact]
    public void ReusedTidWithDifferentGeneration_DoesNotCloseOldWait()
    {
        var oldThread = Thread(10, 0, 5, 1);
        var newThread = Thread(10, 0, 5, 2);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(oldThread, null, 10, "Executive", core: 0);

        var closed = accumulator.ProcessSwitch(
            null, newThread, 20, "Unknown", core: 0);
        var result = accumulator.Complete(100);

        Assert.Null(closed.Blocked);
        Assert.Equal(1, result.UnmatchedBlockedIntervalCount);
        Assert.Equal(1, result.IdentityMismatchCount);
    }

    [Fact]
    public void ReusedPidWithSameTid_DoesNotJoinProcessInstances()
    {
        var first = Thread(10, 0, 5, 1);
        var second = Thread(10, 50, 5, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(first, null, 10, "Executive", core: 0);

        var closed = accumulator.ProcessSwitch(
            null, second, 60, "Unknown", core: 0);
        var result = accumulator.Complete(100);

        Assert.Null(closed.Blocked);
        Assert.Equal(1, result.UnmatchedBlockedIntervalCount);
        Assert.Equal(1, result.IdentityMismatchCount);
    }

    [Fact]
    public void SwitchInThenOut_ClosesExactRunningIntervalOnCore()
    {
        var thread = Thread(10, 0, 5, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(null, thread, 10, "Unknown", core: 3);

        var closed = accumulator.ProcessSwitch(
            thread, null, 30, "WrUserRequest", core: 3);

        Assert.Equal(new RunningInterval(thread, 10, 30, 3), closed.Running);
        Assert.Null(closed.Blocked);
    }

    [Fact]
    public void CoreIdentityMismatch_DropsRunningIntervalInsteadOfGuessing()
    {
        var running = Thread(10, 0, 5, 1);
        var reportedOld = Thread(10, 0, 6, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(null, running, 10, "Unknown", core: 2);

        var closed = accumulator.ProcessSwitch(
            reportedOld, null, 30, "Executive", core: 2);
        var result = accumulator.Complete(100);

        Assert.Null(closed.Running);
        Assert.Equal(1, result.UnmatchedRunningIntervalCount);
        Assert.Equal(1, result.IdentityMismatchCount);
    }

    [Fact]
    public void Stop_ClosesRunningButDoesNotInventBlockedEnd()
    {
        var running = Thread(10, 0, 5, 1);
        var blocked = Thread(10, 0, 6, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(null, running, 10, "Unknown", core: 1);
        accumulator.ProcessSwitch(blocked, null, 20, "Executive", core: 2);

        var runningStop = accumulator.Stop(running, 40);
        var blockedStop = accumulator.Stop(blocked, 50);
        var result = accumulator.Complete(100);

        Assert.Equal(new RunningInterval(running, 10, 40, 1), runningStop.Running);
        Assert.Null(blockedStop.Blocked);
        Assert.Equal(1, result.UnmatchedBlockedIntervalCount);
    }

    [Fact]
    public void Complete_ClosesRunningAtTraceEndButLeavesBlockedUnmatched()
    {
        var running = Thread(10, 0, 5, 1);
        var blocked = Thread(10, 0, 6, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(null, running, 10, "Unknown", core: 1);
        accumulator.ProcessSwitch(blocked, null, 20, "Executive", core: 2);

        var result = accumulator.Complete(100);

        Assert.Equal([new RunningInterval(running, 10, 100, 1)], result.ClosedAtTraceEnd);
        Assert.Equal(0, result.UnmatchedRunningIntervalCount);
        Assert.Equal(1, result.UnmatchedBlockedIntervalCount);
    }

    [Fact]
    public void Complete_ClipsRunningIntervalToResolvedThreadEnd()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(10, 0),
            EndUs: 100,
            StartObserved: true,
            EndObserved: true);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes: [process],
            threads:
            [
                new ThreadLifecycleEvent(
                    10, 5, 50, ThreadLifecycleEventKind.Start, Observed: true),
            ]);
        var running = Assert.Single(identities.Threads.Lifetimes).Key;
        var accumulator = new SchedulerIntervalAccumulator(identities.Threads.EndUsFor);
        accumulator.ProcessSwitch(null, running, 90, "Unknown", core: 1);

        var result = accumulator.Complete(200);

        Assert.Equal(
            [new RunningInterval(running, 90, 100, 1)],
            result.ClosedAtTraceEnd);
    }

    [Fact]
    public void BackwardSwitch_DoesNotEmitNegativeInterval()
    {
        var thread = Thread(10, 0, 5, 1);
        var accumulator = new SchedulerIntervalAccumulator();
        accumulator.ProcessSwitch(null, thread, 20, "Unknown", core: 0);

        var closed = accumulator.ProcessSwitch(
            thread, null, 10, "Executive", core: 0);
        var result = accumulator.Complete(100);

        Assert.Null(closed.Running);
        Assert.Equal(1, result.UnmatchedRunningIntervalCount);
    }

    [Fact]
    public void NegativeTimestamp_IsRejected()
    {
        var accumulator = new SchedulerIntervalAccumulator();

        Assert.Throws<ArgumentOutOfRangeException>(() => accumulator.ProcessSwitch(
            null, Thread(10, 0, 5, 1), -1, "Unknown", core: 0));
    }

    private static ThreadInstanceKey Thread(
        int pid,
        long processStartUs,
        int tid,
        long generation) =>
        new(new ProcessInstanceKey(pid, processStartUs), tid, generation);
}
