using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class DiskIoAnalysisTests
{
    private const string FileIoFixture = "fixtures/small_fileio.etl";

    [Fact]
    public void DiskIoAnalysis_AccountsRequestsWithoutStacks()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var response = tools.DiskIoAnalysis(FileIoFixture, top: 20);
        var metrics = response.Summary.Metrics;

        Assert.Equal(metrics.ReadCount + metrics.WriteCount, metrics.TotalCount);
        Assert.Equal(metrics.ReadBytes + metrics.WriteBytes, metrics.TotalBytes);
        Assert.Equal(metrics.TotalCount, response.MatchedEventCount);
        Assert.Equal(metrics.TotalCount, response.Disks.Sum(row => row.Metrics.TotalCount));
        Assert.Equal(metrics.TotalBytes, response.Disks.Sum(row => row.Metrics.TotalBytes));
        Assert.DoesNotContain(response.Warnings ?? [], warning =>
            warning.Contains("stacks_unavailable", StringComparison.OrdinalIgnoreCase));

        if (metrics.TotalCount == 0)
        {
            Assert.Equal("event_class_not_observed", response.NoDataReason);
            Assert.Contains(response.Warnings ?? [], warning =>
                warning.Contains("DiskIO", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.Equal("observed", response.CapabilityStatus);
            Assert.Null(response.NoDataReason);
        }
    }

    [Fact]
    public void DiskIoAnalysis_TimelineIsCompleteAndBounded()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var response = tools.DiskIoAnalysis(FileIoFixture, bucketUs: 1);

        Assert.True(response.EffectiveBucketUs >= response.RequestedBucketUs);
        Assert.True(response.Timeline.Count <= 512);
        if (response.Summary.Metrics.TotalCount == 0)
        {
            Assert.Empty(response.Timeline);
            return;
        }

        Assert.NotEmpty(response.Timeline);
        Assert.Equal(response.Summary.StartUs, response.Timeline[0].StartUs);
        Assert.Equal(response.Summary.EndUs, response.Timeline[^1].EndUs);
        Assert.Equal(
            response.Summary.Metrics.TotalCount,
            response.Timeline.Sum(bucket => bucket.Metrics.TotalCount));
        Assert.Equal(
            response.Summary.Metrics.TotalBytes,
            response.Timeline.Sum(bucket => bucket.Metrics.TotalBytes));
    }

    [Fact]
    public void DiskIoAnalysis_SummaryOnlySuppressesBoundedSections()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var response = tools.DiskIoAnalysis(
            FileIoFixture,
            bucketUs: 1_000,
            summaryOnly: true);

        Assert.True(response.SummaryOnly);
        Assert.Empty(response.TopProcesses);
        Assert.Empty(response.TopFiles);
        Assert.Empty(response.Disks);
        Assert.Empty(response.Timeline);
        Assert.Equal(0, response.EffectiveBucketUs);
    }

    [Fact]
    public void DiskIoAnalysis_RejectsInvalidBoundsBeforeLoadingTrace()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiskIoAnalysis("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiskIoAnalysis("nonexistent.etl", bucketUs: -1));
    }
}
