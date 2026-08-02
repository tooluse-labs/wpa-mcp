using System.ComponentModel;
using System.Reflection;
using WpaMcp.Core;
using WpaMcp.Analyzers;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

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

        var resp = meta.ProcessCreateTiming(MmapFixture, parentPid: spawner, pageSize: 50);
        Assert.Equal(spawner, resp.ParentPid);
        Assert.True(resp.SpawnCount > 0);
        Assert.NotEmpty(resp.Children);
        Assert.True(resp.MatchedEventCount > 0);
        Assert.True(resp.MatchedEventCount <= resp.SpawnCount);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Null(resp.NoDataReason);
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
        var resp = meta.ProcessCreateTiming(MmapFixture, parentPid: 999_999, pageSize: 100);
        Assert.Equal(0, resp.SpawnCount);
        Assert.Empty(resp.Children);
        Assert.Contains(resp.Warnings, w =>
            w.StartsWith("scope_not_found:", StringComparison.Ordinal));
        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("scope_not_found", resp.NoDataReason);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal(0, resp.MatchedEventCount);
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
        Assert.Contains("does not identify", warning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("usually points to", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProcessCreateTiming_RejectsBadInput()
    {
        var meta = new MetaTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 0, pageSize: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: -1, pageSize: 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 1, pageSize: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            meta.ProcessCreateTiming("nonexistent.etl", parentPid: 1, pageSize: 1001));
    }

    [Fact]
    public void ProcessCreateTiming_ToolDescriptionDoesNotAttributeKernelGapToAv()
    {
        var method = typeof(MetaTools).GetMethod(nameof(MetaTools.ProcessCreateTiming));
        var description = method?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.NotNull(description);
        Assert.Contains("does not identify", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("burn time", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChildBelongsToParentInstance_RejectsChildFromReusedParentLifetime()
    {
        var selectedParent = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 42, StartUs: 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);

        Assert.True(ProcessCreateTimingAnalysis.ChildBelongsToParentInstance(
            selectedParent, observedParentPid: 42, childStartUs: 150));
        Assert.False(ProcessCreateTimingAnalysis.ChildBelongsToParentInstance(
            selectedParent, observedParentPid: 42, childStartUs: 350));
        Assert.False(ProcessCreateTimingAnalysis.ChildBelongsToParentInstance(
            selectedParent, observedParentPid: 99, childStartUs: 150));
    }

    [Fact]
    public void TimingEstimators_HaveDeclaredEvenMedianMeanRoundingAndNearestRankBoundaries()
    {
        Assert.Equal(2, ProcessCreateTimingAnalysis.RoundedMean([1, 2]));
        Assert.Equal(2, ProcessCreateTimingAnalysis.MedianRounded([1, 2]));
        Assert.Equal(2, ProcessCreateTimingAnalysis.NearestRank(
            [1, 2], numerator: 95, denominator: 100));

        var twenty = Enumerable.Range(1, 20).Select(value => (long)value).ToArray();
        Assert.Equal(11, ProcessCreateTimingAnalysis.MedianRounded(twenty));
        Assert.Equal(19, ProcessCreateTimingAnalysis.NearestRank(
            twenty, numerator: 95, denominator: 100));
    }

    [Fact]
    public void ProcessCreateTiming_ReturnsOnlyObservedChildStarts()
    {
        if (!File.Exists(MmapFixture)) return;
        var cache = new TraceCache(capacity: 2);
        var meta = new MetaTools(cache);
        var inventory = meta.ListProcesses(MmapFixture, top: 1000).Rows;
        var spawner = inventory
            .Where(row => row.ParentPid > 0 && row.ProcessStartObserved)
            .GroupBy(row => row.ParentPid)
            .Select(group => group.Key)
            .FirstOrDefault(parentPid => inventory.Any(row => row.Pid == parentPid));
        if (spawner == 0) return;

        var response = meta.ProcessCreateTiming(
            MmapFixture,
            spawner,
            pageSize: 1000);
        var observedKeys = inventory
            .Where(row => row.ParentPid == spawner && row.ProcessStartObserved)
            .Select(row => (row.Pid, row.StartUs))
            .ToHashSet();

        Assert.All(response.Children, child =>
            Assert.Contains((child.Pid, child.StartTimeUs), observedKeys));
        Assert.Equal(response.SpawnCount, response.MatchedEventCount);
        Assert.Equal(
            inventory.Count(row => row.ParentPid == spawner && !row.ProcessStartObserved),
            response.BackfilledChildrenExcluded);
    }
}
