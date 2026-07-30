using WprMcp.Analyzers;
using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public sealed class StartupWindowTests
{
    [Fact]
    public void Create_ObservedEarlyExit_IsCompleteShortLifetime()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, 100), EndUs: 350,
            StartObserved: true, EndObserved: true);

        var window = StartupWindow.Create(
            lifetime, startupWindowUs: 1_000, traceDurationUs: 5_000);

        Assert.Equal(new TimeWindow(100, 350), window.Bounds);
        Assert.Equal(1_100, window.RequestedEndUs);
        Assert.Equal("Complete", window.Status);
        Assert.Null(window.Code);
    }

    [Fact]
    public void Create_TraceEndCutsRequestedWindow_IsPartial()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, 900), EndUs: 1_000,
            StartObserved: true, EndObserved: false);

        var window = StartupWindow.Create(
            lifetime, startupWindowUs: 500, traceDurationUs: 1_000);

        Assert.Equal(new TimeWindow(900, 1_000), window.Bounds);
        Assert.Equal("Partial", window.Status);
        Assert.Equal("startup_window_truncated", window.Code);
    }

    [Fact]
    public void Build_PreExistingProcessIsExcludedExactlyOnce()
    {
        var metadata = new StartupProcessMetadata(
            new ProcessLifetime(new ProcessInstanceKey(9, 0), 2_000,
                StartObserved: false, EndObserved: false),
            ParentPid: 1,
            Name: "resident.exe",
            LifetimeCpuUs: 50,
            LifetimeImageLoadCount: 4);

        var result = StartupProcessCatalog.Build(
            [metadata], startupWindowUs: 500, traceDurationUs: 2_000,
            nameSubstring: null, maxCollectionItems: Validation.MaxCollectionItems);

        Assert.Empty(result.Eligible);
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal("startup_start_not_observed", exclusion.Code);
        Assert.Equal(metadata.Lifetime.Key, exclusion.Process);
    }

    [Fact]
    public void Build_OrdinaryDiscoveryBoundsEligibleAndExcludedSamples()
    {
        var metadata = Enumerable.Range(1, Validation.MaxCollectionItems + 10)
            .Select(index => Process(
                pid: index,
                startUs: index * 10L,
                startObserved: index % 2 == 0))
            .ToList();

        var result = StartupProcessCatalog.Build(
            metadata, startupWindowUs: 5, traceDurationUs: 10_000,
            nameSubstring: null, maxCollectionItems: 8);

        Assert.Equal(
            metadata.Count(item => item.Lifetime.StartObserved),
            result.TotalEligibleCount);
        Assert.Equal(
            metadata.Count(item => !item.Lifetime.StartObserved),
            result.TotalUnobservedStartCount);
        Assert.Equal(0, result.TotalOtherExcludedCount);
        Assert.Equal(8, result.Eligible.Count);
        Assert.Equal(8, result.Excluded.Count);
        Assert.True(result.EligibleHasMore);
        Assert.True(result.ExcludedHasMore);
    }

    [Fact]
    public void Build_UnsortedInputRetainsEarliestBoundedSamples()
    {
        var metadata = Enumerable.Range(1, 10)
            .Reverse()
            .Select(index => Process(
                pid: index,
                startUs: index * 10L,
                startObserved: true));

        var result = StartupProcessCatalog.Build(
            metadata,
            startupWindowUs: 5,
            traceDurationUs: 1_000,
            nameSubstring: null,
            maxCollectionItems: 3);

        Assert.Equal(10, result.TotalEligibleCount);
        Assert.Equal(
            new long[] { 10, 20, 30 },
            result.Eligible.Select(item => item.Process.StartUs));
        Assert.True(result.EligibleHasMore);
    }

    [Fact]
    public void Create_CheckedAdditionRejectsOverflow()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, long.MaxValue - 4), long.MaxValue,
            StartObserved: true, EndObserved: false);

        Assert.Throws<OverflowException>(() =>
            StartupWindow.Create(
                lifetime, startupWindowUs: 10, traceDurationUs: long.MaxValue));
    }

    private static StartupProcessMetadata Process(
        int pid,
        long startUs,
        bool startObserved) =>
        new(
            new ProcessLifetime(
                new ProcessInstanceKey(pid, startUs),
                EndUs: startUs + 100,
                StartObserved: startObserved,
                EndObserved: true),
            ParentPid: 1,
            Name: $"process-{pid}.exe",
            LifetimeCpuUs: 10,
            LifetimeImageLoadCount: 1);
}
