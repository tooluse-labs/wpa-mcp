using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class TraceAccessPolicyTests
{
    [Fact]
    public async Task AllowAnyTracePathLoadsTraceOutsideConfiguredRoots()
    {
        var directory = CreateTempDirectory();
        try
        {
            var tracePath = Path.Combine(directory, "sample.etl");
            await File.WriteAllTextAsync(tracePath, "policy test payload");
            var options = new TraceRuntimeOptions(
                TraceAccessMode.IdOnly,
                [],
                Path.Combine(directory, "artifacts"),
                64L * 1024 * 1024,
                64L * 1024 * 1024,
                128,
                TimeSpan.FromDays(7),
                AllowAnyTracePath: true);
            using var policy = new TraceAccessPolicy(options);

            await using var source = await policy.OpenAsync(tracePath);

            Assert.EndsWith(
                "sample.etl", source.Identity.FinalPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DefaultPolicyWithoutRootsLoadsAnyLocalPath()
    {
        var directory = CreateTempDirectory();
        try
        {
            var tracePath = Path.Combine(directory, "sample.etl");
            await File.WriteAllTextAsync(tracePath, "policy test payload");
            var options = new TraceRuntimeOptions(
                TraceAccessMode.IdOnly,
                [],
                Path.Combine(directory, "artifacts"),
                64L * 1024 * 1024,
                64L * 1024 * 1024,
                128,
                TimeSpan.FromDays(7));
            using var policy = new TraceAccessPolicy(options);

            await using var source = await policy.OpenAsync(tracePath);

            Assert.EndsWith(
                "sample.etl", source.Identity.FinalPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task OutsideRootsDenialNamesRulePathAndConfiguredRoots()
    {
        var rootDirectory = CreateTempDirectory();
        var outsideDirectory = CreateTempDirectory();
        try
        {
            var tracePath = Path.Combine(outsideDirectory, "sample.etl");
            await File.WriteAllTextAsync(tracePath, "policy test payload");
            var options = new TraceRuntimeOptions(
                TraceAccessMode.IdOnly,
                [rootDirectory],
                Path.Combine(outsideDirectory, "artifacts"),
                64L * 1024 * 1024,
                64L * 1024 * 1024,
                128,
                TimeSpan.FromDays(7));
            using var policy = new TraceAccessPolicy(options);

            var ex = await Assert.ThrowsAsync<TraceAccessException>(
                () => policy.OpenAsync(tracePath).AsTask());

            Assert.Equal("trace_access_denied", ex.Code);
            Assert.Contains("trace_path_outside_allowed_roots", ex.Message);
            Assert.Contains(Path.GetFileName(rootDirectory), ex.Message);
            Assert.Contains("--trace-root", ex.Message);
        }
        finally
        {
            Directory.Delete(rootDirectory, recursive: true);
            Directory.Delete(outsideDirectory, recursive: true);
        }
    }

    [Fact]
    public void AccessDenialProjectionPreservesPolicyDetail()
    {
        var error = ContractMcpServerTool.MapException(new TraceAccessException(
            "trace_access_denied",
            "Trace access policy rejected the source (trace_path_outside_allowed_roots: detail)."));

        Assert.Equal("trace_access_denied", error.Code);
        Assert.Contains("trace_path_outside_allowed_roots", error.Message);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(), "wpa-mcp-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
