namespace WpaMcp.Core;

internal sealed record SymbolRuntimeOptions(
    string DefaultPolicyReference,
    IReadOnlyList<string> ApprovedLocalRoots,
    string? StoreRoot,
    long MaxArtifactBytes,
    long MaxStoreBytes)
{
    internal const string LocalRootsEnvironmentVariable = "WPAMCP_SYMBOL_LOCAL_ROOTS";
    internal const string StoreRootEnvironmentVariable = "WPAMCP_SYMBOL_STORE_ROOT";
    internal const long DefaultMaxArtifactBytes = 2L * 1024 * 1024 * 1024;
    internal const long DefaultMaxStoreBytes = 16L * 1024 * 1024 * 1024;

    internal static SymbolRuntimeOptions Defaults(
        Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        var rawRoots = getEnvironmentVariable(LocalRootsEnvironmentVariable);
        var roots = string.IsNullOrWhiteSpace(rawRoots)
            ? []
            : rawRoots.Split(
                    Path.PathSeparator,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        var storeRoot = getEnvironmentVariable(StoreRootEnvironmentVariable);
        return new SymbolRuntimeOptions(
            "local",
            roots,
            string.IsNullOrWhiteSpace(storeRoot) ? null : storeRoot,
            DefaultMaxArtifactBytes,
            DefaultMaxStoreBytes);
    }

    internal SymbolRuntimeOptions ValidatePure()
    {
        if (!string.Equals(DefaultPolicyReference, "local", StringComparison.Ordinal))
            throw new ArgumentException("The secure-default symbol policy reference must be 'local'.");
        foreach (var root in ApprovedLocalRoots)
            ValidateLocalAbsolutePath(root, "approved local symbol root");
        if (StoreRoot is not null)
            ValidateLocalAbsolutePath(StoreRoot, "private symbol store root");
        if (ApprovedLocalRoots.Count != 0 && StoreRoot is null)
        {
            throw new ArgumentException(
                $"A private symbol store is required when local roots are enabled. Configure --symbol-store-root or {StoreRootEnvironmentVariable}.");
        }
        if (MaxArtifactBytes <= 0 || MaxArtifactBytes > DefaultMaxArtifactBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxArtifactBytes),
                $"Maximum symbol artifact bytes must be from 1 through {DefaultMaxArtifactBytes}.");
        }
        if (MaxStoreBytes < MaxArtifactBytes || MaxStoreBytes > DefaultMaxStoreBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxStoreBytes),
                $"Maximum symbol store bytes must be at least MaxArtifactBytes and no greater than {DefaultMaxStoreBytes}.");
        }
        var normalizedRoots = ApprovedLocalRoots
            .Select(static root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static root => root, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var normalizedStoreRoot = StoreRoot is null
            ? null
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(StoreRoot));
        if (normalizedStoreRoot is not null && normalizedRoots.Any(root =>
                IsSameOrDescendant(normalizedStoreRoot, root) ||
                IsSameOrDescendant(root, normalizedStoreRoot)))
        {
            throw new ArgumentException(
                "The private symbol store and approved candidate roots must be disjoint; neither may contain the other.");
        }

        return this with
        {
            ApprovedLocalRoots = normalizedRoots,
            StoreRoot = normalizedStoreRoot,
        };
    }

    internal ApprovedSymbolPolicySnapshot CreatePolicySnapshot() => new(
        policyReference: DefaultPolicyReference,
        policyRevision: "startup-local-policy-v1",
        approvedLocalRoots: ApprovedLocalRoots,
        networkPolicy: SymbolNetworkPolicy.Denied,
        approvedOrigins: [],
        cacheProfile: StoreRoot is null
            ? "disabled"
            : "private-content-addressed-v1");

    private static bool IsSameOrDescendant(string candidate, string ancestor) =>
        string.Equals(candidate, ancestor, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(
            ancestor + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateLocalAbsolutePath(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new ArgumentException($"The {label} must be an absolute local path.");
        if (value.StartsWith("\\\\", StringComparison.Ordinal) ||
            value.StartsWith("\\??\\", StringComparison.Ordinal) ||
            value.StartsWith("\\?\\", StringComparison.Ordinal) ||
            value.StartsWith("\\.\\", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() &&
             value.AsSpan(Math.Min(2, value.Length)).Contains(':')))
        {
            throw new ArgumentException(
                $"The {label} must not use UNC, device, or alternate-stream syntax.");
        }
    }
}

/// <summary>
/// Default when no approved roots/store are configured. It guarantees that an
/// honest not_ready context can still be prepared without touching the filesystem.
/// </summary>
internal sealed class DisabledVerifiedSymbolArtifactStore : IVerifiedSymbolArtifactStore
{
    public ValueTask<IVerifiedSymbolArtifactPin?> TryVerifyAndPinLocalAsync(
        ApprovedLocalSymbolCandidate candidate,
        TraceModulePdbIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IVerifiedSymbolArtifactPin?>(null);
    }
}
