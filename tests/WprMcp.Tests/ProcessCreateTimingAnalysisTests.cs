using WprMcp.Core;
using WprMcp.Analyzers;
using WprMcp.Output;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class ProcessCreateTimingAnalysisTests
{
    // small_mmap.etl spawns 8 short-lived processes from a known parent — the only fixture
    // with definite parent-child relationships in the trace window.
    private const string MmapFixture = "fixtures/small_mmap.etl";

    [Fact]
    public void ProcessCreateTiming_FindsChildrenForKnownSpawner()
    {
        if (!File.Exists(MmapFixture)) return;

        // Pick the parent dynamically — find ANY process that spawned at least 1 child during
        // the trace. Avoids hard-coding a PID that varies between captures.
        var meta = new MetaTools(new TraceCache(capacity: 2));
        var procResp = meta.ListProcesses(MmapFixture, top: 1000);
        var spawner = procResp.Rows
            .GroupBy(r => r.ParentPid)
            .Where(g => g.Key > 0 && procResp.Rows.Any(r => r.Pid == g.Key))
            .Where(g => g.Count(child => child.StartUs > 0) >= 1)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
        if (spawner == 0) return;

        var resp = meta.ProcessCreateTiming(MmapFixture, parentPid: spawner, top: 50);
        Assert.Equal(spawner, resp.ParentPid);
        Assert.True(resp.SpawnCount > 0);
        Assert.NotEmpty(resp.Children);
        // Children must be in chronological order.
        for (var i = 1; i < resp.Children.Count; i++)
            Assert.True(resp.Children[i - 1].StartTimeUs <= resp.Children[i].StartTimeUs);
        // First child's GapFromPreviousSpawnUs is null by definition.
        Assert.Null(resp.Children[0].GapFromPreviousSpawnUs);
        // Aggregates are populated when at least one child loaded a DLL during the trace.
        if (resp.Children.Any(c => c.FirstImageLoadOffsetUs.HasValue))
        {
            Assert.NotNull(resp.MedianKernelGapUs);
            Assert.NotNull(resp.MaxKernelGapUs);
            Assert.True(resp.MaxKernelGapUs >= resp.MedianKernelGapUs);
        }
    }

    [Fact]
    public void ProcessCreateTiming_UnknownParentReturnsEmptyWithWarning()
    {
        if (!File.Exists(MmapFixture)) return;
        var meta = new MetaTools(new TraceCache(capacity: 2));
        var resp = meta.ProcessCreateTiming(MmapFixture, parentPid: 999_999, top: 100);
        Assert.Equal(0, resp.SpawnCount);
        Assert.Empty(resp.Children);
        Assert.Contains(resp.Warnings, w => w.Contains("No children found", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProcessCreateTiming_WarnsOnSlowFirstImageLoadGaps()
    {
        var rows = new[]
        {
            new ChildSpawnTiming(
                Pid: 100,
                Name: "child-a",
                StartTimeUs: 10_000,
                FirstImageLoadOffsetUs: ProcessCreateTimingAnalysis.VerySlowFirstImageLoadGapUs,
                ImageLoadCount: 4,
                GapFromPreviousSpawnUs: null),
            new ChildSpawnTiming(
                Pid: 101,
                Name: "child-b",
                StartTimeUs: 20_000,
                FirstImageLoadOffsetUs: ProcessCreateTimingAnalysis.SlowFirstImageLoadGapUs,
                ImageLoadCount: 3,
                GapFromPreviousSpawnUs: 10_000),
            new ChildSpawnTiming(
                Pid: 102,
                Name: "child-c",
                StartTimeUs: 30_000,
                FirstImageLoadOffsetUs: ProcessCreateTimingAnalysis.SlowFirstImageLoadGapUs - 1,
                ImageLoadCount: 3,
                GapFromPreviousSpawnUs: 10_000)
        };
        var warnings = new List<string>();

        ProcessCreateTimingAnalysis.AddKernelGapWarnings(rows, warnings);

        var warning = Assert.Single(warnings);
        Assert.Contains("2 very slow child process first-image-load gap", warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("child-a(100)=5000ms", warning, StringComparison.Ordinal);
        Assert.Contains("child-b(101)=1000ms", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("child-c", warning, StringComparison.Ordinal);
        Assert.Contains("AV/EDR", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessCreateTiming_RejectsBadInput()
    {
        var meta = new MetaTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 0, top: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: -1, top: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 1, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 1, top: 1001));
    }
}
