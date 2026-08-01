using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;

namespace WpaMcp.Analyzers;

internal readonly record struct RunningInterval(
    ThreadInstanceKey Thread,
    long StartUs,
    long EndUs,
    int Core);

internal readonly record struct BlockedInterval(
    ThreadInstanceKey Thread,
    long StartUs,
    long EndUs,
    string WaitReason,
    CallStackIndex BlockingStack = CallStackIndex.Invalid);

internal readonly record struct ClosedSchedulerIntervals(
    RunningInterval? Running,
    BlockedInterval? Blocked);

internal readonly record struct IncompleteBlockedInterval(
    ThreadInstanceKey Thread,
    long StartUs,
    long EndUs,
    string Code);

internal sealed record SchedulerIntervalResult(
    IReadOnlyList<RunningInterval> ClosedAtTraceEnd,
    int UnmatchedRunningIntervalCount,
    int UnmatchedBlockedIntervalCount,
    int IdentityMismatchCount,
    IReadOnlyList<IncompleteBlockedInterval> UnmatchedBlockedIntervals)
{
    public int CountScopedUnmatchedBlockedIntervals(ThreadAnalysisScope scope) =>
        UnmatchedBlockedIntervals.Count(interval =>
            scope.MatchesThread(interval.Thread) &&
            (scope.Window.IntersectDurationUs(interval.StartUs, interval.EndUs) > 0 ||
             (interval.EndUs <= interval.StartUs &&
              (scope.Window.ContainsPoint(interval.StartUs) ||
               scope.Window.ContainsPoint(interval.EndUs)))));
}

internal sealed class SchedulerIntervalAccumulator
{
    private readonly Func<ThreadInstanceKey, long?>? _threadEndUs;
    private readonly Dictionary<ThreadInstanceKey, BlockedStart> _blocked = new();
    private readonly Dictionary<int, RunningStart> _runningByCore = new();
    private int _unmatchedRunningIntervalCount;
    private int _unmatchedBlockedIntervalCount;
    private int _identityMismatchCount;
    private readonly List<IncompleteBlockedInterval> _unmatchedBlockedIntervals = [];
    private bool _completed;

    public SchedulerIntervalAccumulator(
        Func<ThreadInstanceKey, long?>? threadEndUs = null)
    {
        _threadEndUs = threadEndUs;
    }

    public ClosedSchedulerIntervals ProcessSwitch(
        ThreadInstanceKey? oldThread,
        ThreadInstanceKey? newThread,
        long timestampUs,
        string waitReason,
        int core,
        CallStackIndex oldThreadBlockingStack = CallStackIndex.Invalid)
    {
        EnsureMutable();
        RequireTimestamp(timestampUs);
        if (core < 0)
            throw new ArgumentOutOfRangeException(nameof(core));

        RunningInterval? closedRunning = null;
        if (_runningByCore.Remove(core, out var running))
        {
            if (oldThread.HasValue && oldThread.Value == running.Thread)
            {
                closedRunning = CloseRunning(running, timestampUs, core);
            }
            else
            {
                _unmatchedRunningIntervalCount++;
                _identityMismatchCount++;
            }
        }
        else if (oldThread.HasValue && IsRunningOnAnotherCore(oldThread.Value, core))
        {
            _identityMismatchCount++;
        }

        if (oldThread.HasValue)
        {
            if (_blocked.TryGetValue(oldThread.Value, out var overwritten))
            {
                _unmatchedBlockedIntervalCount++;
                _identityMismatchCount++;
                _unmatchedBlockedIntervals.Add(new IncompleteBlockedInterval(
                    oldThread.Value,
                    overwritten.StartUs,
                    timestampUs,
                    "overwritten_switch_out"));
            }
            _blocked[oldThread.Value] = new BlockedStart(
                timestampUs,
                string.IsNullOrEmpty(waitReason) ? "Unknown" : waitReason,
                oldThreadBlockingStack);
        }

        BlockedInterval? closedBlocked = null;
        if (newThread.HasValue)
        {
            if (_blocked.Remove(newThread.Value, out var blocked))
            {
                if (timestampUs > blocked.StartUs)
                {
                    closedBlocked = new BlockedInterval(
                        newThread.Value,
                        blocked.StartUs,
                        timestampUs,
                        blocked.Reason,
                        blocked.BlockingStack);
                }
                else
                {
                    _unmatchedBlockedIntervalCount++;
                    _unmatchedBlockedIntervals.Add(new IncompleteBlockedInterval(
                        newThread.Value,
                        blocked.StartUs,
                        timestampUs,
                        "non_positive_interval"));
                }
            }
            else if (HasBlockedRawIdentity(newThread.Value))
            {
                _identityMismatchCount++;
            }

            RemoveDuplicateRunningState(newThread.Value, core);
            _runningByCore[core] = new RunningStart(newThread.Value, timestampUs);
        }

        return new ClosedSchedulerIntervals(closedRunning, closedBlocked);
    }

    public ClosedSchedulerIntervals Stop(ThreadInstanceKey thread, long timestampUs)
    {
        EnsureMutable();
        RequireTimestamp(timestampUs);

        RunningInterval? closedRunning = null;
        foreach (var item in _runningByCore
                     .Where(item => item.Value.Thread == thread)
                     .OrderBy(item => item.Key)
                     .ToArray())
        {
            _runningByCore.Remove(item.Key);
            var candidate = CloseRunning(item.Value, timestampUs, item.Key);
            if (candidate.HasValue && !closedRunning.HasValue)
            {
                closedRunning = candidate;
            }
            else if (candidate.HasValue)
            {
                _unmatchedRunningIntervalCount++;
                _identityMismatchCount++;
            }
        }

        if (_blocked.Remove(thread, out var blocked))
        {
            _unmatchedBlockedIntervalCount++;
            _unmatchedBlockedIntervals.Add(new IncompleteBlockedInterval(
                thread,
                blocked.StartUs,
                timestampUs,
                "thread_stopped_while_blocked"));
        }

        return new ClosedSchedulerIntervals(closedRunning, Blocked: null);
    }

    public SchedulerIntervalResult Complete(long traceEndUs)
    {
        EnsureMutable();
        RequireTimestamp(traceEndUs);

        var closedAtTraceEnd = new List<RunningInterval>();
        foreach (var item in _runningByCore.OrderBy(item => item.Key))
        {
            var interval = CloseRunning(item.Value, traceEndUs, item.Key);
            if (interval.HasValue)
                closedAtTraceEnd.Add(interval.Value);
        }

        foreach (var item in _blocked)
        {
            _unmatchedBlockedIntervals.Add(new IncompleteBlockedInterval(
                item.Key,
                item.Value.StartUs,
                traceEndUs,
                "open_at_trace_end"));
        }
        _unmatchedBlockedIntervalCount += _blocked.Count;
        _runningByCore.Clear();
        _blocked.Clear();
        _completed = true;

        return new SchedulerIntervalResult(
            closedAtTraceEnd,
            _unmatchedRunningIntervalCount,
            _unmatchedBlockedIntervalCount,
            _identityMismatchCount,
            _unmatchedBlockedIntervals.ToArray());
    }

    private RunningInterval? CloseRunning(RunningStart running, long endUs, int core)
    {
        if (_threadEndUs?.Invoke(running.Thread) is { } threadEndUs)
            endUs = TimeWindow.ClipEnd(endUs, threadEndUs);
        if (endUs <= running.StartUs)
        {
            _unmatchedRunningIntervalCount++;
            return null;
        }

        return new RunningInterval(running.Thread, running.StartUs, endUs, core);
    }

    private bool IsRunningOnAnotherCore(ThreadInstanceKey thread, int core) =>
        _runningByCore.Any(item => item.Key != core && item.Value.Thread == thread);

    private bool HasBlockedRawIdentity(ThreadInstanceKey thread) =>
        _blocked.Keys.Any(blocked =>
            blocked.Process.Pid == thread.Process.Pid && blocked.Tid == thread.Tid);

    private void RemoveDuplicateRunningState(ThreadInstanceKey thread, int targetCore)
    {
        foreach (var duplicateCore in _runningByCore
                     .Where(item => item.Key != targetCore && item.Value.Thread == thread)
                     .Select(item => item.Key)
                     .ToArray())
        {
            _runningByCore.Remove(duplicateCore);
            _unmatchedRunningIntervalCount++;
            _identityMismatchCount++;
        }
    }

    private static void RequireTimestamp(long timestampUs)
    {
        if (timestampUs < 0)
            throw new ArgumentOutOfRangeException(nameof(timestampUs));
    }

    private void EnsureMutable()
    {
        if (_completed)
            throw new InvalidOperationException("Scheduler interval accumulator is complete.");
    }

    private readonly record struct RunningStart(
        ThreadInstanceKey Thread,
        long StartUs);

    private readonly record struct BlockedStart(
        long StartUs,
        string Reason,
        CallStackIndex BlockingStack);
}
