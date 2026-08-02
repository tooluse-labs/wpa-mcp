using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class ScopedUnresolvedLifetimeTests
{
    private const int ReusedPid = 42;
    private static readonly TimeWindow Window = new(0, 110);

    [Fact]
    public void ProcessScope_RawCandidateRejectsReuseGapAndOtherLifetime()
    {
        var identities = ReusedPidIdentities();
        var all = ProcessAnalysisScope.Resolve(
            Window, pid: null, processStartUs: null, identities);
        var aggregate = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: null, identities);
        var exactFirst = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);

        Assert.True(all.MatchesRawUnresolvedCandidate(
            identities, eventPid: 99, timestampUs: 50));
        Assert.True(aggregate.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 20));
        Assert.False(aggregate.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 50));
        Assert.False(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 70));
        Assert.True(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 40, atEndpoint: true));
        Assert.False(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 40));
        Assert.False(all.MatchesRawUnresolvedCandidate(
            identities, eventPid: 99, timestampUs: Window.EndUs, atEndpoint: true));
    }

    [Fact]
    public void ThreadScope_RawCandidateSeparatesAllPidAggregateAndExactScopes()
    {
        var identities = ReusedPidIdentities();
        var all = WaitTools.ResolveStackScope(
            Window, pid: null, tid: null, processStartUs: null,
            threadStartUs: null, identities);
        var aggregate = WaitTools.ResolveStackScope(
            Window, ReusedPid, tid: null, processStartUs: null,
            threadStartUs: null, identities);
        var exactFirst = ExactFirstProcessThreadScope(identities);

        Assert.True(all.MatchesRawUnresolvedCandidate(
            identities, pid: 99, tid: 123, timestampUs: 50));
        Assert.False(aggregate.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, tid: 7, timestampUs: 50));
        Assert.True(aggregate.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, tid: 7, timestampUs: 70));
        Assert.False(aggregate.MatchesRawUnresolvedCandidate(
            identities, pid: 99, tid: 7, timestampUs: 20));
        Assert.True(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, tid: 7, timestampUs: 40,
            atEndpoint: true));
        Assert.False(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, tid: 7, timestampUs: 70,
            atEndpoint: true));
        Assert.False(all.MatchesRawUnresolvedCandidate(
            identities, pid: 99, tid: 123, timestampUs: Window.EndUs,
            atEndpoint: true));
    }

    [Fact]
    public void ProcessScope_AdjacentReuseBoundaryRequiresExplicitEndpointSemantics()
    {
        var identities = AdjacentPidIdentities();
        var exactFirst = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);
        var exactSecond = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 40, identities);

        Assert.False(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 40));
        Assert.True(exactFirst.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 40, atEndpoint: true));
        Assert.True(exactSecond.MatchesRawUnresolvedCandidate(
            identities, ReusedPid, timestampUs: 40));
    }

    [Fact]
    public void MemoryPool_UnresolvedGapRemainsTraceWideButNotScoped()
    {
        var identities = ReusedPidIdentities();
        var scope = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);
        var accumulator = new MemoryResourceAnalysis.InstanceAccumulator(
            process => process.ToString());
        accumulator.AddPoolObservation(
            process: null,
            timeUs: 50,
            isAllocation: false,
            entry: 7,
            bytes: 64,
            tag: "GAP ",
            poolKind: "paged",
            rawPoolEvent: false,
            rawPid: ReusedPid);
        accumulator.AddPoolObservation(
            process: null,
            timeUs: 70,
            isAllocation: false,
            entry: 8,
            bytes: 64,
            tag: "NEXT",
            poolKind: "paged",
            rawPoolEvent: false,
            rawPid: ReusedPid);

        var projection = accumulator.ProjectPoolObservations(
            Window, scope, identities);
        var contract = MemoryResourceAnalysis.ClassifyDataContract(
            scope,
            eventClassObserved: true,
            matchedEventCount: 0,
            scopedIdentityUnresolvedEventCount:
                projection.ScopedIdentityUnresolvedFreeCount);

        Assert.Equal(2, projection.TraceIdentityUnresolvedFreeCount);
        Assert.Equal(0, projection.ScopedIdentityUnresolvedFreeCount);
        Assert.Equal("no_events_in_scope", contract.NoDataReason);
    }

    [Fact]
    public void GcAndJit_RawCandidatesHonorLifetimeAndEndpointSemantics()
    {
        var identities = ReusedPidIdentities();
        var aggregate = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: null, identities);
        var exactFirst = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);

        Assert.False(GcAnalysis.MatchesRawScope(
            aggregate, identities, ReusedPid, timestampUs: 50));
        Assert.False(JitAnalysis.MatchesRawScope(
            exactFirst, identities, ReusedPid, timestampUs: 70));
        Assert.False(GcAnalysis.MatchesRawScope(
            exactFirst, identities, ReusedPid, timestampUs: 40));
        Assert.True(JitAnalysis.MatchesRawScope(
            exactFirst,
            identities,
            ReusedPid,
            timestampUs: 40,
            atEndpoint: true));
    }

    [Fact]
    public void NetConnection_GapAndOtherLifetimeDoNotBecomeScopedUnattributed()
    {
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 120,
            processLifetimes: ReusedPidLifetimes(),
            events:
            [
                new NetConnectionEvent(
                    Pid: ReusedPid,
                    ConnId: 7,
                    Kind: NetConnectionEventKind.Connect,
                    TimeUs: 50,
                    RemoteAddress: "10.0.0.1",
                    RemotePort: 443,
                    LocalAddress: "10.0.0.2",
                    LocalPort: 50000,
                    IsIPv6: false),
                new NetConnectionEvent(
                    Pid: ReusedPid,
                    ConnId: 8,
                    Kind: NetConnectionEventKind.Connect,
                    TimeUs: 70,
                    RemoteAddress: "10.0.0.3",
                    RemotePort: 443,
                    LocalAddress: "10.0.0.4",
                    LocalPort: 50001,
                    IsIPv6: false),
            ],
            pid: ReusedPid,
            top: 10,
            window: Window,
            processStartUs: 0);

        Assert.Equal(1, response.TraceIdentityUnresolvedEndpointCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedEndpointCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
    }

    [Fact]
    public void Wait_UnresolvedGapDoesNotChangeNoDataReason()
    {
        var identities = ReusedPidIdentities();
        var scope = ExactFirstProcessThreadScope(identities);
        var processScope = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);
        var projection = new WaitAnalysis.WaitProjectionAccumulator(
            scope,
            processScope,
            identities.Threads.StartUsFor,
            identities);
        projection.OnContextSwitch(new SchedulerSwitchObservation(
            OldThread: null,
            OldProcessName: string.Empty,
            NewThread: null,
            NewProcessName: string.Empty,
            TimestampUs: 50,
            BlockingStack: CallStackIndex.Invalid,
            OldPid: ReusedPid,
            OldTid: 7,
            OldIdentityUnresolved: true));
        projection.OnContextSwitch(new SchedulerSwitchObservation(
            OldThread: null,
            OldProcessName: string.Empty,
            NewThread: null,
            NewProcessName: string.Empty,
            TimestampUs: 70,
            BlockingStack: CallStackIndex.Invalid,
            OldPid: ReusedPid,
            OldTid: 7,
            OldIdentityUnresolved: true));

        var response = projection.Build(
            top: 10,
            unmatchedBlockedIntervalCount: 0,
            warnings: null);

        Assert.Equal(2, response.TraceIdentityUnresolvedCSwitchSideCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedCSwitchSideCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void BlockedTime_UnresolvedGapIsNotScoped()
    {
        var identities = ReusedPidIdentities();
        var scope = ExactFirstProcessThreadScope(identities);

        Assert.False(BlockedTimeStackAnalysis.IsScopedUnresolvedSide(
            scope, identities, ReusedPid, tid: 7, timestampUs: 50));
        Assert.True(BlockedTimeStackAnalysis.IsScopedUnresolvedSide(
            scope, identities, ReusedPid, tid: 7, timestampUs: 20));
        Assert.False(BlockedTimeStackAnalysis.IsScopedUnresolvedSide(
            scope, identities, ReusedPid, tid: 7, timestampUs: 70));
    }

    [Fact]
    public void ClrContention_UnresolvedEndpointHonorsLifetimeAndStopBoundary()
    {
        var identities = ReusedPidIdentities();
        var scope = ExactFirstProcessThreadScope(identities);

        Assert.False(ClrContentionStackAnalysis.IsScopedUnresolvedEndpoint(
            scope, identities, ReusedPid, tid: 7, timestampUs: 50));
        Assert.False(ClrContentionStackAnalysis.IsScopedUnresolvedEndpoint(
            scope, identities, ReusedPid, tid: 7, timestampUs: 70));
        Assert.False(ClrContentionStackAnalysis.IsScopedUnresolvedEndpoint(
            scope, identities, ReusedPid, tid: 7, timestampUs: 40));
        Assert.True(ClrContentionStackAnalysis.IsScopedUnresolvedEndpoint(
            scope,
            identities,
            ReusedPid,
            tid: 7,
            timestampUs: 40,
            atEndpoint: true));
    }

    [Fact]
    public void CpuPrecise_UnresolvedGapRemainsTraceWideButNotScoped()
    {
        var identities = ReusedPidIdentities();
        var scope = ExactFirstProcessThreadScope(identities);
        var processScope = ProcessAnalysisScope.Resolve(
            Window, ReusedPid, processStartUs: 0, identities);
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            scope,
            traceEndUs: 120,
            processScope: processScope,
            identities: identities);
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 50));
        accumulator.ReportUnresolvedCSwitchSide(
            ReusedPid, tid: 7, timestampUs: 50);
        accumulator.ReportUnresolvedCSwitchSide(
            ReusedPid, tid: 7, timestampUs: 70);

        var response = accumulator.BuildResponse();

        Assert.Equal(2, response.TraceIdentityUnresolvedCSwitchSideCount);
        Assert.Equal(0, response.ScopedIdentityUnresolvedCSwitchSideCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    private static ThreadAnalysisScope ExactFirstProcessThreadScope(
        TraceIdentityIndex identities) =>
        WaitTools.ResolveStackScope(
            Window,
            ReusedPid,
            tid: null,
            processStartUs: 0,
            threadStartUs: null,
            identities);

    private static TraceIdentityIndex ReusedPidIdentities() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 120,
            processes: ReusedPidLifetimes(),
            threads: []);

    private static TraceIdentityIndex AdjacentPidIdentities() =>
        TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 120,
            processes:
            [
                new(
                    new ProcessInstanceKey(ReusedPid, 0),
                    EndUs: 40,
                    StartObserved: true,
                    EndObserved: true),
                new(
                    new ProcessInstanceKey(ReusedPid, 40),
                    EndUs: 100,
                    StartObserved: true,
                    EndObserved: true),
            ],
            threads: []);

    private static ProcessLifetime[] ReusedPidLifetimes() =>
    [
        new(
            new ProcessInstanceKey(ReusedPid, 0),
            EndUs: 40,
            StartObserved: true,
            EndObserved: true),
        new(
            new ProcessInstanceKey(ReusedPid, 60),
            EndUs: 100,
            StartObserved: true,
            EndObserved: true),
    ];
}
