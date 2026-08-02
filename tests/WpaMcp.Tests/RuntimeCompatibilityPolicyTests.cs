using System.Text.Json;
using WpaMcp;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class RuntimeCompatibilityPolicyTests
{
    [Fact]
    public void Current03DevelopmentProfile_RemainsRunnableButCannotBeReleased()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate("0.3.0");

        Assert.Equal(RuntimeReleaseStage.DevelopmentPre04, profile.ReleaseStage);
        Assert.Equal(ToolContractMode.V2, profile.ContractMode);
        Assert.Equal(TraceAccessMode.IdOnly, profile.TraceReferenceMode);
        Assert.True(profile.Runnable);
        Assert.False(profile.ReleaseEligible);
        Assert.Contains(profile.ReleaseBlockers, blocker =>
            blocker.StartsWith("release_line_not_defined_by_adr_0005", StringComparison.Ordinal));
    }

    [Fact]
    public void Version04_DefaultsToContract2AndIdOnlyWithoutInventingALegacyFloor()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate("0.4.7");

        Assert.Equal(RuntimeReleaseStage.V04, profile.ReleaseStage);
        Assert.Equal(ToolContractMode.V2, profile.ContractMode);
        Assert.Equal(TraceAccessMode.IdOnly, profile.TraceReferenceMode);
        Assert.True(profile.Runnable);
        Assert.False(profile.ReleaseEligible);
        Assert.DoesNotContain(profile.ReleaseBlockers, blocker =>
            blocker.Contains("legacy", StringComparison.Ordinal));
        Assert.Equal(
            [RuntimeCompatibilityPolicy.ArtifactTransientPeakStatus],
            profile.ExternalKnownBlockers);
        RuntimeCompatibilityPolicy.RequireRunnable(profile);
    }

    [Fact]
    public void Version04_LegacySelectionFailsClosedBecauseItWasNeverAReleasedRuntimeContract()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate(
            "0.4.7",
            ToolContractMode.Legacy,
            contractModeExplicit: true,
            traceReferenceOverride: null,
            traceReferenceModeExplicit: false);

        Assert.False(profile.Runnable);
        Assert.False(profile.ReleaseEligible);
        Assert.True(profile.ContractModeExplicit);
        Assert.Contains(RuntimeCompatibilityPolicy.LegacyContractImplementationStatus,
            profile.RuntimeBlockers);
    }

    [Fact]
    public void Version05_DefaultsToContract2AndIdOnly()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate("0.5.0");

        Assert.Equal(RuntimeReleaseStage.V05, profile.ReleaseStage);
        Assert.Equal(ToolContractMode.V2, profile.ContractMode);
        Assert.Equal(TraceAccessMode.IdOnly, profile.TraceReferenceMode);
        Assert.False(profile.ContractModeExplicit);
        Assert.False(profile.TraceReferenceModeExplicit);
        Assert.True(profile.Runnable);
        Assert.False(profile.ReleaseEligible);
        Assert.Equal(
            [RuntimeCompatibilityPolicy.ArtifactTransientPeakStatus],
            profile.ExternalKnownBlockers);
        Assert.Empty(profile.Warnings);
    }

    [Fact]
    public void Version05_RawPathCompatibilityIsExplicitAndDeprecated()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate(
            "0.5.3",
            traceReferenceOverride: TraceAccessMode.Compatibility,
            traceReferenceModeExplicit: true);

        Assert.True(profile.Runnable);
        Assert.False(profile.ReleaseEligible);
        Assert.Contains(profile.Warnings, warning =>
            warning.StartsWith("raw_trace_path_deprecated", StringComparison.Ordinal));
    }

    [Fact]
    public void Version05_LegacySelectionDoesNotMasqueradeAsImplemented()
    {
        var profile = RuntimeCompatibilityPolicy.Evaluate(
            "0.5.3",
            ToolContractMode.Legacy,
            contractModeExplicit: true);

        Assert.False(profile.Runnable);
        Assert.Contains(RuntimeCompatibilityPolicy.LegacyContractImplementationStatus,
            profile.RuntimeBlockers);
    }

    [Fact]
    public void Version1_RemovesBothCompatibilityModesAndRequiresHistoricalGate()
    {
        var defaults = RuntimeCompatibilityPolicy.Evaluate("1.0.0");
        Assert.True(defaults.Runnable);
        Assert.False(defaults.ReleaseEligible);
        Assert.Contains(RuntimeCompatibilityPolicy.V1DeprecationGateStatus,
            defaults.ReleaseBlockers);
        Assert.Equal(
            [RuntimeCompatibilityPolicy.ArtifactTransientPeakStatus],
            defaults.ExternalKnownBlockers);

        var rawPath = RuntimeCompatibilityPolicy.Evaluate(
            "1.0.0",
            traceReferenceOverride: TraceAccessMode.Compatibility,
            traceReferenceModeExplicit: true);
        Assert.False(rawPath.Runnable);
        Assert.Contains(rawPath.RuntimeBlockers, blocker =>
            blocker.StartsWith("trace_reference_mode_removed", StringComparison.Ordinal));

        var legacy = RuntimeCompatibilityPolicy.Evaluate(
            "1.0.0",
            ToolContractMode.Legacy,
            contractModeExplicit: true);
        Assert.False(legacy.Runnable);
        Assert.Contains(legacy.RuntimeBlockers, blocker =>
            blocker.StartsWith("contract_mode_removed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("2.0 ")]
    [InlineData("v2")]
    public void ContractModeParser_IsClosedAndExact(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            RuntimeCompatibilityPolicy.ParseContractMode(value, "test"));
    }

    [Theory]
    [InlineData("0.6.0")]
    [InlineData("2.0.0")]
    public void UndefinedReleaseLinesFailClosed(string version)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            RuntimeCompatibilityPolicy.Evaluate(version));
        Assert.Contains("not defined by ADR 0005", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ServerOptions_CliOverridesEnvironmentAndRemovesStartupModesFromHostArgs()
    {
        var values = BaseEnvironment();
        // CLI precedence is applied before parsing, so a valid CLI selection can
        // recover from stale/invalid environment values.
        values[RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable] = "stale-contract";
        values[TraceRuntimeOptions.AccessModeEnvironmentVariable] = "stale-trace-mode";

        var options = McpServerOptions.Parse(
            [
                "--contract-mode", "2.0",
                "--trace-reference-mode", "id_only",
                "--urls", "http://localhost",
            ],
            key => values.GetValueOrDefault(key),
            runtimeVersion: "0.5.1");

        Assert.Equal(ToolContractMode.V2, options.CompatibilityProfile.ContractMode);
        Assert.Equal(TraceAccessMode.IdOnly, options.TraceRuntime.AccessMode);
        Assert.True(options.CompatibilityProfile.ContractModeExplicit);
        Assert.True(options.CompatibilityProfile.TraceReferenceModeExplicit);
        Assert.Equal(new[] { "--urls", "http://localhost" }, options.HostArgs);
    }

    [Fact]
    public void ServerOptions_Default04ProfileUsesContract2AndIdOnly()
    {
        var values = BaseEnvironment();
        var options = McpServerOptions.Parse(
            [],
            key => values.GetValueOrDefault(key),
            runtimeVersion: "0.4.0");

        Assert.Equal(ToolContractMode.V2, options.CompatibilityProfile.ContractMode);
        Assert.Equal(TraceAccessMode.IdOnly, options.TraceRuntime.AccessMode);
    }

    [Fact]
    public void ServerOptions_InvalidEnvironmentModeFailsWhenCliDoesNotOverrideIt()
    {
        var values = BaseEnvironment();
        values[RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable] = "stale-contract";

        var error = Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            [],
            key => values.GetValueOrDefault(key),
            runtimeVersion: "0.5.0"));

        Assert.Contains(RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeResource_IsExplicitAboutImmutableSelectionAndBlockers()
    {
        var record = RuntimeCompatibilityPolicy.Evaluate("0.3.0").ToResourceRecord();
        var json = JsonSerializer.Serialize(record, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Contains("\"selectionScope\":\"startup_immutable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"contractMode\":\"2.0\"", json, StringComparison.Ordinal);
        Assert.Contains("\"traceReferenceMode\":\"id_only\"", json, StringComparison.Ordinal);
        Assert.Contains(
            "\"outputSchemaDialect\":\"https://json-schema.org/draft/2020-12/schema\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"outputSchemaReferenceProfile\":\"root_local_defs_safe_id\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"outputSchemaReferenceRequirement\":\"on_demand_content_addressed_resource_or_get_tool_contract\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"outputSchemaExternalReferencePolicy\":\"forbidden\"",
            json,
            StringComparison.Ordinal);
        Assert.Contains("\"releaseStatus\":\"blocked\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolsListCursorBinding_UsesSelectedRuntimeContractMode()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var tools = catalog.CreateProtocolTools(services);
        var profile = RuntimeCompatibilityPolicy.Evaluate("0.5.0");
        var pagination = new ToolsListPaginationFilters(
            tools,
            catalog.CatalogVersion,
            contractMode: profile.ContractModeName);

        Assert.Equal(profile.ContractModeName, pagination.Binding.ContractMode);
    }

    [Fact]
    public void Telemetry_RecordsModesExplicitnessWarningsAndReleaseBoundary()
    {
        using var writer = new StringWriter();
        using var telemetry = new ToolTelemetry(
            new ToolTelemetryOptions(true, ToolTelemetryDestination.Stderr, null),
            new byte[32],
            writer);
        var profile = RuntimeCompatibilityPolicy.Evaluate(
            "0.5.0",
            traceReferenceOverride: TraceAccessMode.Compatibility,
            traceReferenceModeExplicit: true);

        telemetry.RecordRuntimeProfile(profile);

        using var document = JsonDocument.Parse(writer.ToString());
        var root = document.RootElement;
        Assert.Equal("runtime_profile", root.GetProperty("event_type").GetString());
        Assert.Equal("2.0", root.GetProperty("contract_mode").GetString());
        Assert.Equal("compatibility", root.GetProperty("trace_reference_mode").GetString());
        Assert.True(root.GetProperty("trace_reference_mode_explicit").GetBoolean());
        Assert.Equal("blocked",
            root.GetProperty("release_status").GetString());
        Assert.NotEmpty(root.GetProperty("external_known_blockers").EnumerateArray());
        Assert.NotEmpty(root.GetProperty("deprecation_warnings").EnumerateArray());
    }

    private static Dictionary<string, string?> BaseEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), "wpa-mcp-runtime-profile-source");
        var artifacts = Path.Combine(Path.GetTempPath(), "wpa-mcp-runtime-profile-artifacts");
        return new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [TraceRuntimeOptions.AllowedRootsEnvironmentVariable] = root,
            [TraceRuntimeOptions.ArtifactRootEnvironmentVariable] = artifacts,
        };
    }
}
