using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class SymbolLifecycleTools(SymbolToolRuntime runtime)
{
    private readonly SymbolToolRuntime _runtime = runtime;

    [McpServerTool(
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = true,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Prepares or reuses a canonical immutable SymbolContextId for one already-loaded TraceId under a startup-approved named policy. " +
        "This is the only operation allowed to probe approved local symbol roots or populate the private verified-symbol store. " +
        "The secure default policy is local-only and performs no network access. The response separates trace PDB identity, verified local readiness, and actual frame resolution; preparation never claims that frame names were resolved. " +
        "Equivalent trace generation, policy, resolver, privacy, contract, module, and verified-artifact inputs return the same active SymbolContextId. " +
        "Later stack queries must explicitly supply this ID with resolveSymbols=true; no query falls back to _NT_SYMBOL_PATH, the trace directory, arbitrary disk search, or a symbol server. " +
        "No startUs/endUs: preparation applies to immutable whole-trace module identities, not a trace-event window.")]
    public async Task<PrepareSymbolsResponse> PrepareSymbols(
        [Description("Canonical TraceId returned by load_trace; raw paths are never accepted.")]
        string traceId,
        [Description("Startup-approved named symbol policy. Omit to use the configured local-only default; roots and origins cannot be supplied inline.")]
        string? symbolPolicyRef = null,
        [Description("Maximum trace-native module/PDB identity rows to return (default 100, max 1000). Context preparation always evaluates the complete identity set; this limit affects projection only.")]
        int top = 100,
        CancellationToken cancellationToken = default)
    {
        try
        {
            Validation.RequireTop(top);
            var prepared = await _runtime.PrepareAsync(
                traceId,
                symbolPolicyRef,
                cancellationToken).ConfigureAwait(false);
            var descriptor = prepared.Descriptor;
            var evidence = descriptor.Evidence;
            return new PrepareSymbolsResponse(
                traceId,
                descriptor.SymbolContextId,
                descriptor.SymbolPolicyReference,
                descriptor.ResolverVersion,
                evidence.LocalReadinessState,
                evidence.LocalReadinessMeasurementBasis,
                evidence.ModulesWithPdbIdentity,
                evidence.ModulesWithVerifiedSymbolArtifact,
                evidence.VerifiedSymbolArtifactCount,
                evidence.FrameResolutionState,
                evidence.FramesAttempted,
                evidence.FramesResolved,
                evidence.FrameResolutionRate,
                NetworkAccessed: false,
                prepared.TraceSnapshot.PublicModulePdbIdentities.Take(top).ToArray(),
                [
                    "pdb_identity_is_trace_metadata_not_symbol_resolution",
                    "verified_artifact_readiness_is_not_frame_resolution",
                    "frame_resolution_unmeasured_during_preparation",
                ]);
        }
        catch (SymbolContextException exception)
        {
            var error = SymbolContextPublicErrorProjection.Project(exception);
            throw new SymbolToolContractException(
                error.Code,
                error.DetailCode,
                error.Message,
                exception);
        }
    }
}
