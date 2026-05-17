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

    [Fact]
    public void TraceDirectoryDefault_IsInsertedBeforeSymbolServers()
    {
        var localSymbols = Path.Combine(Path.GetTempPath(), "local-symbols");
        var traceDir = Path.Combine(Path.GetTempPath(), "trace-symbols");
        var current = $"{localSymbols};SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols";

        var updated = SymbolPathDefaults.AddLocalPathBeforeSymbolServers(current, traceDir);

        Assert.Equal(
            $"{localSymbols};{traceDir};SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
            updated);
    }

    [Fact]
    public void TraceCache_AddsTraceDirectoryToSymbolPath()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols");
            var cache = new TraceCache(capacity: 2);

            cache.Get("fixtures/small_cpu.etl");

            var traceDir = Path.GetDirectoryName(Path.GetFullPath("fixtures/small_cpu.etl"))!;
            Assert.StartsWith(traceDir + ";SRV*", Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
        }
        finally { Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved); }
    }

    [Theory]
    [InlineData("ntoskrnl", "msdl.microsoft.com")]
    [InlineData("msedge", "msdl.microsoft.com")]
    [InlineData("msedgewebview2", "msdl.microsoft.com")]
    [InlineData("chrome", "chromium-browser-symsrv")]
    [InlineData("electron", "chromium-browser-symsrv")]
    [InlineData("ffmpeg", "no public PDB server")]
    [InlineData("ffprobe", "no public PDB server")]
    [InlineData("MyApp", "set_symbol_path")]
    public void SuggestServerForModule_RoutesByModuleName(string moduleName, string expectedHintFragment)
    {
        var hint = SymbolTools.SuggestServerForModule(moduleName);
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
