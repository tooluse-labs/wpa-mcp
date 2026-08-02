namespace WpaMcp.Output;

/// <summary>
/// Safe trace-native CodeView identity. This is metadata evidence only: it never
/// implies that a PDB was found or that a frame name was resolved.
/// </summary>
public sealed record PreparedSymbolModuleIdentity(
    string ImageName,
    string BinaryFormat,
    string ExpectedPdbName,
    string PdbSignature,
    int PdbAge,
    string EvidenceState);

/// <summary>
/// Public projection of an immutable symbol context. Internal generation,
/// revision, artifact path, and content-hash identities are intentionally absent.
/// </summary>
public sealed record PrepareSymbolsResponse(
    [property: WpaMcp.Core.ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
    string TraceId,
    [property: WpaMcp.Core.ToolOpaqueLocator("symbol_context_id", "^sym_[0-9a-f]{32}$")]
    string SymbolContextId,
    string SymbolPolicyRef,
    string ResolverMode,
    string LocalReadinessState,
    string LocalReadinessMeasurementBasis,
    int ModulesWithPdbIdentity,
    int ModulesWithVerifiedSymbolArtifact,
    int VerifiedSymbolArtifactCount,
    string FrameResolutionState,
    long? FramesAttempted,
    long? FramesResolved,
    double? FrameResolutionRate,
    bool NetworkAccessed,
    IReadOnlyList<PreparedSymbolModuleIdentity> ModulePdbIdentities,
    IReadOnlyList<string> EvidenceBoundaries);
