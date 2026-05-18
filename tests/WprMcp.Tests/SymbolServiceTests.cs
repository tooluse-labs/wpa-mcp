using WprMcp.Core;
using WprMcp.Output;
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
        Assert.NotNull(resp.NativeSymbolSupport);
        Assert.Contains(resp.NativeSymbolSupport.Dependencies, dep => dep.Name == "msdia140.dll");
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath("fixtures/small_cpu.etl")),
            resp.TraceDirectory);
        Assert.True(resp.TraceDirectoryInSymbolPath);
        Assert.All(resp.Modules, module => Assert.False(string.IsNullOrWhiteSpace(module.LookupStatus)));
    }

    [Fact]
    public void FindLocalSymbolCandidates_FindsFlatPdbAndSymbolStoreLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-diag-{Guid.NewGuid():N}");
        try
        {
            var signature = Guid.Parse("4b025cce-b6d2-5baf-4c4c-44205044422e");
            var flatPdb = Path.Combine(root, "flat", "quark.dll.pdb");
            var storePdb = Path.Combine(
                root,
                "store",
                "quark.dll.pdb",
                "4B025CCEB6D25BAF4C4C44205044422E1",
                "quark.dll.pdb");
            Directory.CreateDirectory(Path.GetDirectoryName(flatPdb)!);
            Directory.CreateDirectory(Path.GetDirectoryName(storePdb)!);
            File.WriteAllText(flatPdb, "");
            File.WriteAllText(storePdb, "");

            var symbolPath = $"{Path.Combine(root, "flat")};SRV*C:\\Symbols*{Path.Combine(root, "store")};SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols";
            var candidates = SymbolTools.FindLocalSymbolCandidates(symbolPath, "quark.dll.pdb", signature, 1);

            Assert.Contains(flatPdb, candidates);
            Assert.Contains(storePdb, candidates);
            Assert.DoesNotContain(candidates, candidate => candidate.Contains("https://", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindLocalSymbolCandidates_ProbesCacheEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-cache-{Guid.NewGuid():N}");
        try
        {
            var signature = Guid.Parse("4b025cce-b6d2-5baf-4c4c-44205044422e");
            var storePdb = Path.Combine(
                root,
                "quark.dll.pdb",
                "4B025CCEB6D25BAF4C4C44205044422E1",
                "quark.dll.pdb");
            Directory.CreateDirectory(Path.GetDirectoryName(storePdb)!);
            File.WriteAllText(storePdb, "");

            var candidates = SymbolTools.FindLocalSymbolCandidates(
                $"cache*{root};srv*https://msdl.microsoft.com/download/symbols",
                "quark.dll.pdb",
                signature,
                1);

            Assert.Contains(storePdb, candidates);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_DoesNotResolveFlatNameOnlyCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-flat-{Guid.NewGuid():N}");
        try
        {
            var module = FirstModuleWithPdbIdentity();
            var pdbName = Path.GetFileName(module.PdbName);
            var flatPdb = Path.Combine(root, pdbName);
            Directory.CreateDirectory(root);
            File.WriteAllText(flatPdb, "");

            var status = SymbolTools.DiagnoseModule(module, root, NativeSupportReady());

            Assert.False(status.Resolved);
            Assert.Equal("found_flat_candidate_identity_unverified", status.LookupStatus);
            Assert.Contains(flatPdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_ResolvesCacheSymbolStoreMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-store-{Guid.NewGuid():N}");
        try
        {
            var module = FirstModuleWithPdbIdentity();
            var pdbName = Path.GetFileName(module.PdbName);
            var storePdb = Path.Combine(
                root,
                pdbName,
                SymbolStoreKey(module.PdbSignature, module.PdbAge),
                pdbName);
            Directory.CreateDirectory(Path.GetDirectoryName(storePdb)!);
            File.WriteAllText(storePdb, "");

            var status = SymbolTools.DiagnoseModule(module, $"cache*{root}", NativeSupportReady());

            Assert.True(status.Resolved);
            Assert.Equal("found_in_local_symbol_path", status.LookupStatus);
            Assert.Contains(storePdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindLocalSymbolCandidates_SkipsMalformedAndUncSymbolPathEntries()
    {
        var malformed = "C:\\bad" + '\0' + "path";
        var symbolPath = $"{malformed};\\\\server\\share\\symbols;SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols";

        var candidates = SymbolTools.FindLocalSymbolCandidates(
            symbolPath,
            "quark.dll.pdb",
            Guid.NewGuid(),
            1);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildSymbolPathEntryWarnings_ReportsMalformedAndUncEntries()
    {
        var malformed = "C:\\bad" + '\0' + "path";
        var symbolPath = $"{malformed};\\\\server\\share\\symbols;SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols";

        var warnings = SymbolTools.BuildSymbolPathEntryWarnings(symbolPath);

        Assert.Contains(warnings, warning => warning.Contains("malformed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.Contains("UNC", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, warning => warning.Contains("https://", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSymbolPathEntryWarnings_ReportsMissingRelativeEntries()
    {
        var relativePath = $"missing-symbols-{Guid.NewGuid():N}";

        var warnings = SymbolTools.BuildSymbolPathEntryWarnings(relativePath);

        Assert.Contains(warnings, warning =>
            warning.Contains("relative", StringComparison.OrdinalIgnoreCase) &&
            warning.Contains(relativePath, StringComparison.OrdinalIgnoreCase));
    }

    private static Microsoft.Diagnostics.Tracing.Etlx.TraceModuleFile FirstModuleWithPdbIdentity()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        return trace.ModuleFiles.First(module =>
            !string.IsNullOrWhiteSpace(Path.GetFileName(module.PdbName)) &&
            module.PdbSignature != Guid.Empty &&
            module.PdbAge > 0);
    }

    private static NativeSymbolSupportStatus NativeSupportReady()
        => new(
            Architecture: "amd64",
            Msdia140Present: true,
            KernelTraceControlPresent: true,
            Status: "ready",
            Dependencies: Array.Empty<NativeDependencyStatus>(),
            Suggestion: null);

    private static string SymbolStoreKey(Guid signature, int age)
        => signature.ToString("N").ToUpperInvariant() + age.ToString("X");
}
