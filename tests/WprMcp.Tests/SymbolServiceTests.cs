using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public class SymbolServiceTests
{
    [Fact]
    public void SetPath_Replace_OverwritesEnv()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "OLD");
            var svc = new SymbolService();
            svc.SetPath("NEW", append: false);
            Assert.Equal("NEW", svc.CurrentPath);
            Assert.Equal("NEW", Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Fact]
    public void SetPath_Append_ConcatenatesWithSemicolon()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "A");
            var svc = new SymbolService();
            svc.SetPath("B", append: true);
            Assert.Equal("A;B", svc.CurrentPath);
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Fact]
    public void AddServer_AppendsSrvPrefixedEntry()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var svc = new SymbolService();
            var cache = Path.Combine(Path.GetTempPath(), $"wpa-mcp-sym-{Guid.NewGuid():N}");
            svc.AddServer("https://example.com/symbols", cache);
            Assert.Equal($"SRV*{cache}*https://example.com/symbols", svc.CurrentPath);
            Assert.True(Directory.Exists(cache));
            Directory.Delete(cache);
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Fact]
    public void DiagnoseSymbols_ReturnsAtLeastOneModule()
    {
        var svc = new SymbolService();
        var cache = new TraceCache(capacity: 2);
        var tools = new WprMcp.Tools.SymbolTools(svc, cache);
        var resp = tools.DiagnoseSymbols("fixtures/small_cpu.etl");
        Assert.NotEmpty(resp.Modules);
    }
}
