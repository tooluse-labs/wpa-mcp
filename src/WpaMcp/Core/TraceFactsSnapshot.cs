using System.Diagnostics;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record TraceFactsBuildBudget(
    long MaxLogicalEvents,
    TimeSpan MaxElapsed)
{
    internal static TraceFactsBuildBudget Default { get; } = new(
        MaxLogicalEvents: 250_000_000,
        MaxElapsed: TimeSpan.FromMinutes(10));

    internal void ThrowIfExceeded(
        long logicalEvents,
        Stopwatch elapsed,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (logicalEvents > MaxLogicalEvents || elapsed.Elapsed > MaxElapsed)
        {
            throw new TraceFactsSnapshotException(
                "trace_facts_budget_exceeded",
                "The generation facts scan exceeded its bounded event or elapsed-time budget.");
        }
    }
}

internal sealed class TraceFactsSnapshotException : Exception
{
    internal TraceFactsSnapshotException(string detailCode, string message)
        : base(message)
    {
        DetailCode = detailCode;
        ToolFailureCaptureContext.Record(this);
    }

    internal string Code => "budget_exceeded";
    internal string DetailCode { get; }
}

internal sealed record TracePdbIdentityFact(
    string ModuleName,
    string? ModulePath,
    string? PdbName,
    Guid PdbSignature,
    int PdbAge);

internal sealed record TraceCaptureIntegrityFacts(
    long ReportedEventsLost,
    string State,
    string MeasurementBasis);

internal sealed record TraceFactsProvenance(
    string EventCountRepresentation,
    string EventParser,
    string IdentityDerivation,
    string StackCoverageDenominators,
    string SymbolEvidence);

internal sealed record TraceFactsSnapshot(
    long GenerationSequence,
    TraceCapabilities Capabilities,
    TraceMetadata Metadata,
    TraceIdentityIndex Identity,
    IReadOnlyList<ProcessRow> Processes,
    IReadOnlyList<TracePdbIdentityFact> PdbIdentities,
    TraceCaptureIntegrityFacts CaptureIntegrity,
    TraceFactsProvenance Provenance,
    long DurationUs,
    long LogicalEventCount,
    long EventsWithAttachedStacks,
    TimeSpan PhysicalScanElapsed);

internal sealed record TraceFactsScanTelemetry(
    long GenerationSequence,
    long LogicalRequestCount,
    long PhysicalPassCount,
    string State,
    int ActiveWaiterCount);

internal enum TraceFactsAcquisitionKind
{
    ReadySnapshotReuse,
    JoinedInFlight,
    StartedNewBuild,
}

/// <summary>
/// Per-caller facts acquisition evidence. ParticipatingPhysicalPassCount is scoped
/// to this caller; it is deliberately not the generation's cumulative pass count.
/// </summary>
internal sealed record TraceFactsAcquisition(
    TraceFactsSnapshot Snapshot,
    TraceFactsAcquisitionKind Kind,
    int ParticipatingPhysicalPassCount);

internal static class TraceFactsSnapshotBuilder
{
    internal static TraceFactsSnapshot Build(
        TraceLog trace,
        long generationSequence,
        CancellationToken cancellationToken,
        TraceFactsBuildBudget budget)
    {
        var scan = TraceCapabilitiesDetector.Scan(trace, cancellationToken, budget);
        cancellationToken.ThrowIfCancellationRequested();
        var metadata = TraceMetadataAnalysis.AnalyzeFromScan(
            trace,
            scan.LogicalEvents);
        IReadOnlyList<ProcessRow> processes = Array.AsReadOnly(
            ProcessProjection.Rows(trace, includeSystem: true).ToArray());
        IReadOnlyList<TracePdbIdentityFact> pdbIdentities = Array.AsReadOnly(
            trace.ModuleFiles
                .Select(module => new TracePdbIdentityFact(
                    module.Name ?? string.Empty,
                    module.FilePath,
                    module.PdbName,
                    module.PdbSignature,
                    module.PdbAge))
                .ToArray());
        var eventsLost = trace.EventsLost;
        return new TraceFactsSnapshot(
            generationSequence,
            scan.Capabilities,
            metadata,
            scan.Identity,
            processes,
            pdbIdentities,
            new TraceCaptureIntegrityFacts(
                eventsLost,
                eventsLost > 0 ? "reported_event_loss" : "no_reported_event_loss",
                "TraceLog.EventsLost"),
            new TraceFactsProvenance(
                "tracelog_etlx_materialized_logical_events",
                "TraceEvent typed parsers plus AllEvents in one dispatcher pass",
                "kernel process/thread lifecycle endpoints plus TraceLog process-table backfill",
                "global attached-event denominator and event-domain-specific stack/metric denominators",
                "trace PDB name/GUID/age identity only; no readiness probe or frame lookup"),
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds),
            scan.LogicalEvents.TotalLogicalEvents,
            scan.LogicalEvents.EventsWithAttachedStacks,
            scan.Elapsed);
    }
}

/// <summary>
/// One immutable facts snapshot cache per TraceCache generation. Logical waiters
/// share one physical event pass; cancellation is waiter-local until no waiters
/// remain, at which point the owned operation is cancelled and can be retried.
/// </summary>
internal sealed class TraceFactsSnapshotCache : IDisposable
{
    private readonly object _gate = new();
    private readonly long _generationSequence;
    private readonly Func<CancellationToken, TraceFactsSnapshot> _builder;
    private readonly Func<Func<TraceFactsSnapshot>, Task<TraceFactsSnapshot>> _startBuilderTask;
    private readonly Action _acquireOperationPin;
    private readonly Action _releaseOperationPin;
    private TraceFactsSnapshot? _snapshot;
    private Flight? _flight;
    private long _logicalRequests;
    private long _physicalPasses;
    private bool _disposed;

    internal TraceFactsSnapshotCache(
        long generationSequence,
        Func<CancellationToken, TraceFactsSnapshot> builder,
        Action acquireOperationPin,
        Action releaseOperationPin,
        Func<Func<TraceFactsSnapshot>, Task<TraceFactsSnapshot>>? startBuilderTask = null)
    {
        _generationSequence = generationSequence;
        _builder = builder;
        _acquireOperationPin = acquireOperationPin;
        _releaseOperationPin = releaseOperationPin;
        _startBuilderTask = startBuilderTask ??
            (work => Task.Run(work, CancellationToken.None));
    }

    internal TraceFactsSnapshot Get(
        CancellationToken cancellationToken = default) =>
        GetAcquisitionAsync(cancellationToken).GetAwaiter().GetResult().Snapshot;

    internal async Task<TraceFactsSnapshot> GetAsync(
        CancellationToken cancellationToken = default) =>
        (await GetAcquisitionAsync(cancellationToken).ConfigureAwait(false)).Snapshot;

    internal TraceFactsAcquisition GetAcquisition(
        CancellationToken cancellationToken = default) =>
        GetAcquisitionAsync(cancellationToken).GetAwaiter().GetResult();

    internal async Task<TraceFactsAcquisition> GetAcquisitionAsync(
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _logicalRequests);
        TraceFactsAcquisitionKind? acquisitionKind = null;
        var participatingPhysicalPasses = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Flight flight;
            var observeCompletion = false;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_snapshot is not null)
                {
                    return new TraceFactsAcquisition(
                        _snapshot,
                        acquisitionKind ?? TraceFactsAcquisitionKind.ReadySnapshotReuse,
                        participatingPhysicalPasses);
                }

                var startedNewBuild = false;
                if (_flight is null)
                {
                    _acquireOperationPin();
                    try
                    {
                        var operationCancellation = new CancellationTokenSource();
                        var task = _startBuilderTask(
                            () => _builder(operationCancellation.Token));
                        flight = new Flight(task, operationCancellation);
                        _flight = flight;
                        Interlocked.Increment(ref _physicalPasses);
                        // The caller must own a waiter before a completion observer
                        // can clear/dispose an already-completed flight.
                        flight.AddWaiter();
                        observeCompletion = true;
                        startedNewBuild = true;
                    }
                    catch
                    {
                        _releaseOperationPin();
                        throw;
                    }
                }
                else
                {
                    flight = _flight;
                    flight.AddWaiter();
                }
                acquisitionKind ??= startedNewBuild
                    ? TraceFactsAcquisitionKind.StartedNewBuild
                    : TraceFactsAcquisitionKind.JoinedInFlight;
                checked { participatingPhysicalPasses++; }
            }

            if (observeCompletion)
                _ = ObserveCompletionAsync(flight);

            try
            {
                var snapshot = await flight.Task.WaitAsync(cancellationToken)
                    .ConfigureAwait(false);
                return new TraceFactsAcquisition(
                    snapshot,
                    acquisitionKind!.Value,
                    participatingPhysicalPasses);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                flight.OperationCancellationRequested)
            {
                // A new caller raced the teardown of a flight whose prior waiters
                // all cancelled. Wait for teardown, then start/join a fresh pass.
                try
                {
                    await flight.Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                await Task.Yield();
            }
            finally
            {
                flight.ReleaseWaiter();
            }
        }
    }

    internal TraceFactsScanTelemetry GetTelemetry()
    {
        lock (_gate)
        {
            return new TraceFactsScanTelemetry(
                _generationSequence,
                Interlocked.Read(ref _logicalRequests),
                Interlocked.Read(ref _physicalPasses),
                _snapshot is not null
                    ? "ready"
                    : _flight is not null
                        ? "building"
                        : _disposed
                            ? "disposed"
                            : "not_started",
                _flight?.WaiterCount ?? 0);
        }
    }

    internal bool TryGetReady(out TraceFactsSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_snapshot is not null)
            {
                snapshot = _snapshot;
                return true;
            }

            snapshot = null!;
            return false;
        }
    }

    private async Task ObserveCompletionAsync(Flight flight)
    {
        TraceFactsSnapshot? completed = null;
        try
        {
            completed = await flight.Task.ConfigureAwait(false);
        }
        catch
        {
            // Every waiter observes the original failure. A later request gets a
            // clean flight rather than a poisoned Lazy exception.
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_flight, flight))
                {
                    if (completed is not null)
                        _snapshot = completed;
                    _flight = null;
                }
            }
            flight.Dispose();
            _releaseOperationPin();
        }
    }

    public void Dispose()
    {
        Flight? flight;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            flight = _flight;
            _snapshot = null;
        }
        flight?.Cancel();
    }

    private sealed class Flight(
        Task<TraceFactsSnapshot> task,
        CancellationTokenSource operationCancellation) : IDisposable
    {
        private int _waiters;
        private int _disposed;

        internal Task<TraceFactsSnapshot> Task { get; } = task;
        internal bool OperationCancellationRequested =>
            operationCancellation.IsCancellationRequested;
        internal int WaiterCount => Volatile.Read(ref _waiters);

        internal void AddWaiter() => Interlocked.Increment(ref _waiters);

        internal void ReleaseWaiter()
        {
            if (Interlocked.Decrement(ref _waiters) == 0 && !Task.IsCompleted)
                Cancel();
        }

        internal void Cancel()
        {
            try
            {
                operationCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                operationCancellation.Dispose();
        }
    }
}
