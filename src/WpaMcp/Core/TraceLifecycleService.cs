using System.Collections.Concurrent;

namespace WpaMcp.Core;

internal readonly record struct TraceSourceFlightKey(
    string ComparableFinalPath,
    uint VolumeSerialNumber,
    ulong FileId,
    long Length,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc)
{
    internal static TraceSourceFlightKey From(TraceSourceHandleIdentity identity) =>
        new(
            identity.FinalPath.ToUpperInvariant(),
            identity.VolumeSerialNumber,
            identity.FileId,
            identity.Length,
            identity.CreationTimeUtc,
            identity.LastWriteTimeUtc);
}

internal enum TraceSourceValidationEvidence
{
    OpenedHandleSnapshotContentHash,
    CachedFileIdentityLengthAndTimestamps,
}

internal sealed record TraceArtifactLoadResult(
    OwnedTraceArtifact Artifact,
    TraceSourceValidationEvidence SourceValidation);

internal sealed class TraceArtifactLoader
{
    private const int MaxKnownGenerations = 128;
    private readonly TraceAccessPolicy _accessPolicy;
    private readonly OwnedTraceArtifactStore _artifactStore;
    private readonly ConcurrentDictionary<
        TraceSourceFlightKey,
        MaterializationFlight> _inflight = [];
    private readonly object _knownGate = new();
    private readonly Dictionary<TraceSourceFlightKey, KnownArtifact> _known = [];
    private readonly LinkedList<TraceSourceFlightKey> _knownLru = [];

    internal TraceArtifactLoader(
        TraceAccessPolicy accessPolicy,
        OwnedTraceArtifactStore artifactStore)
    {
        _accessPolicy = accessPolicy;
        _artifactStore = artifactStore;
    }

    internal async Task<TraceArtifactLoadResult> LoadAsync(
        string rawPath,
        bool forceRefresh,
        CancellationToken cancellationToken = default)
    {
        ValidatedTraceSource? source = await _accessPolicy.OpenAsync(
            rawPath,
            cancellationToken).ConfigureAwait(false);
        try
        {
            var key = TraceSourceFlightKey.From(source.Identity);

            if (!forceRefresh && TryGetKnown(key, out var known))
            {
                return new TraceArtifactLoadResult(
                    known,
                    TraceSourceValidationEvidence.CachedFileIdentityLengthAndTimestamps);
            }

            // A flight owns the opened source handle and uses an operation lifetime
            // independent of every waiter. Caller cancellation only stops that
            // caller's wait; it cannot cancel or dispose shared materialization.
            var candidate = new MaterializationFlight(
                source,
                flight => MaterializeFlightAsync(key, flight));
            source = null;
            var flight = _inflight.GetOrAdd(key, candidate);
            if (!ReferenceEquals(flight, candidate))
                await candidate.DisposeAsync().ConfigureAwait(false);

            var artifact = await flight.WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new TraceArtifactLoadResult(
                artifact,
                TraceSourceValidationEvidence.OpenedHandleSnapshotContentHash);
        }
        finally
        {
            if (source is not null)
                await source.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<OwnedTraceArtifact> MaterializeFlightAsync(
        TraceSourceFlightKey key,
        MaterializationFlight flight)
    {
        try
        {
            var artifact = await _artifactStore.GetOrCreateAsync(
                flight.Source,
                flight.OperationToken).ConfigureAwait(false);
            RememberKnown(key, artifact);
            return artifact;
        }
        finally
        {
            await flight.DisposeAsync().ConfigureAwait(false);
            TryRemoveExact(key, flight);
        }
    }

    internal int CachedGenerationCount
    {
        get
        {
            lock (_knownGate)
                return _known.Count;
        }
    }

    internal int InFlightCount => _inflight.Count;

    internal int InFlightWaiterCount =>
        _inflight.Values.Sum(flight => flight.WaiterCount);

    internal Task<OwnedTraceArtifactLease> AcquireLeaseAsync(
        OwnedTraceArtifact artifact,
        CancellationToken cancellationToken) =>
        _artifactStore.AcquireLeaseAsync(artifact, cancellationToken);

    internal void ForgetKnown(OwnedTraceArtifact artifact)
    {
        lock (_knownGate)
        {
            foreach (var pair in _known
                         .Where(pair => string.Equals(
                             pair.Value.Artifact.ArtifactKey,
                             artifact.ArtifactKey,
                             StringComparison.Ordinal))
                         .ToArray())
            {
                _known.Remove(pair.Key);
                _knownLru.Remove(pair.Value.Node);
            }
        }
    }

    private bool TryGetKnown(
        TraceSourceFlightKey key,
        out OwnedTraceArtifact artifact)
    {
        lock (_knownGate)
        {
            if (!_known.TryGetValue(key, out var known) ||
                !File.Exists(known.Artifact.TracePath))
            {
                if (known is not null)
                {
                    _known.Remove(key);
                    _knownLru.Remove(known.Node);
                }
                artifact = null!;
                return false;
            }

            _knownLru.Remove(known.Node);
            _knownLru.AddLast(known.Node);
            artifact = known.Artifact;
            return true;
        }
    }

    private void RememberKnown(
        TraceSourceFlightKey key,
        OwnedTraceArtifact artifact)
    {
        lock (_knownGate)
        {
            if (_known.TryGetValue(key, out var existing))
            {
                existing.Artifact = artifact;
                _knownLru.Remove(existing.Node);
                _knownLru.AddLast(existing.Node);
            }
            else
            {
                var node = _knownLru.AddLast(key);
                _known.Add(key, new KnownArtifact(artifact, node));
            }

            while (_known.Count > MaxKnownGenerations)
            {
                var oldest = _knownLru.First!;
                _knownLru.RemoveFirst();
                _known.Remove(oldest.Value);
            }
        }
    }

    private bool TryRemoveExact(
        TraceSourceFlightKey key,
        MaterializationFlight expected) =>
        ((ICollection<KeyValuePair<
            TraceSourceFlightKey,
            MaterializationFlight>>)_inflight)
        .Remove(new KeyValuePair<
            TraceSourceFlightKey,
            MaterializationFlight>(key, expected));

    private sealed class MaterializationFlight : IAsyncDisposable
    {
        private ValidatedTraceSource? _source;
        private readonly Lazy<Task<OwnedTraceArtifact>> _task;
        private readonly CancellationTokenSource _operationCancellation = new();
        private int _waiters;
        private int _disposed;

        internal MaterializationFlight(
            ValidatedTraceSource source,
            Func<MaterializationFlight, Task<OwnedTraceArtifact>> materialize)
        {
            _source = source;
            _task = new Lazy<Task<OwnedTraceArtifact>>(
                () => materialize(this),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        internal ValidatedTraceSource Source =>
            Volatile.Read(ref _source)
            ?? throw new ObjectDisposedException(nameof(MaterializationFlight));

        internal CancellationToken OperationToken => _operationCancellation.Token;
        internal int WaiterCount => Volatile.Read(ref _waiters);

        internal async Task<OwnedTraceArtifact> WaitAsync(
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _waiters);
            var task = _task.Value;
            try
            {
                return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (Interlocked.Decrement(ref _waiters) == 0 && !task.IsCompleted)
                {
                    try
                    {
                        _operationCancellation.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Operation completion won the race and already disposed it.
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            var source = Interlocked.Exchange(ref _source, null);
            try
            {
                if (source is not null)
                    await source.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                _operationCancellation.Dispose();
            }
        }
    }

    private sealed class KnownArtifact(
        OwnedTraceArtifact artifact,
        LinkedListNode<TraceSourceFlightKey> node)
    {
        internal OwnedTraceArtifact Artifact { get; set; } = artifact;
        internal LinkedListNode<TraceSourceFlightKey> Node { get; } = node;
    }
}

internal sealed record TraceLifecycleLoadResult(
    TraceHandleLoadResult Handle,
    OwnedTraceArtifact Artifact,
    TraceSourceValidationEvidence SourceValidation,
    bool ForceRefreshApplied);

internal sealed class TraceLifecycleService(
    TraceArtifactLoader loader,
    TraceHandleRegistry registry)
{
    internal TraceLifecycleLoadResult Load(
        string principal,
        string rawPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        TraceArtifactLoadResult loaded;
        OwnedTraceArtifactLease artifactLease;
        for (var attempt = 0; ; attempt++)
        {
            loaded = loader.LoadAsync(rawPath, forceRefresh, cancellationToken)
                .GetAwaiter().GetResult();
            try
            {
                artifactLease = loader.AcquireLeaseAsync(
                        loaded.Artifact,
                        cancellationToken)
                    .GetAwaiter().GetResult();
                break;
            }
            catch (TraceAccessException ex) when (
                attempt == 0 && ex.Code == "trace_artifact_expired")
            {
                // A retention pass may retire an unpinned known object between the
                // bounded known-cache lookup and handle pinning. Forget and retry
                // one complete secure materialization; never follow the stale path.
                loader.ForgetKnown(loaded.Artifact);
            }
        }

        try
        {
            var handle = registry.Load(
                principal,
                artifactLease.TracePath,
                forceRefresh: false,
                cancellationToken);
            artifactLease.VerifyUnchanged();
            if (handle.ReusedExisting)
            {
                artifactLease.Dispose();
            }
            else
            {
                registry.RegisterDrainCallback(
                    principal,
                    handle.TraceId,
                    artifactLease.Dispose);
                artifactLease = null!;
            }
            return new TraceLifecycleLoadResult(
                handle,
                loaded.Artifact,
                loaded.SourceValidation,
                forceRefresh);
        }
        finally
        {
            artifactLease?.Dispose();
        }
    }

    internal ValueTask<TraceLifecycleLoadResult> LoadAsync(
        string principal,
        string rawPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        new(Load(principal, rawPath, forceRefresh, cancellationToken));
}

/// <summary>
/// Public DI seam for tool activation. Its state and operations remain internal so
/// registry principals and owned artifact paths cannot become an accidental API.
/// </summary>
public sealed class TraceToolRuntime
{
    private readonly TraceLifecycleService _lifecycle;
    private readonly TraceHandleRegistry _registry;
    private readonly StdioSessionPrincipal _principal;

    internal TraceToolRuntime(
        TraceLifecycleService lifecycle,
        TraceHandleRegistry registry,
        StdioSessionPrincipal principal)
    {
        _lifecycle = lifecycle;
        _registry = registry;
        _principal = principal;
    }

    internal TraceLifecycleLoadResult Load(
        string rawPath,
        bool forceRefresh,
        CancellationToken cancellationToken) =>
        _lifecycle.Load(
            _principal.RegistryKey,
            rawPath,
            forceRefresh,
            cancellationToken);

    internal TraceHandleLease Acquire(
        string traceId,
        CancellationToken cancellationToken) =>
        _registry.Acquire(
            _principal.RegistryKey,
            traceId,
            cancellationToken);

    internal TraceHandleUnloadResult Unload(string traceId) =>
        _registry.Unload(_principal.RegistryKey, traceId);
}
