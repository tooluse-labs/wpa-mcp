using WpaMcp.Analyzers;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class ThreadInstanceCatalogTests
{
    [Fact]
    public void StopThenStartSameTid_CreatesNewGeneration()
    {
        var process = new ProcessInstanceKey(10, 0);
        var catalog = new ThreadInstanceCatalog();
        catalog.Start(process, tid: 44, startUs: 10, startObserved: true);
        catalog.Stop(process, tid: 44, endUs: 20);
        catalog.Start(process, tid: 44, startUs: 30, startObserved: true);
        catalog.Complete(traceEndUs: 100);

        Assert.Equal(new long[] { 1, 2 }, catalog.Lifetimes.Select(x => x.Key.Generation));
    }

    [Fact]
    public void Resolve_TwoGenerationsInWindow_IsAmbiguous()
    {
        var catalog = CatalogWithTidReuse();

        var result = catalog.Resolve(
            new ThreadSelector(10, 44, null, null),
            new TimeWindow(0, 100));

        Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
        Assert.Equal(2, result.Candidates.Count);
        Assert.Null(result.Value);
    }

    [Fact]
    public void Resolve_ThreadStartSelector_SelectsExactGeneration()
    {
        var catalog = CatalogWithTidReuse();

        var result = catalog.Resolve(
            new ThreadSelector(10, 44, ProcessStartUs: 0, ThreadStartUs: 30),
            new TimeWindow(0, 100));

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ThreadInstanceKey(new ProcessInstanceKey(10, 0), 44, 2), result.Value);
    }

    [Fact]
    public void Resolve_ThreadGenerationSelectsLifetimeWhenStartsAreEqual()
    {
        var process = new ProcessInstanceKey(10, 0);
        var catalog = new ThreadInstanceCatalog();
        catalog.Stop(process, tid: 44, endUs: 20);
        catalog.Stop(process, tid: 44, endUs: 40);
        catalog.Complete(traceEndUs: 100);

        var ambiguous = catalog.Resolve(
            new ThreadSelector(
                10, 44, ProcessStartUs: 0, ThreadStartUs: 0),
            new TimeWindow(0, 50));
        var exact = catalog.Resolve(
            new ThreadSelector(
                10, 44, ProcessStartUs: 0, ThreadStartUs: 0,
                ThreadGeneration: 2),
            new TimeWindow(0, 50));

        Assert.Equal(InstanceResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(InstanceResolutionStatus.Resolved, exact.Status);
        Assert.Equal(new ThreadInstanceKey(process, 44, 2), exact.Value);
    }

    [Fact]
    public void StartReuseWithoutStop_ClosesPreviousGenerationAsInferred()
    {
        var process = new ProcessInstanceKey(10, 0);
        var catalog = new ThreadInstanceCatalog();
        catalog.Start(process, tid: 44, startUs: 10, startObserved: true);
        catalog.Start(process, tid: 44, startUs: 30, startObserved: true);
        catalog.Complete(traceEndUs: 100);

        Assert.Collection(
            catalog.Lifetimes,
            first =>
            {
                Assert.Equal(10, first.StartUs);
                Assert.Equal(30, first.EndUs);
                Assert.True(first.StartObserved);
                Assert.False(first.EndObserved);
            },
            second =>
            {
                Assert.Equal(30, second.StartUs);
                Assert.Equal(100, second.EndUs);
                Assert.True(second.StartObserved);
                Assert.False(second.EndObserved);
            });
    }

    [Fact]
    public void StopWithoutStart_CreatesProcessBoundInferredGeneration()
    {
        var process = new ProcessInstanceKey(10, 5);
        var catalog = new ThreadInstanceCatalog();
        catalog.Stop(process, tid: 44, endUs: 20);
        catalog.Complete(traceEndUs: 100);

        var lifetime = Assert.Single(catalog.Lifetimes);
        Assert.Equal(5, lifetime.StartUs);
        Assert.Equal(20, lifetime.EndUs);
        Assert.False(lifetime.StartObserved);
        Assert.True(lifetime.EndObserved);
    }

    [Fact]
    public void Resolve_SamePidAcrossProcessInstances_RequiresProcessStart()
    {
        var firstProcess = new ProcessInstanceKey(10, 0);
        var secondProcess = new ProcessInstanceKey(10, 60);
        var catalog = new ThreadInstanceCatalog();
        catalog.Start(firstProcess, tid: 44, startUs: 10, startObserved: true);
        catalog.Stop(firstProcess, tid: 44, endUs: 50);
        catalog.Start(secondProcess, tid: 44, startUs: 70, startObserved: true);
        catalog.Complete(traceEndUs: 100);

        var ambiguous = catalog.Resolve(
            new ThreadSelector(10, 44, null, null), new TimeWindow(0, 100));
        var resolved = catalog.Resolve(
            new ThreadSelector(10, 44, ProcessStartUs: 60, ThreadStartUs: null),
            new TimeWindow(0, 100));

        Assert.Equal(InstanceResolutionStatus.Ambiguous, ambiguous.Status);
        Assert.Equal(
            new ThreadInstanceKey(secondProcess, 44, 1),
            resolved.Value);
    }

    [Fact]
    public void Resolve_SameTidAcrossProcesses_DoesNotJoinProcesses()
    {
        var firstProcess = new ProcessInstanceKey(10, 0);
        var secondProcess = new ProcessInstanceKey(11, 0);
        var catalog = new ThreadInstanceCatalog();
        catalog.Start(firstProcess, tid: 44, startUs: 10, startObserved: true);
        catalog.Start(secondProcess, tid: 44, startUs: 10, startObserved: true);
        catalog.Complete(traceEndUs: 100);

        var result = catalog.Resolve(
            new ThreadSelector(10, 44, null, null), new TimeWindow(0, 100));

        Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
        Assert.Equal(new ThreadInstanceKey(firstProcess, 44, 1), result.Value);
    }

    [Fact]
    public void ResolveSwitch_AtSameUsStopAndReuseAssignsSidesToDistinctInstances()
    {
        var oldProcess = new ProcessInstanceKey(10, 0);
        var newProcess = new ProcessInstanceKey(10, 100);
        var catalog = new ThreadInstanceCatalog(
        [
            new ProcessLifetime(oldProcess, 100, true, true),
            new ProcessLifetime(newProcess, 200, true, false),
        ]);
        catalog.Start(oldProcess, tid: 44, startUs: 10, startObserved: true);
        catalog.Stop(oldProcess, tid: 44, endUs: 100, endObserved: true);
        catalog.Start(newProcess, tid: 44, startUs: 100, startObserved: true);
        catalog.Stop(newProcess, tid: 44, endUs: 150, endObserved: true);
        catalog.Complete(traceEndUs: 200);

        var resolution = catalog.ResolveSwitch(
            oldPid: 10,
            oldTid: 44,
            newPid: 10,
            newTid: 44,
            timestampUs: 100);

        Assert.Equal(oldProcess, resolution.OldThread.Value?.Process);
        Assert.Equal(newProcess, resolution.NewThread.Value?.Process);
    }

    [Fact]
    public void ResolveAtEndpoint_PrefersRequestedEndProvenance()
    {
        var observedProcess = new ProcessInstanceKey(10, 0);
        var inferredProcess = new ProcessInstanceKey(10, 25);
        var catalog = new ThreadInstanceCatalog(
        [
            new ProcessLifetime(observedProcess, 100, true, true),
            new ProcessLifetime(inferredProcess, 100, true, false),
        ]);
        catalog.Stop(observedProcess, tid: 44, endUs: 100, endObserved: true);
        catalog.Stop(inferredProcess, tid: 44, endUs: 100, endObserved: false);
        catalog.Complete(traceEndUs: 200);

        var observed = catalog.ResolveAtEndpoint(
            pid: 10, tid: 44, timestampUs: 100, preferredEndObserved: true);
        var inferred = catalog.ResolveAtEndpoint(
            pid: 10, tid: 44, timestampUs: 100, preferredEndObserved: false);
        var unspecified = catalog.ResolveAtEndpoint(
            pid: 10, tid: 44, timestampUs: 100);

        Assert.Equal(observedProcess, observed.Value?.Process);
        Assert.Equal(inferredProcess, inferred.Value?.Process);
        Assert.Equal(InstanceResolutionStatus.Ambiguous, unspecified.Status);
    }

    [Fact]
    public void ResolveAtEndpoint_DoesNotFallBackToNewInstancePointMatch()
    {
        var newProcess = new ProcessInstanceKey(10, 100);
        var catalog = new ThreadInstanceCatalog(
        [new ProcessLifetime(newProcess, 200, true, false)]);
        catalog.Start(newProcess, tid: 44, startUs: 100, startObserved: true);
        catalog.Stop(newProcess, tid: 44, endUs: 150, endObserved: true);
        catalog.Complete(traceEndUs: 200);

        var endpoint = catalog.ResolveAtEndpoint(
            pid: 10,
            tid: 44,
            timestampUs: 100,
            preferredEndObserved: true);
        var point = catalog.ResolveAt(pid: 10, tid: 44, timestampUs: 100);

        Assert.Equal(InstanceResolutionStatus.Unresolved, endpoint.Status);
        Assert.Equal(newProcess, point.Value?.Process);
    }

    private static ThreadInstanceCatalog CatalogWithTidReuse()
    {
        var process = new ProcessInstanceKey(10, 0);
        var catalog = new ThreadInstanceCatalog();
        catalog.Start(process, tid: 44, startUs: 10, startObserved: true);
        catalog.Stop(process, tid: 44, endUs: 20);
        catalog.Start(process, tid: 44, startUs: 30, startObserved: true);
        catalog.Complete(traceEndUs: 100);
        return catalog;
    }
}
