using System.Reflection;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal enum ToolContractMode
{
    Legacy,
    V2,
}

internal enum RuntimeReleaseStage
{
    DevelopmentPre04,
    V04,
    V05,
    V1OrLater,
}

/// <summary>
/// One immutable startup profile. Contract and trace-reference modes are selected
/// before the transport reads stdin and cannot be changed by a tool call.
/// </summary>
internal sealed record RuntimeCompatibilityProfile(
    string RuntimeVersion,
    RuntimeReleaseStage ReleaseStage,
    ToolContractMode ContractMode,
    bool ContractModeExplicit,
    TraceAccessMode TraceReferenceMode,
    bool TraceReferenceModeExplicit,
    bool Runnable,
    bool ReleaseEligible,
    IReadOnlyList<string> RuntimeBlockers,
    IReadOnlyList<string> ReleaseBlockers,
    IReadOnlyList<string> ExternalKnownBlockers,
    IReadOnlyList<string> Warnings)
{
    internal string ReleaseLine => ReleaseStage switch
    {
        RuntimeReleaseStage.DevelopmentPre04 => "development_pre_0_4",
        RuntimeReleaseStage.V04 => "0.4.x",
        RuntimeReleaseStage.V05 => "0.5.x",
        RuntimeReleaseStage.V1OrLater => "1.0_or_later",
        _ => throw new ArgumentOutOfRangeException(nameof(ReleaseStage)),
    };

    internal string ContractModeName => ContractMode switch
    {
        ToolContractMode.Legacy => "legacy",
        ToolContractMode.V2 => ToolContractVersions.V2,
        _ => throw new ArgumentOutOfRangeException(nameof(ContractMode)),
    };

    internal string TraceReferenceModeName => TraceReferenceMode switch
    {
        TraceAccessMode.Compatibility => "compatibility",
        TraceAccessMode.IdOnly => "id_only",
        _ => throw new ArgumentOutOfRangeException(nameof(TraceReferenceMode)),
    };

    internal RuntimeCompatibilityResourceRecord ToResourceRecord() => new(
        SchemaVersion: "runtime-profile.v1",
        RuntimeVersion,
        ReleaseLine,
        ContractMode: ContractModeName,
        ContractModeExplicit,
        TraceReferenceMode: TraceReferenceModeName,
        TraceReferenceModeExplicit,
        OutputSchemaDialect: RuntimeCompatibilityPolicy.OutputSchemaDialect,
        OutputSchemaReferenceProfile: RuntimeCompatibilityPolicy.OutputSchemaReferenceProfile,
        OutputSchemaReferenceRequirement: RuntimeCompatibilityPolicy.OutputSchemaReferenceRequirement,
        OutputSchemaExternalReferencePolicy: RuntimeCompatibilityPolicy.OutputSchemaExternalReferencePolicy,
        SelectionScope: "startup_immutable",
        Runnable,
        ReleaseStatus: ReleaseEligible ? "eligible" : "blocked",
        RuntimeBlockers,
        ReleaseBlockers,
        ExternalKnownBlockers,
        Warnings,
        LegacyContractStatus: RuntimeCompatibilityPolicy.LegacyContractImplementationStatus,
        LegacyContractRemovalRelease: RuntimeCompatibilityPolicy.LegacyContractRemovalRelease,
        RawPathCompatibilityRemovalRelease: TraceRuntimeOptions.CompatibilityRemovalRelease,
        V1DeprecationGateStatus: RuntimeCompatibilityPolicy.V1DeprecationGateStatus);
}

internal static class RuntimeCompatibilityPolicy
{
    internal const string ContractModeEnvironmentVariable = "WPAMCP_CONTRACT_MODE";
    internal const string LegacyContractImplementationStatus =
        "unsupported:no_released_legacy_result_contract_exists;contract_2.0_is_the_only_runtime_shape";
    internal const string LegacyContractRemovalRelease = "not_applicable";
    internal const string V1DeprecationGateStatus =
        "release_blocked:no_reviewed_full_0.5.x_window_or_usage_telemetry_evidence";
    internal const string ArtifactTransientPeakStatus =
        "release_blocked:retained_quota_only;single_materialization_checkpoint_budget;opaque_converter_transient_peak_unproven";
    internal const string OutputSchemaDialect =
        "https://json-schema.org/draft/2020-12/schema";
    internal const string OutputSchemaReferenceProfile =
        "root_local_defs_safe_id";
    internal const string OutputSchemaReferenceRequirement =
        "on_demand_content_addressed_resource_or_get_tool_contract";
    internal const string OutputSchemaExternalReferencePolicy =
        "forbidden";

    // This becomes true only in the same reviewed change that records one complete
    // 0.5.x deprecation window and its usage-telemetry decision. An environment
    // variable cannot waive a release-history requirement.
    private const bool V1DeprecationGateApproved = false;
    private const bool LegacyContractImplemented = false;

    internal static RuntimeCompatibilityProfile EvaluateCurrent(
        ToolContractMode? contractOverride = null,
        bool contractModeExplicit = false,
        TraceAccessMode? traceReferenceOverride = null,
        bool traceReferenceModeExplicit = false) =>
        Evaluate(
            CurrentRuntimeVersion(),
            contractOverride,
            contractModeExplicit,
            traceReferenceOverride,
            traceReferenceModeExplicit);

    internal static RuntimeCompatibilityProfile Evaluate(
        string runtimeVersion,
        ToolContractMode? contractOverride = null,
        bool contractModeExplicit = false,
        TraceAccessMode? traceReferenceOverride = null,
        bool traceReferenceModeExplicit = false)
    {
        var version = ParseVersion(runtimeVersion);
        var stage = SelectStage(version);
        var defaults = Defaults(stage);
        var contract = contractOverride ?? defaults.Contract;
        var traceReference = traceReferenceOverride ?? defaults.TraceReference;
        var runtimeBlockers = new List<string>();
        var releaseBlockers = new List<string>();
        var externalKnownBlockers = new List<string> { ArtifactTransientPeakStatus };
        var warnings = new List<string>();

        if (stage == RuntimeReleaseStage.V1OrLater)
        {
            if (contract != ToolContractMode.V2)
                runtimeBlockers.Add("contract_mode_removed:legacy_is_not_allowed_at_1.0_or_later");
            if (traceReference != TraceAccessMode.IdOnly)
                runtimeBlockers.Add("trace_reference_mode_removed:raw_path_compatibility_is_not_allowed_at_1.0_or_later");
        }

        if (contract == ToolContractMode.Legacy && !LegacyContractImplemented)
        {
            runtimeBlockers.Add(LegacyContractImplementationStatus);
        }

        if (stage == RuntimeReleaseStage.DevelopmentPre04)
        {
            releaseBlockers.Add("release_line_not_defined_by_adr_0005:version_is_before_0.4.0");
        }
        else if (stage == RuntimeReleaseStage.V1OrLater && !V1DeprecationGateApproved)
        {
            releaseBlockers.Add(V1DeprecationGateStatus);
        }

        releaseBlockers.AddRange(runtimeBlockers.Select(blocker => "runtime_profile_unavailable:" + blocker));
        releaseBlockers.AddRange(externalKnownBlockers);

        if (traceReference == TraceAccessMode.Compatibility)
        {
            warnings.Add(
                "raw_trace_path_deprecated:migrate_to_load_trace_and_trace_id;removed_in_1.0.0");
        }
        if (stage == RuntimeReleaseStage.DevelopmentPre04)
        {
            warnings.Add(
                "development_release_profile:ADR_0005_defines_publishable_windows_starting_at_0.4.x");
        }

        return new RuntimeCompatibilityProfile(
            NormalizeVersion(runtimeVersion),
            stage,
            contract,
            contractModeExplicit,
            traceReference,
            traceReferenceModeExplicit,
            RuntimeBlockers: runtimeBlockers.ToArray(),
            Runnable: runtimeBlockers.Count == 0,
            ReleaseEligible: releaseBlockers.Count == 0,
            ReleaseBlockers: releaseBlockers.ToArray(),
            ExternalKnownBlockers: externalKnownBlockers.ToArray(),
            Warnings: warnings.ToArray());
    }

    internal static void RequireRunnable(RuntimeCompatibilityProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.Runnable)
        {
            throw new ArgumentException(
                "The selected startup compatibility profile is unavailable: " +
                string.Join("; ", profile.RuntimeBlockers));
        }
    }

    internal static ToolContractMode ParseContractMode(string raw, string source) =>
        raw switch
        {
            "legacy" => ToolContractMode.Legacy,
            ToolContractVersions.V2 => ToolContractMode.V2,
            _ => throw new ArgumentException(
                $"{source} must be 'legacy' or '{ToolContractVersions.V2}'."),
        };

    internal static string CurrentRuntimeVersion()
    {
        var assembly = typeof(RuntimeCompatibilityPolicy).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? throw new InvalidOperationException("The runtime assembly has no version identity.");
    }

    private static (ToolContractMode Contract, TraceAccessMode TraceReference) Defaults(
        RuntimeReleaseStage stage) => stage switch
    {
        RuntimeReleaseStage.DevelopmentPre04 => (ToolContractMode.V2, TraceAccessMode.IdOnly),
        RuntimeReleaseStage.V04 => (ToolContractMode.V2, TraceAccessMode.IdOnly),
        RuntimeReleaseStage.V05 => (ToolContractMode.V2, TraceAccessMode.IdOnly),
        RuntimeReleaseStage.V1OrLater => (ToolContractMode.V2, TraceAccessMode.IdOnly),
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    private static RuntimeReleaseStage SelectStage(Version version)
    {
        if (version.Major == 1)
            return RuntimeReleaseStage.V1OrLater;
        if (version.Major > 1 || version.Minor > 5)
        {
            throw new ArgumentException(
                $"Runtime release line '{version.Major}.{version.Minor}.x' is not defined by ADR 0005.",
                nameof(version));
        }
        if (version.Minor >= 5)
            return RuntimeReleaseStage.V05;
        if (version.Minor == 4)
            return RuntimeReleaseStage.V04;
        return RuntimeReleaseStage.DevelopmentPre04;
    }

    private static Version ParseVersion(string raw)
    {
        var normalized = NormalizeVersion(raw);
        var separator = normalized.IndexOfAny(['-', '+']);
        var numeric = separator < 0 ? normalized : normalized[..separator];
        if (!Version.TryParse(numeric, out var version) ||
            version.Major < 0 || version.Minor < 0 || version.Build < 0)
        {
            throw new ArgumentException(
                $"Runtime version '{raw}' must contain major, minor, and patch numbers.",
                nameof(raw));
        }
        return version;
    }

    private static string NormalizeVersion(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new ArgumentException("Runtime version is required.", nameof(raw));
        return raw.Trim();
    }
}
