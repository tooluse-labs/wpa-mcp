using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class DiskIoStackAnalysisTests
{
    // small_fileio.etl was captured with FileIO.light, which includes DiskIO. Use it for
    // disk-IO testing — small_cpu.etl's CPU.light profile excludes DiskIO entirely.
    private const string FileIoFixture = "fixtures/small_fileio.etl";

    [Fact]
    public void DiskIoTopStacks_OnFileIoFixtureReturnsRowsOrEmitsKeywordWarning()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.DiskIoTopStacks(FileIoFixture, top: 20);
        // Disk IO may be absent on a tiny fixture. The warning must not infer capture settings.
        if (resp.Rows.Count == 0)
            Assert.Contains(resp.Warnings, w =>
                w.Contains("stacks_unavailable", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("no_stacks", StringComparison.OrdinalIgnoreCase) ||
                w.Contains("DiskIO", StringComparison.OrdinalIgnoreCase) &&
                w.Contains("does not prove", StringComparison.OrdinalIgnoreCase));
        else
            Assert.True(resp.TotalBytes > 0);
        var coverage = Assert.IsType<WpaMcp.Output.DomainStackCoverage>(resp.StackCoverage);
        Assert.Equal("disk_io", coverage.Domain);
        Assert.Equal("bytes", coverage.MetricName);
        Assert.Equal(resp.TotalOpCount, coverage.TotalEventCount);
        Assert.Equal(resp.TotalBytes, coverage.TotalMetric);
    }

    [Fact]
    public void DiskIoTopStacks_RowsOrderedByExclusiveBytesDesc()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.DiskIoTopStacks(FileIoFixture, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveBytes >= resp.Rows[i].ExclusiveBytes);
    }

    [Fact]
    public void DiskIoTopStacks_OpCountTracksAlongsideBytes()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.DiskIoTopStacks(FileIoFixture, top: 20);
        Assert.All(resp.Rows.Where(r => r.ExclusiveBytes > 0),
            r => Assert.True(r.ExclusiveOpCount > 0,
                $"row {r.Function} has bytes>0 but opCount=0"));
    }

    [Fact]
    public void DiskIoTopStacks_RejectsBadInput()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiskIoTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiskIoTopStacks("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.DiskIoTopStacks("nonexistent.etl", whenBuckets: -1));
    }

    [Fact]
    public void DiskIoCallerCallee_ReturnsExpectedShapeForFrameInTrace()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var topResp = tools.DiskIoTopStacks(FileIoFixture, top: 5);
        if (topResp.Rows.Count == 0) return; // no disk IO on this fixture
        var picked = topResp.Rows[0].Function;

        var ccResp = tools.DiskIoCallerCallee(FileIoFixture, function: picked, top: 10);
        Assert.Equal(picked, ccResp.FocusFunction);
        Assert.Equal("diskBytes", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0);
        Assert.Equal(topResp.StackCoverage, ccResp.StackCoverage);
    }

    [Fact]
    public void DiskIoCallerCallee_RejectsBadInput()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiskIoCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentException>(() =>
            tools.DiskIoCallerCallee("nonexistent.etl", function: "", top: 10));
    }

    [Fact]
    public void DiskIoTopStacks_WhenBucketsPopulatesHistogram()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.DiskIoTopStacks(FileIoFixture, top: 10, whenBuckets: 20);
        if (resp.TotalBytes == 0) return;
        Assert.NotNull(resp.When);
        Assert.Equal(20, resp.When!.Buckets.Length);
        Assert.True(resp.When.Buckets.Sum() <= resp.TotalBytes);
    }
}
