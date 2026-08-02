using System.Collections.Immutable;

namespace WpaMcp.Core;

internal interface IApprovedSymbolPolicyProvider
{
    ValueTask<ApprovedSymbolPolicySnapshot> ResolveAsync(
        SymbolPrincipal principal,
        string normalizedPolicyReference,
        CancellationToken cancellationToken);
}

internal sealed class ApprovedSymbolPolicyCatalog : IApprovedSymbolPolicyProvider
{
    private readonly IReadOnlyDictionary<string, ApprovedSymbolPolicySnapshot> _policies;
    private readonly Func<SymbolPrincipal, ApprovedSymbolPolicySnapshot, bool> _authorize;

    public ApprovedSymbolPolicyCatalog(
        IEnumerable<ApprovedSymbolPolicySnapshot> policies,
        Func<SymbolPrincipal, ApprovedSymbolPolicySnapshot, bool>? authorize = null)
    {
        ArgumentNullException.ThrowIfNull(policies);
        _policies = policies.ToDictionary(
            static policy => policy.PolicyReference,
            StringComparer.Ordinal);
        _authorize = authorize ?? (static (_, _) => true);
    }

    public ValueTask<ApprovedSymbolPolicySnapshot> ResolveAsync(
        SymbolPrincipal principal,
        string normalizedPolicyReference,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_policies.TryGetValue(normalizedPolicyReference, out var policy)
            || !_authorize(principal, policy))
        {
            throw new SymbolContextException(
                SymbolContextFailure.PolicyDenied,
                "The requested symbol policy is not approved for this principal.");
        }

        return ValueTask.FromResult(policy);
    }
}

internal sealed record ApprovedLocalSymbolCandidate(
    string ApprovedRoot,
    ImmutableArray<string> RelativeSegments)
{
    public string GetValidatedPath()
    {
        if (!Path.IsPathFullyQualified(ApprovedRoot))
            throw new InvalidOperationException("The approved root is not fully qualified.");
        if (RelativeSegments.IsDefaultOrEmpty)
            throw new InvalidOperationException("A local symbol candidate requires relative segments.");
        if (RelativeSegments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0))
        {
            throw new InvalidOperationException("A local symbol candidate contains an invalid path segment.");
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ApprovedRoot));
        var combined = RelativeSegments.Aggregate(root, Path.Combine);
        var candidate = Path.GetFullPath(combined);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A local symbol candidate escaped its approved root.");
        return candidate;
    }
}

/// <summary>
/// Artifact-store seam that must copy/verify a candidate against the complete PDB
/// identity before returning a pin. It must never search outside candidate.ApprovedRoot.
/// </summary>
internal interface IVerifiedSymbolArtifactStore
{
    ValueTask<IVerifiedSymbolArtifactPin?> TryVerifyAndPinLocalAsync(
        ApprovedLocalSymbolCandidate candidate,
        TraceModulePdbIdentity expectedIdentity,
        CancellationToken cancellationToken);
}

internal sealed record SymbolPreparationRequest(
    SymbolPrincipal Principal,
    ISymbolTraceGenerationReference Trace,
    ApprovedSymbolPolicySnapshot Policy,
    string PrivacyProfile,
    string ContractVersion);

internal interface ISymbolPreparationResolver
{
    string ResolverVersion { get; }

    ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
        SymbolPreparationRequest request,
        CancellationToken cancellationToken);
}

internal sealed class ResolvedSymbolArtifacts : IAsyncDisposable
{
    private ImmutableArray<IVerifiedSymbolArtifactPin> _pins;
    private bool _detached;

    public ResolvedSymbolArtifacts(IEnumerable<IVerifiedSymbolArtifactPin> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        _pins = pins.ToImmutableArray();
    }

    public IReadOnlyList<IVerifiedSymbolArtifactPin> Pins
        => _detached
            ? throw new ObjectDisposedException(nameof(ResolvedSymbolArtifacts))
            : _pins;

    public ImmutableArray<IVerifiedSymbolArtifactPin> DetachPins()
    {
        if (_detached)
            throw new ObjectDisposedException(nameof(ResolvedSymbolArtifacts));
        _detached = true;
        var pins = _pins;
        _pins = [];
        return pins;
    }

    public async ValueTask DisposeAsync()
    {
        if (_detached)
            return;
        _detached = true;
        List<Exception>? failures = null;
        foreach (var pin in _pins)
        {
            try
            {
                await pin.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }
        _pins = [];
        if (failures is not null)
            throw new AggregateException("One or more resolved symbol artifact pins failed to release.", failures);
    }
}

/// <summary>
/// Safe local-only resolver. Remote-enabled policy is rejected explicitly instead of
/// being silently treated as ready. All local probing is delegated to a verified store
/// and can only occur through SymbolPreparationService.
/// </summary>
internal sealed class LocalOnlySymbolPreparationResolver : ISymbolPreparationResolver
{
    private readonly IVerifiedSymbolArtifactStore _artifactStore;

    public LocalOnlySymbolPreparationResolver(
        IVerifiedSymbolArtifactStore artifactStore,
        string resolverVersion = "local-only-v1")
    {
        _artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
        ResolverVersion = string.IsNullOrWhiteSpace(resolverVersion)
            ? throw new ArgumentException("A resolver version is required.", nameof(resolverVersion))
            : resolverVersion.Trim();
    }

    public string ResolverVersion { get; }

    public async ValueTask<ResolvedSymbolArtifacts> PrepareAsync(
        SymbolPreparationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Policy.NetworkPolicy != SymbolNetworkPolicy.Denied)
        {
            throw new SymbolContextException(
                SymbolContextFailure.RemoteResolutionUnimplemented,
                "This resolver is local-only; approved remote origins require a remote-capable resolver.");
        }

        List<IVerifiedSymbolArtifactPin> pins = [];
        try
        {
            foreach (var expected in request.Trace.ModulePdbIdentities
                         .DistinctBy(static identity => identity.PdbIdentity, StringComparer.Ordinal)
                         .OrderBy(static identity => identity.CanonicalIdentity, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                IVerifiedSymbolArtifactPin? verified = null;
                foreach (var candidate in Candidates(request.Policy, expected))
                {
                    verified = await _artifactStore.TryVerifyAndPinLocalAsync(
                        candidate,
                        expected,
                        cancellationToken).ConfigureAwait(false);
                    if (verified is null)
                        continue;

                    EnsureExactIdentity(verified.Identity, expected);
                    pins.Add(verified);
                    break;
                }
            }

            return new ResolvedSymbolArtifacts(pins);
        }
        catch
        {
            List<Exception>? disposalFailures = null;
            foreach (var pin in pins)
            {
                try
                {
                    await pin.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (disposalFailures ??= []).Add(exception);
                }
            }
            if (disposalFailures is not null)
            {
                throw new AggregateException(
                    "Symbol preparation failed and one or more acquired pins could not be released.",
                    disposalFailures);
            }
            throw;
        }
    }

    private static IEnumerable<ApprovedLocalSymbolCandidate> Candidates(
        ApprovedSymbolPolicySnapshot policy,
        TraceModulePdbIdentity expected)
    {
        var symbolStoreKey = expected.PdbSignature.ToString("N").ToUpperInvariant()
                             + expected.PdbAge.ToString("X", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var root in policy.ApprovedLocalRoots)
        {
            yield return new ApprovedLocalSymbolCandidate(root, [expected.PdbName]);
            yield return new ApprovedLocalSymbolCandidate(
                root,
                [expected.PdbName, symbolStoreKey, expected.PdbName]);
        }
    }

    private static void EnsureExactIdentity(
        VerifiedSymbolArtifactIdentity actual,
        TraceModulePdbIdentity expected)
    {
        if (!string.Equals(actual.PdbName, expected.PdbName, StringComparison.OrdinalIgnoreCase)
            || actual.PdbSignature != expected.PdbSignature
            || actual.PdbAge != expected.PdbAge)
        {
            throw new SymbolContextException(
                SymbolContextFailure.ArtifactVerificationFailed,
                "The verified artifact store returned a mismatched PDB identity.");
        }
    }
}

internal sealed record SymbolPreparedForDelivery(
    SymbolContextDescriptor Descriptor,
    SymbolContextDisclosure Disclosure);

/// <summary>
/// One response's claim on a newly published or canonically reused context. A new
/// context is retired only when every concurrent disclosure rolls back and none was
/// committed. A reused context is never retired by disclosure rollback.
/// </summary>
internal sealed class SymbolContextDisclosure
{
    private readonly SymbolContextPublicationGroup _group;
    private int _resolved;

    internal SymbolContextDisclosure(SymbolContextPublicationGroup group) =>
        _group = group ?? throw new ArgumentNullException(nameof(group));

    internal void Commit()
    {
        if (Interlocked.Exchange(ref _resolved, 1) == 0)
            _group.CommitOne();
    }

    internal ValueTask RollbackAsync() =>
        Interlocked.Exchange(ref _resolved, 1) == 0
            ? _group.RollbackOneAsync()
            : ValueTask.CompletedTask;
}

internal sealed class SymbolContextPublicationGroup
{
    private readonly object _gate = new();
    private readonly SymbolContextRegistry _registry;
    private readonly SymbolPrincipal _principal;
    private readonly SymbolContextPublication _publication;
    private int _unresolved;
    private bool _anyCommitted;
    private bool _retirementStarted;

    internal SymbolContextPublicationGroup(
        SymbolContextRegistry registry,
        SymbolPrincipal principal,
        SymbolContextPublication publication,
        int initialReservations)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _principal = principal;
        _publication = publication ?? throw new ArgumentNullException(nameof(publication));
        if (initialReservations < 0)
            throw new ArgumentOutOfRangeException(nameof(initialReservations));
        _unresolved = initialReservations;
    }

    internal void AddReservation()
    {
        lock (_gate)
        {
            if (_retirementStarted)
                throw new InvalidOperationException("The symbol publication is already rolling back.");
            _unresolved = checked(_unresolved + 1);
        }
    }

    internal void CommitOne()
    {
        lock (_gate)
        {
            ResolveOneLocked();
            _anyCommitted = true;
        }
    }

    internal ValueTask RollbackOneAsync()
    {
        var retire = false;
        lock (_gate)
        {
            ResolveOneLocked();
            retire = ShouldRetireLocked();
        }
        return retire ? RetireAsync() : ValueTask.CompletedTask;
    }

    internal ValueTask RollbackIfUnreferencedAsync()
    {
        var retire = false;
        lock (_gate)
            retire = ShouldRetireLocked();
        return retire ? RetireAsync() : ValueTask.CompletedTask;
    }

    private void ResolveOneLocked()
    {
        if (_unresolved <= 0)
            throw new InvalidOperationException("The symbol publication has no unresolved disclosure.");
        _unresolved--;
    }

    private bool ShouldRetireLocked()
    {
        if (!_publication.Created || _anyCommitted || _unresolved != 0 || _retirementStarted)
            return false;
        _retirementStarted = true;
        return true;
    }

    private async ValueTask RetireAsync()
    {
        _ = await _registry.RetireAsync(
            _principal,
            _publication.Descriptor.SymbolContextId,
            waitForDrain: true,
            CancellationToken.None).ConfigureAwait(false);
    }
}

/// <summary>
/// The sole orchestration API allowed to invoke an ISymbolPreparationResolver. It
/// canonicalizes equivalent concurrent preparation and publishes only successful,
/// still-observed immutable snapshots.
/// </summary>
internal sealed class SymbolPreparationService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PreparationFlight> _flights = new(StringComparer.Ordinal);
    private readonly SymbolContextRegistry _registry;
    private readonly IApprovedSymbolPolicyProvider _policies;
    private readonly ISymbolPreparationResolver _resolver;

    public SymbolPreparationService(
        SymbolContextRegistry registry,
        IApprovedSymbolPolicyProvider policies,
        ISymbolPreparationResolver resolver)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        if (string.IsNullOrWhiteSpace(resolver.ResolverVersion))
            throw new ArgumentException("The symbol resolver version is required.", nameof(resolver));
    }

    public async ValueTask<SymbolContextDescriptor> PrepareAsync(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        string symbolPolicyReference,
        string privacyProfile,
        string contractVersion,
        CancellationToken cancellationToken = default)
    {
        var prepared = await PrepareForDeliveryAsync(
            principal,
            trace,
            symbolPolicyReference,
            privacyProfile,
            contractVersion,
            cancellationToken).ConfigureAwait(false);
        prepared.Disclosure.Commit();
        return prepared.Descriptor;
    }

    internal async ValueTask<SymbolPreparedForDelivery> PrepareForDeliveryAsync(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        string symbolPolicyReference,
        string privacyProfile,
        string contractVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trace);
        var normalizedPolicyRef = ApprovedSymbolPolicySnapshot.NormalizePolicyReference(symbolPolicyReference);
        _registry.RecordPrepareAttempt(principal);
        var policy = await _policies.ResolveAsync(
            principal,
            normalizedPolicyRef,
            cancellationToken).ConfigureAwait(false);
        if (!string.Equals(policy.PolicyReference, normalizedPolicyRef, StringComparison.Ordinal))
        {
            throw new SymbolContextException(
                SymbolContextFailure.PolicyDenied,
                "The policy provider returned a different policy reference.");
        }

        // Copy the generation binding and module metadata before the caller can release
        // its trace lease. No public path or mutable TraceLog is retained by the flight.
        var traceSnapshot = new OpaqueSymbolTraceGenerationReference(
            trace.GenerationIdentity,
            trace.ModulePdbIdentities);
        var flightKey = SymbolContextDefinition.CreatePreparationRevision(
            principal,
            traceSnapshot,
            policy,
            _resolver.ResolverVersion,
            privacyProfile,
            contractVersion);

        PreparationFlight flight;
        var start = false;
        lock (_gate)
        {
            if (!_flights.TryGetValue(flightKey, out flight!))
            {
                flight = new PreparationFlight(_registry, principal);
                _flights.Add(flightKey, flight);
                start = true;
            }
            flight.Join();
        }

        if (start)
        {
            flight.Start(() => RunPreparationAsync(
                flight,
                principal,
                traceSnapshot,
                policy,
                privacyProfile,
                contractVersion),
                () => RemoveFlight(flightKey, flight));
        }

        var disclosureClaimed = false;
        try
        {
            var publication = await flight.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var disclosure = flight.ClaimDisclosure();
            disclosureClaimed = true;
            return new SymbolPreparedForDelivery(publication.Descriptor, disclosure);
        }
        finally
        {
            await flight.LeaveAsync(disclosureClaimed).ConfigureAwait(false);
        }
    }

    private async Task<SymbolContextPublication> RunPreparationAsync(
        PreparationFlight flight,
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        ApprovedSymbolPolicySnapshot policy,
        string privacyProfile,
        string contractVersion)
    {
        var request = new SymbolPreparationRequest(
            principal,
            trace,
            policy,
            privacyProfile,
            contractVersion);
        await using var resolution = await _resolver.PrepareAsync(
            request,
            flight.CancellationToken).ConfigureAwait(false);
        flight.BeginPublicationOrThrow();

        var pins = resolution.DetachPins();
        PreparedSymbolContext? prepared = null;
        try
        {
            var definition = SymbolContextDefinition.Create(
                principal,
                trace,
                policy,
                _resolver.ResolverVersion,
                pins.Select(static pin => pin.Identity),
                privacyProfile,
                contractVersion);
            prepared = new PreparedSymbolContext(
                definition,
                SymbolPreparationEvidence.Create(
                    trace.ModulePdbIdentities,
                    pins.Select(static pin => pin.Identity).ToArray()),
                pins);
        }
        catch
        {
            List<Exception>? disposalFailures = null;
            foreach (var pin in pins)
            {
                try
                {
                    await pin.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    (disposalFailures ??= []).Add(exception);
                }
            }
            if (disposalFailures is not null)
            {
                throw new AggregateException(
                    "Symbol-context construction failed and one or more pins could not be released.",
                    disposalFailures);
            }
            throw;
        }

        // PublishAsync consumes prepared on success, reuse, and failure.
        return await _registry.PublishWithDispositionAsync(
            principal,
            prepared).ConfigureAwait(false);
    }

    private void RemoveFlight(string flightKey, PreparationFlight flight)
    {
        lock (_gate)
        {
            if (_flights.TryGetValue(flightKey, out var current)
                && ReferenceEquals(current, flight))
            {
                _flights.Remove(flightKey);
            }
        }
    }

    private sealed class PreparationFlight
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cancellation = new();
        private readonly SymbolContextRegistry _registry;
        private readonly SymbolPrincipal _principal;
        private readonly TaskCompletionSource<SymbolContextPublication> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _waiters;
        private bool _publicationStarted;
        private SymbolContextPublicationGroup? _publicationGroup;

        internal PreparationFlight(
            SymbolContextRegistry registry,
            SymbolPrincipal principal)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _principal = principal;
        }

        public Task<SymbolContextPublication> Task => _completion.Task;

        public CancellationToken CancellationToken => _cancellation.Token;

        public void Join()
        {
            lock (_gate)
            {
                _waiters++;
                _publicationGroup?.AddReservation();
            }
        }

        public async ValueTask LeaveAsync(bool disclosureClaimed)
        {
            var cancel = false;
            SymbolContextPublicationGroup? rollback = null;
            lock (_gate)
            {
                if (_waiters > 0)
                    _waiters--;
                if (!disclosureClaimed && _publicationGroup is not null)
                    rollback = _publicationGroup;
                cancel = _waiters == 0 && !_publicationStarted && !_completion.Task.IsCompleted;
            }
            if (cancel)
                _cancellation.Cancel();
            if (rollback is not null)
                await rollback.RollbackOneAsync().ConfigureAwait(false);
        }

        public SymbolContextDisclosure ClaimDisclosure()
        {
            lock (_gate)
            {
                return new SymbolContextDisclosure(
                    _publicationGroup ?? throw new InvalidOperationException(
                        "The symbol publication disclosure group is unavailable."));
            }
        }

        public void BeginPublicationOrThrow()
        {
            lock (_gate)
            {
                if (_waiters == 0 || _cancellation.IsCancellationRequested)
                    throw new OperationCanceledException(_cancellation.Token);
                _publicationStarted = true;
            }
        }

        public void Start(
            Func<Task<SymbolContextPublication>> operation,
            Action completed)
            => _ = CompleteAsync(operation, completed);

        private async Task CompleteAsync(
            Func<Task<SymbolContextPublication>> operation,
            Action completed)
        {
            try
            {
                var publication = await operation().ConfigureAwait(false);
                SymbolContextPublicationGroup group;
                lock (_gate)
                {
                    group = new SymbolContextPublicationGroup(
                        _registry,
                        _principal,
                        publication,
                        _waiters);
                    _publicationGroup = group;
                    _completion.TrySetResult(publication);
                }
                await group.RollbackIfUnreferencedAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
            {
                _completion.TrySetCanceled(_cancellation.Token);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                completed();
                _cancellation.Dispose();
            }
        }
    }
}
