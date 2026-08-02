using System.Globalization;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal static class SymbolTraceGenerationIdentity
{
    internal static string FromCacheSequence(long sequence)
    {
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        return "trace-cache-generation-v1:" +
               sequence.ToString(CultureInfo.InvariantCulture);
    }
}

internal sealed class SymbolTraceGenerationSnapshot : ISymbolTraceGenerationReference
{
    private SymbolTraceGenerationSnapshot(
        string generationIdentity,
        IReadOnlyList<TraceModulePdbIdentity> modulePdbIdentities,
        IReadOnlyList<PreparedSymbolModuleIdentity> publicModulePdbIdentities)
    {
        GenerationIdentity = generationIdentity;
        ModulePdbIdentities = modulePdbIdentities;
        PublicModulePdbIdentities = publicModulePdbIdentities;
    }

    public string GenerationIdentity { get; }

    public IReadOnlyList<TraceModulePdbIdentity> ModulePdbIdentities { get; }

    internal IReadOnlyList<PreparedSymbolModuleIdentity> PublicModulePdbIdentities { get; }

    internal static SymbolTraceGenerationSnapshot Create(TraceHandleLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var identities = lease.Trace.ModuleFiles
            .Where(HasCompletePdbIdentity)
            .Select(CreateIdentity)
            .DistinctBy(static item => item.Internal.CanonicalIdentity, StringComparer.Ordinal)
            .OrderBy(static item => item.Internal.CanonicalIdentity, StringComparer.Ordinal)
            .ToArray();
        return new SymbolTraceGenerationSnapshot(
            SymbolTraceGenerationIdentity.FromCacheSequence(lease.CacheGenerationSequence),
            identities.Select(static item => item.Internal).ToArray(),
            identities.Select(static item => item.Public).ToArray());
    }

    private static bool HasCompletePdbIdentity(TraceModuleFile module) =>
        !string.IsNullOrWhiteSpace(Path.GetFileName(module.PdbName)) &&
        module.PdbSignature != Guid.Empty &&
        module.PdbAge > 0;

    private static (TraceModulePdbIdentity Internal, PreparedSymbolModuleIdentity Public)
        CreateIdentity(TraceModuleFile module)
    {
        var imageName = Path.GetFileName(module.FilePath);
        if (string.IsNullOrWhiteSpace(imageName))
            imageName = string.IsNullOrWhiteSpace(module.Name) ? "<unknown>" : module.Name;
        var pdbName = Path.GetFileName(module.PdbName)!;
        var binaryFormat = module.BinaryFormat.ToString().ToLowerInvariant();
        var imageIdentity = string.Join(
            ':',
            "trace-module-v1",
            module.ModuleFileIndex.ToString(),
            module.ImageId.ToString("x", CultureInfo.InvariantCulture),
            module.ImageChecksum.ToString("x", CultureInfo.InvariantCulture),
            module.ImageSize.ToString("x", CultureInfo.InvariantCulture));
        var identity = new TraceModulePdbIdentity(
            imageIdentity,
            imageName,
            // TraceEvent's module metadata identifies the binary format but does not
            // provide a trustworthy machine architecture on this API surface.
            "unknown",
            pdbName,
            module.PdbSignature,
            module.PdbAge);
        return (
            identity,
            new PreparedSymbolModuleIdentity(
                imageName,
                binaryFormat,
                pdbName,
                module.PdbSignature.ToString("D"),
                module.PdbAge,
                "trace_pdb_identity"));
    }
}

internal sealed record SymbolToolPreparationResult(
    SymbolContextDescriptor Descriptor,
    SymbolTraceGenerationSnapshot TraceSnapshot);

/// <summary>
/// Public DI seam for symbol lifecycle tools. The principal, generation binding,
/// policy snapshot, and resolver remain internal and cannot be caller supplied.
/// </summary>
public sealed class SymbolToolRuntime
{
    internal const string ContractVersion = "2.0";

    private readonly TraceToolRuntime _traces;
    private readonly StdioSessionPrincipal _sessionPrincipal;
    private readonly SymbolPreparationService _preparation;
    private readonly string _defaultPolicyReference;
    private readonly string _privacyProfile;

    internal SymbolToolRuntime(
        TraceToolRuntime traces,
        StdioSessionPrincipal sessionPrincipal,
        SymbolPreparationService preparation,
        string defaultPolicyReference,
        string privacyProfile = "off")
    {
        _traces = traces ?? throw new ArgumentNullException(nameof(traces));
        _sessionPrincipal = sessionPrincipal ?? throw new ArgumentNullException(nameof(sessionPrincipal));
        _preparation = preparation ?? throw new ArgumentNullException(nameof(preparation));
        _defaultPolicyReference = ApprovedSymbolPolicySnapshot.NormalizePolicyReference(
            defaultPolicyReference);
        _privacyProfile = ToolPrivacyOptions.Parse(privacyProfile, nameof(privacyProfile)).Profile;
    }

    internal async ValueTask<SymbolToolPreparationResult> PrepareAsync(
        string traceId,
        string? symbolPolicyReference,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        // The trace-generation lease spans resolver work and registry publication.
        // Otherwise a concurrent unload could report drained and retire the trace
        // before prepare_symbols publishes a context bound to that generation.
        using var traceLease = _traces.Acquire(traceId, cancellationToken);
        var snapshot = SymbolTraceGenerationSnapshot.Create(traceLease);

        var prepared = await _preparation.PrepareForDeliveryAsync(
            new SymbolPrincipal(_sessionPrincipal.RegistryKey),
            snapshot,
            symbolPolicyReference ?? _defaultPolicyReference,
            _privacyProfile,
            ContractVersion,
            cancellationToken).ConfigureAwait(false);
        try
        {
            if (!SymbolPreparationDeliveryContext.TryRegister(prepared.Disclosure))
                prepared.Disclosure.Commit();
        }
        catch
        {
            await prepared.Disclosure.RollbackAsync().ConfigureAwait(false);
            throw;
        }
        return new SymbolToolPreparationResult(prepared.Descriptor, snapshot);
    }
}

internal sealed class SymbolToolContractException : InvalidOperationException
{
    internal SymbolToolContractException(
        string code,
        string? detailCode,
        string message,
        Exception? innerException = null,
        string? symbolContextId = null)
        : base(message, innerException)
    {
        if (!SymbolToolErrorProjection.KnownPublicCodes.Contains(code))
            throw new InvalidOperationException($"Unreviewed SymbolToolContractException code '{code}'.");
        Code = code;
        DetailCode = detailCode;
        SymbolContextId = symbolContextId;
        ToolFailureCaptureContext.Record(this);
    }

    public string Code { get; }

    public string? DetailCode { get; }

    public string? SymbolContextId { get; }
}
