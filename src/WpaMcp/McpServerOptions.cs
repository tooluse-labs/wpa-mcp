using WpaMcp.Core;

namespace WpaMcp;

internal sealed record TraceRuntimeOptions(
    TraceAccessMode AccessMode,
    IReadOnlyList<string> AllowedRoots,
    string ArtifactRoot,
    long MaxInputTraceBytes,
    long MaxArtifactStoreBytes,
    int MaxArtifactObjects,
    TimeSpan ArtifactRetentionTtl)
{
    internal const string AccessModeEnvironmentVariable = "WPAMCP_TRACE_REFERENCE_MODE";
    internal const string AllowedRootsEnvironmentVariable = "WPAMCP_TRACE_ROOTS";
    internal const string ArtifactRootEnvironmentVariable = "WPAMCP_TRACE_ARTIFACT_ROOT";
    internal const string ArtifactRetentionMinutesEnvironmentVariable =
        "WPAMCP_TRACE_ARTIFACT_RETENTION_MINUTES";
    internal const string CompatibilityRemovalRelease = "0.6.0";
    internal const long DefaultMaxInputTraceBytes = 64L * 1024 * 1024 * 1024;
    internal const long DefaultMaxArtifactStoreBytes = 64L * 1024 * 1024 * 1024;
    internal const long HardMaxArtifactStoreBytes = 256L * 1024 * 1024 * 1024;
    internal const int DefaultMaxArtifactObjects = 128;
    internal static readonly TimeSpan DefaultArtifactRetentionTtl = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MinimumArtifactRetentionTtl = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MaximumArtifactRetentionTtl = TimeSpan.FromDays(365);

    internal static TraceRuntimeOptions Defaults(
        TraceAccessMode accessMode,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        getEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var roots = ParseEnvironmentRoots(
            getEnvironmentVariable(AllowedRootsEnvironmentVariable));
        if (roots.Count == 0)
        {
            var documents = Environment.GetFolderPath(
                Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrWhiteSpace(documents))
            {
                throw new ArgumentException(
                    $"No default trace root is available. Configure --trace-root or {AllowedRootsEnvironmentVariable}.");
            }
            roots.Add(documents);
        }

        var artifactRoot = getEnvironmentVariable(ArtifactRootEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(artifactRoot))
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new ArgumentException(
                    $"No default artifact root is available. Configure --trace-artifact-root or {ArtifactRootEnvironmentVariable}.");
            }
            artifactRoot = Path.Combine(localAppData, "WpaMcp", "trace-artifacts");
        }

        var artifactRetentionTtl = ParseArtifactRetentionMinutes(
            getEnvironmentVariable(ArtifactRetentionMinutesEnvironmentVariable),
            ArtifactRetentionMinutesEnvironmentVariable,
            DefaultArtifactRetentionTtl);

        return new TraceRuntimeOptions(
            accessMode,
            roots,
            artifactRoot,
            DefaultMaxInputTraceBytes,
            DefaultMaxArtifactStoreBytes,
            DefaultMaxArtifactObjects,
            artifactRetentionTtl);
    }

    internal TraceRuntimeOptions ValidatePure()
    {
        if (AllowedRoots.Count == 0)
            throw new ArgumentException("At least one local trace root is required.");
        foreach (var root in AllowedRoots)
            ValidateLocalAbsolutePath(root, "trace root");
        ValidateLocalAbsolutePath(ArtifactRoot, "trace artifact root");
        if (MaxInputTraceBytes <= 0 || MaxInputTraceBytes > DefaultMaxInputTraceBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxInputTraceBytes),
                $"Maximum input trace bytes must be from 1 through {DefaultMaxInputTraceBytes}.");
        }
        if (MaxArtifactStoreBytes <= 0 ||
            MaxArtifactStoreBytes > HardMaxArtifactStoreBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxArtifactStoreBytes),
                $"Artifact-store bytes must be from 1 through {HardMaxArtifactStoreBytes}.");
        }
        if (MaxArtifactObjects <= 0 || MaxArtifactObjects > 4096)
            throw new ArgumentOutOfRangeException(nameof(MaxArtifactObjects));
        if (ArtifactRetentionTtl < MinimumArtifactRetentionTtl ||
            ArtifactRetentionTtl > MaximumArtifactRetentionTtl)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ArtifactRetentionTtl),
                $"Artifact retention TTL must be from {MinimumArtifactRetentionTtl.TotalMinutes:0} " +
                $"through {MaximumArtifactRetentionTtl.TotalMinutes:0} minutes.");
        }

        var artifact = Path.GetFullPath(ArtifactRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var configuredRoot in AllowedRoots)
        {
            var root = Path.GetFullPath(configuredRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (artifact.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                artifact.StartsWith(
                    root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "The artifact root must be outside every allowed source root.");
            }
        }
        return this;
    }

    internal static TraceAccessMode ParseAccessMode(string raw, string source) =>
        raw.ToLowerInvariant() switch
        {
            "id_only" or "id-only" => TraceAccessMode.IdOnly,
            _ => throw new ArgumentException(
                $"{source} must be 'id_only'; raw-path query compatibility was removed."),
        };

    private static List<string> ParseEnvironmentRoots(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries).ToList();

    private static TimeSpan ParseArtifactRetentionMinutes(
        string? raw,
        string source,
        TimeSpan fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;
        if (!long.TryParse(
                raw,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var minutes) ||
            minutes <= 0)
        {
            throw new ArgumentException($"{source} must be a positive integer number of minutes.");
        }
        try
        {
            return TimeSpan.FromMinutes(minutes);
        }
        catch (OverflowException exception)
        {
            throw new ArgumentException(
                $"{source} exceeds the supported retention interval.",
                exception);
        }
    }

    private static void ValidateLocalAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"The {label} must be an absolute local path.");
        if (value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            value.StartsWith("\\??\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"The {label} must not use a UNC or device namespace.");
        }
    }
}

internal sealed record McpServerOptions(
    string[] HostArgs,
    int? CacheSize,
    TraceRuntimeOptions TraceRuntime,
    RuntimeCompatibilityProfile CompatibilityProfile,
    SymbolRuntimeOptions SymbolRuntime,
    ToolPrivacyOptions Privacy,
    ToolExecutionBudgetOptions ExecutionBudgets,
    CapabilityPolicyProfile CapabilityPolicy)
{
    public static McpServerOptions Parse(string[] args) =>
        Parse(args, Environment.GetEnvironmentVariable);

    internal static McpServerOptions Parse(
        string[] args,
        Func<string, string?> getEnvironmentVariable,
        string? runtimeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var hostArgs = new List<string>();
        int? cacheSize = null;
        var selectedContractRaw = getEnvironmentVariable(
            RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable);
        var contractModeSource = RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable;
        var contractModeExplicit = !string.IsNullOrWhiteSpace(selectedContractRaw);
        var selectedTraceReferenceRaw = getEnvironmentVariable(
            TraceRuntimeOptions.AccessModeEnvironmentVariable);
        var traceReferenceModeSource = TraceRuntimeOptions.AccessModeEnvironmentVariable;
        var traceReferenceModeExplicit = !string.IsNullOrWhiteSpace(selectedTraceReferenceRaw);
        var selectedDisabledCapabilitiesRaw = getEnvironmentVariable(
            CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable);
        var capabilityPolicySource =
            CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable;
        var runtime = TraceRuntimeOptions.Defaults(
            TraceAccessMode.IdOnly,
            getEnvironmentVariable);
        var symbolRuntime = SymbolRuntimeOptions.Defaults(getEnvironmentVariable);
        var privacy = ToolPrivacyOptions.Parse(
            getEnvironmentVariable(ToolPrivacyOptions.EnvironmentVariable),
            ToolPrivacyOptions.EnvironmentVariable);
        var executionBudgets = ToolExecutionBudgetOptions.FromEnvironment(getEnvironmentVariable);
        var configuredRoots = new List<string>();
        var configuredSymbolRoots = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--symbol-path":
                    throw new ArgumentException(
                        "--symbol-path is unavailable in secure-default. Configure an approved --symbol-local-root plus --symbol-store-root, then call prepare_symbols.");

                case "--symbol-local-root":
                    configuredSymbolRoots.Add(
                        RequireValue(args, ref i, "--symbol-local-root"));
                    break;

                case "--symbol-store-root":
                    symbolRuntime = symbolRuntime with
                    {
                        StoreRoot = RequireValue(args, ref i, "--symbol-store-root"),
                    };
                    break;

                case "--max-symbol-artifact-bytes":
                    var rawMaxSymbolArtifactBytes = RequireValue(
                        args,
                        ref i,
                        "--max-symbol-artifact-bytes");
                    if (!long.TryParse(
                            rawMaxSymbolArtifactBytes,
                            out var maxSymbolArtifactBytes))
                    {
                        throw new ArgumentException(
                            "--max-symbol-artifact-bytes must be a positive integer.");
                    }
                    symbolRuntime = symbolRuntime with
                    {
                        MaxArtifactBytes = maxSymbolArtifactBytes,
                    };
                    break;

                case "--max-symbol-store-bytes":
                    var rawMaxSymbolStoreBytes = RequireValue(
                        args,
                        ref i,
                        "--max-symbol-store-bytes");
                    if (!long.TryParse(rawMaxSymbolStoreBytes, out var maxSymbolStoreBytes))
                    {
                        throw new ArgumentException(
                            "--max-symbol-store-bytes must be a positive integer.");
                    }
                    symbolRuntime = symbolRuntime with
                    {
                        MaxStoreBytes = maxSymbolStoreBytes,
                    };
                    break;

                case "--cache-size":
                    var rawCacheSize = RequireValue(args, ref i, "--cache-size");
                    if (!int.TryParse(rawCacheSize, out var parsedCacheSize) || parsedCacheSize <= 0)
                        throw new ArgumentException("--cache-size must be a positive integer.");
                    cacheSize = parsedCacheSize;
                    break;

                case "--privacy-profile":
                    privacy = ToolPrivacyOptions.Parse(
                        RequireValue(args, ref i, "--privacy-profile"),
                        "--privacy-profile");
                    break;

                case "--contract-mode":
                    selectedContractRaw = RequireValue(args, ref i, "--contract-mode");
                    contractModeSource = "--contract-mode";
                    contractModeExplicit = true;
                    break;

                case "--trace-reference-mode":
                    selectedTraceReferenceRaw = RequireValue(
                        args,
                        ref i,
                        "--trace-reference-mode");
                    traceReferenceModeSource = "--trace-reference-mode";
                    traceReferenceModeExplicit = true;
                    break;

                case CapabilityPolicyProfile.DisableCapabilitiesOption:
                    selectedDisabledCapabilitiesRaw = RequireValue(
                        args,
                        ref i,
                        CapabilityPolicyProfile.DisableCapabilitiesOption);
                    capabilityPolicySource = CapabilityPolicyProfile.DisableCapabilitiesOption;
                    break;

                case "--trace-root":
                    configuredRoots.Add(RequireValue(args, ref i, "--trace-root"));
                    break;

                case "--trace-artifact-root":
                    runtime = runtime with
                    {
                        ArtifactRoot = RequireValue(args, ref i, "--trace-artifact-root"),
                    };
                    break;

                case "--max-input-trace-bytes":
                    var rawMaxBytes = RequireValue(args, ref i, "--max-input-trace-bytes");
                    if (!long.TryParse(rawMaxBytes, out var maxBytes))
                    {
                        throw new ArgumentException(
                            "--max-input-trace-bytes must be a positive integer.");
                    }
                    runtime = runtime with { MaxInputTraceBytes = maxBytes };
                    break;

                case "--trace-artifact-store-bytes":
                    var rawArtifactBytes = RequireValue(
                        args,
                        ref i,
                        "--trace-artifact-store-bytes");
                    if (!long.TryParse(rawArtifactBytes, out var artifactBytes))
                    {
                        throw new ArgumentException(
                            "--trace-artifact-store-bytes must be a positive integer.");
                    }
                    runtime = runtime with { MaxArtifactStoreBytes = artifactBytes };
                    break;

                case "--trace-artifact-max-objects":
                    var rawArtifactObjects = RequireValue(
                        args,
                        ref i,
                        "--trace-artifact-max-objects");
                    if (!int.TryParse(rawArtifactObjects, out var artifactObjects))
                    {
                        throw new ArgumentException(
                            "--trace-artifact-max-objects must be a positive integer.");
                    }
                    runtime = runtime with { MaxArtifactObjects = artifactObjects };
                    break;

                case "--trace-artifact-retention-minutes":
                    var rawArtifactRetentionMinutes = RequireValue(
                        args,
                        ref i,
                        "--trace-artifact-retention-minutes");
                    if (!long.TryParse(
                            rawArtifactRetentionMinutes,
                            System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var artifactRetentionMinutes) ||
                        artifactRetentionMinutes <= 0)
                    {
                        throw new ArgumentException(
                            "--trace-artifact-retention-minutes must be a positive integer.");
                    }
                    try
                    {
                        runtime = runtime with
                        {
                            ArtifactRetentionTtl = TimeSpan.FromMinutes(
                                artifactRetentionMinutes),
                        };
                    }
                    catch (OverflowException exception)
                    {
                        throw new ArgumentException(
                            "--trace-artifact-retention-minutes exceeds the supported interval.",
                            exception);
                    }
                    break;

                default:
                    hostArgs.Add(args[i]);
                    break;
            }
        }

        if (configuredRoots.Count != 0)
            runtime = runtime with { AllowedRoots = configuredRoots };
        if (configuredSymbolRoots.Count != 0)
        {
            symbolRuntime = symbolRuntime with
            {
                ApprovedLocalRoots = configuredSymbolRoots,
            };
        }

        ToolContractMode? selectedContract = string.IsNullOrWhiteSpace(selectedContractRaw)
            ? null
            : RuntimeCompatibilityPolicy.ParseContractMode(
                selectedContractRaw,
                contractModeSource);
        TraceAccessMode? selectedTraceReference = string.IsNullOrWhiteSpace(selectedTraceReferenceRaw)
            ? null
            : TraceRuntimeOptions.ParseAccessMode(
                selectedTraceReferenceRaw,
                traceReferenceModeSource);
        var compatibilityProfile = RuntimeCompatibilityPolicy.Evaluate(
            runtimeVersion ?? RuntimeCompatibilityPolicy.CurrentRuntimeVersion(),
            selectedContract,
            contractModeExplicit,
            selectedTraceReference,
            traceReferenceModeExplicit);
        RuntimeCompatibilityPolicy.RequireRunnable(compatibilityProfile);
        runtime = runtime with
        {
            AccessMode = compatibilityProfile.TraceReferenceMode,
        };
        var capabilityPolicy = CapabilityPolicyProfile.Parse(
            selectedDisabledCapabilitiesRaw,
            capabilityPolicySource);

        return new(
            hostArgs.ToArray(),
            cacheSize,
            runtime.ValidatePure(),
            compatibilityProfile,
            symbolRuntime.ValidatePure(),
            privacy,
            executionBudgets,
            capabilityPolicy);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
            throw new ArgumentException($"{option} requires a value.");

        index++;
        return args[index];
    }
}
