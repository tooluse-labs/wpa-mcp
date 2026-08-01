using System.ComponentModel;
using System.Reflection;
using System.Reflection.PortableExecutable;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

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
                "WpaMcp", "Symbols");
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
    public void TraceCache_DoesNotMutateConfiguredSymbolPath()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols");
            var cache = new TraceCache(capacity: 2);

            var configured = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
            var trace = cache.Get("fixtures/small_cpu.etl");

            var traceDir = Path.GetDirectoryName(Path.GetFullPath("fixtures/small_cpu.etl"))!;
            Assert.Equal(configured, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
            using var reader = StackSourceTopN.OpenSymbolReader(trace, TextWriter.Null);
            Assert.StartsWith(traceDir + ";SRV*", reader.SymbolPath);
            Assert.Equal(configured, Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
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
    public void PdbNameAlone_IsNotACompletePdbIdentity()
    {
        Assert.False(SymbolTools.HasCompletePdbIdentity(
            "sample.pdb", Guid.Empty, pdbAge: 0));
        Assert.True(SymbolTools.HasCompletePdbIdentity(
            "sample.pdb", Guid.NewGuid(), pdbAge: 1));
    }

    [Fact]
    public void DiagnoseSymbols_ReturnsAtLeastOneModule()
    {
        var svc = new SymbolService();
        var cache = new TraceCache(capacity: 2);
        var tools = new WpaMcp.Tools.SymbolTools(svc, cache);
        var resp = tools.DiagnoseSymbols("fixtures/small_cpu.etl");
        Assert.NotEmpty(resp.Modules);
        Assert.NotNull(resp.NativeSymbolSupport);
        Assert.Contains(resp.NativeSymbolSupport.Dependencies, dep => dep.Name == "msdia140.dll");
        Assert.Equal(
            Path.GetDirectoryName(Path.GetFullPath("fixtures/small_cpu.etl")),
            resp.TraceDirectory);
        Assert.True(resp.TraceDirectoryInSymbolPath);
        Assert.Equal(resp.CurrentSymbolPath, resp.ConfiguredSymbolPath);
        Assert.Contains(resp.TraceDirectory, resp.EffectiveSymbolPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            resp.TraceDirectoryInSymbolPath,
            resp.TraceDirectoryInEffectiveSymbolPath);
        Assert.Equal("not_measured", resp.FrameResolutionMeasurementState);
        Assert.Equal(resp.DefaultCacheDir, resp.CacheDir);
        Assert.Equal(svc.DefaultCacheDir, resp.DefaultCacheDir);
        Assert.All(resp.Modules, module => Assert.False(string.IsNullOrWhiteSpace(module.LookupStatus)));
        Assert.All(resp.Modules, module =>
        {
            Assert.Null(module.FrameCount);
            Assert.Null(module.Resolved);
            Assert.Equal("not_measured", module.FrameResolutionState);
        });
    }

    [Fact]
    public void DiagnoseSymbols_DescriptionsDiscloseCandidateCapAndDefaultCacheSemantics()
    {
        var cacheDescription = typeof(DiagnoseSymbolsResponse)
            .GetProperty(nameof(DiagnoseSymbolsResponse.CacheDir))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;
        var defaultCacheDescription = typeof(DiagnoseSymbolsResponse)
            .GetProperty(nameof(DiagnoseSymbolsResponse.DefaultCacheDir))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;
        var candidatesDescription = typeof(ModuleSymbolStatus)
            .GetProperty(nameof(ModuleSymbolStatus.LocalSymbolCandidates))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        Assert.Contains("compatibility alias", cacheDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not prove", cacheDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fallback", defaultCacheDescription, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("At most 10", candidatesDescription, StringComparison.Ordinal);
        Assert.Contains("mapped drive", candidatesDescription, StringComparison.OrdinalIgnoreCase);
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
            File.WriteAllBytes(
                flatPdb,
                System.Text.Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"));
            File.WriteAllText(storePdb, "");

            var cacheRoot = Path.Combine(root, "cache");
            Directory.CreateDirectory(cacheRoot);
            var symbolPath = $"{Path.Combine(root, "flat")};SRV*{cacheRoot}*{Path.Combine(root, "store")};SRV*{cacheRoot}*https://msdl.microsoft.com/download/symbols";
            var candidates = SymbolTools.FindLocalSymbolCandidates(symbolPath, "quark.dll.pdb", signature, 1);

            Assert.Contains(flatPdb, candidates);
            Assert.Contains(storePdb, candidates);
            Assert.Equal(new[] { flatPdb, storePdb }, candidates);
            Assert.DoesNotContain(candidates, candidate => candidate.Contains("https://", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindLocalSymbolCandidates_BarePathDoesNotProbeSymbolStoreLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-bare-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var storePdb = Path.Combine(
                root,
                artifact.PdbName,
                SymbolStoreKey(artifact.Signature, artifact.Age),
                artifact.PdbName);
            Directory.CreateDirectory(Path.GetDirectoryName(storePdb)!);
            File.Copy(artifact.PdbPath, storePdb);

            var bareCandidates = SymbolTools.FindLocalSymbolCandidates(
                root,
                artifact.PdbName,
                artifact.Signature,
                artifact.Age);
            var cacheCandidates = SymbolTools.FindLocalSymbolCandidates(
                $"CACHE*{root}",
                artifact.PdbName,
                artifact.Signature,
                artifact.Age);
            var bareStatus = DiagnosePortableModule(artifact, root);
            var cacheStatus = DiagnosePortableModule(artifact, $"CACHE*{root}");

            Assert.Empty(bareCandidates);
            Assert.Equal(new[] { storePdb }, cacheCandidates);
            Assert.False(bareStatus.LocalPdbReady);
            Assert.Equal("not_found_in_local_symbol_path", bareStatus.LookupStatus);
            Assert.True(cacheStatus.LocalPdbReady);
            Assert.Equal("exact_identity_match", cacheStatus.LookupStatus);
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
    public void DiagnoseModule_VerifiesAllCandidatesBeforeCappingDisplayedPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-many-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var configuredRoots = new List<string>();
            for (var i = 0; i < 11; i++)
            {
                var candidateRoot = Path.Combine(root, $"invalid-{i:D2}");
                Directory.CreateDirectory(candidateRoot);
                File.WriteAllText(Path.Combine(candidateRoot, artifact.PdbName), "not a PDB");
                configuredRoots.Add(candidateRoot);
            }

            var exactRoot = Path.Combine(root, "exact-last");
            Directory.CreateDirectory(exactRoot);
            var exactPdb = Path.Combine(exactRoot, artifact.PdbName);
            File.Copy(artifact.PdbPath, exactPdb);
            configuredRoots.Add(exactRoot);

            var status = DiagnosePortableModule(
                artifact,
                string.Join(';', configuredRoots));

            Assert.True(status.LocalPdbReady);
            Assert.Equal("exact_identity_match", status.LookupStatus);
            Assert.Equal(12, status.LocalSymbolCandidateCount);
            Assert.True(status.LocalSymbolCandidatesTruncated);
            Assert.Equal(10, status.LocalSymbolCandidates?.Count);
            Assert.Equal(exactPdb, status.LocalSymbolCandidates?[0]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_VerifiesMatchingPortablePdbIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-flat-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var flatPdb = Path.Combine(root, artifact.PdbName);
            Directory.CreateDirectory(root);
            File.Copy(artifact.PdbPath, flatPdb);

            var status = DiagnosePortableModule(
                artifact,
                root,
                nativeSupport: NativeSupportMissing());

            Assert.Null(status.Resolved);
            Assert.Null(status.FrameCount);
            Assert.True(status.HasPdbName);
            Assert.True(status.HasCompletePdbIdentity);
            Assert.True(status.LocalPdbReady);
            Assert.Equal("not_measured", status.FrameResolutionState);
            Assert.Equal("exact_identity_match", status.LookupStatus);
            Assert.Equal(
                "module_metadata_and_local_pdb_identity_verification",
                status.EvidenceScope);
            Assert.Null(status.FailureReason);
            Assert.Contains(flatPdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
            Assert.Equal(1, status.LocalSymbolCandidateCount);
            Assert.False(status.LocalSymbolCandidatesTruncated);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_RejectsPortablePdbWithWrongGuidOrAge()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-mismatch-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var mismatches = new[]
            {
                (Signature: Guid.NewGuid(), Age: artifact.Age),
                (Signature: artifact.Signature, Age: checked(artifact.Age + 1))
            };

            foreach (var (signature, age) in mismatches)
            {
                var candidateRoot = Path.Combine(root, $"candidate-{age}-{signature:N}");
                Directory.CreateDirectory(candidateRoot);
                File.Copy(artifact.PdbPath, Path.Combine(candidateRoot, artifact.PdbName));

                var status = DiagnosePortableModule(
                    artifact,
                    candidateRoot,
                    expectedSignature: signature,
                    expectedAge: age);

                Assert.False(status.LocalPdbReady);
                Assert.Equal("identity_mismatch", status.LookupStatus);
                Assert.Equal(
                    "module_metadata_and_local_pdb_identity_verification",
                    status.EvidenceScope);
                Assert.Contains("GUID/Age", status.FailureReason ?? "");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_DoesNotClassifyAmbiguousWindowsPdbOpenFailureAsInvalid()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-windows-ambiguous-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            Directory.CreateDirectory(root);
            File.WriteAllBytes(
                Path.Combine(root, artifact.PdbName),
                System.Text.Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"));

            var status = DiagnosePortableModule(
                artifact,
                root,
                nativeSupport: NativeSupportReady());

            Assert.False(status.LocalPdbReady);
            Assert.Equal("candidate_identity_unverified", status.LookupStatus);
            Assert.Contains("cannot distinguish", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("invalid", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_RejectsCorruptPortablePdbCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-corrupt-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, artifact.PdbName), "BSJB"u8.ToArray());

            var status = DiagnosePortableModule(artifact, root);

            Assert.False(status.LocalPdbReady);
            Assert.Equal("invalid_local_pdb_candidate", status.LookupStatus);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_ReportsCandidateIdentityUnverifiedForUnreadablePortablePdb()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-unavailable-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var candidate = Path.Combine(root, artifact.PdbName);
            Directory.CreateDirectory(root);
            File.Copy(artifact.PdbPath, candidate);

            using var exclusiveLease = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            var status = DiagnosePortableModule(artifact, root);

            Assert.False(status.LocalPdbReady);
            Assert.Equal("candidate_identity_unverified", status.LookupStatus);
            Assert.Equal(
                "module_metadata_and_local_pdb_identity_verification",
                status.EvidenceScope);
            Assert.Contains("unreadable", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("native DIA", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_DoesNotTreatDirectoryAsPdbCandidate()
    {
        var root = Path.Combine(
            Path.GetTempPath(), $"wpa-mcp-symbol-unopenable-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var candidate = Path.Combine(root, artifact.PdbName);
            Directory.CreateDirectory(candidate);

            var status = DiagnosePortableModule(artifact, root);

            Assert.False(status.LocalPdbReady);
            Assert.Equal("not_found_in_local_symbol_path", status.LookupStatus);
            Assert.DoesNotContain(
                candidate,
                status.LocalSymbolCandidates ?? Array.Empty<string>());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void NativeDiaSuggestion_UsesReportedServerArchitecture()
    {
        var support = new NativeSymbolSupportStatus(
            Architecture: "arm64",
            Msdia140Present: false,
            KernelTraceControlPresent: false,
            Status: "missing_native_dependency",
            Dependencies: [],
            Suggestion: @"Install native dependencies under C:\app\native\arm64.");

        var suggestion = SymbolTools.NativeDiaSuggestion(support);

        Assert.Contains("arm64", suggestion, StringComparison.Ordinal);
        Assert.DoesNotContain("amd64", suggestion, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnoseModule_DistinguishesMissingNativeReaderFromUnreadableCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-native-reader-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            Directory.CreateDirectory(root);
            File.WriteAllBytes(
                Path.Combine(root, artifact.PdbName),
                System.Text.Encoding.ASCII.GetBytes("Microsoft C/C++ MSF 7.00\r\n\u001aDS\0\0\0"));

            var status = DiagnosePortableModule(
                artifact,
                root,
                nativeSupport: NativeSupportMissing());

            Assert.False(status.LocalPdbReady);
            Assert.Equal("candidate_identity_unverified", status.LookupStatus);
            Assert.Equal(
                "module_metadata_and_local_pdb_identity_verification",
                status.EvidenceScope);
            Assert.Contains("native DIA reader", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("unreadable", status.FailureReason ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_ExactFlatCandidateWinsOverInvalidStoreCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"wpa-mcp-symbol-mixed-{Guid.NewGuid():N}");
        try
        {
            var artifact = PortablePdbBuildArtifact();
            var storeRoot = Path.Combine(root, "store");
            var flatRoot = Path.Combine(root, "flat");
            var storePdb = Path.Combine(
                storeRoot,
                artifact.PdbName,
                SymbolStoreKey(artifact.Signature, artifact.Age),
                artifact.PdbName);
            var flatPdb = Path.Combine(flatRoot, artifact.PdbName);
            Directory.CreateDirectory(Path.GetDirectoryName(storePdb)!);
            Directory.CreateDirectory(flatRoot);
            File.WriteAllBytes(storePdb, Array.Empty<byte>());
            File.Copy(artifact.PdbPath, flatPdb);

            var status = DiagnosePortableModule(
                artifact,
                $"cache*{storeRoot};{flatRoot}");

            Assert.True(status.LocalPdbReady);
            Assert.Equal("exact_identity_match", status.LookupStatus);
            Assert.Equal(
                "module_metadata_and_local_pdb_identity_verification",
                status.EvidenceScope);
            Assert.Contains(storePdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
            Assert.Contains(flatPdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
            Assert.Equal(flatPdb, status.LocalSymbolCandidates?[0]);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiagnoseModule_RejectsEmptyCacheSymbolStoreCandidate()
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

            Assert.Null(status.Resolved);
            Assert.Null(status.FrameCount);
            Assert.True(status.HasPdbName);
            Assert.True(status.HasCompletePdbIdentity);
            Assert.False(status.LocalPdbReady);
            Assert.Equal("not_measured", status.FrameResolutionState);
            Assert.Equal("invalid_local_pdb_candidate", status.LookupStatus);
            Assert.Contains(storePdb, status.LocalSymbolCandidates ?? Array.Empty<string>());
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EffectiveSymbolPathSnapshots_AreAtomicWithConcurrentUpdates()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            var tracePath = Path.GetFullPath("fixtures/small_cpu.etl");
            var traceDir = Path.GetDirectoryName(tracePath)!;
            var service = new SymbolService();
            var observed = new System.Collections.Concurrent.ConcurrentBag<string>();

            Parallel.For(0, 100, i =>
            {
                service.SetPath(i % 2 == 0 ? "A" : "B", append: false);
                observed.Add(SymbolPathState.GetEffectivePath(tracePath));
            });

            Assert.All(observed, path =>
                Assert.True(path == $"A;{traceDir}" || path == $"B;{traceDir}", path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
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

    private static NativeSymbolSupportStatus NativeSupportMissing()
        => new(
            Architecture: "amd64",
            Msdia140Present: false,
            KernelTraceControlPresent: true,
            Status: "missing_native_dependency",
            Dependencies: Array.Empty<NativeDependencyStatus>(),
            Suggestion: "Install native DIA support.");

    private static ModuleSymbolStatus DiagnosePortableModule(
        PortablePdbArtifact artifact,
        string symbolPath,
        Guid? expectedSignature = null,
        int? expectedAge = null,
        NativeSymbolSupportStatus? nativeSupport = null)
        => SymbolTools.DiagnoseModule(
            moduleName: Path.GetFileNameWithoutExtension(artifact.AssemblyPath) ?? "<unknown>",
            filePath: artifact.AssemblyPath,
            pdbName: artifact.PdbName,
            pdbSignature: expectedSignature ?? artifact.Signature,
            pdbAge: expectedAge ?? artifact.Age,
            binaryFormat: "PE",
            symbolPath: symbolPath,
            nativeSupport: nativeSupport ?? NativeSupportReady());

    private static PortablePdbArtifact PortablePdbBuildArtifact()
    {
        var assemblyPath = typeof(SymbolServiceTests).Assembly.Location;
        var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb")
            ?? throw new InvalidDataException("Could not derive the test PDB path.");
        Assert.True(File.Exists(pdbPath), $"Expected portable PDB beside test assembly: {pdbPath}");

        using (var pdbStream = File.OpenRead(pdbPath))
        {
            Span<byte> header = stackalloc byte[4];
            Assert.Equal(header.Length, pdbStream.Read(header));
            Assert.True(header.SequenceEqual("BSJB"u8), $"Expected a portable PDB: {pdbPath}");
        }

        using var assemblyStream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(assemblyStream);
        var codeViewEntry = Assert.Single(
            peReader.ReadDebugDirectory(),
            entry => entry.Type == DebugDirectoryEntryType.CodeView);
        var codeView = peReader.ReadCodeViewDebugDirectoryData(codeViewEntry);
        Assert.NotEqual(Guid.Empty, codeView.Guid);
        Assert.True(codeView.Age > 0);

        var pdbName = Path.GetFileName(codeView.Path)
            ?? throw new InvalidDataException("CodeView record did not contain a PDB file name.");
        return new PortablePdbArtifact(
            assemblyPath,
            pdbPath,
            pdbName,
            codeView.Guid,
            codeView.Age);
    }

    private sealed record PortablePdbArtifact(
        string AssemblyPath,
        string PdbPath,
        string PdbName,
        Guid Signature,
        int Age);

    private static string SymbolStoreKey(Guid signature, int age)
        => signature.ToString("N").ToUpperInvariant() + age.ToString("X");
}
