using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class FileIoStackAnalysisTests
{
    private const string FileIoFixture = "fixtures/small_fileio.etl"; // captured with FileIO keyword

    [Fact]
    public void FileIoTopStacks_OnFileIoFixtureReportsEventsAndTruthfulStackAvailability()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(FileIoFixture, top: 30);
        Assert.True(resp.TotalBytes > 0);
        Assert.True(resp.TotalOpCount > 0);
        var coverage = Assert.IsType<WpaMcp.Output.DomainStackCoverage>(resp.StackCoverage);
        Assert.Equal("file_io", coverage.Domain);
        Assert.Equal("bytes", coverage.MetricName);
        Assert.Equal(resp.TotalOpCount, coverage.TotalEventCount);
        Assert.Equal(resp.TotalBytes, coverage.TotalMetric);
        if (resp.Rows.Count == 0)
        {
            Assert.Equal("no_stacks", coverage.CoverageState);
            Assert.Equal("unavailable", resp.CapabilityStatus);
            Assert.Equal("stacks_unavailable", resp.NoDataReason);
        }
        else
        {
            Assert.True(coverage.StackedEventCount > 0);
        }
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

        if (unfiltered.Rows.Count == 0 || filtered.Rows.Count == 0)
        {
            Assert.Equal("no_stacks", unfiltered.StackCoverage!.CoverageState);
            Assert.Equal("no_stacks", filtered.StackCoverage!.CoverageState);
            Assert.Equal("stacks_unavailable", unfiltered.NoDataReason);
            Assert.Equal("stacks_unavailable", filtered.NoDataReason);
            return;
        }
        Assert.All(unfiltered.Rows, row =>
        {
            Assert.Null(row.ExclusivePctOfTrace);
            Assert.Null(row.InclusivePctOfTrace);
        });
        Assert.All(filtered.Rows, row =>
        {
            Assert.NotNull(row.ExclusivePctOfTrace);
            Assert.NotNull(row.InclusivePctOfTrace);
        });
    }

    [Fact]
    public void FileIoTopStacks_EmptyWindowDoesNotClaimEventClassAbsentFromTrace()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new IoTools(cache);
        var wholeTrace = tools.FileIoTopStacks(FileIoFixture, top: 5, whenBuckets: 1000);
        var histogram = Assert.IsType<WpaMcp.Output.TimeHistogram>(wholeTrace.When);
        var emptyBucket = Array.FindIndex(histogram.Buckets, value => value == 0);
        Assert.True(emptyBucket >= 0, "fixture must contain an empty FileIO histogram bucket");
        var startUs = checked(histogram.StartUs + emptyBucket * histogram.BucketWidthUs);
        var endUs = Math.Min(histogram.EndUs, checked(startUs + histogram.BucketWidthUs));

        var scoped = tools.FileIoTopStacks(
            FileIoFixture,
            top: 5,
            startUs: startUs,
            endUs: endUs);

        Assert.True(wholeTrace.MatchedEventCount > 0);
        Assert.Equal(0, scoped.MatchedEventCount);
        Assert.Equal("unknown", scoped.CapabilityStatus);
        Assert.Equal("no_events_in_scope", scoped.NoDataReason);
        Assert.DoesNotContain(scoped.Warnings, warning => warning.StartsWith(
            "event_class_not_observed:", StringComparison.Ordinal));
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
        Assert.NotNull(ccResp.StackCoverage);
        Assert.Equal(topResp.StackCoverage, ccResp.StackCoverage);
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
        // An empty event family cannot prove how the trace was configured.
        const string CpuFixture = "fixtures/small_cpu.etl";
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopStacks(CpuFixture, top: 10);
        if (resp.TotalOpCount == 0)
            Assert.Contains(resp.Warnings, w =>
                w.Contains("FileIO", StringComparison.OrdinalIgnoreCase) &&
                w.Contains("does not prove", StringComparison.OrdinalIgnoreCase));
    }
}
