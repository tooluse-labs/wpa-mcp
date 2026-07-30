using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class TraceIdentityIndexTests
{
    [Fact]
    public void BuildFromEvents_MapsReusedPidAndTidToDistinctInstances()
    {
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            processes: ReusedProcessLifetimes(),
            threads: ReusedThreadLifecycleEvents());

        Assert.Equal(2, index.Processes.Lifetimes.Count(x => x.Key.Pid == 20));
        Assert.Equal(2, index.Threads.Lifetimes.Count(x => x.Key.Tid == 7));
        Assert.NotEqual(index.Threads.Lifetimes[0].Key, index.Threads.Lifetimes[1].Key);
    }

    [Fact]
    public void BuildFromEvents_ThreadStartAtProcessBoundaryBelongsToNewInstance()
    {
        var second = new ProcessInstanceKey(20, 100);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 0), 100, true, true),
                new ProcessLifetime(second, 200, true, false),
            ],
            threads:
            [
                new ThreadLifecycleEvent(20, 7, 100, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(20, 7, 150, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.Equal(second, lifetime.Key.Process);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void BuildFromEvents_ThreadStopAtProcessEndClosesInsideOwningProcess()
    {
        var process = new ProcessInstanceKey(20, 0);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes: [new ProcessLifetime(process, 100, true, true)],
            threads:
            [
                new ThreadLifecycleEvent(
                    20, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 100, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.Equal(process, lifetime.Key.Process);
        Assert.Equal(10, lifetime.StartUs);
        Assert.Equal(100, lifetime.EndUs);
        Assert.True(lifetime.EndObserved);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void BuildFromEvents_StopAndStartAtReuseBoundaryRemainInTheirOwnProcesses()
    {
        var oldProcess = new ProcessInstanceKey(20, 0);
        var newProcess = new ProcessInstanceKey(20, 100);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [
                new ProcessLifetime(oldProcess, 100, true, true),
                new ProcessLifetime(newProcess, 200, true, false),
            ],
            threads:
            [
                new ThreadLifecycleEvent(
                    20, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 100, ThreadLifecycleEventKind.Stop, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 100, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 150, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

        Assert.Collection(
            index.Threads.Lifetimes.OrderBy(lifetime => lifetime.StartUs),
            lifetime =>
            {
                Assert.Equal(oldProcess, lifetime.Key.Process);
                Assert.Equal(10, lifetime.StartUs);
                Assert.Equal(100, lifetime.EndUs);
            },
            lifetime =>
            {
                Assert.Equal(newProcess, lifetime.Key.Process);
                Assert.Equal(100, lifetime.StartUs);
                Assert.Equal(150, lifetime.EndUs);
            });
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void BuildFromEvents_StopAtSharedProcessEndpointRemainsAmbiguous()
    {
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 0), 100, true, true),
                new ProcessLifetime(new ProcessInstanceKey(20, 25), 100, true, false),
            ],
            threads:
            [new ThreadLifecycleEvent(
                20, 7, 100, ThreadLifecycleEventKind.Stop, Observed: true)]);

        Assert.Empty(index.Threads.Lifetimes);
        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal("thread_process_ambiguous", diagnostic.Code);
        Assert.Equal(InstanceResolutionStatus.Ambiguous, diagnostic.ResolutionStatus);
        Assert.Equal(2, diagnostic.CandidateCount);
    }

    [Fact]
    public void BuildFromEvents_MissingThreadStopCannotCrossReusedProcessBoundary()
    {
        var oldProcess = new ProcessInstanceKey(20, 0);
        var newProcess = new ProcessInstanceKey(20, 100);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [
                new ProcessLifetime(oldProcess, 100, true, true),
                new ProcessLifetime(newProcess, 200, true, false),
            ],
            threads:
            [
                new ThreadLifecycleEvent(
                    20, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 100, ThreadLifecycleEventKind.Start, Observed: true),
            ]);

        Assert.Collection(
            index.Threads.Lifetimes.OrderBy(lifetime => lifetime.StartUs),
            lifetime =>
            {
                Assert.Equal(oldProcess, lifetime.Key.Process);
                Assert.Equal(10, lifetime.StartUs);
                Assert.Equal(100, lifetime.EndUs);
                Assert.False(lifetime.EndObserved);
            },
            lifetime =>
            {
                Assert.Equal(newProcess, lifetime.Key.Process);
                Assert.Equal(100, lifetime.StartUs);
                Assert.Equal(200, lifetime.EndUs);
                Assert.False(lifetime.EndObserved);
            });

        var laterResolution = index.Threads.Resolve(
            new ThreadSelector(20, 7, ProcessStartUs: null, ThreadStartUs: null),
            new TimeWindow(150, 151));
        Assert.Equal(InstanceResolutionStatus.Resolved, laterResolution.Status);
        Assert.Equal(newProcess, laterResolution.Value?.Process);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public void BuildFromEvents_RundownStartPreservesInferredProvenance()
    {
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [new ProcessLifetime(new ProcessInstanceKey(20, 0), 200, false, false)],
            threads:
            [
                new ThreadLifecycleEvent(20, 7, 0, ThreadLifecycleEventKind.RundownStart, Observed: false),
                new ThreadLifecycleEvent(20, 7, 100, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.False(lifetime.StartObserved);
        Assert.True(lifetime.EndObserved);
    }

    [Fact]
    public void BuildFromEvents_RundownTimestampsUseOwningProcessBounds()
    {
        var process = new ProcessInstanceKey(20, 10);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes: [new ProcessLifetime(process, 180, true, false)],
            threads:
            [
                new ThreadLifecycleEvent(
                    20, 7, 50, ThreadLifecycleEventKind.RundownStart, Observed: false),
                new ThreadLifecycleEvent(
                    20, 7, 150, ThreadLifecycleEventKind.RundownStop, Observed: false),
            ]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.Equal(10, lifetime.StartUs);
        Assert.Equal(180, lifetime.EndUs);
        Assert.False(lifetime.StartObserved);
        Assert.False(lifetime.EndObserved);
    }

    [Fact]
    public void BuildFromEvents_LateRundownStartDoesNotSplitObservedThread()
    {
        var process = new ProcessInstanceKey(20, 10);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes: [new ProcessLifetime(process, 180, true, true)],
            threads:
            [
                new ThreadLifecycleEvent(
                    20, 7, 20, ThreadLifecycleEventKind.Start, Observed: true),
                new ThreadLifecycleEvent(
                    20, 7, 50, ThreadLifecycleEventKind.RundownStart, Observed: false),
                new ThreadLifecycleEvent(
                    20, 7, 80, ThreadLifecycleEventKind.Stop, Observed: true),
            ]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.Equal(20, lifetime.StartUs);
        Assert.Equal(80, lifetime.EndUs);
        Assert.True(lifetime.StartObserved);
        Assert.True(lifetime.EndObserved);
    }

    [Fact]
    public void BuildFromEvents_StopWithoutStartCreatesInferredStart()
    {
        var process = new ProcessInstanceKey(20, 25);
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes: [new ProcessLifetime(process, 200, true, false)],
            threads:
            [new ThreadLifecycleEvent(20, 7, 100, ThreadLifecycleEventKind.Stop, Observed: true)]);

        var lifetime = Assert.Single(index.Threads.Lifetimes);
        Assert.Equal(25, lifetime.StartUs);
        Assert.False(lifetime.StartObserved);
        Assert.True(lifetime.EndObserved);
    }

    [Fact]
    public void BuildFromEvents_AmbiguousOwningProcessAddsDiagnosticWithoutGuessing()
    {
        var index = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 200,
            processes:
            [
                new ProcessLifetime(new ProcessInstanceKey(20, 0), 150, true, true),
                new ProcessLifetime(new ProcessInstanceKey(20, 75), 200, true, false),
            ],
            threads:
            [new ThreadLifecycleEvent(20, 7, 100, ThreadLifecycleEventKind.Start, Observed: true)]);

        Assert.Empty(index.Threads.Lifetimes);
        var diagnostic = Assert.Single(index.Diagnostics);
        Assert.Equal(InstanceResolutionStatus.Ambiguous, diagnostic.ResolutionStatus);
        Assert.Equal(20, diagnostic.Pid);
        Assert.Equal(7, diagnostic.Tid);
    }

    [Fact]
    public void BuildProcessLifetimes_RealStartAtZeroRemainsObservedAfterBackfill()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events:
            [new ProcessLifecycleEvent(20, 0, ProcessLifecycleEventKind.Start)],
            backfill: [new ProcessLifetimeBackfill(20, 0, 100)]);

        var lifetime = Assert.Single(lifetimes);
        Assert.Equal(0, lifetime.Key.StartUs);
        Assert.True(lifetime.StartObserved);
        Assert.False(lifetime.EndObserved);
    }

    [Fact]
    public void BuildProcessLifetimes_RundownTimestampsUseTraceBounds()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events:
            [
                new ProcessLifecycleEvent(20, 25, ProcessLifecycleEventKind.RundownStart),
                new ProcessLifecycleEvent(20, 75, ProcessLifecycleEventKind.RundownStop),
            ],
            backfill: Array.Empty<ProcessLifetimeBackfill>());

        var lifetime = Assert.Single(lifetimes);
        Assert.Equal(new ProcessInstanceKey(20, 0), lifetime.Key);
        Assert.Equal(100, lifetime.EndUs);
        Assert.False(lifetime.StartObserved);
        Assert.False(lifetime.EndObserved);
    }

    [Fact]
    public void BuildProcessLifetimes_RundownStopBackfillPreservesTraceEndBound()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events:
            [
                new ProcessLifecycleEvent(
                    20, 75, ProcessLifecycleEventKind.RundownStop),
            ],
            backfill:
            [
                new ProcessLifetimeBackfill(20, 0, 75),
            ]);

        var lifetime = Assert.Single(lifetimes);
        Assert.Equal(new ProcessInstanceKey(20, 0), lifetime.Key);
        Assert.Equal(100, lifetime.EndUs);
        Assert.False(lifetime.EndObserved);
    }

    [Fact]
    public void BuildProcessLifetimes_MissingStopAcceptsEarlierBackfillEnd()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events:
            [
                new ProcessLifecycleEvent(
                    20, 10, ProcessLifecycleEventKind.Start),
            ],
            backfill:
            [
                new ProcessLifetimeBackfill(20, 10, 75),
            ]);

        var lifetime = Assert.Single(lifetimes);
        Assert.Equal(new ProcessInstanceKey(20, 10), lifetime.Key);
        Assert.Equal(75, lifetime.EndUs);
        Assert.True(lifetime.StartObserved);
        Assert.False(lifetime.EndObserved);
    }

    [Fact]
    public void BuildProcessLifetimes_BackfillCannotCrossPidReuseBoundary()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 200,
            events:
            [
                new ProcessLifecycleEvent(20, 10, ProcessLifecycleEventKind.Start),
                new ProcessLifecycleEvent(20, 100, ProcessLifecycleEventKind.Start),
            ],
            backfill:
            [
                new ProcessLifetimeBackfill(20, 10, 150),
            ]);

        var oldLifetime = Assert.Single(
            lifetimes,
            lifetime => lifetime.Key == new ProcessInstanceKey(20, 10));
        Assert.Equal(100, oldLifetime.EndUs);
    }

    [Fact]
    public void BuildProcessLifetimes_LateRundownStartDoesNotSplitObservedProcess()
    {
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events:
            [
                new ProcessLifecycleEvent(20, 10, ProcessLifecycleEventKind.Start),
                new ProcessLifecycleEvent(20, 50, ProcessLifecycleEventKind.RundownStart),
                new ProcessLifecycleEvent(20, 80, ProcessLifecycleEventKind.Stop),
            ],
            backfill: Array.Empty<ProcessLifetimeBackfill>());

        var lifetime = Assert.Single(lifetimes);
        Assert.Equal(new ProcessInstanceKey(20, 10), lifetime.Key);
        Assert.Equal(80, lifetime.EndUs);
        Assert.True(lifetime.StartObserved);
        Assert.True(lifetime.EndObserved);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildProcessLifetimes_RundownOrBackfillAtZeroIsNotObserved(bool includeRundown)
    {
        var events = includeRundown
            ? new[] { new ProcessLifecycleEvent(20, 0, ProcessLifecycleEventKind.RundownStart) }
            : Array.Empty<ProcessLifecycleEvent>();
        var lifetimes = TraceIdentityIndex.BuildProcessLifetimes(
            traceEndUs: 100,
            events,
            backfill: [new ProcessLifetimeBackfill(20, 0, 100)]);

        Assert.False(Assert.Single(lifetimes).StartObserved);
    }

    [Fact]
    public void For_SameTraceLog_ReturnsSameImmutableIndex()
    {
        using var trace = OpenFixture();

        Assert.Same(TraceIdentityIndex.For(trace), TraceIdentityIndex.For(trace));
    }

    [Fact]
    public async Task For_ConcurrentCalls_InvokeInjectedBuilderOnce()
    {
        using var trace = OpenFixture();
        const int workerCount = 16;
        using var barrier = new Barrier(workerCount);
        var invocationCount = 0;

        TraceIdentityIndex Builder(TraceLog _)
        {
            Interlocked.Increment(ref invocationCount);
            return TraceIdentityIndex.BuildFromEvents(
                traceEndUs: 1,
                processes: Array.Empty<ProcessLifetime>(),
                threads: Array.Empty<ThreadLifecycleEvent>());
        }

        var tasks = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                return TraceIdentityIndex.For(trace, Builder);
            }))
            .ToArray();
        var indexes = await Task.WhenAll(tasks);

        Assert.Equal(1, invocationCount);
        Assert.All(indexes, index => Assert.Same(indexes[0], index));
    }

    private static IReadOnlyList<ProcessLifetime> ReusedProcessLifetimes() =>
    [
        new ProcessLifetime(new ProcessInstanceKey(20, 0), 100, true, true),
        new ProcessLifetime(new ProcessInstanceKey(20, 100), 500, true, false),
    ];

    private static IReadOnlyList<ThreadLifecycleEvent> ReusedThreadLifecycleEvents() =>
    [
        new ThreadLifecycleEvent(20, 7, 10, ThreadLifecycleEventKind.Start, Observed: true),
        new ThreadLifecycleEvent(20, 7, 90, ThreadLifecycleEventKind.Stop, Observed: true),
        new ThreadLifecycleEvent(20, 7, 120, ThreadLifecycleEventKind.Start, Observed: true),
        new ThreadLifecycleEvent(20, 7, 200, ThreadLifecycleEventKind.Stop, Observed: true),
    ];

    private static TraceLog OpenFixture() =>
        TraceLog.OpenOrConvert("fixtures/small_cpu.etl");
}
