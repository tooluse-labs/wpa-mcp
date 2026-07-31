using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class FileIoStackAnalysisTests
{
    private const string FileIoFixture = "fixtures/small_fileio.etl"; // captured with FileIO keyword

    [Fact]
    public void FileIoTopStacks_OnFileIoFixtureReturnsRows()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 30);
        Assert.NotEmpty(resp.Rows);
        Assert.True(resp.TotalBytes > 0);
        Assert.True(resp.TotalOpCount > 0);
    }

    [Fact]
    public void FileIoTopStacks_PctOfTraceReflectsCallerFilterIntent()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));

        var unfiltered = tools.FileIoTopStacks(FileIoFixture, top: 30);
        var filtered = tools.FileIoTopStacks(
            FileIoFixture,
            top: 30,
            startUs: 0);

        Assert.NotEmpty(unfiltered.Rows);
        Assert.All(unfiltered.Rows, row =>
        {
            Assert.Null(row.ExclusivePctOfTrace);
            Assert.Null(row.InclusivePctOfTrace);
        });
        Assert.NotEmpty(filtered.Rows);
        Assert.All(filtered.Rows, row =>
        {
            Assert.NotNull(row.ExclusivePctOfTrace);
            Assert.NotNull(row.InclusivePctOfTrace);
        });
    }

    [Fact]
    public void FileIoTopStacks_RowsOrderedByExclusiveBytesDesc()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveBytes >= resp.Rows[i].ExclusiveBytes);
    }

    [Fact]
    public void FileIoTopStacks_OpCountTracksAlongsideBytes()
    {
        // CallTree.ExclusiveCount gives op count "for free" alongside metric=IoSize bytes.
        // For any row with bytes > 0, op count must be > 0 — same one-to-one relationship as
        // PageFault's ExclusiveFaultCount.
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 20);
        Assert.All(resp.Rows.Where(r => r.ExclusiveBytes > 0),
            r => Assert.True(r.ExclusiveOpCount > 0,
                $"row {r.Function} has bytes>0 but opCount=0"));
    }

    [Fact]
    public void FileIoTopStacks_RejectsBadInput()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopStacks("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopStacks("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopStacks("nonexistent.etl", whenBuckets: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopStacks("nonexistent.etl", whenBuckets: 1001));
    }

    [Fact]
    public void FileIoTopStacks_WhenBucketsPopulatesHistogram()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 10, whenBuckets: 20);
        if (resp.TotalBytes == 0) return;
        Assert.NotNull(resp.When);
        Assert.Equal(20, resp.When!.Buckets.Length);
        Assert.True(resp.When.Buckets.Sum() <= resp.TotalBytes);
    }

    [Fact]
    public void FileIoTopStacks_DefaultsToFastSymbolSkippedMode()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 10);

        Assert.Contains(resp.Warnings, w => w.Contains("symbol resolution skipped", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FileIoCallerCallee_ReturnsExpectedShapeForFrameInTrace()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var topResp = tools.FileIoTopStacks(FileIoFixture, top: 5);
        if (topResp.Rows.Count == 0) return;
        var picked = topResp.Rows[0].Function;

        var ccResp = tools.FileIoCallerCallee(FileIoFixture, function: picked, top: 10);
        Assert.Equal(picked, ccResp.FocusFunction);
        Assert.Equal("ioBytes", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0);
    }

    [Fact]
    public void FileIoCallerCallee_RejectsBadInput()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.FileIoCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentException>(() =>
            tools.FileIoCallerCallee("nonexistent.etl", function: "", top: 10));
    }

    [Fact]
    public void FileIoTopStacks_OnTraceWithoutFileIoEmitsKeywordWarning()
    {
        // small_cpu.etl was captured with CPU.light, which omits FileIO. Should yield no rows
        // and a warning that points at the missing keyword.
        const string CpuFixture = "fixtures/small_cpu.etl";
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(CpuFixture, top: 10);
        if (resp.TotalOpCount == 0)
            Assert.Contains(resp.Warnings,
                w => w.Contains("FileIO", StringComparison.OrdinalIgnoreCase));
    }
}
