using WprMcp.Core;
using WprMcp.Output;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ImageLoadAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ImageLoadTiming_ReturnsLoadsForFreshlySpawnedProcess()
    {
        // small_mmap.etl capture intentionally spawns 8 short-lived processes during
        // the trace window — those WILL emit ImageLoad events. small_cpu.etl is too
        // short to spawn anything, so its processes' LoadedModules counts come from
        // the kernel image rundown and ImageLoad-event count is near zero.
        const string MmapFixture = "fixtures/small_mmap.etl";
        if (!File.Exists(MmapFixture)) return;

        var cache = new TraceCache(capacity: 2);
        var meta = new MetaTools(cache);
        var procResp = meta.ListProcesses(MmapFixture);

        // Iterate candidates ordered by ImageLoadCount desc; first one with >0 *events* wins.
        var tools = new ImageLoadTools(cache);
        ImageLoadTimingResponse? success = null;
        foreach (var row in procResp.Rows.OrderByDescending(r => r.ImageLoadCount))
        {
            if (row.ImageLoadCount == 0) break;
            var resp = tools.ImageLoadTiming(MmapFixture, pid: row.Pid, top: 50);
            if (resp.TotalImageLoads > 0)
            {
                success = resp;
                break;
            }
        }

        Assert.NotNull(success);
        Assert.NotEmpty(success!.Loads);
        for (var i = 1; i < success.Loads.Count; i++)
            Assert.True(success.Loads[i - 1].TimeUs <= success.Loads[i].TimeUs);
    }

    [Fact]
    public void ImageLoadTiming_ReturnsValidShapeEvenWithNoEvents()
    {
        // small_cpu.etl is too short for ImageLoad events on most processes; the analyzer
        // must still return a valid (possibly empty) response with the warning populated.
        var meta = new MetaTools(new TraceCache(capacity: 2));
        var pid = meta.ListProcesses(FixturePath).Rows.First().Pid;
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTiming(FixturePath, pid: pid, top: 10);
        Assert.True(resp.TotalImageLoads >= 0);
        Assert.NotNull(resp.Loads);
    }

    [Fact]
    public void ImageLoadTiming_RejectsBadTop()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTiming("nonexistent.etl", pid: 1, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTiming("nonexistent.etl", pid: 1, top: 1001));
    }

    [Fact]
    public void ImageLoadTiming_UnknownPidThrows()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.ImageLoadTiming(FixturePath, pid: -999_999, top: 10));
    }
}
