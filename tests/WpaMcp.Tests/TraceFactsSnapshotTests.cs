using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class TraceFactsSnapshotTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void InspectPlannerTelemetry_DistinguishesNewBuildFromReadySnapshotReuse()
    {
        var builds = 0;
        using var cache = CreateCache((trace, generation, cancellationToken) =>
        {
            Interlocked.Increment(ref builds);
            return Build(trace, generation, cancellationToken);
        });
        var tools = new MetaTools(cache);

        var first = tools.InspectTrace(FixturePath);
        var second = tools.InspectTrace(FixturePath);

        var firstTelemetry = Assert.IsType<WpaMcp.Output.PlannerExecutionTelemetry>(
            first.PlannerExecution);
        Assert.Equal("approved", firstTelemetry.AdmissionStatus);
        Assert.Equal("trace-facts.v1", firstTelemetry.OperationVersion);
        Assert.Equal("started_new_build", firstTelemetry.SnapshotAcquisition);
        Assert.Equal(1, firstTelemetry.PhysicalTracePassCount);
        Assert.Equal("measured_current_call_participation", firstTelemetry.PhysicalTracePassCountState);
        Assert.Equal(first.Trace.EventCount, firstTelemetry.ScannedEventCount);
        Assert.Equal("measured_generation_snapshot", firstTelemetry.ScannedEventCountState);
        Assert.Null(firstTelemetry.MatchedEventCount);
        Assert.Equal(
            "not_applicable_no_scoped_match_predicate",
            firstTelemetry.MatchedEventCountState);
        Assert.Equal(
            new[] { "planner_admission", "trace_facts_acquisition", "result_projection" },
            firstTelemetry.PhaseDurations.Select(phase => phase.Phase));
        Assert.All(firstTelemetry.PhaseDurations, phase => Assert.True(phase.DurationUs >= 0));
        Assert.Contains(
            "generation_snapshot_build_duration_is_not_replayed_as_current_call_time",
            firstTelemetry.EvidenceBoundaries);

        var secondTelemetry = Assert.IsType<WpaMcp.Output.PlannerExecutionTelemetry>(
            second.PlannerExecution);
        Assert.Equal("ready_snapshot_reuse", secondTelemetry.SnapshotAcquisition);
        Assert.Equal(0, secondTelemetry.PhysicalTracePassCount);
        Assert.Equal(firstTelemetry.ScannedEventCount, secondTelemetry.ScannedEventCount);
        Assert.True(secondTelemetry.ScannedEventCount > 0);
        Assert.Equal(1, Volatile.Read(ref builds));
    }

    [Fact]
    public async Task ConcurrentInspectPlannerTelemetry_DistinguishesNewBuildFromJoinedFlight()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cache = CreateCache((trace, generation, cancellationToken) =>
        {
            started.Set();
            release.Wait(cancellationToken);
            return Build(trace, generation, cancellationToken);
        });
        using var observer = cache.Acquire(FixturePath);
        var tools = new MetaTools(cache);

        var leader = Task.Run(() => tools.InspectTrace(FixturePath));
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        var joiner = Task.Run(() => tools.InspectTrace(FixturePath));
        Assert.True(SpinWait.SpinUntil(
            () => observer.FactsTelemetry.ActiveWaiterCount == 2,
            TimeSpan.FromSeconds(30)));
        release.Set();

        var responses = await Task.WhenAll(leader, joiner).WaitAsync(TimeSpan.FromSeconds(30));
        var telemetry = responses.Select(response =>
            Assert.IsType<WpaMcp.Output.PlannerExecutionTelemetry>(response.PlannerExecution)).ToArray();
        Assert.Contains(telemetry, item => item.SnapshotAcquisition == "started_new_build");
        Assert.Contains(telemetry, item => item.SnapshotAcquisition == "joined_in_flight");
        Assert.All(telemetry, item => Assert.Equal(1, item.PhysicalTracePassCount));
        Assert.All(telemetry, item => Assert.Equal(responses[0].Trace.EventCount, item.ScannedEventCount));
        Assert.Equal(1, observer.FactsTelemetry.PhysicalPassCount);
    }

    [Theory]
    [InlineData("diagnose_window")]
    [InlineData("diagnose_high_wait")]
    [InlineData("diagnose_slow_startup")]
    public void NonAdmittedCompositePlannerTelemetry_UsesNullUnavailableCounts(string toolName)
    {
        var telemetry = new QueryPlanner(ActiveToolCatalog.LoadAndValidate())
            .DescribeNotAdmitted(toolName);

        Assert.Equal("not_admitted_evidence_missing", telemetry.AdmissionStatus);
        Assert.Equal("direct_tool_execution_planner_not_admitted", telemetry.ExecutionStatus);
        Assert.Equal("not_admitted", telemetry.SnapshotAcquisition);
        Assert.NotEmpty(telemetry.MissingEvidence);
        Assert.Empty(telemetry.LogicalAnalyzersExecuted);
        Assert.Null(telemetry.PhysicalTracePassCount);
        Assert.Null(telemetry.ScannedEventCount);
        Assert.Null(telemetry.MatchedEventCount);
        Assert.Equal("unavailable_not_admitted", telemetry.PhysicalTracePassCountState);
        Assert.Equal("unavailable_not_admitted", telemetry.ScannedEventCountState);
        Assert.Equal("unavailable_not_admitted", telemetry.MatchedEventCountState);
        Assert.Null(telemetry.PhysicalPassLimit);
        Assert.Contains("no_single_dispatch_claim", telemetry.EvidenceBoundaries);
    }

    [Fact]
    public void InspectPlannerTelemetry_HasClosedReviewedOutputSchema()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<InspectTraceResponse>();

        Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        Assert.Empty(ToolOutputSchemaLinter.LintReviewedNumericClosure(schema));
        var json = schema.ToJsonString();
        Assert.Contains("plannerExecution", json, StringComparison.Ordinal);
        Assert.Contains("physicalTracePassCountState", json, StringComparison.Ordinal);
        Assert.Contains("scannedEventCountState", json, StringComparison.Ordinal);
        Assert.Contains("matchedEventCountState", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentConsumers_ShareOneGenerationPhysicalPass()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var builds = 0;
        using var cache = CreateCache((trace, generation, cancellationToken) =>
        {
            Interlocked.Increment(ref builds);
            started.Set();
            release.Wait(cancellationToken);
            return Build(trace, generation, cancellationToken);
        });
        using var lease = cache.Acquire(FixturePath);

        var requests = Enumerable.Range(0, 8)
            .Select(_ => lease.GetFactsAsync(CancellationToken.None))
            .ToArray();
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        Assert.True(SpinWait.SpinUntil(
            () => lease.FactsTelemetry.ActiveWaiterCount == requests.Length,
            TimeSpan.FromSeconds(30)));

        release.Set();
        var snapshots = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(snapshots, snapshot => Assert.Same(snapshots[0], snapshot));
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.Equal(requests.Length, lease.FactsTelemetry.LogicalRequestCount);
        Assert.Equal(1, lease.FactsTelemetry.PhysicalPassCount);
        Assert.Equal("ready", lease.FactsTelemetry.State);
    }

    [Fact]
    public async Task ImmediatelyCompletedBuild_IsPinnedBeforeCompletionObserverRuns()
    {
        using var seedCache = CreateCache(Build);
        using var seedLease = seedCache.Acquire(FixturePath);
        var snapshot = seedLease.GetFacts(CancellationToken.None);
        var pins = 0;
        using var facts = new TraceFactsSnapshotCache(
            snapshot.GenerationSequence,
            _ => snapshot,
            () => Interlocked.Increment(ref pins),
            () => Interlocked.Decrement(ref pins),
            startBuilderTask: work => Task.FromResult(work()));

        var acquisitions = await Task.WhenAll(
            Enumerable.Range(0, 16)
                .Select(_ => facts.GetAcquisitionAsync(CancellationToken.None)));

        Assert.All(acquisitions, acquisition => Assert.Same(snapshot, acquisition.Snapshot));
        Assert.Equal(1, facts.GetTelemetry().PhysicalPassCount);
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref pins) == 0,
            TimeSpan.FromSeconds(5)));
        Assert.Equal("ready", facts.GetTelemetry().State);
    }

    [Fact]
    public async Task InspectCancellation_IsWaiterLocal_AndCannotProduceLateSuccess()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var builds = 0;
        using var cache = CreateCache((trace, generation, operationCancellation) =>
        {
            Interlocked.Increment(ref builds);
            started.Set();
            release.Wait(operationCancellation);
            return Build(trace, generation, operationCancellation);
        });
        using var observer = cache.Acquire(FixturePath);
        var tools = new MetaTools(cache);
        using var leaderCancellation = new CancellationTokenSource();

        var leader = Task.Run(() =>
            tools.InspectTrace(FixturePath, cancellationToken: leaderCancellation.Token));
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        var follower = Task.Run(() =>
            tools.InspectTrace(FixturePath, cancellationToken: CancellationToken.None));
        Assert.True(SpinWait.SpinUntil(
            () => observer.FactsTelemetry.ActiveWaiterCount == 2,
            TimeSpan.FromSeconds(30)));

        leaderCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await leader.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(follower.IsCompleted);
        Assert.Equal(1, Volatile.Read(ref builds));

        release.Set();
        var response = await follower.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(response.Trace.EventCount > 0);
        Assert.Equal("joined_in_flight", response.PlannerExecution!.SnapshotAcquisition);
        Assert.Equal(1, response.PlannerExecution.PhysicalTracePassCount);
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.True(leader.IsCanceled || leader.IsFaulted);
    }

    [Fact]
    public async Task AllWaitersCancelled_CancelsPhysicalScan_AndLaterRequestRetries()
    {
        using var started = new ManualResetEventSlim();
        using var operationCancelled = new ManualResetEventSlim();
        var builds = 0;
        using var cache = CreateCache((trace, generation, cancellationToken) =>
        {
            var invocation = Interlocked.Increment(ref builds);
            if (invocation == 1)
            {
                started.Set();
                cancellationToken.WaitHandle.WaitOne();
                operationCancelled.Set();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return Build(trace, generation, cancellationToken);
        });
        using var observer = cache.Acquire(FixturePath);
        var tools = new MetaTools(cache);
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();

        var first = Task.Run(() =>
            tools.InspectTrace(FixturePath, cancellationToken: firstCancellation.Token));
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
        var second = Task.Run(() =>
            tools.InspectTrace(FixturePath, cancellationToken: secondCancellation.Token));
        Assert.True(SpinWait.SpinUntil(
            () => observer.FactsTelemetry.ActiveWaiterCount == 2,
            TimeSpan.FromSeconds(30)));

        firstCancellation.Cancel();
        secondCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(operationCancelled.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(
            () => observer.FactsTelemetry.State == "not_started",
            TimeSpan.FromSeconds(5)));

        var retry = tools.InspectTrace(FixturePath);
        Assert.True(retry.Trace.EventCount > 0);
        Assert.Equal("started_new_build", retry.PlannerExecution!.SnapshotAcquisition);
        Assert.Equal(1, retry.PlannerExecution.PhysicalTracePassCount);
        Assert.Equal(2, Volatile.Read(ref builds));
        Assert.Equal(2, observer.FactsTelemetry.PhysicalPassCount);
        Assert.Equal("ready", observer.FactsTelemetry.State);
    }

    [Fact]
    public async Task RetiringGeneration_DrainsInFlightFactsBeforeTraceDisposal()
    {
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var disposeCount = 0;
        using var cache = CreateCache(
            (trace, generation, cancellationToken) =>
            {
                started.Set();
                release.Wait(cancellationToken);
                return Build(trace, generation, cancellationToken);
            },
            () => Interlocked.Increment(ref disposeCount));
        var lease = cache.Acquire(FixturePath);
        var generation = lease.GenerationIdentity;
        var factsTask = lease.GetFactsAsync(CancellationToken.None);
        Assert.True(started.Wait(TimeSpan.FromSeconds(30)));

        Assert.True(cache.RetireGeneration(generation));
        lease.Dispose();
        Assert.Equal(0, Volatile.Read(ref disposeCount));

        release.Set();
        var facts = await factsTask.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(facts.LogicalEventCount > 0);
        Assert.True(SpinWait.SpinUntil(
            () => Volatile.Read(ref disposeCount) == 1,
            TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void GenerationRetirement_InvalidatesFactsWithoutMutatingOldSnapshot()
    {
        using var cache = CreateCache(Build);
        using var first = cache.Acquire(FixturePath);
        var firstFacts = first.GetFacts(CancellationToken.None);
        var firstGeneration = first.GenerationIdentity;
        var firstEventCount = firstFacts.LogicalEventCount;

        Assert.True(cache.RetireGeneration(firstGeneration));
        using var second = cache.Acquire(FixturePath);
        var secondFacts = second.GetFacts(CancellationToken.None);

        Assert.NotEqual(firstFacts.GenerationSequence, secondFacts.GenerationSequence);
        Assert.NotSame(firstFacts, secondFacts);
        Assert.Equal(firstEventCount, firstFacts.LogicalEventCount);
        Assert.Equal(firstEventCount, secondFacts.LogicalEventCount);
        Assert.Equal(1, first.FactsTelemetry.PhysicalPassCount);
        Assert.Equal(1, second.FactsTelemetry.PhysicalPassCount);
    }

    [Fact]
    public void InspectListAndIdentity_ReuseSnapshotAndPreserveEstablishedFacts()
    {
        var builds = 0;
        using var cache = CreateCache((trace, generation, cancellationToken) =>
        {
            Interlocked.Increment(ref builds);
            return Build(trace, generation, cancellationToken);
        });
        using var lease = cache.Acquire(FixturePath);
        var trace = lease.Trace;

        var identity = TraceIdentityIndex.For(trace);
        var inspect = new MetaTools(cache).InspectTrace(FixturePath);
        var inspectMetadata = Assert.IsType<TraceMetadata>(inspect.Metadata);
        var processes = new MetaTools(cache).ListProcesses(
            FixturePath,
            top: 1000,
            includeSystem: true);
        var facts = lease.GetFacts(CancellationToken.None);
        var expectedProcesses = ProcessProjection.Rows(trace, includeSystem: true);

        Assert.Same(identity, facts.Identity);
        Assert.Equal(trace.EventCount, facts.LogicalEventCount);
        Assert.Equal(trace.EventsLost, facts.CaptureIntegrity.ReportedEventsLost);
        Assert.Equal(facts.LogicalEventCount, inspect.Trace.EventCount);
        Assert.Equal(facts.LogicalEventCount, inspectMetadata.ProviderEvents.TotalEventCount);
        Assert.Equal(expectedProcesses, facts.Processes);
        Assert.Equal(
            expectedProcesses
                .OrderByDescending(row => row.CpuUs)
                .ThenBy(row => row.Pid)
                .ThenBy(row => row.StartUs),
            processes.Rows);
        Assert.Equal("TraceLog.EventsLost", facts.CaptureIntegrity.MeasurementBasis);
        Assert.Contains("PDB", facts.Provenance.SymbolEvidence, StringComparison.Ordinal);
        Assert.Equal(1, Volatile.Read(ref builds));
        Assert.Equal(1, lease.FactsTelemetry.PhysicalPassCount);
        Assert.Equal(4, lease.FactsTelemetry.LogicalRequestCount);
    }

    [Fact]
    public void FactsBudgetFailure_IsStructuredAndDoesNotPoisonGenerationCache()
    {
        using var cache = CreateCache(Build);
        using var lease = cache.Acquire(FixturePath);

        var failure = Assert.Throws<TraceFactsSnapshotException>(() =>
            TraceFactsSnapshotBuilder.Build(
                lease.Trace,
                lease.GenerationIdentity.Sequence,
                CancellationToken.None,
                new TraceFactsBuildBudget(
                    MaxLogicalEvents: 1,
                    MaxElapsed: TimeSpan.FromMinutes(1))));
        Assert.Equal("budget_exceeded", failure.Code);
        Assert.Equal("trace_facts_budget_exceeded", failure.DetailCode);
        var publicError = ContractMcpServerTool.MapException(failure);
        Assert.Equal("budget_exceeded", publicError.Code);
        Assert.DoesNotContain(failure.DetailCode, publicError.Message, StringComparison.Ordinal);

        var facts = lease.GetFacts(CancellationToken.None);
        Assert.True(facts.LogicalEventCount > 1);
        Assert.Equal(1, lease.FactsTelemetry.PhysicalPassCount);
    }

    private static TraceFactsSnapshot Build(
        TraceLog trace,
        long generation,
        CancellationToken cancellationToken) =>
        TraceFactsSnapshotBuilder.Build(
            trace,
            generation,
            cancellationToken,
            TraceFactsBuildBudget.Default);

    private static TraceCache CreateCache(
        Func<TraceLog, long, CancellationToken, TraceFactsSnapshot> factsBuilder,
        Action? disposed = null) =>
        new(
            capacity: 2,
            openTrace: static path => TraceLog.OpenOrConvert(path),
            disposeTrace: trace =>
            {
                trace.Dispose();
                disposed?.Invoke();
            },
            factsBuilder: factsBuilder);
}
