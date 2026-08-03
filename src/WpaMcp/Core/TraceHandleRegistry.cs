using System.Security.Cryptography;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Analyzers;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal enum TraceAccessMode
{
    Compatibility,
    IdOnly,
}

internal enum TraceHandlePersistence
{
    Persistent,
    Ephemeral,
}

internal enum TraceSourceGenerationAssurance
{
    /// <summary>
    /// The current TraceCache generation key uses canonical path, file identity
    /// where the OS exposes it, length, creation time, and last-write time. An
    /// adversarial in-place rewrite preserving every one of those values requires
    /// an explicit force refresh.
    /// </summary>
    FileIdentityLengthAndTimestamps,
}

internal enum TraceHandleLookupStatus
{
    Ready,
    Unknown,
    Expired,
    Unloaded,
}

internal enum TraceHandleUnloadStatus
{
    Unloaded,
    AlreadyUnloaded,
    Expired,
    Unknown,
}

internal sealed record TraceHandleRegistryOptions(
    int MaxActiveHandlesPerPrincipal = 8,
    int MaxCreationsPerPrincipalPerWindow = 32,
    TimeSpan CreationRateWindow = default,
    TimeSpan IdleLifetime = default,
    TimeSpan AbsoluteLifetime = default,
    int MaxTombstonesPerPrincipal = 64,
    TimeSpan TombstoneLifetime = default)
{
    internal static TraceHandleRegistryOptions Defaults { get; } = new(
        MaxActiveHandlesPerPrincipal: 8,
        MaxCreationsPerPrincipalPerWindow: 32,
        CreationRateWindow: TimeSpan.FromMinutes(1),
        IdleLifetime: TimeSpan.FromMinutes(30),
        AbsoluteLifetime: TimeSpan.FromHours(8),
        MaxTombstonesPerPrincipal: 64,
        TombstoneLifetime: TimeSpan.FromHours(8));

    internal TraceHandleRegistryOptions NormalizeAndValidate()
    {
        if (MaxActiveHandlesPerPrincipal <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxActiveHandlesPerPrincipal));
        if (MaxCreationsPerPrincipalPerWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxCreationsPerPrincipalPerWindow));
        if (CreationRateWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(CreationRateWindow));
        if (IdleLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdleLifetime));
        if (AbsoluteLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(AbsoluteLifetime));
        if (IdleLifetime > AbsoluteLifetime)
            throw new ArgumentException("Idle lifetime cannot exceed absolute lifetime.");
        if (MaxTombstonesPerPrincipal <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTombstonesPerPrincipal));
        if (TombstoneLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TombstoneLifetime));

        return this;
    }
}

internal static class TraceId
{
    internal const string Prefix = "trc_";
    internal const int Length = 36;

    internal static bool HasReservedPrefix(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    internal static bool IsCanonical(string? value)
    {
        if (value is null || value.Length != Length ||
            !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = Prefix.Length; i < value.Length; i++)
        {
            var c = value[i];
            if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                return false;
        }

        return true;
    }
}

internal interface ITraceIdGenerator
{
    string CreateTraceId();
}

internal sealed class CryptographicTraceIdGenerator : ITraceIdGenerator
{
    public string CreateTraceId()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return string.Concat(TraceId.Prefix, Convert.ToHexStringLower(bytes));
    }
}

internal sealed class TraceReferenceException : Exception
{
    internal TraceReferenceException(
        string code,
        string message,
        TraceHandleLookupStatus? status = null,
        string? detailCode = null)
        : base(message)
    {
        Code = code;
        Status = status;
        DetailCode = detailCode;
        ToolFailureCaptureContext.Record(this);
    }

    internal string Code { get; }
    internal TraceHandleLookupStatus? Status { get; }
    internal string? DetailCode { get; }
}

internal sealed record TraceHandleLoadResult(
    string TraceId,
    bool ReusedExisting,
    TraceHandlePersistence Persistence,
    TraceSourceGenerationAssurance SourceGenerationAssurance,
    bool ForceRefreshApplied);

internal sealed record TraceHandleUnloadResult(
    TraceHandleUnloadStatus Status,
    int ActiveLeases,
    Task DrainTask);

internal sealed record TracePrincipalRegistryStatus(
    int ActivePersistentHandles,
    int Tombstones,
    int CreationsInRateWindow);

internal sealed record TraceReferenceDescriptor(
    string TraceId,
    TraceHandlePersistence Persistence,
    bool LoadedFromRawPath,
    TraceSourceGenerationAssurance SourceGenerationAssurance,
    bool CanonicalHandleCreated);

/// <summary>
/// A principal-scoped handle registry. Public tokens never contain a source path,
/// file stamp, cache generation, or artifact identity.
/// </summary>
internal sealed class TraceHandleRegistry : IDisposable
{
    private readonly TraceCache _cache;
    private readonly TraceHandleRegistryOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ITraceIdGenerator _traceIds;
    private readonly object _gate = new();
    private readonly Dictionary<string, PrincipalState> _principals =
        new(StringComparer.Ordinal);
    private readonly Dictionary<TraceCache.FileStamp, GenerationAnchor> _generations = [];
    private bool _disposed;

    internal TraceHandleRegistry(
        TraceCache cache,
        TraceHandleRegistryOptions? options = null,
        TimeProvider? timeProvider = null,
        ITraceIdGenerator? traceIds = null)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _options = (options ?? TraceHandleRegistryOptions.Defaults).NormalizeAndValidate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _traceIds = traceIds ?? new CryptographicTraceIdGenerator();
    }

    internal TraceHandleLoadResult Load(
        string principal,
        string rawPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principal);
        ArgumentException.ThrowIfNullOrWhiteSpace(rawPath);
        var cacheRefreshRequested = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedStamp = TraceCache.FileStamp.Capture(rawPath);
            List<RetirementWork> expiries;

            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var now = _timeProvider.GetTimestamp();
                var state = GetOrCreatePrincipalLocked(principal);
                PurgePrincipalLocked(state, now);
                expiries = RetireExpiredLocked(state, now);

                if (expiries.Count == 0 && !forceRefresh &&
                    state.ActiveByGeneration.TryGetValue(observedStamp, out var existing))
                {
                    existing.LastReleaseTimestamp = now;
                    return LoadResult(existing.TraceId, reusedExisting: true, forceRefresh);
                }
                if (expiries.Count == 0)
                {
                    EnsureAdmissionLocked(state, now);
                    if (!forceRefresh &&
                        _generations.TryGetValue(observedStamp, out var generation) &&
                        !generation.Released)
                    {
                        return PublishPersistentLocked(state, generation, now, forceRefresh);
                    }
                }
            }

            if (expiries.Count != 0)
            {
                CompleteRetirements(expiries);
                continue;
            }

            if (forceRefresh && !cacheRefreshRequested)
            {
                // This is the explicit escape hatch for an in-place rewrite that
                // deliberately preserves file identity, size, and timestamps.
                _cache.Unload(rawPath);
                cacheRefreshRequested = true;
            }

            TraceLease? candidate = null;
            try
            {
                candidate = _cache.Acquire(rawPath);
                cancellationToken.ThrowIfCancellationRequested();
                var generationIdentity = candidate.GenerationIdentity;
                TraceHandleLoadResult? result = null;
                expiries = [];

                lock (_gate)
                {
                    ThrowIfDisposedLocked();
                    var now = _timeProvider.GetTimestamp();
                    var state = GetOrCreatePrincipalLocked(principal);
                    PurgePrincipalLocked(state, now);
                    expiries = RetireExpiredLocked(state, now);

                    if (expiries.Count == 0 && !forceRefresh &&
                        state.ActiveByGeneration.TryGetValue(
                            generationIdentity.Stamp,
                            out var existing))
                    {
                        existing.LastReleaseTimestamp = now;
                        result = LoadResult(existing.TraceId, reusedExisting: true, forceRefresh);
                    }
                    else if (expiries.Count == 0)
                    {
                        EnsureAdmissionLocked(state, now);
                        GenerationAnchor generation;
                        var addedGeneration = false;
                        GenerationAnchor? displacedGeneration = null;
                        if (!forceRefresh && _generations.TryGetValue(
                                generationIdentity.Stamp,
                                out var shared) &&
                            !shared.Released)
                        {
                            generation = shared;
                        }
                        else
                        {
                            generation = new GenerationAnchor(
                                generationIdentity.Stamp,
                                generationIdentity.Sequence,
                                candidate);
                            if (forceRefresh)
                            {
                                _generations.TryGetValue(
                                    generationIdentity.Stamp,
                                    out displacedGeneration);
                                _generations[generationIdentity.Stamp] = generation;
                            }
                            else
                            {
                                _generations.Add(generationIdentity.Stamp, generation);
                            }
                            addedGeneration = true;
                        }

                        Entry? displacedCanonical = null;
                        if (forceRefresh)
                        {
                            state.ActiveByGeneration.TryGetValue(
                                generationIdentity.Stamp,
                                out displacedCanonical);
                            state.ActiveByGeneration.Remove(generationIdentity.Stamp);
                        }

                        try
                        {
                            result = PublishPersistentLocked(
                                state,
                                generation,
                                now,
                                forceRefresh);
                            if (addedGeneration)
                                candidate = null;
                        }
                        catch
                        {
                            if (addedGeneration && generation.OwnerCount == 0)
                            {
                                generation.Released = true;
                                if (displacedGeneration is null)
                                    _generations.Remove(generation.Stamp);
                                else
                                    _generations[generation.Stamp] = displacedGeneration;
                            }
                            if (displacedCanonical is not null)
                            {
                                state.ActiveByGeneration[generation.Stamp] =
                                    displacedCanonical;
                            }
                            throw;
                        }
                    }
                }

                candidate?.Dispose();
                candidate = null;
                if (expiries.Count != 0)
                {
                    CompleteRetirements(expiries);
                    continue;
                }

                return result!;
            }
            finally
            {
                candidate?.Dispose();
            }
        }
    }

    internal ValueTask<TraceHandleLoadResult> LoadAsync(
        string principal,
        string rawPath,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Load(principal, rawPath, forceRefresh, cancellationToken));

    internal TraceHandleLease Acquire(
        string principal,
        string traceId,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principal);
        ValidateCanonicalTraceId(traceId);
        cancellationToken.ThrowIfCancellationRequested();

        RetirementWork? expiry = null;
        TraceHandleLease? lease = null;
        TraceHandleLookupStatus status;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var now = _timeProvider.GetTimestamp();
            var state = GetOrCreatePrincipalLocked(principal);
            PurgePrincipalLocked(state, now);

            if (state.ActiveById.TryGetValue(traceId, out var entry))
            {
                if (IsExpiredLocked(entry, now))
                {
                    expiry = RetireLocked(entry, TraceHandleLookupStatus.Expired, now);
                    status = TraceHandleLookupStatus.Expired;
                }
                else
                {
                    var backendLease = entry.Generation.AnchorLease.CloneBound();
                    checked { entry.ActiveLeases++; }
                    lease = new TraceHandleLease(
                        backendLease,
                        () => ReleasePersistentLease(entry));
                    status = TraceHandleLookupStatus.Ready;
                }
            }
            else if (state.Tombstones.TryGetValue(traceId, out var tombstone))
            {
                status = tombstone.Status;
            }
            else
            {
                status = TraceHandleLookupStatus.Unknown;
            }
        }

        if (expiry is not null)
            CompleteRetirement(expiry);
        if (lease is not null)
            return lease;

        throw LookupFailure(status);
    }

    internal ValueTask<TraceHandleLease> AcquireAsync(
        string principal,
        string traceId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Acquire(principal, traceId, cancellationToken));

    internal void RegisterDrainCallback(
        string principal,
        string traceId,
        Action callback)
    {
        ValidatePrincipal(principal);
        ValidateCanonicalTraceId(traceId);
        ArgumentNullException.ThrowIfNull(callback);

        Task drainTask;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var state = GetOrCreatePrincipalLocked(principal);
            if (state.ActiveById.TryGetValue(traceId, out var entry))
            {
                drainTask = entry.Drained.Task;
            }
            else if (state.Tombstones.TryGetValue(traceId, out var tombstone) &&
                     tombstone.Entry is not null)
            {
                drainTask = tombstone.Entry.Drained.Task;
            }
            else
            {
                throw LookupFailure(TraceHandleLookupStatus.Unknown);
            }
        }

        _ = drainTask.ContinueWith(
            static (_, state) => ((Action)state!).Invoke(),
            callback,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal TraceHandleUnloadResult Unload(string principal, string traceId)
    {
        ValidatePrincipal(principal);
        ValidateCanonicalTraceId(traceId);

        RetirementWork? retirement = null;
        TraceHandleUnloadResult result;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var now = _timeProvider.GetTimestamp();
            var state = GetOrCreatePrincipalLocked(principal);
            PurgePrincipalLocked(state, now);

            if (state.ActiveById.TryGetValue(traceId, out var entry))
            {
                var activeLeases = entry.ActiveLeases;
                var expired = IsExpiredLocked(entry, now);
                retirement = RetireLocked(
                    entry,
                    expired
                        ? TraceHandleLookupStatus.Expired
                        : TraceHandleLookupStatus.Unloaded,
                    now);
                result = new TraceHandleUnloadResult(
                    expired
                        ? TraceHandleUnloadStatus.Expired
                        : TraceHandleUnloadStatus.Unloaded,
                    activeLeases,
                    entry.Drained.Task);
            }
            else if (state.Tombstones.TryGetValue(traceId, out var tombstone))
            {
                result = new TraceHandleUnloadResult(
                    tombstone.Status == TraceHandleLookupStatus.Expired
                        ? TraceHandleUnloadStatus.Expired
                        : TraceHandleUnloadStatus.AlreadyUnloaded,
                    tombstone.Entry?.ActiveLeases ?? 0,
                    tombstone.Entry?.Drained.Task ?? Task.CompletedTask);
            }
            else
            {
                result = new TraceHandleUnloadResult(
                    TraceHandleUnloadStatus.Unknown,
                    ActiveLeases: 0,
                    Task.CompletedTask);
            }
        }

        if (retirement is not null)
            CompleteRetirement(retirement);
        return result;
    }

    internal TraceHandleLookupStatus GetLookupStatus(string principal, string traceId)
    {
        ValidatePrincipal(principal);
        ValidateCanonicalTraceId(traceId);

        RetirementWork? expiry = null;
        TraceHandleLookupStatus status;
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var now = _timeProvider.GetTimestamp();
            var state = GetOrCreatePrincipalLocked(principal);
            PurgePrincipalLocked(state, now);
            if (state.ActiveById.TryGetValue(traceId, out var entry))
            {
                if (IsExpiredLocked(entry, now))
                {
                    expiry = RetireLocked(entry, TraceHandleLookupStatus.Expired, now);
                    status = TraceHandleLookupStatus.Expired;
                }
                else
                {
                    status = TraceHandleLookupStatus.Ready;
                }
            }
            else if (state.Tombstones.TryGetValue(traceId, out var tombstone))
            {
                status = tombstone.Status;
            }
            else
            {
                status = TraceHandleLookupStatus.Unknown;
            }
        }

        if (expiry is not null)
            CompleteRetirement(expiry);
        return status;
    }

    internal int SweepExpired()
    {
        List<RetirementWork> retirements = [];
        lock (_gate)
        {
            ThrowIfDisposedLocked();
            var now = _timeProvider.GetTimestamp();
            foreach (var state in _principals.Values)
            {
                PurgePrincipalLocked(state, now);
                retirements.AddRange(RetireExpiredLocked(state, now));
            }
        }

        CompleteRetirements(retirements);
        return retirements.Count;
    }

    internal TracePrincipalRegistryStatus GetPrincipalStatus(string principal)
    {
        ValidatePrincipal(principal);
        while (true)
        {
            List<RetirementWork> expiries;
            TracePrincipalRegistryStatus? result = null;
            lock (_gate)
            {
                ThrowIfDisposedLocked();
                var now = _timeProvider.GetTimestamp();
                var state = GetOrCreatePrincipalLocked(principal);
                PurgePrincipalLocked(state, now);
                expiries = RetireExpiredLocked(state, now);
                if (expiries.Count == 0)
                {
                    result = new TracePrincipalRegistryStatus(
                        state.ActiveById.Count,
                        state.Tombstones.Count,
                        state.CreationTimestamps.Count);
                }
            }

            CompleteRetirements(expiries);
            if (result is not null)
                return result;
        }
    }

    public void Dispose()
    {
        List<Entry> retirements = [];
        List<GenerationAnchor> orphanedGenerations = [];
        HashSet<GenerationAnchor> generations = [];
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            foreach (var state in _principals.Values)
            {
                foreach (var entry in state.ActiveById.Values)
                {
                    entry.State = TraceHandleLookupStatus.Unloaded;
                    entry.AnchorReleased = false;
                    retirements.Add(entry);
                    generations.Add(entry.Generation);
                }
                state.ActiveById.Clear();
                state.ActiveByGeneration.Clear();
            }

            foreach (var generation in _generations.Values)
                generations.Add(generation);
            foreach (var generation in generations)
            {
                if (!generation.Released)
                {
                    generation.Released = true;
                    orphanedGenerations.Add(generation);
                }
            }
            _generations.Clear();
            _principals.Clear();
        }

        Exception? firstFailure = null;
        foreach (var generation in orphanedGenerations)
        {
            try
            {
                RetireCacheGenerationAndDisposeAnchor(
                    generation,
                    generation.AnchorLease);
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        lock (_gate)
        {
            foreach (var retirement in retirements)
            {
                retirement.AnchorReleased = true;
                CompleteDrainLocked(retirement);
            }
        }

        if (firstFailure is not null)
            throw firstFailure;
    }

    private TraceHandleLoadResult PublishPersistentLocked(
        PrincipalState state,
        GenerationAnchor generation,
        long now,
        bool forceRefresh)
    {
        EnsureAdmissionLocked(state, now);
        var traceId = CreateUniqueTraceIdLocked(state);
        checked { generation.OwnerCount++; }
        var entry = new Entry(state, traceId, generation, now);
        state.ActiveById.Add(traceId, entry);
        state.ActiveByGeneration.Add(generation.Stamp, entry);
        RecordCreationLocked(state, now);
        return LoadResult(traceId, reusedExisting: false, forceRefresh);
    }

    private RetirementWork RetireLocked(
        Entry entry,
        TraceHandleLookupStatus finalStatus,
        long now)
    {
        entry.State = finalStatus;
        entry.Principal.ActiveById.Remove(entry.TraceId);
        if (entry.Principal.ActiveByGeneration.TryGetValue(
                entry.Generation.Stamp,
                out var current) &&
            ReferenceEquals(current, entry))
        {
            entry.Principal.ActiveByGeneration.Remove(entry.Generation.Stamp);
        }

        AddTombstoneLocked(entry.Principal, entry.TraceId, finalStatus, now, entry);
        entry.AnchorReleased = false;
        var anchorToRelease = ReleaseGenerationOwnerLocked(entry.Generation);
        return new RetirementWork(entry, anchorToRelease);
    }

    private void CompleteRetirement(RetirementWork retirement)
    {
        Exception? failure = null;
        try
        {
            if (retirement.AnchorToRelease is not null)
            {
                RetireCacheGenerationAndDisposeAnchor(
                    retirement.Entry.Generation,
                    retirement.AnchorToRelease);
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            lock (_gate)
            {
                retirement.Entry.AnchorReleased = true;
                CompleteDrainLocked(retirement.Entry);
            }
        }

        if (failure is not null)
            throw failure;
    }

    private void RetireCacheGenerationAndDisposeAnchor(
        GenerationAnchor generation,
        TraceLease anchor)
    {
        Exception? failure = null;
        try
        {
            _cache.RetireGeneration(new TraceCache.GenerationIdentity(
                generation.Stamp,
                generation.CacheGenerationSequence));
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            anchor.Dispose();
        }
        catch (Exception ex)
        {
            failure = failure is null
                ? ex
                : new AggregateException(failure, ex);
        }

        if (failure is not null)
            throw failure;
    }

    private void ReleasePersistentLease(Entry entry)
    {
        lock (_gate)
        {
            if (entry.ActiveLeases <= 0)
                throw new InvalidOperationException("Trace registry lease count underflow.");
            entry.ActiveLeases--;
            entry.LastReleaseTimestamp = _timeProvider.GetTimestamp();
            CompleteDrainLocked(entry);
        }
    }

    private TraceLease? ReleaseGenerationOwnerLocked(GenerationAnchor generation)
    {
        if (generation.Released)
            return null;
        if (generation.OwnerCount <= 0)
            throw new InvalidOperationException("Trace generation owner count underflow.");

        generation.OwnerCount--;
        if (generation.OwnerCount != 0)
            return null;

        generation.Released = true;
        if (_generations.TryGetValue(generation.Stamp, out var current) &&
            ReferenceEquals(current, generation))
        {
            _generations.Remove(generation.Stamp);
        }
        return generation.AnchorLease;
    }

    private void CompleteDrainLocked(Entry entry)
    {
        if (entry.State != TraceHandleLookupStatus.Ready &&
            entry.AnchorReleased &&
            entry.ActiveLeases == 0)
        {
            entry.Drained.TrySetResult();
        }
    }

    private bool IsExpiredLocked(Entry entry, long now)
    {
        if (_timeProvider.GetElapsedTime(entry.CreatedTimestamp, now) >=
            _options.AbsoluteLifetime)
        {
            return true;
        }

        return entry.ActiveLeases == 0 &&
               _timeProvider.GetElapsedTime(entry.LastReleaseTimestamp, now) >=
               _options.IdleLifetime;
    }

    private List<RetirementWork> RetireExpiredLocked(PrincipalState state, long now)
    {
        List<RetirementWork> retirements = [];
        foreach (var entry in state.ActiveById.Values.ToArray())
        {
            if (IsExpiredLocked(entry, now))
            {
                retirements.Add(RetireLocked(
                    entry,
                    TraceHandleLookupStatus.Expired,
                    now));
            }
        }
        return retirements;
    }

    private void CompleteRetirements(IEnumerable<RetirementWork> retirements)
    {
        List<Exception>? failures = null;
        foreach (var retirement in retirements)
        {
            try
            {
                CompleteRetirement(retirement);
            }
            catch (Exception ex)
            {
                failures ??= [];
                failures.Add(ex);
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more trace handles failed to retire.", failures);
    }

    private static TraceHandleLoadResult LoadResult(
        string traceId,
        bool reusedExisting,
        bool forceRefresh) =>
        new(
            traceId,
            reusedExisting,
            TraceHandlePersistence.Persistent,
            TraceSourceGenerationAssurance.FileIdentityLengthAndTimestamps,
            forceRefresh);

    private void EnsureAdmissionLocked(PrincipalState state, long now)
    {
        PurgeCreationWindowLocked(state, now);
        if (state.ActiveById.Count >=
            _options.MaxActiveHandlesPerPrincipal)
        {
            throw new TraceReferenceException(
                "budget_exceeded",
                "The principal trace-handle quota is exhausted.",
                detailCode: "trace_handle_quota_exceeded");
        }
        if (state.CreationTimestamps.Count >= _options.MaxCreationsPerPrincipalPerWindow)
        {
            throw new TraceReferenceException(
                "budget_exceeded",
                "The principal trace-handle creation rate is exhausted.",
                detailCode: "trace_handle_rate_exceeded");
        }
    }

    private void RecordCreationLocked(PrincipalState state, long now) =>
        state.CreationTimestamps.Enqueue(now);

    private void PurgePrincipalLocked(PrincipalState state, long now)
    {
        PurgeCreationWindowLocked(state, now);
        while (state.TombstoneOrder.Count != 0)
        {
            var oldest = state.TombstoneOrder.Peek();
            if (_timeProvider.GetElapsedTime(oldest.RetiredTimestamp, now) <
                _options.TombstoneLifetime)
            {
                break;
            }

            state.TombstoneOrder.Dequeue();
            if (state.Tombstones.TryGetValue(oldest.TraceId, out var current) &&
                ReferenceEquals(current, oldest))
            {
                state.Tombstones.Remove(oldest.TraceId);
            }
        }
    }

    private void PurgeCreationWindowLocked(PrincipalState state, long now)
    {
        while (state.CreationTimestamps.Count != 0 &&
               _timeProvider.GetElapsedTime(state.CreationTimestamps.Peek(), now) >=
               _options.CreationRateWindow)
        {
            state.CreationTimestamps.Dequeue();
        }
    }

    private void AddTombstoneLocked(
        PrincipalState state,
        string traceId,
        TraceHandleLookupStatus status,
        long now,
        Entry? entry = null)
    {
        var tombstone = new Tombstone(traceId, status, now, entry);
        state.Tombstones[traceId] = tombstone;
        state.TombstoneOrder.Enqueue(tombstone);
        while (state.Tombstones.Count > _options.MaxTombstonesPerPrincipal)
        {
            var oldest = state.TombstoneOrder.Dequeue();
            if (state.Tombstones.TryGetValue(oldest.TraceId, out var current) &&
                ReferenceEquals(current, oldest))
            {
                state.Tombstones.Remove(oldest.TraceId);
            }
        }
    }

    private string CreateUniqueTraceIdLocked(PrincipalState state)
    {
        for (var attempt = 0; attempt < 128; attempt++)
        {
            var candidate = _traceIds.CreateTraceId();
            if (!TraceId.IsCanonical(candidate))
            {
                throw new InvalidOperationException(
                    "The trace ID generator returned a non-canonical token.");
            }
            if (!state.ActiveById.ContainsKey(candidate) &&
                !state.Tombstones.ContainsKey(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Unable to generate a unique trace ID.");
    }

    private PrincipalState GetOrCreatePrincipalLocked(string principal)
    {
        if (!_principals.TryGetValue(principal, out var state))
        {
            state = new PrincipalState();
            _principals.Add(principal, state);
        }
        return state;
    }

    private void ThrowIfDisposedLocked()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(TraceHandleRegistry));
    }

    private static void ValidatePrincipal(string principal)
    {
        if (string.IsNullOrWhiteSpace(principal))
            throw new ArgumentException("A principal/session identifier is required.", nameof(principal));
    }

    private static void ValidateCanonicalTraceId(string traceId)
    {
        if (!TraceId.IsCanonical(traceId))
        {
            throw new TraceReferenceException(
                "invalid_argument",
                "The trace reference uses a reserved prefix but is not a canonical trace ID.",
                detailCode: "malformed_trace_id");
        }
    }

    private static TraceReferenceException LookupFailure(TraceHandleLookupStatus status) =>
        status switch
        {
            TraceHandleLookupStatus.Expired => new TraceReferenceException(
                "trace_not_loaded",
                "The trace handle has expired.",
                status,
                "expired"),
            TraceHandleLookupStatus.Unloaded => new TraceReferenceException(
                "trace_not_loaded",
                "The trace handle has been unloaded.",
                status,
                "unloaded"),
            _ => new TraceReferenceException(
                "trace_not_loaded",
                "The trace handle is unknown in this principal/session.",
                TraceHandleLookupStatus.Unknown,
                "unknown"),
        };

    private sealed class PrincipalState
    {
        internal Dictionary<string, Entry> ActiveById { get; } = new(StringComparer.Ordinal);
        internal Dictionary<TraceCache.FileStamp, Entry> ActiveByGeneration { get; } = [];
        internal Dictionary<string, Tombstone> Tombstones { get; } = new(StringComparer.Ordinal);
        internal Queue<Tombstone> TombstoneOrder { get; } = [];
        internal Queue<long> CreationTimestamps { get; } = [];
    }

    private sealed class GenerationAnchor(
        TraceCache.FileStamp stamp,
        long cacheGenerationSequence,
        TraceLease anchorLease)
    {
        internal TraceCache.FileStamp Stamp { get; } = stamp;
        internal long CacheGenerationSequence { get; } = cacheGenerationSequence;
        internal TraceLease AnchorLease { get; } = anchorLease;
        internal int OwnerCount { get; set; }
        internal bool Released { get; set; }
    }

    private sealed class Entry(
        PrincipalState principal,
        string traceId,
        GenerationAnchor generation,
        long now)
    {
        internal PrincipalState Principal { get; } = principal;
        internal string TraceId { get; } = traceId;
        internal GenerationAnchor Generation { get; } = generation;
        internal long CreatedTimestamp { get; } = now;
        internal long LastReleaseTimestamp { get; set; } = now;
        internal TraceHandleLookupStatus State { get; set; } = TraceHandleLookupStatus.Ready;
        internal int ActiveLeases { get; set; }
        internal bool AnchorReleased { get; set; }
        internal TaskCompletionSource Drained { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Tombstone(
        string TraceId,
        TraceHandleLookupStatus Status,
        long RetiredTimestamp,
        Entry? Entry);

    private sealed record RetirementWork(Entry Entry, TraceLease? AnchorToRelease);
}

internal sealed class TraceHandleLease : IDisposable
{
    private LeaseState? _state;

    internal TraceHandleLease(TraceLease backendLease, Action release)
    {
        _state = new LeaseState(backendLease, release);
    }

    internal TraceLog Trace => GetBackendLease().Trace;
    internal TraceCapabilities Capabilities => GetBackendLease().Capabilities;
    internal TraceMetadata Metadata => GetBackendLease().Metadata;
    internal TraceFactsSnapshot GetFacts(CancellationToken cancellationToken) =>
        GetBackendLease().GetFacts(cancellationToken);
    internal Task<TraceFactsSnapshot> GetFactsAsync(CancellationToken cancellationToken) =>
        GetBackendLease().GetFactsAsync(cancellationToken);
    internal TraceFactsScanTelemetry FactsTelemetry =>
        GetBackendLease().FactsTelemetry;
    internal bool TryGetReadyFacts(out TraceFactsSnapshot snapshot) =>
        GetBackendLease().TryGetReadyFacts(out snapshot);
    internal long CacheGenerationSequence =>
        GetBackendLease().GenerationIdentity.Sequence;

    internal TraceLease CloneBackendLease() => GetBackendLease().CloneBound();

    public void Dispose()
    {
        var state = Interlocked.Exchange(ref _state, null);
        if (state is null)
            return;

        try
        {
            state.BackendLease.Dispose();
        }
        finally
        {
            state.Release();
        }
    }

    private TraceLease GetBackendLease() =>
        Volatile.Read(ref _state)?.BackendLease
        ?? throw new ObjectDisposedException(nameof(TraceHandleLease));

    private sealed record LeaseState(TraceLease BackendLease, Action Release);
}

internal sealed class ResolvedTraceReference : IDisposable
{
    internal ResolvedTraceReference(
        TraceReferenceDescriptor descriptor,
        TraceHandleLease lease,
        IReadOnlyList<string> warnings)
    {
        Descriptor = descriptor;
        Lease = lease;
        Warnings = warnings;
    }

    internal TraceReferenceDescriptor Descriptor { get; }
    internal TraceHandleLease Lease { get; }
    internal IReadOnlyList<string> Warnings { get; }

    public void Dispose() => Lease.Dispose();
}

internal sealed class TraceReferenceResolver(
    TraceHandleRegistry registry,
    TraceLifecycleService? lifecycle = null)
{
    private readonly TraceHandleRegistry _registry =
        registry ?? throw new ArgumentNullException(nameof(registry));
    private readonly TraceLifecycleService? _lifecycle = lifecycle;

    internal ResolvedTraceReference ResolveQuery(
        string principal,
        string traceReference,
        TraceAccessMode mode,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceReference);

        if (TraceId.HasReservedPrefix(traceReference))
        {
            if (!TraceId.IsCanonical(traceReference))
            {
                throw new TraceReferenceException(
                    "invalid_argument",
                    "The trace reference uses a reserved prefix but is not a canonical trace ID.",
                    detailCode: "malformed_trace_id");
            }

            return new ResolvedTraceReference(
                new TraceReferenceDescriptor(
                    traceReference,
                    TraceHandlePersistence.Persistent,
                    LoadedFromRawPath: false,
                    TraceSourceGenerationAssurance.FileIdentityLengthAndTimestamps,
                    CanonicalHandleCreated: false),
                _registry.Acquire(principal, traceReference, cancellationToken),
                []);
        }

        throw new TraceReferenceException(
            "invalid_argument",
            "Trace queries require the canonical traceId returned by load_trace.",
            detailCode: "raw_path_not_allowed");
    }

    internal void RollbackUndeliveredCompatibilityHandle(
        string principal,
        string traceId)
    {
        _registry.Unload(principal, traceId);
    }
}
