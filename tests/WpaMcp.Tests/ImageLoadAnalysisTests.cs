using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

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
        var procResp = meta.ListProcesses(MmapFixture, top: 1000);

        // Iterate candidates ordered by ImageLoadCount desc; first one with >0 *events* wins.
        var tools = new ImageLoadTools(cache);
        ImageLoadTimingResponse? success = null;
        foreach (var row in procResp.Rows.OrderByDescending(r => r.ImageLoadCount))
        {
            if (row.ImageLoadCount == 0) break;
            var resp = tools.ImageLoadTiming(MmapFixture, pid: row.Pid, pageSize: 50);
            if (resp.TotalImageLoads > 0)
            {
                success = resp;
                break;
            }
        }

        Assert.NotNull(success);
        Assert.NotEmpty(success!.Loads);
        Assert.Equal(success.TotalImageLoads, success.MatchedEventCount);
        Assert.Equal("observed", success.CapabilityStatus);
        Assert.Equal("ok", success.ScopeStatus);
        Assert.Null(success.NoDataReason);
        for (var i = 1; i < success.Loads.Count; i++)
            Assert.True(success.Loads[i - 1].TimeUs <= success.Loads[i].TimeUs);
    }

    [Fact]
    public void ImageLoadTiming_PopulatesGapsAndFirstLoadOffset()
    {
        const string MmapFixture = "fixtures/small_mmap.etl";
        if (!File.Exists(MmapFixture)) return;

        var cache = new TraceCache(capacity: 2);
        var meta = new MetaTools(cache);
        var procResp = meta.ListProcesses(MmapFixture, top: 1000);
        var tools = new ImageLoadTools(cache);

        ImageLoadTimingResponse? hit = null;
        foreach (var row in procResp.Rows.OrderByDescending(r => r.ImageLoadCount))
        {
            if (row.ImageLoadCount == 0) break;
            var resp = tools.ImageLoadTiming(MmapFixture, pid: row.Pid, pageSize: 200);
            if (resp.TotalImageLoads >= 2) { hit = resp; break; }
        }
        Assert.NotNull(hit);
        // First load row keeps GapFromPrevUs=null; subsequent rows have it populated.
        Assert.Null(hit!.Loads[0].GapFromPrevUs);
        Assert.NotNull(hit.Loads[1].GapFromPrevUs);
        // Gap is non-negative (loads are chronologically ordered).
        Assert.True(hit.Loads[1].GapFromPrevUs!.Value >= 0);
        // FirstLoadOffsetUs == first row's TimeFromProcessStartUs.
        Assert.Equal(hit.Loads[0].TimeFromProcessStartUs, hit.FirstLoadOffsetUs);
        // MaxGapUs == max of all populated gaps.
        var expectedMax = hit.Loads.Skip(1).Max(r => r.GapFromPrevUs!.Value);
        Assert.Equal(expectedMax, hit.MaxGapUs);
    }

    [Fact]
    public void ImageLoadTopGaps_ReturnsSortedDescending()
    {
        const string MmapFixture = "fixtures/small_mmap.etl";
        if (!File.Exists(MmapFixture)) return;

        var cache = new TraceCache(capacity: 2);
        var meta = new MetaTools(cache);
        var procResp = meta.ListProcesses(MmapFixture, top: 1000);
        var tools = new ImageLoadTools(cache);

        ImageLoadTopGapsResponse? hit = null;
        foreach (var row in procResp.Rows.OrderByDescending(r => r.ImageLoadCount))
        {
            if (row.ImageLoadCount == 0) break;
            var resp = tools.ImageLoadTopGaps(MmapFixture, pid: row.Pid, top: 10);
            if (resp.TopGaps.Count >= 2) { hit = resp; break; }
        }
        Assert.NotNull(hit);
        for (var i = 1; i < hit!.TopGaps.Count; i++)
            Assert.True(hit.TopGaps[i - 1].GapFromPrevUs >= hit.TopGaps[i].GapFromPrevUs);
        Assert.All(hit.TopGaps, r => Assert.NotNull(r.GapFromPrevUs));
    }

    [Fact]
    public void ImageLoadTopGaps_RejectsBadTop()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.ImageLoadTopGaps("nonexistent.etl", pid: 1, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.ImageLoadTopGaps("nonexistent.etl", pid: 1, top: 1001));
    }

    [Fact]
    public void ImageLoadTopGaps_UnknownPidReturnsStructuredEmptyResponse()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var response = tools.ImageLoadTopGaps(
            FixturePath, pid: 999_999, top: 10, processStartUs: 123);

        Assert.Empty(response.TopGaps);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void ImageLoadTiming_ReturnsValidShapeEvenWithNoEvents()
    {
        // small_cpu.etl is too short for ImageLoad events on most processes; the analyzer
        // must still return a valid (possibly empty) response with the warning populated.
        var meta = new MetaTools(new TraceCache(capacity: 2));
        var pid = meta.ListProcesses(FixturePath).Rows.First().Pid;
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var resp = tools.ImageLoadTiming(FixturePath, pid: pid, pageSize: 10);
        Assert.True(resp.TotalImageLoads >= 0);
        Assert.NotNull(resp.Loads);
    }

    [Fact]
    public void ImageLoadTiming_RejectsBadTop()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTiming("nonexistent.etl", pid: 1, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ImageLoadTiming("nonexistent.etl", pid: 1, pageSize: 1001));
    }

    [Fact]
    public void ImageLoadTiming_UnknownPidReturnsStructuredEmptyResponse()
    {
        var tools = new ImageLoadTools(new TraceCache(capacity: 2));
        var response = tools.ImageLoadTiming(
            FixturePath, pid: 999_999, pageSize: 10, processStartUs: 123);

        Assert.Empty(response.Loads);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void ProjectLoads_ExcludesEventsFromReusedPidLifetime()
    {
        var selected = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 42, StartUs: 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var observations = new[]
        {
            new ImageLoadObservation(42, 110, "first.dll", 1),
            new ImageLoadObservation(42, 190, "second.dll", 2),
            new ImageLoadObservation(42, 210, "reused.dll", 3),
            new ImageLoadObservation(99, 150, "other.dll", 4),
        };

        var loads = ImageLoadAnalysis.ProjectLoads(observations, selected);

        Assert.Collection(
            loads,
            row =>
            {
                Assert.Equal("first.dll", row.FileName);
                Assert.Equal(10, row.TimeFromProcessStartUs);
                Assert.Null(row.GapFromPrevUs);
            },
            row =>
            {
                Assert.Equal("second.dll", row.FileName);
                Assert.Equal(90, row.TimeFromProcessStartUs);
                Assert.Equal(80, row.GapFromPrevUs);
            });
    }

    [Fact]
    public void SelectProcessInstance_RequiresStartForReusedPid()
    {
        var lifetimes = new[]
        {
            new ProcessLifetime(new ProcessInstanceKey(42, 100), 200, true, true),
            new ProcessLifetime(new ProcessInstanceKey(42, 300), 400, true, true),
        };

        var error = Assert.Throws<ArgumentException>(() =>
            ImageLoadAnalysis.SelectProcessInstance(lifetimes, pid: 42, processStartUs: null));

        Assert.Contains("process_start_required", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("ambiguous_process_instance", error.Message, StringComparison.Ordinal);
        Assert.Contains("100", error.Message, StringComparison.Ordinal);
        Assert.Contains("300", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            new ProcessInstanceKey(42, 300),
            ImageLoadAnalysis.SelectProcessInstance(
                lifetimes, pid: 42, processStartUs: 300).Key);
    }

    [Fact]
    public void ProjectLoads_InferredProcessStartSuppressesStartupRelativeOffsets()
    {
        var inferred = new ProcessLifetime(
            new ProcessInstanceKey(42, 100),
            EndUs: 200,
            StartObserved: false,
            EndObserved: false);
        var loads = ImageLoadAnalysis.ProjectLoads(
            [new ImageLoadObservation(42, 110, "a.dll", 1, EventIndex: 7)],
            inferred);

        var row = Assert.Single(loads);
        Assert.Equal(110, row.TimeUs);
        Assert.Null(row.TimeFromProcessStartUs);
    }

    [Fact]
    public void ProjectLoads_UsesEventIndexToOrderDuplicateTimestamps()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(42, 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var loads = ImageLoadAnalysis.ProjectLoads(
            [
                new ImageLoadObservation(42, 110, "later.dll", 1, EventIndex: 9),
                new ImageLoadObservation(42, 110, "earlier.dll", 1, EventIndex: 3),
            ],
            process);

        Assert.Equal([3L, 9L], loads.Select(row => row.EventIndex));
        Assert.Equal(["earlier.dll", "later.dll"], loads.Select(row => row.FileName));
    }

    [Fact]
    public void TopGaps_UsesTimeAndEventIndexAsStableTieBreakers()
    {
        var rows = ImageLoadAnalysis.RankTopGaps(
            [
                new ImageLoadRow(300, 300, "time-later.dll", 1, 50, EventIndex: 1),
                new ImageLoadRow(200, 200, "event-later.dll", 1, 50, EventIndex: 9),
                new ImageLoadRow(200, 200, "event-earlier.dll", 1, 50, EventIndex: 3),
                new ImageLoadRow(400, 400, "largest-gap.dll", 1, 60, EventIndex: 12),
                new ImageLoadRow(100, 100, "no-gap.dll", 1, null, EventIndex: 0),
            ],
            top: 10);

        Assert.Equal(
            ["largest-gap.dll", "event-earlier.dll", "event-later.dll", "time-later.dll"],
            rows.Select(row => row.FileName));
    }
}
