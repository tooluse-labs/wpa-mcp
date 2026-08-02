using System.Text.Json;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Tests.ContractBaselines;

namespace WpaMcp.Tests;

public sealed class SideEffectInventoryTests
{
    private static readonly string[] AllowedPathAccessStates =
    [
        "none",
        "loaded_owned_trace_artifact",
        "loaded_owned_trace_and_verified_symbol_artifacts",
        "caller_trace_and_owned_artifacts",
        "caller_trace_owned_and_verified_symbol_artifacts",
        "approved_symbol_roots_and_owned_store",
        "configured_telemetry_and_server_owned_roots",
    ];

    private static readonly string[] AllowedDiskWriteStates =
    [
        "none",
        "conditional_owned_artifact_materialization",
        "conditional_verified_symbol_copy",
        "conditional_startup_and_write_when_enabled",
    ];

    private static readonly string[] AllowedNetworkStates =
    [
        "none",
        "approved_symbol_origins_declared_currently_unimplemented",
        "conditional_configured_sink",
    ];

    private static readonly string[] AllowedProcessStateStates =
    [
        "bounded_discovery_cursor_registry_mutation",
        "trace_lease_and_analysis_cache_mutation",
        "trace_handle_artifact_and_analysis_cache_mutation",
        "trace_handle_and_artifact_registry_mutation",
        "trace_handle_retirement",
        "symbol_context_registry_and_store_mutation",
        "host_call_tracking_and_registry_lifecycle",
    ];

    private static readonly string[] AllowedExternalStorageStates =
    [
        "none",
        "approved_local_source_only",
        "approved_symbol_origins_declared_currently_unimplemented",
        "conditional_configured_sink",
    ];

    private static readonly string[] AllowedMeasurementStates =
    [
        "static_reviewed",
        "static_reviewed_dependency_behavior",
        "release_blocked_physical_peak_unproven",
        "not_runtime_measured",
    ];

    [Fact]
    public void Inventory_CoversEveryActiveToolAndBothTraceReferenceProfilesExactlyOnce()
    {
        var (inventory, repoRoot) = LoadInventory();
        var activeCatalog = ActiveToolCatalog.LoadAndValidate();
        var activeTools = activeCatalog.Tools
            .OrderBy(tool => tool.ToolName, StringComparer.Ordinal)
            .ToArray();
        var inventoryTools = inventory.Tools
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal("side-effect-inventory.v1", inventory.FormatVersion);
        AssertRepoFileExists(repoRoot, inventory.Observation.CatalogSource, "active catalog source");
        Assert.Equal(
            "tests/WpaMcp.Tests/ContractBaselines/active-tools.v1.json",
            inventory.Observation.ActiveCatalogSnapshotTarget);
        Assert.Equal(activeTools.Length, inventory.Observation.ToolCount);
        Assert.Equal(activeTools.Length, inventoryTools.Length);
        Assert.Equal(
            inventoryTools.Length,
            inventoryTools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(activeTools.Select(tool => tool.ToolName), inventoryTools.Select(tool => tool.Name));
        Assert.Equal(new[] { "compatibility", "id_only" },
            inventory.Observation.Profiles.OrderBy(profile => profile, StringComparer.Ordinal));

        var activeByName = activeTools.ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);
        foreach (var entry in inventoryTools)
        {
            var active = activeByName[entry.Name];
            var sideEffectClass = Assert.Single(active.SideEffects);
            Assert.Equal(sideEffectClass, entry.ManifestSideEffectClass);
            Assert.Equal(IdOnlyFamily(sideEffectClass), entry.IdOnlyFamily);
            Assert.Equal(CompatibilityFamily(sideEffectClass), entry.CompatibilityFamily);
        }
    }

    [Fact]
    public void Inventory_UsesClosedStatesAndResolvableEvidence()
    {
        var (inventory, repoRoot) = LoadInventory();

        AssertClosedEnum(inventory, "pathAccess", AllowedPathAccessStates);
        AssertClosedEnum(inventory, "diskWrite", AllowedDiskWriteStates);
        AssertClosedEnum(inventory, "network", AllowedNetworkStates);
        AssertClosedEnum(inventory, "processState", AllowedProcessStateStates);
        AssertClosedEnum(inventory, "externalStorage", AllowedExternalStorageStates);
        AssertClosedEnum(inventory, "measurementState", AllowedMeasurementStates);

        AssertBoundary(
            inventory.HostWrapperEffects.Id,
            inventory.HostWrapperEffects.Effects,
            inventory.HostWrapperEffects.Triggers,
            inventory.HostWrapperEffects.EvidenceSources,
            repoRoot);

        var families = inventory.Families.ToDictionary(family => family.Id, StringComparer.Ordinal);
        Assert.Equal(inventory.Families.Count, families.Count);
        Assert.Equal(
            families.Keys.OrderBy(name => name, StringComparer.Ordinal),
            inventory.Tools.SelectMany(tool => new[] { tool.IdOnlyFamily, tool.CompatibilityFamily })
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal));
        foreach (var family in inventory.Families)
        {
            AssertBoundary(
                family.Id,
                family.Effects,
                family.Triggers,
                family.EvidenceSources,
                repoRoot);
        }

        Assert.Contains("tool request domain", inventory.AnnotationBoundary.Included, StringComparison.Ordinal);
        Assert.Contains("host observability", inventory.AnnotationBoundary.Excluded, StringComparison.Ordinal);
        Assert.Contains("hostWrapperEffects", inventory.AnnotationBoundary.Reason, StringComparison.Ordinal);
        Assert.Contains("ID-only", inventory.AnnotationBoundary.TraceReferenceRule, StringComparison.Ordinal);
        Assert.Contains("compatibility", inventory.AnnotationBoundary.TraceReferenceRule, StringComparison.Ordinal);
        Assert.Contains("SymbolContextId", inventory.AnnotationBoundary.SymbolRule, StringComparison.Ordinal);
        Assert.Contains("physical peak", inventory.AnnotationBoundary.ArtifactQuotaRule, StringComparison.Ordinal);
    }

    [Fact]
    public void Inventory_ProfileAnnotationsExactlyMatchProductionCatalogProjection()
    {
        var (inventory, _) = LoadInventory();
        var baseCatalog = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var serverTools = baseCatalog.CreateServerTools(services);
        var compatibilityCatalog = baseCatalog.ProjectTraceReferenceProfile(
            TraceAccessMode.Compatibility,
            serverTools);
        var idOnly = baseCatalog.Tools.ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);
        var compatibility = compatibilityCatalog.Tools.ToDictionary(
            tool => tool.ToolName,
            StringComparer.Ordinal);
        var families = inventory.Families.ToDictionary(family => family.Id, StringComparer.Ordinal);

        foreach (var entry in inventory.Tools)
        {
            AssertAnnotations(
                idOnly[entry.Name].Annotations,
                families[entry.IdOnlyFamily].ExpectedAnnotations,
                $"{entry.Name}/id_only");
            AssertAnnotations(
                compatibility[entry.Name].Annotations,
                families[entry.CompatibilityFamily].ExpectedAnnotations,
                $"{entry.Name}/compatibility");
        }
    }

    [Fact]
    public void Inventory_DoesNotConfuseRetainedQuotaWithPhysicalPeakCap()
    {
        var (inventory, _) = LoadInventory();
        var materializingFamilies = inventory.Families
            .Where(family => family.Effects.DiskWrite.State == "conditional_owned_artifact_materialization")
            .ToArray();

        Assert.NotEmpty(materializingFamilies);
        Assert.All(materializingFamilies, family =>
        {
            Assert.Equal(
                "release_blocked_physical_peak_unproven",
                family.Effects.DiskWrite.MeasurementState);
            Assert.Contains(
                family.Triggers,
                trigger => trigger.Contains(OwnedTraceArtifactStore.TemporarySpaceAssurance, StringComparison.Ordinal));
        });
    }

    private static string IdOnlyFamily(string sideEffectClass) => sideEffectClass;

    private static string CompatibilityFamily(string sideEffectClass) => sideEffectClass switch
    {
        "loaded_trace_query" => "raw_trace_query",
        "loaded_trace_stack_query" => "raw_trace_stack_query",
        _ => sideEffectClass,
    };

    private static void AssertAnnotations(
        ToolAnnotations actual,
        AnnotationExpectation expected,
        string label)
    {
        Assert.Equal(expected.ReadOnlyHint, actual.ReadOnlyHint);
        Assert.Equal(expected.IdempotentHint, actual.IdempotentHint);
        Assert.Equal(expected.OpenWorldHint, actual.OpenWorldHint);
        Assert.Equal(expected.DestructiveHint, actual.DestructiveHint);
    }

    private static (SideEffectInventory Inventory, string RepoRoot) LoadInventory()
    {
        var repoRoot = LocateRepoRoot();
        var path = Path.Combine(repoRoot, "eng", "contract-baselines", "side-effect-inventory.v1.json");
        var inventory = JsonSerializer.Deserialize<SideEffectInventory>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(inventory);
        return (inventory, repoRoot);
    }

    private static void AssertBoundary(
        string id,
        EffectMatrix effects,
        IReadOnlyList<string> triggers,
        IReadOnlyList<EvidenceSource> evidenceSources,
        string repoRoot)
    {
        AssertEffect(id, "pathAccess", effects.PathAccess, AllowedPathAccessStates);
        AssertEffect(id, "diskWrite", effects.DiskWrite, AllowedDiskWriteStates);
        AssertEffect(id, "network", effects.Network, AllowedNetworkStates);
        AssertEffect(id, "processState", effects.ProcessState, AllowedProcessStateStates);
        AssertEffect(id, "externalStorage", effects.ExternalStorage, AllowedExternalStorageStates);

        Assert.NotEmpty(triggers);
        Assert.All(triggers, trigger => Assert.False(string.IsNullOrWhiteSpace(trigger)));
        Assert.NotEmpty(evidenceSources);
        foreach (var source in evidenceSources)
        {
            Assert.False(string.IsNullOrWhiteSpace(source.Member), $"{id} has evidence without a member.");
            Assert.False(string.IsNullOrWhiteSpace(source.Basis), $"{id} has evidence without a basis.");
            AssertRepoFileExists(repoRoot, source.Path, $"boundary {id}");
        }
    }

    private static void AssertEffect(
        string id,
        string dimension,
        EffectState effect,
        IReadOnlyList<string> allowedStates)
    {
        Assert.Contains(effect.State, allowedStates);
        Assert.Contains(effect.MeasurementState, AllowedMeasurementStates);
        Assert.False(string.IsNullOrWhiteSpace(effect.State), $"{id}.{dimension} has no state.");
    }

    private static void AssertClosedEnum(
        SideEffectInventory inventory,
        string name,
        IReadOnlyList<string> expected)
    {
        Assert.True(inventory.StateEnums.TryGetValue(name, out var actual), $"Missing enum {name}.");
        Assert.Equal(
            expected.OrderBy(value => value, StringComparer.Ordinal),
            actual.OrderBy(value => value, StringComparer.Ordinal));
    }

    private static void AssertRepoFileExists(string repoRoot, string relativePath, string owner)
    {
        Assert.False(Path.IsPathRooted(relativePath), $"{owner} evidence path must be repo-relative: {relativePath}");
        var fullPath = Path.GetFullPath(Path.Combine(
            repoRoot,
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = Path.TrimEndingDirectorySeparator(repoRoot) + Path.DirectorySeparatorChar;
        Assert.True(
            fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase),
            $"{owner} evidence escapes the repository: {relativePath}");
        Assert.True(File.Exists(fullPath), $"{owner} evidence file does not exist: {relativePath}");
    }

    private static string LocateRepoRoot()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("WPAMCP_TEST_REPO_ROOT");
        var seeds = string.IsNullOrWhiteSpace(configuredRoot)
            ? new[] { Environment.CurrentDirectory, AppContext.BaseDirectory }
            : new[] { configuredRoot, Environment.CurrentDirectory, AppContext.BaseDirectory };
        foreach (var seed in seeds)
        {
            for (var directory = new DirectoryInfo(seed);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}

internal sealed record SideEffectInventory(
    string FormatVersion,
    SideEffectObservation Observation,
    SideEffectAnnotationBoundary AnnotationBoundary,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StateEnums,
    HostWrapperEffects HostWrapperEffects,
    IReadOnlyList<SideEffectFamily> Families,
    IReadOnlyList<SideEffectToolEntry> Tools,
    IReadOnlyList<string> KnownFindings);

internal sealed record SideEffectObservation(
    string CatalogSource,
    string ActiveCatalogSnapshotTarget,
    int ToolCount,
    IReadOnlyList<string> Profiles,
    string ReviewScope,
    string ReviewedAt);

internal sealed record SideEffectAnnotationBoundary(
    string Included,
    string Excluded,
    string Reason,
    string TraceReferenceRule,
    string SymbolRule,
    string ArtifactQuotaRule);

internal sealed record HostWrapperEffects(
    string Id,
    string Description,
    EffectMatrix Effects,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<EvidenceSource> EvidenceSources);

internal sealed record SideEffectFamily(
    string Id,
    string Description,
    EffectMatrix Effects,
    IReadOnlyList<string> Triggers,
    AnnotationExpectation ExpectedAnnotations,
    IReadOnlyList<EvidenceSource> EvidenceSources);

internal sealed record EffectMatrix(
    EffectState PathAccess,
    EffectState DiskWrite,
    EffectState Network,
    EffectState ProcessState,
    EffectState ExternalStorage);

internal sealed record EffectState(string State, string MeasurementState);

internal sealed record AnnotationExpectation(
    bool ReadOnlyHint,
    bool IdempotentHint,
    bool OpenWorldHint,
    bool DestructiveHint);

internal sealed record EvidenceSource(string Path, string Member, string Basis);

internal sealed record SideEffectToolEntry(
    string Name,
    string ManifestSideEffectClass,
    string IdOnlyFamily,
    string CompatibilityFamily);
