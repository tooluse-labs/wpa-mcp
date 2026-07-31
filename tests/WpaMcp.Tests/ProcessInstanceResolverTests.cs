using WpaMcp.Analyzers;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class ProcessInstanceResolverTests
{
    [Fact]
    public void Resolve_OverlappingPidLifetimes_IsAmbiguous()
    {
        var resolver = new ProcessInstanceResolver(new[]
        {
            new ProcessLifetime(
                new ProcessInstanceKey(7, 10), 100,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                new ProcessInstanceKey(7, 80), 150,
                StartObserved: true, EndObserved: true),
        });

        var result = resolver.Resolve(pid: 7, timestampUs: 90, processStartUs: null);

        Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Resolve_ProcessStartSelector_DisambiguatesExactLifetime()
    {
        var resolver = ResolverWithReusedPid();

        var result = resolver.Resolve(7, timestampUs: 120, processStartUs: 100);

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ProcessInstanceKey(7, 100), result.Value);
        Assert.Equal([new ProcessInstanceKey(7, 100)], result.Candidates);
    }

    [Fact]
    public void Resolve_NoContainingLifetime_IsUnresolved()
    {
        var result = ResolverWithReusedPid().Resolve(
            pid: 7, timestampUs: 75, processStartUs: null);

        Assert.Equal(InstanceResolutionStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Resolve_EndBoundary_IsExcludedAndNextStartBoundaryIsIncluded()
    {
        var resolver = new ProcessInstanceResolver(new[]
        {
            new ProcessLifetime(
                new ProcessInstanceKey(7, 10), 100,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                new ProcessInstanceKey(7, 100), 200,
                StartObserved: true, EndObserved: true),
        });

        var result = resolver.Resolve(pid: 7, timestampUs: 100, processStartUs: null);

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ProcessInstanceKey(7, 100), result.Value);
    }

    [Fact]
    public void ResolveAtEndpoint_EndBoundarySelectsEndingLifetimeBeforePointFallback()
    {
        var resolver = new ProcessInstanceResolver(new[]
        {
            new ProcessLifetime(
                new ProcessInstanceKey(7, 10), 100,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                new ProcessInstanceKey(7, 100), 200,
                StartObserved: true, EndObserved: false),
        });

        var result = resolver.ResolveAtEndpoint(
            pid: 7,
            timestampUs: 100);

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ProcessInstanceKey(7, 10), result.Value);
    }

    [Fact]
    public void ResolveAtEndpoint_SharedEndpointRemainsAmbiguous()
    {
        var resolver = new ProcessInstanceResolver(new[]
        {
            new ProcessLifetime(
                new ProcessInstanceKey(7, 10), 100,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                new ProcessInstanceKey(7, 20), 100,
                StartObserved: true, EndObserved: false),
        });

        var result = resolver.ResolveAtEndpoint(
            pid: 7,
            timestampUs: 100);

        Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
        Assert.Null(result.Value);
        Assert.Equal(2, result.Candidates.Count);
    }

    [Fact]
    public void Resolve_ReusedPid_SelectsOnlyLifetimeContainingTimestamp()
    {
        var resolver = ResolverWithReusedPid();

        var first = resolver.Resolve(7, timestampUs: 20, processStartUs: null);
        var second = resolver.Resolve(7, timestampUs: 150, processStartUs: null);

        Assert.Equal(new ProcessInstanceKey(7, 10), first.Value);
        Assert.Equal(new ProcessInstanceKey(7, 100), second.Value);
    }

    [Fact]
    public void Resolve_ExactSelectorOutsideLifetime_IsUnresolved()
    {
        var result = ResolverWithReusedPid().Resolve(
            pid: 7, timestampUs: 20, processStartUs: 100);

        Assert.Equal(InstanceResolutionStatus.Unresolved, result.Status);
        Assert.Null(result.Value);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void Constructor_SortsLifetimesByPidThenStart()
    {
        var resolver = new ProcessInstanceResolver(new[]
        {
            new ProcessLifetime(new ProcessInstanceKey(8, 50), 60, true, true),
            new ProcessLifetime(new ProcessInstanceKey(7, 100), 200, true, true),
            new ProcessLifetime(new ProcessInstanceKey(7, 10), 50, true, true),
        });

        Assert.Equal(
            [
                new ProcessInstanceKey(7, 10),
                new ProcessInstanceKey(7, 100),
                new ProcessInstanceKey(8, 50),
            ],
            resolver.Lifetimes.Select(lifetime => lifetime.Key));
    }

    private static ProcessInstanceResolver ResolverWithReusedPid() =>
        new(new[]
        {
            new ProcessLifetime(
                new ProcessInstanceKey(7, 10), 50,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                new ProcessInstanceKey(7, 100), 200,
                StartObserved: true, EndObserved: true),
        });
}
