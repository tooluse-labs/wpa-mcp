using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class BlockedTimeStackAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void BlockedProjection_ResumeOutsideWindow_UsesSwitchOutBlockingStackAndClippedMetric()
    {
        var result = BlockedTimeStackAnalysis.ProjectSynthetic(
            switchOutUs: 90,
            resumeUs: 210,
            window: new TimeWindow(100, 200),
            blockingStack: (CallStackIndex)11,
            ordinaryResumeStack: (CallStackIndex)22);

        Assert.Equal(100, result.TotalBlockedUs);
        var sample = Assert.Single(result.Samples);
        Assert.Equal(100, sample.MetricUs);
        Assert.Equal((CallStackIndex)11, sample.SourceStack);
        Assert.Equal((CallStackIndex)22, sample.OrdinaryResumeStack);
        Assert.NotEqual(sample.OrdinaryResumeStack, sample.SourceStack);
    }

    [Fact]
    public void WaitTopStacks_ReturnsRowsOrEmitsKeywordWarning()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 20);
        // Same contract as WaitAnalysis: the small_cpu.etl fixture may or may not include
        // CSwitch on a given OS build. Either we get rows OR we get a clear warning.
        if (resp.Rows.Count == 0)
            Assert.Contains(resp.Warnings,
                w => w.Contains("CSwitch", StringComparison.OrdinalIgnoreCase) ||
                     w.Contains("no blocked-time samples", StringComparison.OrdinalIgnoreCase));
        else
            Assert.True(resp.TotalBlockedUs > 0);
    }

    [Fact]
    public void WaitTopStacks_RowsOrderedByExclusiveBlockedDesc()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveBlockedUs >= resp.Rows[i].ExclusiveBlockedUs);
    }

    [Fact]
    public void WaitTopStacks_EmitsResolutionStats()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 10);
        Assert.True(resp.Stats.ResolutionRate >= 0.0 && resp.Stats.ResolutionRate <= 1.0);
    }

    [Fact]
    public void WaitTopStacks_RejectsBadTop()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.WaitTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.WaitTopStacks("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void WaitTopStacks_ExclusivePctSumsCloseTo100()
    {
        // PerfView-parity sanity: every leaf's exclusive samples sum to ~100% of the total.
        // Same invariant CpuAnalysis relies on; if we accidentally drop or double-count blocked
        // time, this catches it.
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 1000);
        if (resp.Rows.Count == 0) return;

        // ExclusivePct over filtered total. With top=1000 we capture essentially every leaf
        // that contributes meaningfully, so the sum should be very close to 100.
        var sum = resp.Rows.Sum(r => r.ExclusivePct);
        Assert.InRange(sum, 95.0, 100.5);
    }

    [Fact]
    public void WaitTopStacks_NoStackSamplesAttributedToSyntheticRoot()
    {
        // PerfView-parity invariant #1 (mirrors CpuAnalysis): samples without a callstack must
        // land on the "?!?" synthetic root, not be silently dropped. We can't directly assert
        // "?!?" appears (it might not, on a high-quality stackwalk fixture), but we CAN assert
        // that no row has a clearly-bogus name like an empty string.
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 50);
        Assert.All(resp.Rows, r => Assert.False(string.IsNullOrEmpty(r.Function)));
    }

    [Fact]
    public void WaitTopStacks_WhenBucketsPopulatesHistogram()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var resp = tools.WaitTopStacks(FixturePath, top: 10, whenBuckets: 20);
        if (resp.TotalBlockedUs == 0) return; // no CSwitch resumes, nothing to bucket
        Assert.NotNull(resp.When);
        Assert.Equal(20, resp.When!.Buckets.Length);
        Assert.True(resp.When.BucketWidthUs > 0);
        Assert.Equal(resp.TotalBlockedUs, resp.When.Buckets.Sum());
    }

    [Fact]
    public void WaitTopStacks_RejectsBadWhenBuckets()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.WaitTopStacks("nonexistent.etl", whenBuckets: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.WaitTopStacks("nonexistent.etl", whenBuckets: 1001));
    }

    [Fact]
    public void WaitCallerCallee_ReturnsExpectedShapeForFrameInTrace()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var topResp = tools.WaitTopStacks(FixturePath, top: 5);
        if (topResp.Rows.Count == 0) return; // fixture lacks CSwitch stacks
        // Use whatever frame is present at top — even ?!? is valid for shape verification.
        var picked = topResp.Rows[0].Function;

        var ccResp = tools.WaitCallerCallee(FixturePath, function: picked, top: 10);
        Assert.Equal(picked, ccResp.FocusFunction);
        Assert.Equal("blockedUs", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0);
        Assert.Equal(topResp.TotalBlockedUs, ccResp.SourceTotalMetric);
        Assert.Equal(
            topResp.UnmatchedBlockedIntervalCount,
            ccResp.UnmatchedIntervalCount);
    }

    [Fact]
    public void WaitCallerCallee_RejectsBadInput()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.WaitCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentException>(() =>
            tools.WaitCallerCallee("nonexistent.etl", function: "", top: 10));
    }

    [Fact]
    public void WaitTopStacks_PidFilterIsRespected()
    {
        // When we filter by a pid, total blocked μs in the filtered output should be
        // <= unfiltered total. (Strictly less in any non-degenerate case.)
        var tools = new WaitTools(new TraceCache(capacity: 2));
        var unfiltered = tools.WaitTopStacks(FixturePath, top: 100);
        if (unfiltered.Rows.Count == 0) return;

        var meta = new MetaTools(new TraceCache(capacity: 2));
        var firstNonResidentPid = meta.ListProcesses(FixturePath, top: 100).Rows
            .FirstOrDefault(r => !r.TraceResident && r.CpuUs > 0)?.Pid;
        if (firstNonResidentPid is null) return;

        var filtered = tools.WaitTopStacks(FixturePath, top: 100, pid: firstNonResidentPid);
        Assert.True(filtered.TotalBlockedUs <= unfiltered.TotalBlockedUs);
    }
}
