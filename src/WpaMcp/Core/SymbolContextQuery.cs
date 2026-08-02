namespace WpaMcp.Core;

internal sealed record SymbolFrameLookupRequest(
    string TraceGenerationIdentity,
    string ImageIdentity,
    string Architecture,
    string PdbName,
    Guid PdbSignature,
    int PdbAge,
    ulong NormalizedAddress,
    string PrivacyProfile,
    string ContractVersion);

internal sealed record SymbolFrameResolutionMeasurement(
    string MeasurementState,
    long FramesAttempted,
    long FramesResolved,
    double? FrameResolutionRate,
    string MeasurementBasis);

internal sealed record SymbolFrameLookupResult(
    string LookupState,
    string? FunctionName,
    bool FromNegativeCache,
    SymbolFrameResolutionMeasurement Measurement);

/// <summary>
/// A context-bound lookup engine may inspect only the supplied pinned artifact. It is
/// not given a symbol path, policy roots, origins, environment accessor, or cache search API.
/// </summary>
internal interface IContextBoundSymbolFrameResolver
{
    string ResolverVersion { get; }

    ValueTask<string?> ResolveFrameAsync(
        IVerifiedSymbolArtifactPin artifact,
        SymbolFrameLookupRequest request,
        CancellationToken cancellationToken);
}

internal sealed record SymbolNegativeCacheKey(
    string ContextRevision,
    string TraceGenerationIdentity,
    string ResolverVersion,
    string PdbIdentity,
    string ImageIdentity,
    string Architecture,
    ulong NormalizedAddress,
    string SymbolArtifactContentIdentity,
    string PrivacyProfile,
    string ContractVersion)
{
    public static SymbolNegativeCacheKey Create(
        SymbolContextDefinition context,
        SymbolFrameLookupRequest request,
        VerifiedSymbolArtifactIdentity artifact)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(artifact);
        return new SymbolNegativeCacheKey(
            context.Revision,
            context.TraceGenerationIdentity,
            context.ResolverVersion,
            string.Join(
                '\u001f',
                request.PdbName.ToLowerInvariant(),
                request.PdbSignature.ToString("N"),
                request.PdbAge.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            request.ImageIdentity,
            request.Architecture.ToLowerInvariant(),
            request.NormalizedAddress,
            artifact.ContentSha256,
            context.PrivacyProfile,
            context.ContractVersion);
    }
}

internal sealed class SymbolNegativeResultCache
{
    private readonly object _gate = new();
    private readonly HashSet<SymbolNegativeCacheKey> _entries = [];
    private readonly int _maxEntries;

    public SymbolNegativeResultCache(int maxEntries = 100_000)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        _maxEntries = maxEntries;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _entries.Count;
        }
    }

    public bool Contains(SymbolNegativeCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
            return _entries.Contains(key);
    }

    public void Record(SymbolNegativeCacheKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (_entries.Count < _maxEntries)
                _entries.Add(key);
        }
    }

    /// <summary>
    /// Invalidates only the named immutable context. New preparation never changes
    /// negative evidence retained for an older context revision.
    /// </summary>
    public int InvalidateContext(string contextRevision)
    {
        if (string.IsNullOrWhiteSpace(contextRevision))
            throw new ArgumentException("A context revision is required.", nameof(contextRevision));
        lock (_gate)
        {
            return _entries.RemoveWhere(key =>
                string.Equals(key.ContextRevision, contextRevision, StringComparison.Ordinal));
        }
    }
}

/// <summary>
/// Query-time symbol resolution requires a live SymbolContextLease. There is no
/// nullable-context overload, so callers without a context can only use the pure
/// SymbolEvidenceBoundary.WithoutContext metadata projection.
/// </summary>
internal sealed class ContextBoundSymbolQueryService
{
    private readonly IContextBoundSymbolFrameResolver _resolver;
    private readonly SymbolNegativeResultCache _negativeCache;

    public ContextBoundSymbolQueryService(
        IContextBoundSymbolFrameResolver resolver,
        SymbolNegativeResultCache? negativeCache = null)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _negativeCache = negativeCache ?? new SymbolNegativeResultCache();
    }

    public async ValueTask<SymbolFrameLookupResult> ResolveAsync(
        SymbolContextLease contextLease,
        SymbolFrameLookupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contextLease);
        ArgumentNullException.ThrowIfNull(request);
        var context = contextLease.Definition;
        EnsureExactContextBinding(context, request);
        if (!string.Equals(_resolver.ResolverVersion, context.ResolverVersion, StringComparison.Ordinal))
        {
            throw new SymbolContextException(
                SymbolContextFailure.Expired,
                "The query resolver version does not match the immutable symbol context.");
        }

        var pin = contextLease.ArtifactPins.FirstOrDefault(candidate =>
            string.Equals(candidate.Identity.PdbName, request.PdbName, StringComparison.OrdinalIgnoreCase)
            && candidate.Identity.PdbSignature == request.PdbSignature
            && candidate.Identity.PdbAge == request.PdbAge);
        if (pin is null)
        {
            return new SymbolFrameLookupResult(
                LookupState: "not_available_in_context",
                FunctionName: null,
                FromNegativeCache: false,
                Measurement: new SymbolFrameResolutionMeasurement(
                    MeasurementState: "not_attempted",
                    FramesAttempted: 0,
                    FramesResolved: 0,
                    FrameResolutionRate: null,
                    MeasurementBasis: "direct"));
        }

        // SymbolContextRegistry.AcquireAsync validates every artifact pin once at the
        // query/batch boundary. Production pins retain a non-writable opened handle,
        // so re-hashing the complete PDB for every frame would add O(PDB bytes) work
        // to the lookup inner loop without strengthening the held-handle guarantee.
        var negativeKey = SymbolNegativeCacheKey.Create(context, request, pin.Identity);
        if (_negativeCache.Contains(negativeKey))
            return Unresolved(fromNegativeCache: true);

        string? function;
        try
        {
            function = await _resolver.ResolveFrameAsync(
                pin,
                request,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException or ObjectDisposedException)
        {
            throw new SymbolContextException(
                SymbolContextFailure.Expired,
                "A pinned verified symbol artifact can no longer satisfy the immutable context.",
                exception);
        }

        if (string.IsNullOrWhiteSpace(function))
        {
            _negativeCache.Record(negativeKey);
            return Unresolved(fromNegativeCache: false);
        }

        return new SymbolFrameLookupResult(
            LookupState: "resolved",
            FunctionName: function,
            FromNegativeCache: false,
            Measurement: new SymbolFrameResolutionMeasurement(
                MeasurementState: "measured",
                FramesAttempted: 1,
                FramesResolved: 1,
                FrameResolutionRate: 1,
                MeasurementBasis: "direct"));
    }

    private static SymbolFrameLookupResult Unresolved(bool fromNegativeCache)
        => new(
            LookupState: "unresolved",
            FunctionName: null,
            FromNegativeCache: fromNegativeCache,
            Measurement: new SymbolFrameResolutionMeasurement(
                MeasurementState: "measured",
                FramesAttempted: 1,
                FramesResolved: 0,
                FrameResolutionRate: 0,
                MeasurementBasis: "direct"));

    private static void EnsureExactContextBinding(
        SymbolContextDefinition context,
        SymbolFrameLookupRequest request)
    {
        if (!string.Equals(
                context.TraceGenerationIdentity,
                request.TraceGenerationIdentity,
                StringComparison.Ordinal)
            || !string.Equals(context.PrivacyProfile, request.PrivacyProfile, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(context.ContractVersion, request.ContractVersion, StringComparison.OrdinalIgnoreCase))
        {
            throw new SymbolContextException(
                SymbolContextFailure.TraceBindingMismatch,
                "The symbol lookup request does not match the immutable context binding.");
        }
    }
}
