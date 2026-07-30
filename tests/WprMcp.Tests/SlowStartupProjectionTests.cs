using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tests;

public sealed class SlowStartupProjectionTests
{
    [Fact]
    public void Rank_UsesStartupMetricsAndRequiredTieBreakers()
    {
        var p1 = Observation(pid: 8, startUs: 200, endUs: 400, lifetimeCpuUs: 1);
        var p2 = Observation(pid: 9, startUs: 100, endUs: 300, lifetimeCpuUs: 9_999);
        var p3 = Observation(pid: 7, startUs: 100, endUs: 300, lifetimeCpuUs: 2);
        var metrics = Metrics(
            (p1.Process, cpuUs: 20L),
            (p2.Process, cpuUs: 20L),
            (p3.Process, cpuUs: 20L));

        var ranked = SlowStartupProjection.Rank(
            [p1, p2, p3], metrics, EmptyImages(p1, p2, p3),
            nameSubstring: null, minWaitRatio: 0, maxCandidates: 3);

        Assert.Equal([7, 9, 8], ranked.Select(row => row.Process.Pid));
        Assert.All(ranked, row => Assert.Equal(10.0, row.StartupWaitRatio));
    }

    [Fact]
    public void Rank_ZeroStartupCpuHasNullRatioAndLifetimeValuesCannotRescueIt()
    {
        var process = Observation(
            pid: 8, startUs: 100, endUs: 200, lifetimeCpuUs: 1_000_000);
        var ranked = SlowStartupProjection.Rank(
            [process], Metrics((process.Process, cpuUs: 0L)), EmptyImages(process),
            nameSubstring: null, minWaitRatio: 0, maxCandidates: 5);

        Assert.Empty(ranked);
        Assert.Null(SlowStartupProjection.StartupWaitRatio(100, 0));
    }

    [Fact]
    public void EvidencePlan_FirstImageChildIsHalfOpenAndInsideParent()
    {
        var candidate = Candidate(
            pid: 5, processStartUs: 100, startupEndUs: 500,
            firstImageLoadUs: 350);

        var plan = SlowStartupProjection.PlanEvidence(
            candidate, slowFirstImageLoadThresholdUs: 200);

        Assert.Equal(new TimeWindow(100, 500), plan.ParentWindow);
        Assert.Equal(new TimeWindow(100, 350), plan.FirstImageChildWindow);
        Assert.True(plan.ParentWindow.ContainsPoint(
            plan.FirstImageChildWindow!.Value.StartUs));
        Assert.True(
            plan.FirstImageChildWindow.Value.EndUs <= plan.ParentWindow.EndUs);
    }

    [Fact]
    public void Rank_PostWindowSchedulerAndImageActivityCannotChangeResult()
    {
        var process = Observation(pid: 8, startUs: 100, endUs: 200, lifetimeCpuUs: 1);
        var thread = new ThreadInstanceKey(process.Process, 4, 1);
        var baselineMetrics = StartupMetricsAccumulator.Project(
            [process], [new RunningInterval(thread, 110, 130, 0)], []);
        var noisyMetrics = StartupMetricsAccumulator.Project(
            [process],
            [
                new RunningInterval(thread, 110, 130, 0),
                new RunningInterval(thread, 300, 900, 0),
            ],
            [new BlockedInterval(thread, 400, 800, "Executive")]);
        var baselineImages = StartupImageLoadAnalysis.Project(
            [new StartupImageLoadEvent(process.Process, 120, "first.dll", 1)],
            [process], maxRowsPerProcess: 10);
        var noisyImages = StartupImageLoadAnalysis.Project(
            [
                new StartupImageLoadEvent(process.Process, 120, "first.dll", 1),
                new StartupImageLoadEvent(process.Process, 700, "late.dll", 1),
            ],
            [process], maxRowsPerProcess: 10);

        var baseline = SlowStartupProjection.Rank(
            [process], baselineMetrics, baselineImages, null, 0, 1);
        var noisy = SlowStartupProjection.Rank(
            [process], noisyMetrics, noisyImages, null, 0, 1);

        var baselineCandidate = Assert.Single(baseline);
        var noisyCandidate = Assert.Single(noisy);
        Assert.Equal(baselineCandidate.Process, noisyCandidate.Process);
        Assert.Equal(
            baselineCandidate.ObservedStartupWallUs,
            noisyCandidate.ObservedStartupWallUs);
        Assert.Equal(baselineCandidate.StartupCpuUs, noisyCandidate.StartupCpuUs);
        Assert.Equal(
            baselineCandidate.StartupBlockedUs,
            noisyCandidate.StartupBlockedUs);
        Assert.Equal(
            baselineCandidate.StartupWaitRatio,
            noisyCandidate.StartupWaitRatio);
        Assert.Equal(
            baselineCandidate.StartupBlockedUsByReason.OrderBy(item => item.Key),
            noisyCandidate.StartupBlockedUsByReason.OrderBy(item => item.Key));
        Assert.Equal(
            baselineCandidate.StartupImageLoads,
            noisyCandidate.StartupImageLoads);
    }

    private static StartupProcessObservation Observation(
        int pid, long startUs, long endUs, long lifetimeCpuUs)
    {
        var process = new ProcessInstanceKey(pid, startUs);
        var lifetime = new ProcessLifetime(
            process, EndUs: endUs, StartObserved: true, EndObserved: true);
        return new StartupProcessObservation(
            new StartupProcessMetadata(
                lifetime, ParentPid: 1, Name: $"p{pid}.exe",
                LifetimeCpuUs: lifetimeCpuUs, LifetimeImageLoadCount: 99),
            new StartupWindow(
                process, new TimeWindow(startUs, endUs), RequestedEndUs: endUs,
                TraceDurationUs: endUs, ProcessStartObserved: true,
                ProcessEndObserved: true, Status: "Complete", Code: null));
    }

    private static IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Metrics(
        params (ProcessInstanceKey Process, long cpuUs)[] values) =>
        values.ToDictionary(
            item => item.Process,
            item => new StartupSchedulerMetrics(
                item.cpuUs, StartupBlockedUs: 0,
                new Dictionary<string, long>(StringComparer.Ordinal),
                RunningIntervalCount: 0, BlockedIntervalCount: 0));

    private static IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> EmptyImages(
        params StartupProcessObservation[] values) =>
        values.ToDictionary(
            item => item.Process,
            _ => new StartupImageLoadBucket(
                TotalAvailable: 0,
                FirstLoads: Array.Empty<ImageLoadRow>(),
                HasMore: false));

    private static SlowStartupCandidateData Candidate(
        int pid, long processStartUs, long startupEndUs, long firstImageLoadUs)
    {
        var observation = Observation(
            pid, processStartUs, startupEndUs, lifetimeCpuUs: 1);
        return new SlowStartupCandidateData(
            observation.Process,
            observation.Metadata.ParentPid,
            observation.Metadata.Name,
            observation.Window,
            ObservedStartupWallUs: observation.Window.Bounds.DurationUs,
            StartupCpuUs: 20,
            StartupBlockedUs: 0,
            StartupWaitRatio: observation.Window.Bounds.DurationUs / 20.0,
            new Dictionary<string, long>(StringComparer.Ordinal),
            StartupImageLoadCount: 1,
            StartupImageLoadsHasMore: false,
            [new ImageLoadRow(
                firstImageLoadUs,
                firstImageLoadUs - processStartUs,
                "first.dll",
                ImageSize: 1,
                GapFromPrevUs: null)],
            observation.LifetimeWallUs,
            observation.Metadata.LifetimeCpuUs,
            observation.LifetimeWaitRatio,
            observation.Metadata.LifetimeImageLoadCount);
    }
}
