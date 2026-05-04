using WprMcp.Core;
using WprMcp.Tools;
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
    public void AddServer_CalledTwiceWithSameUrl_DoesNotDuplicate()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var svc = new SymbolService();
            var cache = Path.Combine(Path.GetTempPath(), $"wpa-mcp-sym-{Guid.NewGuid():N}");
            svc.AddServer("https://example.com/symbols", cache);
            svc.AddServer("https://example.com/symbols", cache);
            // Idempotency contract documented in SymbolTools.AddSymbolServer description.
            Assert.Equal($"SRV*{cache}*https://example.com/symbols", svc.CurrentPath);
            Directory.Delete(cache);
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Fact]
    public void AddServer_DifferentUrls_AppendsBoth()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var svc = new SymbolService();
            var cache = Path.Combine(Path.GetTempPath(), $"wpa-mcp-sym-{Guid.NewGuid():N}");
            svc.AddServer("https://a.example.com/symbols", cache);
            svc.AddServer("https://b.example.com/symbols", cache);
            Assert.Equal(
                $"SRV*{cache}*https://a.example.com/symbols;SRV*{cache}*https://b.example.com/symbols",
                svc.CurrentPath);
            Directory.Delete(cache);
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Fact]
    public void AddServer_NullCacheDir_UsesLocalAppDataDefault()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var svc = new SymbolService();
            var expectedCache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WprMcp", "Symbols");
            svc.AddServer("https://example.com/symbols", cacheDir: null);
            Assert.Equal($"SRV*{expectedCache}*https://example.com/symbols", svc.CurrentPath);
            Assert.True(Directory.Exists(expectedCache));
            // Don't delete the default cache — production runs share it.
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Theory]
    [InlineData("C:\\Windows\\System32\\ntoskrnl.exe", "msdl.microsoft.com")]
    [InlineData("C:\\Program Files\\Microsoft\\Edge\\msedge.exe", "msdl.microsoft.com")]
    [InlineData("C:\\Program Files\\Google\\Chrome\\Application\\chrome.dll", "chromium-browser-symsrv")]
    [InlineData("C:\\Program Files\\Electron\\electron.exe", "chromium-browser-symsrv")]
    [InlineData("C:\\src\\myproduct\\out\\release\\MyApp.dll", "set_symbol_path")]
    public void SuggestServerForModule_RoutesByPathSubstring(string filePath, string expectedHintFragment)
    {
        var hint = SymbolTools.SuggestServerForModule(filePath);
        Assert.Contains(expectedHintFragment, hint);
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
