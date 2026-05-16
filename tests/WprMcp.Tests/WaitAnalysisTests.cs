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
    public void WaitAnalysis_WindowedPidFilterSurvivesOutOfWindowTidReuse()
    {
        var accumulator = new WaitAnalysisAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.Process(new WaitAnalysisSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            TimeStampRelativeMSec: 90));
        accumulator.Process(new WaitAnalysisSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            TimeStampRelativeMSec: 120));
        accumulator.Process(new WaitAnalysisSwitchEvent(
            OldProcessId: 200,
            OldProcessName: "other",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            TimeStampRelativeMSec: 220));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(100, row.Pid);
        Assert.Equal("target", row.ProcessName);
        Assert.Equal(42, row.Tid);
        Assert.Equal(30_000, row.BlockedUs);
        Assert.Equal(1, resp.TotalCSwitches);
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
}
