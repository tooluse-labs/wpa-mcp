using System.Security.Cryptography;

namespace WpaMcp.Core;

internal sealed record SymbolContextRegistryOptions(
    int MaxContextsPerPrincipal,
    int MaxPrepareAttemptsPerWindow,
    TimeSpan PrepareRateWindow,
    TimeSpan IdleTtl,
    TimeSpan AbsoluteTtl,
    int MaxTombstonesPerPrincipal)
{
    public static SymbolContextRegistryOptions Default { get; } = new(
        MaxContextsPerPrincipal: 64,
        MaxPrepareAttemptsPerWindow: 30,
        PrepareRateWindow: TimeSpan.FromMinutes(1),
        IdleTtl: TimeSpan.FromMinutes(15),
        AbsoluteTtl: TimeSpan.FromHours(2),
        MaxTombstonesPerPrincipal: 256);

    public void Validate()
    {
        if (MaxContextsPerPrincipal <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxContextsPerPrincipal));
        if (MaxPrepareAttemptsPerWindow <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPrepareAttemptsPerWindow));
        if (PrepareRateWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PrepareRateWindow));
        if (IdleTtl <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(IdleTtl));
        if (AbsoluteTtl < IdleTtl)
            throw new ArgumentOutOfRangeException(nameof(AbsoluteTtl));
        if (MaxTombstonesPerPrincipal <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTombstonesPerPrincipal));
    }
}

internal enum SymbolContextFailure
{
    Malformed,
    Unknown,
    Expired,
    Retired,
    TraceBindingMismatch,
    QuotaExceeded,
    RateLimited,
    PolicyDenied,
    RemoteResolutionUnimplemented,
    ArtifactVerificationFailed,
}

internal sealed class SymbolContextException : InvalidOperationException
{
    internal SymbolContextException(
        SymbolContextFailure failure,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        ToolFailureCaptureContext.Record(this);
    }

    public SymbolContextFailure Failure { get; }
}

internal sealed record SymbolContextPublicError(
    string Code,
    string? DetailCode,
    string Message);

internal static class SymbolContextPublicErrorProjection
{
    private const string UnavailableMessage =
        "The symbol context is unavailable; prepare a new context for this trace and policy.";

    /// <summary>
    /// Projects internal lifecycle states onto ADR 0003's closed public error registry.
    /// The default projection deliberately does not reveal whether any token exists.
    /// </summary>
    public static SymbolContextPublicError Project(
        SymbolContextException exception,
        bool includeSamePrincipalLifecycleDetail = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Failure switch
        {
            SymbolContextFailure.Malformed => new(
                "invalid_argument",
                "malformed_symbol_context_id",
                "The symbol context identifier is malformed."),
            SymbolContextFailure.Unknown or
            SymbolContextFailure.Expired or
            SymbolContextFailure.Retired or
            SymbolContextFailure.TraceBindingMismatch => new(
                "symbol_context_expired",
                includeSamePrincipalLifecycleDetail
                    ? LifecycleDetail(exception.Failure)
                    : null,
                UnavailableMessage),
            SymbolContextFailure.QuotaExceeded or SymbolContextFailure.RateLimited => new(
                "budget_exceeded",
                includeSamePrincipalLifecycleDetail
                    ? exception.Failure == SymbolContextFailure.QuotaExceeded
                        ? "symbol_context_quota_exceeded"
                        : "symbol_context_rate_exceeded"
                    : null,
                "The symbol-context resource budget was exceeded."),
            SymbolContextFailure.PolicyDenied => new(
                "symbol_policy_denied",
                includeSamePrincipalLifecycleDetail ? "symbol_policy_denied" : null,
                "The requested symbol policy is not approved for this principal."),
            SymbolContextFailure.RemoteResolutionUnimplemented or
            SymbolContextFailure.ArtifactVerificationFailed => new(
                "analysis_failed",
                includeSamePrincipalLifecycleDetail
                    ? exception.Failure == SymbolContextFailure.RemoteResolutionUnimplemented
                        ? "symbol_remote_resolution_unimplemented"
                        : "symbol_artifact_verification_failed"
                    : null,
                "Symbol preparation could not produce a verified immutable context."),
            _ => new("analysis_failed", null, "Symbol context processing failed."),
        };
    }

    private static string LifecycleDetail(SymbolContextFailure failure) => failure switch
    {
        SymbolContextFailure.Unknown => "symbol_context_unknown",
        SymbolContextFailure.Expired => "symbol_context_expired",
        SymbolContextFailure.Retired => "symbol_context_retired",
        SymbolContextFailure.TraceBindingMismatch => "symbol_context_trace_mismatch",
        _ => throw new ArgumentOutOfRangeException(nameof(failure)),
    };
}

internal sealed class SymbolContextDescriptor
{
    internal SymbolContextDescriptor(
        string symbolContextId,
        string contextRevision,
        string traceGenerationIdentity,
        string symbolPolicyReference,
        string symbolPolicyRevision,
        string resolverVersion,
        string privacyProfile,
        string contractVersion,
        DateTimeOffset preparedAtUtc,
        DateTimeOffset absoluteExpiresAtUtc,
        SymbolPreparationEvidence evidence)
    {
        SymbolContextId = symbolContextId;
        ContextRevision = contextRevision;
        TraceGenerationIdentity = traceGenerationIdentity;
        SymbolPolicyReference = symbolPolicyReference;
        SymbolPolicyRevision = symbolPolicyRevision;
        ResolverVersion = resolverVersion;
        PrivacyProfile = privacyProfile;
        ContractVersion = contractVersion;
        PreparedAtUtc = preparedAtUtc;
        AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
        Evidence = evidence;
    }

    public string SymbolContextId { get; }

    public string ContextRevision { get; }

    // Internal-only binding. This property must never be projected into a wire DTO.
    internal string TraceGenerationIdentity { get; }

    public string SymbolPolicyReference { get; }

    public string SymbolPolicyRevision { get; }

    public string ResolverVersion { get; }

    public string PrivacyProfile { get; }

    public string ContractVersion { get; }

    public DateTimeOffset PreparedAtUtc { get; }

    public DateTimeOffset AbsoluteExpiresAtUtc { get; }

    public SymbolPreparationEvidence Evidence { get; }
}

internal enum SymbolContextRetireDisposition
{
    Retired,
    Draining,
    AlreadyRetired,
    Unknown,
}

internal sealed record SymbolContextRetireResult(
    SymbolContextRetireDisposition Disposition,
    int ActiveLeases);

internal sealed record SymbolContextPublication(
    SymbolContextDescriptor Descriptor,
    bool Created);

/// <summary>
/// Principal-scoped immutable symbol contexts. A context owns verified artifact pins
/// until it is retired and its final query lease is released.
/// </summary>
internal sealed class SymbolContextRegistry : IAsyncDisposable
{
    internal const string Prefix = "sym_";
    internal const int LocatorHexLength = 32;
    internal const int TokenLength = 4 + LocatorHexLength;

    private readonly object _gate = new();
    private readonly Dictionary<SymbolPrincipal, PrincipalState> _principals = [];
    private readonly SymbolContextRegistryOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<byte[]> _randomBytes;
    private bool _disposed;

    public SymbolContextRegistry(
        SymbolContextRegistryOptions options,
        Func<DateTimeOffset>? utcNow = null,
        Func<byte[]>? randomBytes = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _randomBytes = randomBytes ?? (static () => RandomNumberGenerator.GetBytes(16));
    }

    internal int ActiveCount(SymbolPrincipal principal)
    {
        lock (_gate)
            return _principals.TryGetValue(principal, out var state)
                ? state.ActiveByToken.Count
                : 0;
    }

    internal int OwnedCount(SymbolPrincipal principal)
    {
        lock (_gate)
            return _principals.TryGetValue(principal, out var state)
                ? state.Owned.Count
                : 0;
    }

    internal int TombstoneCount(SymbolPrincipal principal)
    {
        lock (_gate)
            return _principals.TryGetValue(principal, out var state)
                ? state.Tombstones.Count
                : 0;
    }

    /// <summary>
    /// Charges a public prepare attempt before it can probe roots or origins. Reuse of
    /// an existing context remains bounded too, preventing canonical-reuse probe abuse.
    /// </summary>
    public void RecordPrepareAttempt(SymbolPrincipal principal)
    {
        ValidatePrincipal(principal);
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _utcNow();
            var state = GetOrCreatePrincipalLocked(principal);
            PrunePrepareAttemptsLocked(state, now);
            if (state.PrepareAttempts.Count >= _options.MaxPrepareAttemptsPerWindow)
            {
                throw new SymbolContextException(
                    SymbolContextFailure.RateLimited,
                    "The symbol preparation rate limit was exceeded.");
            }

            state.PrepareAttempts.Enqueue(now);
        }
    }

    /// <summary>
    /// Publishes a fully verified immutable context or reuses the canonical active ID.
    /// Ownership of <paramref name="prepared"/> always transfers to this method.
    /// </summary>
    public async ValueTask<SymbolContextDescriptor> PublishAsync(
        SymbolPrincipal principal,
        PreparedSymbolContext prepared) =>
        (await PublishWithDispositionAsync(principal, prepared).ConfigureAwait(false))
        .Descriptor;

    internal async ValueTask<SymbolContextPublication> PublishWithDispositionAsync(
        SymbolPrincipal principal,
        PreparedSymbolContext prepared)
    {
        ValidatePrincipal(principal);
        ArgumentNullException.ThrowIfNull(prepared);
        if (prepared.Definition.Principal != principal)
        {
            await prepared.DisposeAsync().ConfigureAwait(false);
            throw new ArgumentException("The prepared context principal does not match the registry scope.", nameof(prepared));
        }

        while (true)
        {
            List<Entry> pendingDisposals = [];
            Entry? published = null;
            Entry? canonical = null;
            Exception? failure = null;
            lock (_gate)
            {
                try
                {
                    ThrowIfDisposed();
                    var now = _utcNow();
                    var state = GetOrCreatePrincipalLocked(principal);
                    PruneLocked(state, now, pendingDisposals);

                    if (state.ActiveByRevision.TryGetValue(prepared.Definition.Revision, out canonical))
                    {
                        // Hold an internal lease while checking the exact old pins. A
                        // canonical ID is reusable only while it can still satisfy its
                        // immutable artifact promise.
                        canonical.ActiveLeases++;
                        canonical.LastAccessUtc = now;
                    }
                    else if (state.Owned.Count >= _options.MaxContextsPerPrincipal)
                    {
                        failure = new SymbolContextException(
                            SymbolContextFailure.QuotaExceeded,
                            "The per-principal symbol context quota was exceeded.");
                    }
                    else
                    {
                        var token = MintTokenLocked(state);
                        published = new Entry(
                            principal,
                            token,
                            prepared,
                            now,
                            now + _options.AbsoluteTtl);
                        state.ActiveByToken.Add(token, published);
                        state.ActiveByRevision.Add(prepared.Definition.Revision, published);
                        state.Owned.Add(published);
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            }

            StartDisposals(pendingDisposals);
            if (failure is not null)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
                throw failure;
            }
            if (published is not null)
                return new SymbolContextPublication(published.Descriptor, Created: true);

            var canonicalUsable = true;
            try
            {
                foreach (var pin in canonical!.Prepared.Pins)
                {
                    if (!await pin.IsAvailableAsync(CancellationToken.None).ConfigureAwait(false))
                    {
                        canonicalUsable = false;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                canonicalUsable = false;
            }

            var remainsCanonical = false;
            pendingDisposals = [];
            lock (_gate)
            {
                if (_principals.TryGetValue(principal, out var state)
                    && state.ActiveByRevision.TryGetValue(
                        prepared.Definition.Revision,
                        out var current)
                    && ReferenceEquals(current, canonical))
                {
                    if (canonicalUsable)
                    {
                        remainsCanonical = true;
                    }
                    else
                    {
                        RetireEntryLocked(
                            state,
                            canonical,
                            SymbolContextFailure.Expired,
                            _utcNow(),
                            pendingDisposals);
                    }
                }
            }
            StartDisposals(pendingDisposals);
            var disposalAfterRelease = Release(canonical!);

            if (remainsCanonical)
            {
                await prepared.DisposeAsync().ConfigureAwait(false);
                return new SymbolContextPublication(canonical!.Descriptor, Created: false);
            }

            // If our validation lease was the last lease on the retired context, wait
            // for pin release so a tight per-principal quota does not reject its safe
            // replacement spuriously. An independently retired context with other
            // callers remains charged and the next loop applies the normal quota.
            if (disposalAfterRelease is not null)
                await disposalAfterRelease.ConfigureAwait(false);
        }
    }

    public async ValueTask<SymbolContextLease> AcquireAsync(
        SymbolPrincipal principal,
        string symbolContextId,
        string expectedTraceGenerationIdentity,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principal);
        if (!HasCanonicalShape(symbolContextId))
        {
            throw new SymbolContextException(
                SymbolContextFailure.Malformed,
                "The symbol context identifier is malformed.");
        }
        if (string.IsNullOrWhiteSpace(expectedTraceGenerationIdentity))
            throw new ArgumentException("An expected trace generation identity is required.", nameof(expectedTraceGenerationIdentity));

        List<Entry> pendingDisposals = [];
        Entry? acquired = null;
        SymbolContextException? lookupFailure = null;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _utcNow();
            if (!_principals.TryGetValue(principal, out var state))
            {
                lookupFailure = Unknown();
            }
            else
            {
                PruneLocked(state, now, pendingDisposals);
                if (state.Tombstones.TryGetValue(symbolContextId, out var tombstone))
                {
                    lookupFailure = tombstone.Failure == SymbolContextFailure.Expired
                        ? Expired()
                        : Retired();
                }
                else if (!state.ActiveByToken.TryGetValue(symbolContextId, out acquired))
                {
                    // A token scoped to another principal is intentionally identical to
                    // a random unknown token here; no global token lookup is performed.
                    lookupFailure = Unknown();
                }
                else if (!string.Equals(
                             acquired.Prepared.Definition.TraceGenerationIdentity,
                             expectedTraceGenerationIdentity,
                             StringComparison.Ordinal))
                {
                    acquired = null;
                    lookupFailure = new SymbolContextException(
                        SymbolContextFailure.TraceBindingMismatch,
                        "The symbol context is not bound to the requested trace generation.");
                }
                else
                {
                    acquired.ActiveLeases++;
                    acquired.LastAccessUtc = now;
                }
            }
        }

        StartDisposals(pendingDisposals);
        if (lookupFailure is not null)
            throw lookupFailure;

        try
        {
            foreach (var pin in acquired!.Prepared.Pins)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await pin.IsAvailableAsync(cancellationToken).ConfigureAwait(false))
                {
                    await ExpireForArtifactLossAsync(acquired).ConfigureAwait(false);
                    throw Expired("A pinned verified symbol artifact is no longer available.");
                }
            }

            return new SymbolContextLease(
                acquired.Descriptor,
                acquired.Prepared.Definition,
                acquired.Prepared.Pins,
                () => { _ = Release(acquired); });
        }
        catch
        {
            _ = Release(acquired!);
            throw;
        }
    }

    public async ValueTask<SymbolContextRetireResult> RetireAsync(
        SymbolPrincipal principal,
        string symbolContextId,
        bool waitForDrain,
        CancellationToken cancellationToken = default)
    {
        ValidatePrincipal(principal);
        if (!HasCanonicalShape(symbolContextId))
        {
            throw new SymbolContextException(
                SymbolContextFailure.Malformed,
                "The symbol context identifier is malformed.");
        }

        List<Entry> pendingDisposals = [];
        Entry? retired = null;
        SymbolContextRetireResult immediate;
        lock (_gate)
        {
            ThrowIfDisposed();
            var now = _utcNow();
            if (!_principals.TryGetValue(principal, out var state))
            {
                immediate = new(SymbolContextRetireDisposition.Unknown, 0);
            }
            else
            {
                PruneLocked(state, now, pendingDisposals);
                if (state.Tombstones.ContainsKey(symbolContextId))
                {
                    immediate = new(SymbolContextRetireDisposition.AlreadyRetired, 0);
                }
                else if (!state.ActiveByToken.TryGetValue(symbolContextId, out retired))
                {
                    immediate = new(SymbolContextRetireDisposition.Unknown, 0);
                }
                else
                {
                    var activeLeases = retired.ActiveLeases;
                    RetireEntryLocked(state, retired, SymbolContextFailure.Retired, now, pendingDisposals);
                    immediate = new(
                        activeLeases == 0
                            ? SymbolContextRetireDisposition.Retired
                            : SymbolContextRetireDisposition.Draining,
                        activeLeases);
                }
            }
        }

        StartDisposals(pendingDisposals);
        if (retired is not null && waitForDrain)
        {
            await retired.Disposed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (retired.DisposeFailure is not null)
            {
                throw new InvalidOperationException(
                    "The symbol context retired but one or more artifact pins failed to release.",
                    retired.DisposeFailure);
            }
            return new SymbolContextRetireResult(SymbolContextRetireDisposition.Retired, 0);
        }
        return immediate;
    }

    public async ValueTask DisposeAsync()
    {
        List<Entry> pendingDisposals = [];
        Entry[] owned;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            var now = _utcNow();
            owned = _principals.Values.SelectMany(static state => state.Owned).ToArray();
            foreach (var state in _principals.Values)
            {
                foreach (var entry in state.ActiveByToken.Values.ToArray())
                    RetireEntryLocked(state, entry, SymbolContextFailure.Retired, now, pendingDisposals);
            }
        }

        StartDisposals(pendingDisposals);
        await Task.WhenAll(owned.Select(static entry => entry.Disposed.Task)).ConfigureAwait(false);
        var failures = owned
            .Where(static entry => entry.DisposeFailure is not null)
            .Select(static entry => entry.DisposeFailure!)
            .ToArray();
        if (failures.Length != 0)
            throw new AggregateException("One or more symbol contexts failed to release artifact pins.", failures);
    }

    internal static bool HasCanonicalShape(string? symbolContextId)
    {
        if (symbolContextId is null
            || symbolContextId.Length != TokenLength
            || !symbolContextId.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        for (var index = Prefix.Length; index < symbolContextId.Length; index++)
        {
            var character = symbolContextId[index];
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private async ValueTask ExpireForArtifactLossAsync(Entry entry)
    {
        List<Entry> pendingDisposals = [];
        lock (_gate)
        {
            if (_principals.TryGetValue(entry.Principal, out var state)
                && state.ActiveByToken.TryGetValue(entry.Token, out var current)
                && ReferenceEquals(entry, current))
            {
                RetireEntryLocked(
                    state,
                    entry,
                    SymbolContextFailure.Expired,
                    _utcNow(),
                    pendingDisposals);
            }
        }
        StartDisposals(pendingDisposals);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private Task? Release(Entry entry)
    {
        Entry? pendingDisposal = null;
        Task? disposal = null;
        lock (_gate)
        {
            if (entry.ActiveLeases <= 0)
                return entry.State == EntryState.Disposed ? Task.CompletedTask : null;
            entry.ActiveLeases--;
            if (entry.State == EntryState.Retiring
                && entry.ActiveLeases == 0
                && !entry.DisposalStarted)
            {
                entry.DisposalStarted = true;
                pendingDisposal = entry;
            }
            if (entry.State != EntryState.Active && entry.ActiveLeases == 0)
                disposal = entry.Disposed.Task;
        }

        if (pendingDisposal is not null)
            StartDisposal(pendingDisposal);
        return disposal;
    }

    private PrincipalState GetOrCreatePrincipalLocked(SymbolPrincipal principal)
    {
        if (!_principals.TryGetValue(principal, out var state))
        {
            state = new PrincipalState();
            _principals.Add(principal, state);
        }
        return state;
    }

    private void PruneLocked(
        PrincipalState state,
        DateTimeOffset now,
        List<Entry> pendingDisposals)
    {
        PrunePrepareAttemptsLocked(state, now);
        foreach (var entry in state.ActiveByToken.Values.ToArray())
        {
            if (now >= entry.CreatedUtc
                && now >= entry.LastAccessUtc
                && now - entry.LastAccessUtc <= _options.IdleTtl
                && now <= entry.AbsoluteExpiresAtUtc)
            {
                continue;
            }

            RetireEntryLocked(
                state,
                entry,
                SymbolContextFailure.Expired,
                now,
                pendingDisposals);
        }

        while (state.TombstoneOrder.Count > 0)
        {
            var token = state.TombstoneOrder.Peek();
            if (!state.Tombstones.TryGetValue(token, out var tombstone))
            {
                state.TombstoneOrder.Dequeue();
                continue;
            }
            if (state.Tombstones.Count <= _options.MaxTombstonesPerPrincipal
                && now - tombstone.CreatedUtc <= _options.AbsoluteTtl)
            {
                break;
            }

            state.TombstoneOrder.Dequeue();
            state.Tombstones.Remove(token);
        }
    }

    private void PrunePrepareAttemptsLocked(PrincipalState state, DateTimeOffset now)
    {
        while (state.PrepareAttempts.TryPeek(out var attempt)
               && now - attempt >= _options.PrepareRateWindow)
        {
            state.PrepareAttempts.Dequeue();
        }
    }

    private void RetireEntryLocked(
        PrincipalState state,
        Entry entry,
        SymbolContextFailure failure,
        DateTimeOffset now,
        List<Entry> pendingDisposals)
    {
        if (entry.State != EntryState.Active)
            return;

        entry.State = EntryState.Retiring;
        if (state.ActiveByToken.TryGetValue(entry.Token, out var tokenEntry)
            && ReferenceEquals(entry, tokenEntry))
        {
            state.ActiveByToken.Remove(entry.Token);
        }
        if (state.ActiveByRevision.TryGetValue(entry.Prepared.Definition.Revision, out var revisionEntry)
            && ReferenceEquals(entry, revisionEntry))
        {
            state.ActiveByRevision.Remove(entry.Prepared.Definition.Revision);
        }
        AddTombstoneLocked(state, entry.Token, failure, now);

        if (entry.ActiveLeases == 0 && !entry.DisposalStarted)
        {
            entry.DisposalStarted = true;
            pendingDisposals.Add(entry);
        }
    }

    private void AddTombstoneLocked(
        PrincipalState state,
        string token,
        SymbolContextFailure failure,
        DateTimeOffset now)
    {
        if (state.Tombstones.TryAdd(token, new Tombstone(failure, now)))
            state.TombstoneOrder.Enqueue(token);

        while (state.Tombstones.Count > _options.MaxTombstonesPerPrincipal
               && state.TombstoneOrder.TryDequeue(out var evicted))
        {
            state.Tombstones.Remove(evicted);
        }
    }

    private string MintTokenLocked(PrincipalState state)
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var bytes = _randomBytes();
            if (bytes is null || bytes.Length != 16)
                throw new InvalidOperationException("The symbol context random source must return exactly 16 bytes.");

            var token = Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
            if (!state.ActiveByToken.ContainsKey(token) && !state.Tombstones.ContainsKey(token))
                return token;
        }

        throw new SymbolContextException(
            SymbolContextFailure.QuotaExceeded,
            "The symbol context registry could not mint a unique locator.");
    }

    private static void ValidatePrincipal(SymbolPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(principal.ScopeId))
            throw new ArgumentException("A symbol principal scope is required.", nameof(principal));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(SymbolContextRegistry));
    }

    private void StartDisposals(IEnumerable<Entry> entries)
    {
        foreach (var entry in entries)
            StartDisposal(entry);
    }

    private void StartDisposal(Entry entry) => _ = DisposeEntryAsync(entry);

    private async Task DisposeEntryAsync(Entry entry)
    {
        try
        {
            await entry.Prepared.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            entry.DisposeFailure = exception;
        }
        finally
        {
            lock (_gate)
            {
                entry.State = entry.DisposeFailure is null
                    ? EntryState.Disposed
                    : EntryState.DisposeFailed;
                if (entry.DisposeFailure is null
                    && _principals.TryGetValue(entry.Principal, out var state))
                {
                    state.Owned.Remove(entry);
                }
            }
            entry.Disposed.TrySetResult();
        }
    }

    private static SymbolContextException Unknown()
        => new(SymbolContextFailure.Unknown, "The symbol context is unknown.");

    private static SymbolContextException Expired(string? message = null)
        => new(SymbolContextFailure.Expired, message ?? "The symbol context has expired.");

    private static SymbolContextException Retired()
        => new(SymbolContextFailure.Retired, "The symbol context has been retired.");

    private sealed class PrincipalState
    {
        public Dictionary<string, Entry> ActiveByToken { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, Entry> ActiveByRevision { get; } = new(StringComparer.Ordinal);

        public HashSet<Entry> Owned { get; } = new(ReferenceEqualityComparer.Instance);

        public Dictionary<string, Tombstone> Tombstones { get; } = new(StringComparer.Ordinal);

        public Queue<string> TombstoneOrder { get; } = new();

        public Queue<DateTimeOffset> PrepareAttempts { get; } = new();
    }

    private sealed class Entry
    {
        public Entry(
            SymbolPrincipal principal,
            string token,
            PreparedSymbolContext prepared,
            DateTimeOffset createdUtc,
            DateTimeOffset absoluteExpiresAtUtc)
        {
            Principal = principal;
            Token = token;
            Prepared = prepared;
            CreatedUtc = createdUtc;
            LastAccessUtc = createdUtc;
            AbsoluteExpiresAtUtc = absoluteExpiresAtUtc;
            Descriptor = new SymbolContextDescriptor(
                token,
                prepared.Definition.Revision,
                prepared.Definition.TraceGenerationIdentity,
                prepared.Definition.Policy.PolicyReference,
                prepared.Definition.Policy.SnapshotRevision,
                prepared.Definition.ResolverVersion,
                prepared.Definition.PrivacyProfile,
                prepared.Definition.ContractVersion,
                createdUtc,
                absoluteExpiresAtUtc,
                prepared.Evidence);
        }

        public SymbolPrincipal Principal { get; }

        public string Token { get; }

        public PreparedSymbolContext Prepared { get; }

        public SymbolContextDescriptor Descriptor { get; }

        public DateTimeOffset LastAccessUtc { get; set; }

        public DateTimeOffset CreatedUtc { get; }

        public DateTimeOffset AbsoluteExpiresAtUtc { get; }

        public EntryState State { get; set; }

        public int ActiveLeases { get; set; }

        public bool DisposalStarted { get; set; }

        public Exception? DisposeFailure { get; set; }

        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed record Tombstone(
        SymbolContextFailure Failure,
        DateTimeOffset CreatedUtc);

    private enum EntryState
    {
        Active,
        Retiring,
        Disposed,
        DisposeFailed,
    }

}

internal sealed class SymbolContextLease : IAsyncDisposable
{
    private readonly SymbolContextDescriptor _descriptor;
    private readonly SymbolContextDefinition _definition;
    private readonly IReadOnlyList<IVerifiedSymbolArtifactPin> _artifactPins;
    private Action? _release;

    internal SymbolContextLease(
        SymbolContextDescriptor descriptor,
        SymbolContextDefinition definition,
        IReadOnlyList<IVerifiedSymbolArtifactPin> artifactPins,
        Action release)
    {
        _descriptor = descriptor;
        _definition = definition;
        _artifactPins = artifactPins;
        _release = release;
    }

    public SymbolContextDescriptor Descriptor
    {
        get
        {
            ThrowIfDisposed();
            return _descriptor;
        }
    }

    public SymbolContextDefinition Definition
    {
        get
        {
            ThrowIfDisposed();
            return _definition;
        }
    }

    public IReadOnlyList<IVerifiedSymbolArtifactPin> ArtifactPins
    {
        get
        {
            ThrowIfDisposed();
            return _artifactPins;
        }
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _release) is null)
            throw new ObjectDisposedException(nameof(SymbolContextLease));
    }
}
