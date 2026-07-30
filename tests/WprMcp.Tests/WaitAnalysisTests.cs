using System.Diagnostics;       // ThreadWaitReason (BCL)
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class WaitAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void WaitAnalysis_ReturnsRowsOrEmitsKeywordWarning()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitAnalysis(FixturePath, top: 20);
        // CPU.light may or may not include CSwitch depending on OS build. Either way:
        //   - If CSwitch was captured, we get rows.
        //   - If not, we get a clear warning explaining why.
        if (resp.Rows.Count == 0)
            Assert.Contains(resp.Warnings, w => w.Contains("CSwitch", StringComparison.OrdinalIgnoreCase));
        else
            Assert.True(resp.TotalCSwitches > 0);
    }

    [Fact]
    public void WaitAnalysis_OrdersByBlockedDescending()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitAnalysis(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].BlockedUs >= resp.Rows[i].BlockedUs);
    }

    [Fact]
    public void WaitAnalysis_RejectsBadTop()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.WaitAnalysis("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.WaitAnalysis("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void WaitReasonName_KnownValuesMapToCanonicalNames()
    {
        Assert.Equal("Executive", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)0));
        Assert.Equal("UserRequest", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)6));
        Assert.Equal("WrQueue", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)15));
        Assert.Equal("WrTerminated", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)22));
        Assert.Equal("WrPreempted", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)32));
        Assert.Equal("WrAlertByThreadId", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)37));
    }

    [Fact]
    public void WaitReasonName_OutOfRangeFallsBackToWaitN()
    {
        Assert.Equal("Wait_99", WprMcp.Analyzers.WaitAnalysis.WaitReasonName((ThreadWaitReason)99));
    }

    [Fact]
    public void WaitAnalysis_RowsHaveNamedReasons_NotRawIntegers()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitAnalysis(FixturePath, top: 50);
        if (resp.Rows.Count == 0) return;
        // No reason name should be a bare integer like "15" or "22".
        foreach (var row in resp.Rows)
            foreach (var bucket in row.TopWaitReasons)
                Assert.False(int.TryParse(bucket.Reason, out _),
                    $"reason field is a bare integer: {bucket.Reason}");
    }

    [Fact]
    public void WaitAnalysis_PidFilterIsRespected()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var unfiltered = tools.WaitAnalysis(FixturePath, top: 100);
        if (unfiltered.Rows.Count == 0) return; // No CSwitch — nothing to filter
        var firstPid = unfiltered.Rows[0].Pid;
        var filtered = tools.WaitAnalysis(FixturePath, top: 100, pid: firstPid);
        Assert.All(filtered.Rows, r => Assert.Equal(firstPid, r.Pid));
    }

    [Fact]
    public void WaitAnalysis_EndUsIsExclusive()
    {
        var cswitchTimes = CSwitchTimesUs();
        var distinctTimes = cswitchTimes.Distinct().ToList();
        Assert.True(distinctTimes.Count > 1, "fixture must have CSwitch events at multiple timestamps");

        var endUs = distinctTimes[(distinctTimes.Count - 1) / 2];
        var expectedSwitches = cswitchTimes.Count(t => t < endUs);
        Assert.InRange(expectedSwitches, 1, cswitchTimes.Count - 1);

        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitAnalysis(FixturePath, top: 1000, endUs: endUs);

        Assert.Equal(expectedSwitches, resp.TotalCSwitches);
    }

    [Fact]
    public void WaitAccumulator_TargetTidBelowTop_IsStillReturned()
    {
        var target = Thread(pid: 10, processStartUs: 0, tid: 900, generation: 1);
        var intervals = Enumerable.Range(1, 10)
            .Select(tid => new BlockedInterval(
                Thread(10, 0, tid, 1), 0, 90 - tid, "Executive"))
            .Append(new BlockedInterval(target, 0, 1, "WrUserRequest"))
            .ToArray();

        var response = WprMcp.Analyzers.WaitAnalysis.Project(
            intervals,
            ScopeFor(target, startUs: 0, endUs: 100),
            top: 1);

        var row = Assert.Single(response.Rows);
        Assert.Equal(900, row.Tid);
        Assert.Equal(1, row.BlockedUs);
    }

    [Fact]
    public void WaitAccumulator_TotalIsComputedBeforeTop()
    {
        var intervals = new[]
        {
            new BlockedInterval(Thread(10, 0, 1, 1), 0, 80, "Executive"),
            new BlockedInterval(Thread(10, 0, 2, 1), 10, 70, "WrQueue"),
            new BlockedInterval(Thread(10, 0, 3, 1), 20, 60, "WrUserRequest"),
        };
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 100), Pid: 10, Process: null, Thread: null,
            AggregatesPidLifetimes: true, PidReuseObserved: false);

        var response = WprMcp.Analyzers.WaitAnalysis.Project(intervals, scope, top: 1);

        Assert.Single(response.Rows);
        Assert.Equal(
            intervals.Sum(interval => scope.AccountInterval(
                interval.Thread, interval.StartUs, interval.EndUs)),
            response.TotalBlockedUs);
    }

    [Fact]
    public void WaitAccumulator_ClipsCpuBlockedAndReasonTotalsToOneScope()
    {
        var target = Thread(10, 0, 7, 1);
        var other = Thread(10, 0, 8, 1);
        var scope = ScopeFor(target, startUs: 100, endUs: 200);

        var response = WprMcp.Analyzers.WaitAnalysis.Project(
            intervals:
            [
                new BlockedInterval(target, 90, 210, "WrUserRequest"),
                new BlockedInterval(other, 100, 200, "Executive"),
            ],
            scope,
            top: 1,
            runningIntervals:
            [
                new RunningInterval(target, 50, 150, Core: 2),
                new RunningInterval(other, 100, 200, Core: 3),
            ],
            unmatchedBlockedIntervalCount: 2);

        var row = Assert.Single(response.Rows);
        Assert.Equal(50, row.CpuUs);
        Assert.Equal(100, row.BlockedUs);
        Assert.Equal(100, response.TotalBlockedUs);
        Assert.Equal(2, response.UnmatchedBlockedIntervalCount);
        Assert.Equal(target.Process, response.SelectedProcess);
        Assert.Equal(target, response.SelectedThread);
        var reason = Assert.Single(row.TopWaitReasons);
        Assert.Equal("WrUserRequest", reason.Reason);
        Assert.Equal(100, reason.BlockedUs);
    }

    [Fact]
    public void WaitAccumulator_LegacyPidReuseWarnsWithoutSelectingOneProcess()
    {
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 300), Pid: 10, Process: null, Thread: null,
            AggregatesPidLifetimes: true, PidReuseObserved: true);

        var response = WprMcp.Analyzers.WaitAnalysis.Project(
            [
                new BlockedInterval(Thread(10, 0, 7, 1), 10, 20, "Executive"),
                new BlockedInterval(Thread(10, 200, 7, 1), 210, 230, "Executive"),
            ],
            scope,
            top: 10);

        Assert.Null(response.SelectedProcess);
        Assert.Null(response.SelectedThread);
        Assert.Equal(30, response.TotalBlockedUs);
        Assert.Contains(response.Warnings, warning =>
            warning.Contains("ambiguous_process_instance", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitAnalysis_WindowedPidFilterSurvivesOutOfWindowTidReuse()
    {
        var target = Thread(100, 0, 42, 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(100_000, 200_000), Pid: 100,
            Process: null, Thread: null,
            AggregatesPidLifetimes: true, PidReuseObserved: false);
        var resp = WprMcp.Analyzers.WaitAnalysis.Project(
            [
                new BlockedInterval(target, 90_000, 120_000, "WrUserRequest"),
                new BlockedInterval(
                    Thread(200, 0, 42, 1), 100_000, 180_000, "WrUserRequest"),
            ],
            scope,
            top: 10,
            processNames: new Dictionary<ThreadInstanceKey, string>
            {
                [target] = "target",
            },
            totalCSwitches: 1);

        var row = Assert.Single(resp.Rows);
        Assert.Equal(100, row.Pid);
        Assert.Equal("target", row.ProcessName);
        Assert.Equal(42, row.Tid);
        Assert.Equal(20_000, row.BlockedUs);
        Assert.Equal(1, resp.TotalCSwitches);
    }

    [Fact]
    public void WaitAnalysis_ClipsCpuAndBlockedIntervalsToWindowStart()
    {
        var target = Thread(100, 0, 42, 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(100_000, 200_000), Pid: 100,
            Process: null, Thread: null,
            AggregatesPidLifetimes: true, PidReuseObserved: false);
        var resp = WprMcp.Analyzers.WaitAnalysis.Project(
            [new BlockedInterval(target, 120_000, 150_000, "WrUserRequest")],
            scope,
            top: 10,
            runningIntervals:
            [
                new RunningInterval(target, 90_000, 120_000, Core: 0),
                new RunningInterval(target, 150_000, 250_000, Core: 0),
            ]);

        var row = Assert.Single(resp.Rows);
        Assert.Equal(70_000, row.CpuUs);
        Assert.Equal(30_000, row.BlockedUs);
    }

    [Fact]
    public void WaitAnalysis_StraddlingBlockedIntervalDoesNotEmitEmptyWindowWarning()
    {
        var target = Thread(100, 0, 42, 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(100_000, 200_000), Pid: 100,
            Process: null, Thread: null,
            AggregatesPidLifetimes: true, PidReuseObserved: false);
        var resp = WprMcp.Analyzers.WaitAnalysis.Project(
            [new BlockedInterval(target, 90_000, 250_000, "WrUserRequest")],
            scope,
            top: 10,
            totalCSwitches: 0,
            traceCSwitchCount: 2);

        var row = Assert.Single(resp.Rows);
        Assert.Equal(100_000, row.BlockedUs);
        Assert.Equal(0, resp.TotalCSwitches);
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("none landed inside", StringComparison.OrdinalIgnoreCase));
    }

    private static List<long> CSwitchTimesUs()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var times = new List<long>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadCSwitch += data =>
                times.Add((long)(data.TimeStampRelativeMSec * 1000));
        });
        return times;
    }

    private static ThreadAnalysisScope ScopeFor(
        ThreadInstanceKey thread,
        long startUs,
        long endUs)
    {
        var process = new ProcessLifetime(
            thread.Process, endUs + 100,
            StartObserved: true, EndObserved: true);
        var lifetime = new ThreadLifetime(
            thread, thread.Process.StartUs, endUs + 50,
            StartObserved: true, EndObserved: true);
        return new ThreadAnalysisScope(
            new TimeWindow(startUs, endUs),
            thread.Process.Pid,
            process,
            lifetime,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
    }

    private static ThreadInstanceKey Thread(
        int pid,
        long processStartUs,
        int tid,
        long generation) =>
        new(new ProcessInstanceKey(pid, processStartUs), tid, generation);
}
