using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

public class TraceCapabilitiesDetectorTests
{
    [Theory]
    [InlineData((int)ClrCapabilityEndpoint.GcStop)]
    [InlineData((int)ClrCapabilityEndpoint.GcSuspendEeStart)]
    [InlineData((int)ClrCapabilityEndpoint.GcRestartEeStop)]
    public void GcEndpointAlone_SetsCompatibilitySourceFlagOnly(
        int endpointValue)
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();

        capabilities.Observe((ClrCapabilityEndpoint)endpointValue);

        Assert.True(capabilities.HasClrGc);
        Assert.Equal(1, capabilities.GcIntervalEndpointEventCount);
        Assert.False(capabilities.HasClrJit);
        Assert.Equal(0, capabilities.JitIntervalEndpointEventCount);
        Assert.False(capabilities.HasClrContention);
    }

    [Fact]
    public void MethodLoadVerboseAlone_SetsCompatibilitySourceFlagOnly()
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();

        capabilities.Observe(ClrCapabilityEndpoint.MethodLoadVerbose);

        Assert.True(capabilities.HasClrJit);
        Assert.Equal(1, capabilities.JitIntervalEndpointEventCount);
        Assert.False(capabilities.HasClrGc);
        Assert.False(capabilities.HasClrContention);
    }

    [Fact]
    public void ManagedContentionStopAlone_AdvertisesCapabilityWithoutCoverage()
    {
        var capabilities = new ClrEndpointCapabilityAccumulator();
        capabilities.Observe(ClrCapabilityEndpoint.ContentionStop);
        var process = new ProcessInstanceKey(12, 10);
        var thread = new ThreadInstanceKey(process, 77, 1);
        var pairer = new IntervalPairAccumulator<
            ThreadInstanceKey,
            ContentionStartData,
            ContentionStopData>();
        pairer.AddStop(thread, 130, new ContentionStopData());

        var intervals = pairer.Complete();
        var coverage = TraceCapabilitiesDetector.ProjectClrContentionCoverage(intervals);

        Assert.True(capabilities.HasClrContention);
        Assert.Empty(intervals.Pairs);
        Assert.Single(intervals.UnmatchedStops);
        Assert.Equal("no_events", coverage.CoverageState);
        Assert.Equal(0, coverage.TotalEventCount);
    }

    [Fact]
    public void FinalizerCompletedBatchCount_ExcludesObjectsAndUnmatchedEndpoints()
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 1_000,
            processes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(42, 0),
                    1_000,
                    StartObserved: true,
                    EndObserved: true),
            ],
            threads: Array.Empty<ThreadLifecycleEvent>());
        FinalizerEvent[] events =
        [
            FinalizerEvent.Object(42, 5, "Example"),
            FinalizerEvent.BatchStart(42, 10, clrInstanceId: 1),
            FinalizerEvent.BatchStop(42, 20, clrInstanceId: 1, count: 3),
            FinalizerEvent.BatchStart(42, 30, clrInstanceId: 1),
            FinalizerEvent.BatchStop(42, 40, clrInstanceId: null, count: 4),
            FinalizerEvent.BatchStop(42, 50, clrInstanceId: 2, count: 5),
        ];

        var completed = TraceCapabilitiesDetector.CountCompletedFinalizerBatches(
            identities,
            events);

        Assert.Equal(1, completed);
    }

    [Fact]
    public void RundownOnlyThreadEvidence_AdvertisesSourceWithoutClaimingObservedEndpoints()
    {
        var capability = new ThreadEndpointCapabilityAccumulator();
        capability.Observe(ThreadLifecycleEventKind.RundownStart);
        capability.Observe(ThreadLifecycleEventKind.RundownStop);
        var response = ThreadLifetimeAnalysis.AnalyzeEventsResponse(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(20, 0),
                    100,
                    StartObserved: false,
                    EndObserved: false),
            ],
            events:
            [
                new ThreadLifecycleEvent(
                    20, 7, 0, ThreadLifecycleEventKind.RundownStart, Observed: false),
                new ThreadLifecycleEvent(
                    20, 7, 100, ThreadLifecycleEventKind.RundownStop, Observed: false),
            ],
            pid: 20,
            top: 10,
            processStartUs: 0);

        Assert.True(capability.HasAnySourceEvidence);
        Assert.Equal(0, capability.ObservedEndpointEventCount);
        Assert.Equal(2, capability.RundownEndpointEventCount);
        Assert.Equal(2, capability.SourceEventCount);
        Assert.Equal("partial", response.CapabilityStatus);
        Assert.Equal(2, response.MatchedEventCount);
        Assert.Equal(0, response.MatchedObservedEndpointCount);
        Assert.Equal(2, response.MatchedRundownEndpointCount);
        Assert.Contains(response.Warnings, warning => warning.StartsWith(
            "rundown_only_thread_lifetimes:",
            StringComparison.Ordinal));
        var row = Assert.Single(response.Threads);
        Assert.True(row.TraceResidentStart);
        Assert.True(row.TraceResidentEnd);
    }

    [Fact]
    public void CompletionEvidenceCounters_SeparatePairsUnmatchedAndBoundaries()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(42, 0),
            1_000,
            StartObserved: true,
            EndObserved: true);
        var threadEvents = new ThreadLifecycleEvent[]
        {
            new(42, 10, 10, ThreadLifecycleEventKind.Start, Observed: true),
            new(42, 10, 20, ThreadLifecycleEventKind.Stop, Observed: true),
            new(42, 11, 0, ThreadLifecycleEventKind.RundownStart, Observed: false),
            new(42, 11, 1_000, ThreadLifecycleEventKind.RundownStop, Observed: false),
        };
        var identities = TraceIdentityIndex.BuildFromEvents(
            1_000,
            [process],
            threadEvents);
        var threadEndpoints = new ThreadEndpointCapabilityAccumulator();
        foreach (var endpoint in threadEvents)
            threadEndpoints.Observe(endpoint.Kind);

        var thread = TraceCapabilitiesDetector.CountThreadLifecycleEvidence(
            identities,
            threadEndpoints);
        Assert.Equal(new CompletionEvidenceCountSet(1, 0, 2), thread);

        NetConnectionEvent[] networkEndpoints =
        [
            new(42, 1, NetConnectionEventKind.Connect, 10),
            new(42, 1, NetConnectionEventKind.Disconnect, 20),
            new(42, 2, NetConnectionEventKind.Connect, 30),
            new(42, 3, NetConnectionEventKind.Disconnect, 40),
        ];
        var network = TraceCapabilitiesDetector.CountNetworkConnectionEvidence(
            identities,
            networkEndpoints);
        Assert.Equal(new CompletionEvidenceCountSet(1, 1, 1), network);

        ClrIntervalCapabilityEndpoint[] gcEndpoints =
        [
            new(ClrCapabilityEndpoint.GcStart, 42, 10, 1, GcCount: 1),
            new(ClrCapabilityEndpoint.GcStop, 42, 20, 1, GcCount: 1),
            new(ClrCapabilityEndpoint.GcStart, 42, 30, 1, GcCount: 2),
        ];
        var gc = TraceCapabilitiesDetector.CountGcIntervalEvidence(
            identities,
            gcEndpoints);
        Assert.Equal(new CompletionEvidenceCountSet(1, 1, 0), gc);

        JitIntervalCapabilityEndpoint[] jitEndpoints =
        [
            new(true, 42, 10, 1, MethodId: 7),
            new(false, 42, 20, 1, MethodId: 7),
            new(true, 42, 30, 1, MethodId: 8),
        ];
        var jit = TraceCapabilitiesDetector.CountJitIntervalEvidence(
            identities,
            jitEndpoints);
        Assert.Equal(new CompletionEvidenceCountSet(1, 1, 0), jit);
    }

    [Fact]
    public void CompletionEvidenceCounters_TreatIdentityLossAndSingleEndpointsAsBounded()
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 1_000,
            processes: Array.Empty<ProcessLifetime>(),
            threads: Array.Empty<ThreadLifecycleEvent>());

        var network = TraceCapabilitiesDetector.CountNetworkConnectionEvidence(
            identities,
            [new NetConnectionEvent(99, 1, NetConnectionEventKind.Connect, 10)]);
        Assert.Equal(new CompletionEvidenceCountSet(0, 0, 1), network);

        var gc = TraceCapabilitiesDetector.CountGcIntervalEvidence(
            identities,
            [new ClrIntervalCapabilityEndpoint(
                ClrCapabilityEndpoint.GcStart, 99, 10, ClrInstanceId: 1)]);
        Assert.Equal(new CompletionEvidenceCountSet(0, 0, 1), gc);

        var jit = TraceCapabilitiesDetector.CountJitIntervalEvidence(
            identities,
            [new JitIntervalCapabilityEndpoint(
                IsStart: true, 99, 10, ClrInstanceId: 1, MethodId: 1)]);
        Assert.Equal(new CompletionEvidenceCountSet(0, 0, 1), jit);
    }

    [Fact]
    public void CoverageMetric_RejectsNegativeAndUnsignedOverflow()
    {
        var coverage = new DomainStackCoverageAccumulator("virtual_alloc", "bytes");

        Assert.Throws<InvalidDataException>(() => coverage.Observe(hasStack: true, metric: -1));
        Assert.Throws<InvalidDataException>(() =>
            TraceCapabilitiesDetector.CheckedNonNegativeMetric(-1));
        Assert.Equal(long.MaxValue, TraceCapabilitiesDetector.CheckedUnsignedMetric(long.MaxValue));
        Assert.Throws<OverflowException>(() =>
            TraceCapabilitiesDetector.CheckedUnsignedMetric((ulong)long.MaxValue + 1));
        var snapshot = coverage.Snapshot();
        Assert.Equal(0, snapshot.TotalEventCount);
        Assert.Equal(0, snapshot.TotalMetric);
    }
}
