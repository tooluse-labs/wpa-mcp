using WprMcp;

namespace WprMcp.Tests;

public class McpServerOptionsTests
{
    [Fact]
    public void ParseExtractsServerOptionsAndLeavesHostArgs()
    {
        var options = McpServerOptions.Parse(new[]
        {
            "--symbol-path", "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
            "--urls", "http://localhost",
            "--cache-size", "4"
        });

        Assert.Equal("SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols", options.SymbolPath);
        Assert.Equal(4, options.CacheSize);
        Assert.Equal(new[] { "--urls", "http://localhost" }, options.HostArgs);
    }

    [Fact]
    public void ParseRejectsMissingValues()
    {
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--symbol-path" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size" }));
    }

    [Fact]
    public void ParseRejectsInvalidCacheSize()
    {
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size", "0" }));
        Assert.Throws<ArgumentException>(() => McpServerOptions.Parse(new[] { "--cache-size", "abc" }));
    }

    [Fact]
    public void ApplyToEnvironmentSetsRuntimeConfiguration()
    {
        var savedSymbolPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        var savedCacheSize = Environment.GetEnvironmentVariable("WPRMCP_CACHE_SIZE");
        try
        {
            var options = McpServerOptions.Parse(new[] { "--symbol-path", "X", "--cache-size", "3" });
            options.ApplyToEnvironment();

            Assert.Equal("X", Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
            Assert.Equal("3", Environment.GetEnvironmentVariable("WPRMCP_CACHE_SIZE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", savedSymbolPath);
            Environment.SetEnvironmentVariable("WPRMCP_CACHE_SIZE", savedCacheSize);
        }
    }
}
