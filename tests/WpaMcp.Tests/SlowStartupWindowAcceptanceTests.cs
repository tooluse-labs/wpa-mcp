using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class SlowStartupWindowAcceptanceTests
{
    [Fact]
    public void LaterLifetimeAndPostWindowSignalsCannotChangeEarlierCandidate()
    {
        var early = Metadata(
            33, 100, 250, startObserved: true, endObserved: true, "early.exe");
        var reused = Metadata(
            33, 300, 500, startObserved: true, endObserved: true, "later.exe");
        var catalog = StartupProcessCatalog.Build(
            [early, reused], startupWindowUs: 100, traceDurationUs: 500,
            nameSubstring: null, maxCollectionItems: Validation.MaxCollectionItems);
        var earlyThread = new ThreadInstanceKey(early.Lifetime.Key, 4, 1);
        var laterThread = new ThreadInstanceKey(reused.Lifetime.Key, 4, 1);
        var baselineMetrics = StartupMetricsAccumulator.Project(
            catalog.Eligible,
            [new RunningInterval(earlyThread, 110, 130, 0)],
            Array.Empty<BlockedInterval>());
        var noisyMetrics = StartupMetricsAccumulator.Project(
            catalog.Eligible,
            [
                new RunningInterval(earlyThread, 110, 130, 0),
                new RunningInterval(earlyThread, 220, 290, 0),
                new RunningInterval(laterThread, 320, 340, 0),
            ],
            [new BlockedInterval(laterThread, 350, 390, "Executive")]);
        var baselineImages = StartupImageLoadAnalysis.Project(
            [new StartupImageLoadEvent(early.Lifetime.Key, 120, "first.dll", 1)],
            catalog.Eligible, maxRowsPerProcess: 10);
        var noisyImages = StartupImageLoadAnalysis.Project(
            [
                new StartupImageLoadEvent(early.Lifetime.Key, 120, "first.dll", 1),
                new StartupImageLoadEvent(
                    early.Lifetime.Key, 240, "post-window.dll", 1),
                new StartupImageLoadEvent(
                    reused.Lifetime.Key, 330, "later-life.dll", 1),
            ],
            catalog.Eligible, maxRowsPerProcess: 10);

        var baseline = SlowStartupProjection.Rank(
                catalog.Eligible, baselineMetrics, baselineImages, null, 0, 2)
            .Single(row => row.Process == early.Lifetime.Key);
        var noisy = SlowStartupProjection.Rank(
                catalog.Eligible, noisyMetrics, noisyImages, null, 0, 2)
            .Single(row => row.Process == early.Lifetime.Key);

        AssertEquivalent(baseline, noisy);
        Assert.DoesNotContain(
            noisy.StartupImageLoads,
            load => load.TimeUs >= noisy.StartupWindow.Bounds.EndUs);
    }

    [Fact]
    public void DuplicatePidLifetimesHaveDistinctEvidenceIds()
    {
        var first = Observation(
            Metadata(44, 100, 200, true, true, "one.exe"), 100, 200);
        var second = Observation(
            Metadata(44, 300, 400, true, true, "two.exe"), 300, 400);
        var scheduler = new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
        {
            [first.Process] = Scheduler(cpuUs: 10),
            [second.Process] = Scheduler(cpuUs: 10),
        };
        var images = new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
        {
            [first.Process] = EmptyImages(),
            [second.Process] = EmptyImages(),
        };
        var candidates = SlowStartupProjection.Rank(
            [first, second], scheduler, images, null, 0, 2);
        var ids = candidates
            .Select(candidate =>
                SlowStartupProjection.PlanEvidence(candidate, 1).EvidenceIdPrefix)
            .ToList();

        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("start-100", ids[0] + ids[1]);
        Assert.Contains("start-300", ids[0] + ids[1]);
    }

    [Fact]
    public void PreExistingProcessAndMissingImageEachYieldOneStructuredResult()
    {
        var resident = Metadata(
            70, 0, 500, false, false, "target-app-resident.exe");
        var observed = Metadata(
            71, 100, 300, true, true, "target-app-no-image.exe");
        var catalog = StartupProcessCatalog.Build(
            [resident, observed], startupWindowUs: 100, traceDurationUs: 500,
            nameSubstring: "target-app",
            maxCollectionItems: Validation.MaxCollectionItems);

        Assert.DoesNotContain(
            catalog.Eligible, row => row.Process == resident.Lifetime.Key);
        var exclusion = Assert.Single(catalog.Excluded);
        Assert.Equal("startup_start_not_observed", exclusion.Code);
        var candidate = Assert.Single(SlowStartupProjection.Rank(
            catalog.Eligible,
            new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
            {
                [observed.Lifetime.Key] = Scheduler(cpuUs: 10),
            },
            new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
            {
                [observed.Lifetime.Key] = EmptyImages(),
            },
            nameSubstring: "target-app", minWaitRatio: 0, maxCandidates: 1));
        Assert.Equal(
            "first_image_load_not_observed",
            SlowStartupProjection.PlanEvidence(candidate, 1).NotConcludedCode);
    }

    [Fact]
    public void OrdinaryDiscoveryBoundsUnobservedStartSamples()
    {
        var count = Validation.MaxCollectionItems + 25;
        var metadata = Enumerable.Range(1, count)
            .Select(pid => Metadata(
                pid, pid * 10L, pid * 10L + 5,
                startObserved: false, endObserved: false,
                $"resident-{pid}.exe"));
        var catalog = StartupProcessCatalog.Build(
            metadata, startupWindowUs: 5, traceDurationUs: 100_000,
            nameSubstring: null,
            maxCollectionItems: Validation.MaxCollectionItems);

        Assert.Equal(count, catalog.TotalUnobservedStartCount);
        Assert.Equal(Validation.MaxCollectionItems, catalog.Excluded.Count);
        Assert.True(catalog.ExcludedHasMore);
    }

    [Fact]
    public void TraceDiscoveryEvaluatesExpensiveMetricsOnlyForRetainedCandidates()
    {
        const int processCount = 20;
        const int retainedCount = 3;
        var lifetimes = Enumerable.Range(1, processCount)
            .Select(pid => new ProcessLifetime(
                new ProcessInstanceKey(pid, pid * 10L),
                EndUs: pid * 10L + 100,
                StartObserved: true,
                EndObserved: true))
            .ToArray();
        var metricEvaluationCount = 0;
        var traceProcesses = lifetimes
            .Reverse()
            .Select(lifetime =>
                new StartupTraceProcessMetadata(
                    lifetime.Key,
                    lifetime.EndUs,
                    ParentPid: 1,
                    Name: $"process-{lifetime.Key.Pid}.exe",
                    LifetimeCpuUs: 1,
                    GetLifetimeImageLoadCount: () =>
                    {
                        metricEvaluationCount++;
                        return 1;
                    }));

        var catalog = StartupProcessCatalog.BuildFromTraceMetadata(
            traceProcesses,
            new ProcessInstanceResolver(lifetimes),
            startupWindowUs: 50,
            traceDurationUs: 1_000,
            nameSubstring: null,
            maxCollectionItems: retainedCount,
            hasTraceMetadata: _ => true);

        Assert.Equal(processCount, catalog.TotalEligibleCount);
        Assert.Equal(retainedCount, catalog.Eligible.Count);
        Assert.Equal(retainedCount, metricEvaluationCount);
        Assert.Equal(new long[] { 10, 20, 30 },
            catalog.Eligible.Select(item => item.Process.StartUs));
    }

    [Fact]
    public void TraceDiscoveryExcludesConflictingExactProcessKeysWithoutEvaluatingMetrics()
    {
        var key = new ProcessInstanceKey(90, 10);
        var resolver = new ProcessInstanceResolver(
        [
            new ProcessLifetime(key, 100, true, true),
            new ProcessLifetime(key, 120, true, true),
        ]);
        var traceProcesses = new[]
        {
            new StartupTraceProcessMetadata(
                key,
                EndUs: 120,
                ParentPid: 1,
                Name: "ambiguous.exe",
                LifetimeCpuUs: 1,
                GetLifetimeImageLoadCount: () =>
                    throw new InvalidOperationException("must not evaluate")),
        };

        var catalog = StartupProcessCatalog.BuildFromTraceMetadata(
            traceProcesses,
            resolver,
            startupWindowUs: 50,
            traceDurationUs: 200,
            nameSubstring: null,
            maxCollectionItems: 8,
            hasTraceMetadata: candidate => candidate == key);

        Assert.Empty(catalog.Eligible);
        Assert.Equal(1, catalog.TotalOtherExcludedCount);
        var exclusion = Assert.Single(catalog.Excluded);
        Assert.Equal(key, exclusion.Process);
        Assert.Equal("startup_process_instance_ambiguous", exclusion.Code);
    }

    [Fact]
    public void TraceDiscoveryCanonicalizesCompatibleExactProcessKeysAndEvaluatesMetricsOnce()
    {
        var key = new ProcessInstanceKey(90, 10);
        var evaluationCount = 0;
        var resolver = new ProcessInstanceResolver(
        [
            new ProcessLifetime(key, 100, true, true),
            new ProcessLifetime(key, 120, true, false),
        ]);
        var traceProcesses = new[]
        {
            new StartupTraceProcessMetadata(
                key,
                EndUs: 120,
                ParentPid: 1,
                Name: "compatible.exe",
                LifetimeCpuUs: 1,
                GetLifetimeImageLoadCount: () =>
                {
                    evaluationCount++;
                    return 7;
                }),
        };

        var catalog = StartupProcessCatalog.BuildFromTraceMetadata(
            traceProcesses,
            resolver,
            startupWindowUs: 50,
            traceDurationUs: 200,
            nameSubstring: null,
            maxCollectionItems: 8,
            hasTraceMetadata: candidate => candidate == key);

        var observation = Assert.Single(catalog.Eligible);
        Assert.Equal(1, evaluationCount);
        Assert.Equal(100, observation.Metadata.Lifetime.EndUs);
        Assert.Equal(7, observation.Metadata.LifetimeImageLoadCount);
        Assert.Equal(1, catalog.TotalEligibleCount);
        Assert.Empty(catalog.Excluded);
    }

    [Fact]
    public void ResolverFallbackExcludesConflictingExactProcessKeys()
    {
        var key = new ProcessInstanceKey(90, 10);
        var resolver = new ProcessInstanceResolver(
        [
            new ProcessLifetime(key, 100, true, true),
            new ProcessLifetime(key, 120, true, true),
        ]);

        var catalog = StartupProcessCatalog.BuildFromTraceMetadata(
            Array.Empty<StartupTraceProcessMetadata>(),
            resolver,
            startupWindowUs: 50,
            traceDurationUs: 200,
            nameSubstring: null,
            maxCollectionItems: 8,
            hasTraceMetadata: _ => false);

        Assert.Empty(catalog.Eligible);
        Assert.Equal(1, catalog.TotalOtherExcludedCount);
        var exclusion = Assert.Single(catalog.Excluded);
        Assert.Equal(key, exclusion.Process);
        Assert.Equal("startup_process_instance_ambiguous", exclusion.Code);
    }

    [Fact]
    public void TraceMetadataLookupUsesEndOfQuantizedMicrosecondBucket()
    {
        const double rawStartMilliseconds = 10.0009d;
        var key = new ProcessInstanceKey(
            90,
            TraceTime.FromMilliseconds(rawStartMilliseconds));

        var lookupMilliseconds = StartupProcessCatalog.TraceMetadataLookupMilliseconds(key);

        Assert.Equal(10_000, key.StartUs);
        Assert.True(lookupMilliseconds > rawStartMilliseconds);
        Assert.Equal(10.001d, lookupMilliseconds, precision: 6);
    }

    [Fact]
    public void TraceEndOnlyIsPartialButObservedExitIsComplete()
    {
        var truncated = StartupWindow.Create(
            new ProcessLifetime(
                new ProcessInstanceKey(80, 900), 1_000, true, false),
            startupWindowUs: 500, traceDurationUs: 1_000);
        var exited = StartupWindow.Create(
            new ProcessLifetime(
                new ProcessInstanceKey(81, 800), 950, true, true),
            startupWindowUs: 500, traceDurationUs: 1_000);

        Assert.Equal("Partial", truncated.Status);
        Assert.Equal("startup_window_truncated", truncated.Code);
        Assert.Equal("Complete", exited.Status);
        Assert.Null(exited.Code);
    }

    private static void AssertEquivalent(
        SlowStartupCandidateData expected,
        SlowStartupCandidateData actual)
    {
        Assert.Equal(expected.Process, actual.Process);
        Assert.Equal(expected.ParentPid, actual.ParentPid);
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.StartupWindow, actual.StartupWindow);
        Assert.Equal(expected.ObservedStartupWallUs, actual.ObservedStartupWallUs);
        Assert.Equal(expected.StartupCpuUs, actual.StartupCpuUs);
        Assert.Equal(expected.StartupBlockedUs, actual.StartupBlockedUs);
        Assert.Equal(expected.StartupWaitRatio, actual.StartupWaitRatio);
        Assert.Equal(
            expected.StartupBlockedUsByReason.OrderBy(item => item.Key),
            actual.StartupBlockedUsByReason.OrderBy(item => item.Key));
        Assert.Equal(expected.StartupImageLoadCount, actual.StartupImageLoadCount);
        Assert.Equal(expected.StartupImageLoadsHasMore, actual.StartupImageLoadsHasMore);
        Assert.Equal(expected.StartupImageLoads, actual.StartupImageLoads);
        Assert.Equal(expected.LifetimeWallUs, actual.LifetimeWallUs);
        Assert.Equal(expected.LifetimeCpuUs, actual.LifetimeCpuUs);
        Assert.Equal(expected.LifetimeWaitRatio, actual.LifetimeWaitRatio);
        Assert.Equal(expected.LifetimeImageLoadCount, actual.LifetimeImageLoadCount);
    }

    private static StartupProcessMetadata Metadata(
        int pid,
        long startUs,
        long endUs,
        bool startObserved,
        bool endObserved,
        string name) =>
        new(
            new ProcessLifetime(
                new ProcessInstanceKey(pid, startUs),
                endUs,
                startObserved,
                endObserved),
            ParentPid: 1,
            Name: name,
            LifetimeCpuUs: 1,
            LifetimeImageLoadCount: 0);

    private static StartupProcessObservation Observation(
        StartupProcessMetadata metadata,
        long startUs,
        long endUs) =>
        new(
            metadata,
            new StartupWindow(
                metadata.Lifetime.Key,
                new TimeWindow(startUs, endUs),
                RequestedEndUs: endUs,
                TraceDurationUs: endUs,
                ProcessStartObserved: true,
                ProcessEndObserved: true,
                Status: "Complete",
                Code: null));

    private static StartupSchedulerMetrics Scheduler(long cpuUs) =>
        new(
            cpuUs,
            StartupBlockedUs: 0,
            new Dictionary<string, long>(StringComparer.Ordinal),
            RunningIntervalCount: 0,
            BlockedIntervalCount: 0);

    private static StartupImageLoadBucket EmptyImages() =>
        new(
            TotalAvailable: 0,
            FirstLoads: Array.Empty<ImageLoadRow>(),
            HasMore: false);
}
