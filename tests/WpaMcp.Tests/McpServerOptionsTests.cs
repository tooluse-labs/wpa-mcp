using WpaMcp;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public class McpServerOptionsTests
{
    [Fact]
    public void ParseExtractsServerOptionsAndLeavesHostArgs()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "wpa-mcp-symbol-source");
        var storeRoot = Path.Combine(Path.GetTempPath(), "wpa-mcp-symbol-private-store");
        var options = McpServerOptions.Parse(new[]
        {
            "--symbol-local-root", sourceRoot,
            "--symbol-store-root", storeRoot,
            "--urls", "http://localhost",
            "--cache-size", "4"
        });

        Assert.Equal(Path.GetFullPath(sourceRoot), Assert.Single(options.SymbolRuntime.ApprovedLocalRoots));
        Assert.Equal(Path.GetFullPath(storeRoot), options.SymbolRuntime.StoreRoot);
        Assert.Equal(4, options.CacheSize);
        Assert.Equal(new[] { "--urls", "http://localhost" }, options.HostArgs);
    }

    [Fact]
    public void ParseRejectsMissingValues()
    {
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--symbol-path" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--symbol-local-root" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--symbol-store-root" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size" }));
        Assert.Throws<ArgumentException>(() =>
            McpServerOptions.Parse(new[] { "--trace-artifact-retention-minutes" }));
    }

    [Fact]
    public void ParseRejectsInvalidCacheSize()
    {
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size", "0" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size", "abc" }));
    }

    [Fact]
    public void ParseDoesNotMutateProcessEnvironment_AndLegacySymbolPathIsRejected()
    {
        var savedSymbolPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        var savedCacheSize = Environment.GetEnvironmentVariable("WPAMCP_CACHE_SIZE");
        var options = McpServerOptions.Parse(new[] { "--cache-size", "3" });

        Assert.Equal(3, options.CacheSize);
        Assert.Equal(savedSymbolPath, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        Assert.Equal(savedCacheSize, Environment.GetEnvironmentVariable("WPAMCP_CACHE_SIZE"));
        Assert.Throws<ArgumentException>(() =>
            McpServerOptions.Parse(new[] { "--symbol-path", "X" }));
    }

    [Fact]
    public void SymbolSourceAndPrivateStoreBoundariesMustBeDisjoint()
    {
        var sourceRoot = Path.Combine(Path.GetTempPath(), "wpa-mcp-symbol-source");
        var nestedStore = Path.Combine(sourceRoot, "private-store");

        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[]
        {
            "--symbol-local-root", sourceRoot,
            "--symbol-store-root", nestedStore,
        }));
    }

    [Fact]
    public void ArtifactRetentionTtl_IsStartupScopedAndValidated()
    {
        var fromEnvironment = McpServerOptions.Parse(
            [],
            name => name == TraceRuntimeOptions.ArtifactRetentionMinutesEnvironmentVariable
                ? "120"
                : null,
            runtimeVersion: "0.3.0");
        Assert.Equal(TimeSpan.FromMinutes(120), fromEnvironment.TraceRuntime.ArtifactRetentionTtl);

        var cliOverride = McpServerOptions.Parse(
            ["--trace-artifact-retention-minutes", "90"],
            name => name == TraceRuntimeOptions.ArtifactRetentionMinutesEnvironmentVariable
                ? "120"
                : null,
            runtimeVersion: "0.3.0");
        Assert.Equal(TimeSpan.FromMinutes(90), cliOverride.TraceRuntime.ArtifactRetentionTtl);

        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            ["--trace-artifact-retention-minutes", "0"],
            _ => null,
            runtimeVersion: "0.3.0"));
        Assert.Throws<ArgumentOutOfRangeException>(() => McpServerOptions.Parse(
            ["--trace-artifact-retention-minutes", "525601"],
            _ => null,
            runtimeVersion: "0.3.0"));
    }

    [Fact]
    public void CapabilityPolicy_IsStartupImmutableNormalizedAndCliOverridesEnvironment()
    {
        var fromEnvironment = McpServerOptions.Parse(
            [],
            name => name == CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable
                ? "cpu.sampled.stacks, io.file.activity"
                : null,
            runtimeVersion: "0.3.0");
        Assert.Equal("restricted", fromEnvironment.CapabilityPolicy.ProfileName);
        Assert.Equal(
            ["cpu.sampled.stacks", "io.file.activity"],
            fromEnvironment.CapabilityPolicy.DisabledCapabilityIds);
        Assert.Equal(
            CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable,
            fromEnvironment.CapabilityPolicy.Source);
        Assert.Equal("startup_immutable", fromEnvironment.CapabilityPolicy.SelectionScope);
        Assert.Matches("^[0-9a-f]{64}$", fromEnvironment.CapabilityPolicy.ProfileHash);

        var cliOverride = McpServerOptions.Parse(
            [CapabilityPolicyProfile.DisableCapabilitiesOption, "scheduler.wait.stacks"],
            name => name == CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable
                ? "cpu.sampled.stacks"
                : null,
            runtimeVersion: "0.3.0");
        Assert.Equal(
            ["scheduler.wait.stacks"],
            cliOverride.CapabilityPolicy.DisabledCapabilityIds);
        Assert.Equal(
            CapabilityPolicyProfile.DisableCapabilitiesOption,
            cliOverride.CapabilityPolicy.Source);

        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            [CapabilityPolicyProfile.DisableCapabilitiesOption, "CPU.sampled.stacks"],
            _ => null,
            runtimeVersion: "0.3.0"));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            [CapabilityPolicyProfile.DisableCapabilitiesOption,
                "cpu.sampled.stacks,cpu.sampled.stacks"],
            _ => null,
            runtimeVersion: "0.3.0"));
    }

    [Fact]
    public void ParseRejectsUnconsumedPositionalArguments()
    {
        var ex = Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(
            ["--trace-root", "C:\\Temp", "C:\\tmp", "c:\\unsynced"]));
        Assert.Contains("Unrecognized argument", ex.Message);
        Assert.Contains("--trace-root", ex.Message);
    }

    [Fact]
    public void ParseAccumulatesRepeatedTraceRoots()
    {
        var options = McpServerOptions.Parse(
            ["--trace-root", "C:\\TracesA", "--trace-root", "D:\\CapturesB"]);
        Assert.Equal(2, options.TraceRuntime.AllowedRoots.Count);
        Assert.Contains(options.TraceRuntime.AllowedRoots,
            root => root.Contains("TracesA", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(options.TraceRuntime.AllowedRoots,
            root => root.Contains("CapturesB", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ParseStillForwardsHostOptionValuePairs()
    {
        var options = McpServerOptions.Parse(
            ["--allow-any-trace-path", "--urls", "http://localhost"]);
        Assert.True(options.TraceRuntime.AllowAnyTracePath);
        Assert.Equal(["--urls", "http://localhost"], options.HostArgs);
    }

    [Fact]
    public void AllowAnyTracePathCanComeFromEnvironment()
    {
        var options = McpServerOptions.Parse(
            Array.Empty<string>(),
            name => name == TraceRuntimeOptions.AllowAnyTracePathEnvironmentVariable
                ? "true"
                : null);
        Assert.True(options.TraceRuntime.AllowAnyTracePath);
    }

    [Fact]
    public void EmptyRootsDefaultToUnconfinedAccess()
    {
        var unconfined = new TraceRuntimeOptions(
            TraceAccessMode.IdOnly,
            [],
            Path.Combine(Path.GetTempPath(), "wpa-mcp-test-artifacts"),
            1024,
            1024,
            8,
            TimeSpan.FromDays(1));
        unconfined.ValidatePure();
        Assert.False(unconfined.EnforceTraceRoots);

        var confined = unconfined with { AllowedRoots = new[] { Path.GetTempPath() } };
        Assert.True(confined.EnforceTraceRoots);
        Assert.False((confined with { AllowAnyTracePath = true }).EnforceTraceRoots);
    }
}
