using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace WpaMcp.Core;

/// <summary>
/// An authorization/session boundary for symbol contexts. The value is deliberately
/// opaque to the symbol subsystem and must be supplied by the hosting transport.
/// </summary>
internal readonly record struct SymbolPrincipal
{
    public SymbolPrincipal(string scopeId)
    {
        if (string.IsNullOrWhiteSpace(scopeId))
            throw new ArgumentException("A symbol principal scope is required.", nameof(scopeId));

        ScopeId = scopeId;
    }

    public string ScopeId { get; }

    // Avoid accidentally placing the principal identifier in ordinary diagnostics.
    public override string ToString() => "<symbol-principal>";
}

/// <summary>
/// Adapter seam for the trace-handle registry. GenerationIdentity is an internal,
/// immutable opaque value; implementations must never use a public path here.
/// </summary>
internal interface ISymbolTraceGenerationReference
{
    string GenerationIdentity { get; }

    IReadOnlyList<TraceModulePdbIdentity> ModulePdbIdentities { get; }
}

internal sealed class OpaqueSymbolTraceGenerationReference : ISymbolTraceGenerationReference
{
    public OpaqueSymbolTraceGenerationReference(
        string generationIdentity,
        IEnumerable<TraceModulePdbIdentity> modulePdbIdentities)
    {
        if (string.IsNullOrWhiteSpace(generationIdentity))
            throw new ArgumentException("An opaque trace generation identity is required.", nameof(generationIdentity));

        ArgumentNullException.ThrowIfNull(modulePdbIdentities);
        GenerationIdentity = generationIdentity;
        ModulePdbIdentities = modulePdbIdentities
            .OrderBy(static identity => identity.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public string GenerationIdentity { get; }

    public IReadOnlyList<TraceModulePdbIdentity> ModulePdbIdentities { get; }
}

/// <summary>
/// Trace-native module and CodeView/PDB identity. This is metadata evidence and is
/// intentionally separate from any observed frame-name resolution measurement.
/// </summary>
internal sealed record TraceModulePdbIdentity
{
    public TraceModulePdbIdentity(
        string imageIdentity,
        string imageName,
        string architecture,
        string pdbName,
        Guid pdbSignature,
        int pdbAge)
    {
        ImageIdentity = Require(imageIdentity, nameof(imageIdentity));
        ImageName = Require(imageName, nameof(imageName));
        Architecture = NormalizeToken(architecture, nameof(architecture));
        PdbName = Path.GetFileName(Require(pdbName, nameof(pdbName)));
        if (string.IsNullOrWhiteSpace(PdbName))
            throw new ArgumentException("A PDB file name is required.", nameof(pdbName));
        if (PdbName.Contains(':', StringComparison.Ordinal)
            || PdbName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                "The PDB name must not contain an alternate stream or invalid file-name characters.",
                nameof(pdbName));
        }
        if (pdbSignature == Guid.Empty)
            throw new ArgumentException("A non-empty PDB signature is required.", nameof(pdbSignature));
        if (pdbAge <= 0)
            throw new ArgumentOutOfRangeException(nameof(pdbAge));

        PdbSignature = pdbSignature;
        PdbAge = pdbAge;
        CanonicalIdentity = string.Join(
            '\u001f',
            ImageIdentity,
            ImageName.ToLowerInvariant(),
            Architecture,
            PdbName.ToLowerInvariant(),
            PdbSignature.ToString("N"),
            PdbAge.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public string ImageIdentity { get; }

    public string ImageName { get; }

    public string Architecture { get; }

    public string PdbName { get; }

    public Guid PdbSignature { get; }

    public int PdbAge { get; }

    internal string CanonicalIdentity { get; }

    internal string PdbIdentity => string.Join(
        '\u001f',
        PdbName.ToLowerInvariant(),
        PdbSignature.ToString("N"),
        PdbAge.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string NormalizeToken(string value, string parameterName)
        => Require(value, parameterName).ToLowerInvariant();
}

internal enum SymbolNetworkPolicy
{
    Denied,
    ApprovedOrigins,
}

/// <summary>
/// Immutable result of resolving a startup-approved policy reference. Callers cannot
/// pass arbitrary roots or origins to prepare_symbols.
/// </summary>
internal sealed class ApprovedSymbolPolicySnapshot
{
    public ApprovedSymbolPolicySnapshot(
        string policyReference,
        string policyRevision,
        IEnumerable<string> approvedLocalRoots,
        SymbolNetworkPolicy networkPolicy,
        IEnumerable<Uri>? approvedOrigins,
        string cacheProfile)
    {
        PolicyReference = NormalizePolicyReference(policyReference);
        PolicyRevision = Require(policyRevision, nameof(policyRevision));
        CacheProfile = NormalizeToken(cacheProfile, nameof(cacheProfile));
        NetworkPolicy = networkPolicy;

        ArgumentNullException.ThrowIfNull(approvedLocalRoots);
        ApprovedLocalRoots = approvedLocalRoots
            .Select(NormalizeLocalRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        ApprovedOrigins = (approvedOrigins ?? [])
            .Select(NormalizeOrigin)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static origin => origin, StringComparer.Ordinal)
            .ToImmutableArray();

        if (NetworkPolicy == SymbolNetworkPolicy.Denied && ApprovedOrigins.Length != 0)
        {
            throw new ArgumentException(
                "A denied network policy cannot contain approved origins.",
                nameof(approvedOrigins));
        }
        if (NetworkPolicy == SymbolNetworkPolicy.ApprovedOrigins && ApprovedOrigins.Length == 0)
        {
            throw new ArgumentException(
                "An approved-origins network policy requires at least one origin.",
                nameof(approvedOrigins));
        }

        SnapshotRevision = SymbolCanonicalHash.Compute(builder =>
        {
            builder.Add("symbol-policy-v1");
            builder.Add(PolicyReference);
            builder.Add(PolicyRevision);
            builder.Add(NetworkPolicy.ToString());
            builder.Add(CacheProfile);
            foreach (var root in ApprovedLocalRoots)
                builder.Add(root.ToUpperInvariant());
            foreach (var origin in ApprovedOrigins)
                builder.Add(origin);
        });
    }

    public string PolicyReference { get; }

    public string PolicyRevision { get; }

    public ImmutableArray<string> ApprovedLocalRoots { get; }

    public SymbolNetworkPolicy NetworkPolicy { get; }

    public ImmutableArray<string> ApprovedOrigins { get; }

    public string CacheProfile { get; }

    public string SnapshotRevision { get; }

    internal static string NormalizePolicyReference(string policyReference)
        => NormalizeToken(policyReference, nameof(policyReference));

    private static string NormalizeLocalRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("Approved local symbol roots cannot be empty.", nameof(root));
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("Approved local symbol roots must be fully qualified.", nameof(root));

        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Trim()));
        if (normalized.StartsWith("\\\\", StringComparison.Ordinal)
            || normalized.StartsWith("\\??\\", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows()
                && normalized.AsSpan(Math.Min(2, normalized.Length)).Contains(':')))
        {
            throw new ArgumentException(
                "UNC, device-namespace, and alternate-stream symbol roots are not approved local roots.",
                nameof(root));
        }
        return normalized;
    }

    private static string NormalizeOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri
            || !string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query)
            || !string.IsNullOrEmpty(origin.Fragment)
            || (origin.AbsolutePath is not "" and not "/"))
        {
            throw new ArgumentException("Approved symbol origins must be HTTPS origins.", nameof(origin));
        }

        return origin.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            .ToLowerInvariant();
    }

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string NormalizeToken(string value, string parameterName)
        => Require(value, parameterName).ToLowerInvariant();
}

internal sealed record VerifiedSymbolArtifactIdentity
{
    public VerifiedSymbolArtifactIdentity(
        string contentSha256,
        long byteLength,
        string pdbName,
        Guid pdbSignature,
        int pdbAge,
        string artifactFormat)
    {
        if (!IsLowerHexSha256(contentSha256))
            throw new ArgumentException("The content identity must be 64 lowercase hexadecimal digits.", nameof(contentSha256));
        if (byteLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(byteLength));
        if (pdbSignature == Guid.Empty)
            throw new ArgumentException("A non-empty PDB signature is required.", nameof(pdbSignature));
        if (pdbAge <= 0)
            throw new ArgumentOutOfRangeException(nameof(pdbAge));

        ContentSha256 = contentSha256;
        ByteLength = byteLength;
        PdbName = Path.GetFileName(pdbName ?? throw new ArgumentNullException(nameof(pdbName)));
        if (string.IsNullOrWhiteSpace(PdbName))
            throw new ArgumentException("A PDB file name is required.", nameof(pdbName));
        PdbSignature = pdbSignature;
        PdbAge = pdbAge;
        ArtifactFormat = string.IsNullOrWhiteSpace(artifactFormat)
            ? throw new ArgumentException("An artifact format is required.", nameof(artifactFormat))
            : artifactFormat.Trim().ToLowerInvariant();
        CanonicalIdentity = string.Join(
            '\u001f',
            ContentSha256,
            ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PdbName.ToLowerInvariant(),
            PdbSignature.ToString("N"),
            PdbAge.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ArtifactFormat);
    }

    public string ContentSha256 { get; }

    public long ByteLength { get; }

    public string PdbName { get; }

    public Guid PdbSignature { get; }

    public int PdbAge { get; }

    public string ArtifactFormat { get; }

    internal string CanonicalIdentity { get; }

    internal string PdbIdentity => string.Join(
        '\u001f',
        PdbName.ToLowerInvariant(),
        PdbSignature.ToString("N"),
        PdbAge.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
            return false;
        return value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}

/// <summary>
/// A pin to one already-verified, immutable artifact. Implementations may retain a
/// read handle or artifact-store lease. They must never search for a replacement.
/// </summary>
internal interface IVerifiedSymbolArtifactPin : IAsyncDisposable
{
    VerifiedSymbolArtifactIdentity Identity { get; }

    /// <summary>
    /// Constant-time liveness check for the already-verified immutable handle. Full
    /// content hashing and PDB identity verification belong to artifact ingestion,
    /// never to query acquisition.
    /// </summary>
    ValueTask<bool> IsAvailableAsync(CancellationToken cancellationToken);
}

internal sealed record SymbolPreparationEvidence(
    int ModulesWithPdbIdentity,
    int ModulesWithVerifiedSymbolArtifact,
    int VerifiedSymbolArtifactCount,
    string LocalReadinessState,
    string LocalReadinessMeasurementBasis,
    string FrameResolutionState,
    long? FramesAttempted,
    long? FramesResolved,
    double? FrameResolutionRate)
{
    public static SymbolPreparationEvidence Create(
        IReadOnlyList<TraceModulePdbIdentity> modules,
        IReadOnlyList<VerifiedSymbolArtifactIdentity> artifacts)
    {
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(artifacts);
        var verifiedPdbIdentities = artifacts
            .Select(static artifact => artifact.PdbIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var coveredModules = modules.Count(module =>
            verifiedPdbIdentities.Contains(module.PdbIdentity));
        return new(
            ModulesWithPdbIdentity: modules.Count,
            ModulesWithVerifiedSymbolArtifact: coveredModules,
            VerifiedSymbolArtifactCount: artifacts.Count,
            LocalReadinessState: coveredModules == 0
                ? "not_ready"
                : coveredModules < modules.Count ? "partial" : "ready",
            LocalReadinessMeasurementBasis: "direct",
            FrameResolutionState: "unmeasured",
            FramesAttempted: null,
            FramesResolved: null,
            FrameResolutionRate: null);
    }
}

internal sealed record SymbolEvidenceBoundary(
    int ModulesWithPdbIdentity,
    string LocalReadinessState,
    string LocalReadinessMeasurementBasis,
    string FrameResolutionState,
    long? FramesAttempted,
    long? FramesResolved,
    double? FrameResolutionRate)
{
    /// <summary>
    /// Pure trace-metadata projection. This method deliberately has no resolver, store,
    /// filesystem, environment, or network dependency.
    /// </summary>
    public static SymbolEvidenceBoundary WithoutContext(ISymbolTraceGenerationReference trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        return new SymbolEvidenceBoundary(
            trace.ModulePdbIdentities.Count,
            LocalReadinessState: "unmeasured",
            LocalReadinessMeasurementBasis: "unmeasured",
            FrameResolutionState: "unmeasured",
            FramesAttempted: null,
            FramesResolved: null,
            FrameResolutionRate: null);
    }
}

internal sealed class SymbolContextDefinition
{
    private SymbolContextDefinition(
        SymbolPrincipal principal,
        string traceGenerationIdentity,
        ApprovedSymbolPolicySnapshot policy,
        string resolverVersion,
        ImmutableArray<TraceModulePdbIdentity> modules,
        ImmutableArray<VerifiedSymbolArtifactIdentity> artifacts,
        string privacyProfile,
        string contractVersion,
        string revision)
    {
        Principal = principal;
        TraceGenerationIdentity = traceGenerationIdentity;
        Policy = policy;
        ResolverVersion = resolverVersion;
        Modules = modules;
        Artifacts = artifacts;
        PrivacyProfile = privacyProfile;
        ContractVersion = contractVersion;
        Revision = revision;
    }

    public SymbolPrincipal Principal { get; }

    public string TraceGenerationIdentity { get; }

    public ApprovedSymbolPolicySnapshot Policy { get; }

    public string ResolverVersion { get; }

    public ImmutableArray<TraceModulePdbIdentity> Modules { get; }

    public ImmutableArray<VerifiedSymbolArtifactIdentity> Artifacts { get; }

    public string PrivacyProfile { get; }

    public string ContractVersion { get; }

    /// <summary>Content identity for every immutable input to this context.</summary>
    public string Revision { get; }

    public static SymbolContextDefinition Create(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        ApprovedSymbolPolicySnapshot policy,
        string resolverVersion,
        IEnumerable<VerifiedSymbolArtifactIdentity> artifacts,
        string privacyProfile,
        string contractVersion)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(artifacts);
        var generation = Require(trace.GenerationIdentity, nameof(trace));
        var resolver = Require(resolverVersion, nameof(resolverVersion));
        var privacy = NormalizeToken(privacyProfile, nameof(privacyProfile));
        var contract = NormalizeToken(contractVersion, nameof(contractVersion));
        var modules = trace.ModulePdbIdentities
            .OrderBy(static identity => identity.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();
        var verifiedArtifacts = artifacts
            .DistinctBy(static identity => identity.CanonicalIdentity, StringComparer.Ordinal)
            .OrderBy(static identity => identity.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();

        var revision = SymbolCanonicalHash.Compute(builder =>
        {
            builder.Add("immutable-symbol-context-v1");
            builder.Add(principal.ScopeId);
            builder.Add(generation);
            builder.Add(policy.SnapshotRevision);
            builder.Add(resolver);
            builder.Add(privacy);
            builder.Add(contract);
            foreach (var module in modules)
                builder.Add(module.CanonicalIdentity);
            foreach (var artifact in verifiedArtifacts)
                builder.Add(artifact.CanonicalIdentity);
        });

        return new SymbolContextDefinition(
            principal,
            generation,
            policy,
            resolver,
            modules,
            verifiedArtifacts,
            privacy,
            contract,
            revision);
    }

    internal static string CreatePreparationRevision(
        SymbolPrincipal principal,
        ISymbolTraceGenerationReference trace,
        ApprovedSymbolPolicySnapshot policy,
        string resolverVersion,
        string privacyProfile,
        string contractVersion)
        => SymbolCanonicalHash.Compute(builder =>
        {
            builder.Add("symbol-preparation-flight-v1");
            builder.Add(principal.ScopeId);
            builder.Add(Require(trace.GenerationIdentity, nameof(trace)));
            builder.Add(policy.SnapshotRevision);
            builder.Add(Require(resolverVersion, nameof(resolverVersion)));
            builder.Add(NormalizeToken(privacyProfile, nameof(privacyProfile)));
            builder.Add(NormalizeToken(contractVersion, nameof(contractVersion)));
            foreach (var module in trace.ModulePdbIdentities.OrderBy(
                         static identity => identity.CanonicalIdentity,
                         StringComparer.Ordinal))
            {
                builder.Add(module.CanonicalIdentity);
            }
        });

    private static string Require(string value, string parameterName)
        => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty value is required.", parameterName)
            : value.Trim();

    private static string NormalizeToken(string value, string parameterName)
        => Require(value, parameterName).ToLowerInvariant();
}

internal sealed class PreparedSymbolContext : IAsyncDisposable
{
    private ImmutableArray<IVerifiedSymbolArtifactPin> _pins;
    private int _disposed;

    public PreparedSymbolContext(
        SymbolContextDefinition definition,
        SymbolPreparationEvidence evidence,
        IEnumerable<IVerifiedSymbolArtifactPin> pins)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        ArgumentNullException.ThrowIfNull(pins);
        _pins = pins
            .OrderBy(static pin => pin.Identity.CanonicalIdentity, StringComparer.Ordinal)
            .ToImmutableArray();

        var pinIdentities = _pins
            .Select(static pin => pin.Identity.CanonicalIdentity)
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
        var definitionIdentities = definition.Artifacts
            .Select(static artifact => artifact.CanonicalIdentity)
            .OrderBy(static identity => identity, StringComparer.Ordinal)
            .ToArray();
        if (!pinIdentities.SequenceEqual(definitionIdentities, StringComparer.Ordinal))
            throw new ArgumentException("Artifact pins must exactly match the context definition.", nameof(pins));
    }

    public SymbolContextDefinition Definition { get; }

    public SymbolPreparationEvidence Evidence { get; }

    public IReadOnlyList<IVerifiedSymbolArtifactPin> Pins => _pins;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

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
            throw new AggregateException("One or more verified symbol artifact pins failed to release.", failures);
    }
}

internal sealed class SymbolCanonicalHash : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    private SymbolCanonicalHash()
    {
    }

    public static string Compute(Action<SymbolCanonicalHash> append)
    {
        ArgumentNullException.ThrowIfNull(append);
        using var builder = new SymbolCanonicalHash();
        append(builder);
        return Convert.ToHexString(builder._hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Add(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        _hash.AppendData(length);
        _hash.AppendData(bytes);
    }

    public void Dispose() => _hash.Dispose();
}
