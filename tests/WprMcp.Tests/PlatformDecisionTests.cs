using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Dia2Lib;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace WprMcp.Tests;

public sealed class PlatformDecisionTests
{
    private static readonly string[] RequiredProbeNames =
    [
        "nuget-package-existence-hash",
        "normal-restore",
        "win-x64-restore",
        "release-build",
        "release-unit-tests",
        "golden-traceevent-reads",
        "windows-dia-pdb-resolution",
        "self-contained-publish",
        "self-contained-stdio",
        "native-layout",
        "selected-profile-handshake",
        "delegated-typed-tool-structured-output",
        "cancellation-progress-injection-schema",
        "raw-framing-request-id-guard-seam",
        "tools-list-output-schema",
        "windows-architecture-matrix",
    ];

    private static readonly string[] SdkSurfaceProbeNames =
    [
        "selected-profile-handshake",
        "delegated-typed-tool-structured-output",
        "cancellation-progress-injection-schema",
        "raw-framing-request-id-guard-seam",
    ];

    private static readonly string[] SdkSurfaceHostModes =
    [
        "normal",
        "win-x64-framework-dependent",
        "win-x64-self-contained",
    ];

    [Fact]
    public void CandidateMatrix_HasExactUniqueCandidateIdsAndVersions()
    {
        using var matrix = LoadMatrix();
        var candidates = matrix.RootElement.GetProperty("candidates").EnumerateArray().ToArray();

        Assert.Equal("1.0", matrix.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(
        [
            "net8-stable-stateful|8.0.420|net8.0|1.4.1|2025-11-25|stateful",
            "net10-stable-stateful|10.0.302|net10.0|1.4.1|2025-11-25|stateful",
            "net10-next-stateless|10.0.302|net10.0|2.0.0-rc.1|2026-07-28|stateless-discovery",
        ],
        candidates.Select(candidate => string.Join('|',
            candidate.GetProperty("id").GetString(),
            candidate.GetProperty("sdkVersion").GetString(),
            candidate.GetProperty("targetFramework").GetString(),
            candidate.GetProperty("mcpSdkVersion").GetString(),
            candidate.GetProperty("protocolRevision").GetString(),
            candidate.GetProperty("protocolProfile").GetString())));
        Assert.Equal(candidates.Length, candidates.Select(candidate => candidate.GetProperty("id").GetString()).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CandidateMatrix_CoversNet8Net10StableAndNet10NextProtocol()
    {
        using var matrix = LoadMatrix();
        var candidates = matrix.RootElement.GetProperty("candidates").EnumerateArray().ToArray();

        Assert.Contains(candidates, candidate => candidate.GetProperty("targetFramework").GetString() == "net8.0" && candidate.GetProperty("mcpSdkVersion").GetString() == "1.4.1");
        Assert.Contains(candidates, candidate => candidate.GetProperty("targetFramework").GetString() == "net10.0" && candidate.GetProperty("mcpSdkVersion").GetString() == "1.4.1");
        Assert.Contains(candidates, candidate => candidate.GetProperty("targetFramework").GetString() == "net10.0" && candidate.GetProperty("mcpSdkVersion").GetString() == "2.0.0-rc.1");
    }

    [Fact]
    public void CandidateMatrix_RecordsPlanDateOfficialEvidenceUrls()
    {
        using var matrix = LoadMatrix();
        var evidence = matrix.RootElement.GetProperty("planDateEvidence");

        Assert.Equal("2026-07-29", evidence.GetProperty("observedDate").GetString());
        Assert.Equal("https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core", evidence.GetProperty("dotnetSupportPolicyUrl").GetString());
        Assert.Equal("https://dotnet.microsoft.com/en-us/download/dotnet/10.0", evidence.GetProperty("dotnet10DownloadUrl").GetString());
        Assert.Equal("https://www.nuget.org/packages/ModelContextProtocol/1.4.1", evidence.GetProperty("mcpStablePackageUrl").GetString());
        Assert.Equal("https://www.nuget.org/packages/ModelContextProtocol/2.0.0-rc.1", evidence.GetProperty("mcpPrereleasePackageUrl").GetString());
    }

    [Fact]
    public void CandidateMatrix_DeclaresExactRequiredProbeSdkSubsetAndHostModeArrays()
    {
        using var matrix = LoadMatrix();

        Assert.Equal(RequiredProbeNames, ReadStringArray(matrix.RootElement, "requiredProbeNames"));
        Assert.Equal(SdkSurfaceProbeNames, ReadStringArray(matrix.RootElement, "sdkSurfaceProbeNames"));
        Assert.Equal(SdkSurfaceHostModes, ReadStringArray(matrix.RootElement, "sdkSurfaceHostModes"));
        Assert.Equal(SdkSurfaceProbeNames, RequiredProbeNames.Where(SdkSurfaceProbeNames.Contains));
    }

    [Fact]
    public void CandidateMatrix_DeclaresExactWindowsX64ArchitectureMatrix()
    {
        using var matrix = LoadMatrix();
        var architectures = matrix.RootElement.GetProperty("windowsArchitectureMatrix").EnumerateArray().ToArray();

        var architecture = Assert.Single(architectures);
        Assert.Equal("windows-x64", architecture.GetProperty("id").GetString());
        Assert.Equal("Windows", architecture.GetProperty("osPlatform").GetString());
        Assert.Equal("X64", architecture.GetProperty("osArchitecture").GetString());
        Assert.Equal("X64", architecture.GetProperty("processArchitecture").GetString());
        Assert.Equal("win-x64", architecture.GetProperty("runtimeIdentifier").GetString());
    }

    [Fact]
    public void CandidateRunner_DeclaresEveryRequiredProbe()
    {
        using var contract = InvokeRunnerJson("Get-PlatformRunnerContract | ConvertTo-Json -Depth 8 -Compress");

        Assert.Equal(RequiredProbeNames, ReadStringArray(contract.RootElement, "requiredProbeNames"));
    }

    [Fact]
    public void CandidateRunner_DeclaresExactSdkSurfaceProbeNamesAndHostModes()
    {
        using var contract = InvokeRunnerJson("Get-PlatformRunnerContract | ConvertTo-Json -Depth 8 -Compress");

        Assert.Equal(SdkSurfaceProbeNames, ReadStringArray(contract.RootElement, "sdkSurfaceProbeNames"));
        Assert.Equal(SdkSurfaceHostModes, ReadStringArray(contract.RootElement, "sdkSurfaceHostModes"));
    }

    [Fact]
    public void CandidateRunner_ReleaseUnitTestsExcludeOuterPlatformDecisionTests()
    {
        var source = File.ReadAllText(LocateRepoFile("scripts", "Test-PlatformCandidate.ps1"));
        var releaseUnitTests = Regex.Match(
            source,
            @"'release-unit-tests'\s*=\s*@\((?<arguments>[^)]*)\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        Assert.True(releaseUnitTests.Success, "The release-unit-tests command was not found.");
        Assert.Contains(
            "'--filter', 'FullyQualifiedName!~WprMcp.Tests.PlatformDecisionTests'",
            releaseUnitTests.Groups["arguments"].Value,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateRunner_IsExecutableUnderConstrainedLanguage()
    {
        var source = File.ReadAllText(LocateRepoFile("scripts", "Test-PlatformCandidate.ps1"));
        Assert.DoesNotMatch(new Regex(@"\[(?:IO\.(?:Path|File|Directory)|Environment)\]::", RegexOptions.CultureInvariant), source);
        Assert.DoesNotContain("::IsOSPlatform", source, StringComparison.Ordinal);
        Assert.DoesNotContain("New-Object Collections.Generic", source, StringComparison.Ordinal);
        Assert.DoesNotContain("$ordinaryCommands.GetEnumerator()", source, StringComparison.Ordinal);

        var directory = Path.Combine(Path.GetTempPath(), $"platform-output-root-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var path = directory.Replace("'", "''", StringComparison.Ordinal);
            var emptyPath = Path.Combine(directory, "empty.log").Replace("'", "''", StringComparison.Ordinal);
            var unicodePath = Path.Combine(directory, "unicode.json").Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell(
                $". '{script}' -CandidateId net8-stable-stateful; Write-NewUtf8File -Path '{emptyPath}' -Content ''; Write-NewUtf8File -Path '{unicodePath}' -Content 'matrix-✓'; [ordered]@{{ Root = Resolve-PlatformOutputRoot -Path '{path}'; EmptyLength = (Get-Item -LiteralPath '{emptyPath}').Length; UnicodeLength = (Get-Item -LiteralPath '{unicodePath}').Length; UnicodeFirstByte = [int](Get-Content -LiteralPath '{unicodePath}' -Encoding Byte -TotalCount 1); FileHash = (Get-FileHash -LiteralPath '{unicodePath}' -Algorithm SHA256).Hash }} | ConvertTo-Json -Compress",
                new Dictionary<string, string> { ["PSModulePath"] = Path.Combine(directory, "missing-modules") });
            Assert.True(result.ExitCode == 0, $"Constrained-language path resolution failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var evidence = JsonDocument.Parse(result.Stdout);
            Assert.Equal(directory, evidence.RootElement.GetProperty("Root").GetString(), ignoreCase: true);
            Assert.Equal(0, evidence.RootElement.GetProperty("EmptyLength").GetInt64());
            Assert.Equal(Encoding.UTF8.GetByteCount("matrix-✓"), evidence.RootElement.GetProperty("UnicodeLength").GetInt64());
            Assert.Equal((int)'m', evidence.RootElement.GetProperty("UnicodeFirstByte").GetInt32());
            Assert.Equal("matrix-✓", File.ReadAllText(unicodePath, Encoding.UTF8));
            Assert.Equal(Sha256(unicodePath), evidence.RootElement.GetProperty("FileHash").GetString(), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CandidateRunner_VerifiesNuGetPackageExistenceAndSha512BeforeRestore()
    {
        using var contract = InvokeRunnerJson("Get-CandidateExecutionPlan | ConvertTo-Json -Compress");
        var steps = contract.RootElement.EnumerateArray().Select(item => item.GetString()).ToArray();
        var source = File.ReadAllText(LocateRepoFile("scripts", "Test-PlatformCandidate.ps1"));

        Assert.Equal("nuget-package-existence-hash", steps[0]);
        Assert.True(Array.IndexOf(steps, "nuget-package-existence-hash") < Array.IndexOf(steps, "normal-restore"));
        Assert.True(Array.IndexOf(steps, "nuget-package-existence-hash") < Array.IndexOf(steps, "win-x64-restore"));
        Assert.DoesNotContain("--no-http-cache", source, StringComparison.Ordinal);
        Assert.Contains("'--no-cache'", source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateRunner_SelectsSemVer2NuGetRegistrationResource()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-nuget-service-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var servicePath = Path.Combine(directory, "index.json");
        File.WriteAllText(servicePath, JsonSerializer.Serialize(new
        {
            resources = new[]
            {
                new Dictionary<string, string> { ["@id"] = "https://example.invalid/registration-semver1/", ["@type"] = "RegistrationsBaseUrl" },
                new Dictionary<string, string> { ["@id"] = "https://example.invalid/flat/", ["@type"] = "PackageBaseAddress/3.0.0" },
                new Dictionary<string, string> { ["@id"] = "https://example.invalid/registration-semver2/", ["@type"] = "RegistrationsBaseUrl/3.6.0" },
            },
        }));

        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var serviceArg = servicePath.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net10-next-stateless; $service = Get-Content -LiteralPath '{serviceArg}' -Raw | ConvertFrom-Json; Get-NuGetPackageResourceEndpoints -Service $service | ConvertTo-Json -Compress");

            Assert.True(result.ExitCode == 0, $"NuGet resource selection failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var endpoints = JsonDocument.Parse(result.Stdout);
            Assert.Equal("https://example.invalid/registration-semver2/", endpoints.RootElement.GetProperty("registrationBase").GetString());
            Assert.Equal("https://example.invalid/flat/", endpoints.RootElement.GetProperty("packageBase").GetString());
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public void CandidateRunner_ResolvesRegistrationLeafCatalogAndRejectsVersionOrHashMismatch()
    {
        var fixture = WriteNuGetEvidenceFixture();
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var registration = fixture.RegistrationPath.Replace("'", "''", StringComparison.Ordinal);
            var catalog = fixture.CatalogPath.Replace("'", "''", StringComparison.Ordinal);
            var package = fixture.PackagePath.Replace("'", "''", StringComparison.Ordinal);
            var valid = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Test-NuGetCatalogEvidence -RegistrationPath '{registration}' -CatalogPath '{catalog}' -PackagePath '{package}' -ExpectedVersion '1.4.1' -DownloadedHashBase64 '{fixture.PackageHash}' | ConvertTo-Json -Compress");

            Assert.Equal(0, valid.ExitCode);
            using var evidence = JsonDocument.Parse(valid.Stdout);
            Assert.Equal("https://example.test/catalog/1.4.1.json", evidence.RootElement.GetProperty("catalogUrl").GetString());
            Assert.Equal("SHA512", evidence.RootElement.GetProperty("hashAlgorithm").GetString());
            Assert.Equal(fixture.PackageHash, evidence.RootElement.GetProperty("publishedHashBase64").GetString());

            var catalogNode = JsonNode.Parse(File.ReadAllText(fixture.CatalogPath))!.AsObject();
            catalogNode["version"] = "1.4.2";
            File.WriteAllText(fixture.CatalogPath, catalogNode.ToJsonString());
            Assert.NotEqual(0, RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Test-NuGetCatalogEvidence -RegistrationPath '{registration}' -CatalogPath '{catalog}' -PackagePath '{package}' -ExpectedVersion '1.4.1' -DownloadedHashBase64 '{fixture.PackageHash}'").ExitCode);

            catalogNode["version"] = "1.4.1";
            catalogNode["packageHash"] = Convert.ToBase64String(new byte[64]);
            File.WriteAllText(fixture.CatalogPath, catalogNode.ToJsonString());
            Assert.NotEqual(0, RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Test-NuGetCatalogEvidence -RegistrationPath '{registration}' -CatalogPath '{catalog}' -PackagePath '{package}' -ExpectedVersion '1.4.1' -DownloadedHashBase64 '{fixture.PackageHash}'").ExitCode);
        }
        finally
        {
            Directory.Delete(fixture.Directory, recursive: true);
        }
    }

    [Fact]
    public async Task CandidateRunner_ReusesVerifiedNuGetCacheWithoutRewritingRetainedBytes()
    {
        var fixture = WriteNuGetEvidenceFixture();
        var cache = Path.Combine(Path.GetTempPath(), $"platform-nuget-cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(cache);
        var files = new[]
        {
            (fixture.RegistrationPath, Path.Combine(cache, "modelcontextprotocol.1.4.1.registration.json")),
            (fixture.CatalogPath, Path.Combine(cache, "modelcontextprotocol.1.4.1.catalog.json")),
            (fixture.PackagePath, Path.Combine(cache, "modelcontextprotocol.1.4.1.nupkg")),
        };
        foreach (var file in files)
        {
            File.Copy(file.Item1, file.Item2);
        }
        var metadataPath = Path.Combine(cache, "modelcontextprotocol.1.4.1.verification.json");
        File.WriteAllText(metadataPath, JsonSerializer.Serialize(new
        {
            packageId = "ModelContextProtocol",
            packageVersion = "1.4.1",
            registrationUrl = "https://example.test/registration/1.4.1.json",
            catalogUrl = "https://example.test/catalog/1.4.1.json",
            packageContentUrl = "https://example.test/package/1.4.1.nupkg",
            hashAlgorithm = "SHA512",
            publishedHashBase64 = fixture.PackageHash,
            downloadedHashBase64 = fixture.PackageHash,
            observedUtc = "2026-07-29T00:00:00.0000000+00:00",
            retrievalSource = "Network",
        }));

        try
        {
            var retained = Directory.GetFiles(cache).ToDictionary(path => path, Sha256, StringComparer.OrdinalIgnoreCase);
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var cacheArg = cache.Replace("'", "''", StringComparison.Ordinal);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var candidateEvidence = Path.Combine(Path.GetTempPath(), $"platform-candidate-nuget-{Guid.NewGuid():N}");
                var candidateEvidenceArg = candidateEvidence.Replace("'", "''", StringComparison.Ordinal);
                var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $verified = Get-VerifiedNuGetPackage -PackageVersion '1.4.1' -CacheDirectory '{cacheArg}' -OfflineOnly; $retained = Copy-VerifiedNuGetEvidence -VerifiedPackage $verified -Destination '{candidateEvidenceArg}'; [ordered]@{{ Verification = $verified.Verification; Retained = $retained }} | ConvertTo-Json -Depth 6 -Compress");
                Assert.True(result.ExitCode == 0, $"Cache reuse failed. stdout: {result.Stdout} stderr: {result.Stderr}");
                using var evidence = JsonDocument.Parse(result.Stdout);
                var verification = evidence.RootElement.GetProperty("Verification");
                Assert.Equal("VerifiedCache", verification.GetProperty("retrievalSource").GetString());
                Assert.NotEqual("2026-07-29T00:00:00.0000000+00:00", verification.GetProperty("observedUtc").GetString());
                Assert.True(DateTimeOffset.TryParse(verification.GetProperty("observedUtc").GetString(), out _));
                Assert.Equal(retained, Directory.GetFiles(cache).ToDictionary(path => path, Sha256, StringComparer.OrdinalIgnoreCase));
                var retainedPaths = evidence.RootElement.GetProperty("Retained");
                var copied = retainedPaths.EnumerateObject().Select(property => property.Value.GetString()!).ToArray();
                Assert.Equal(4, copied.Length);
                Assert.All(copied, path => Assert.StartsWith(candidateEvidence, path, StringComparison.OrdinalIgnoreCase));
                Assert.Equal(retained[files[0].Item2], Sha256(retainedPaths.GetProperty("RegistrationPath").GetString()!));
                Assert.Equal(retained[files[1].Item2], Sha256(retainedPaths.GetProperty("CatalogPath").GetString()!));
                Assert.Equal(retained[files[2].Item2], Sha256(retainedPaths.GetProperty("PackagePath").GetString()!));
                using var candidateMetadata = JsonDocument.Parse(File.ReadAllText(retainedPaths.GetProperty("MetadataPath").GetString()!));
                Assert.Equal("VerifiedCache", candidateMetadata.RootElement.GetProperty("retrievalSource").GetString());
                Assert.Equal(verification.GetProperty("observedUtc").GetString(), candidateMetadata.RootElement.GetProperty("observedUtc").GetString());
                await DeleteDirectoryEventuallyAsync(candidateEvidence);
            }
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(cache);
            await DeleteDirectoryEventuallyAsync(fixture.Directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_RestoreConsumesVerifiedCandidateLocalPackageBytes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"platform-restore-evidence-{Guid.NewGuid():N}");
        var source = Path.Combine(root, "candidate-source");
        var packages = Path.Combine(root, "packages");
        var installed = Path.Combine(packages, "modelcontextprotocol", "1.4.1");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(installed);
        var verifiedPackage = Path.Combine(source, "modelcontextprotocol.1.4.1.nupkg");
        var restoredPackage = Path.Combine(installed, "modelcontextprotocol.1.4.1.nupkg");
        var restoredSha512 = Path.Combine(installed, "modelcontextprotocol.1.4.1.nupkg.sha512");
        var restoredMetadata = Path.Combine(installed, ".nupkg.metadata");
        var config = Path.Combine(root, "NuGet.Config");
        var evidence = Path.Combine(root, "normal-restore.package.evidence.json");
        File.WriteAllBytes(verifiedPackage, [1, 3, 3, 7]);
        File.Copy(verifiedPackage, restoredPackage);
        var publishedHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(verifiedPackage)));
        var restoreContentHash = Convert.ToBase64String(SHA512.HashData([8, 6, 7, 5, 3, 0, 9]));
        File.WriteAllText(restoredSha512, publishedHash);
        File.WriteAllText(restoredMetadata, JsonSerializer.Serialize(new { version = 2, contentHash = restoreContentHash, source }));

        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var sourceArg = source.Replace("'", "''", StringComparison.Ordinal);
            var packagesArg = packages.Replace("'", "''", StringComparison.Ordinal);
            var verifiedArg = verifiedPackage.Replace("'", "''", StringComparison.Ordinal);
            var configArg = config.Replace("'", "''", StringComparison.Ordinal);
            var evidenceArg = evidence.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Write-CandidateNuGetConfig -Path '{configArg}' -CandidateSource '{sourceArg}'; New-RestorePackageEvidence -ProbeName 'normal-restore' -PackageVersion '1.4.1' -PackagesDirectory '{packagesArg}' -VerifiedPackagePath '{verifiedArg}' -CandidateSource '{sourceArg}' -ConfigPath '{configArg}' -PublishedHashBase64 '{publishedHash}' -EvidencePath '{evidenceArg}' | ConvertTo-Json -Depth 6 -Compress");

            Assert.True(result.ExitCode == 0, $"Restore evidence creation failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var restoreEvidence = JsonDocument.Parse(result.Stdout);
            Assert.True(restoreEvidence.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal("normal-restore", restoreEvidence.RootElement.GetProperty("probeName").GetString());
            Assert.Equal(source, restoreEvidence.RootElement.GetProperty("metadataSource").GetString(), ignoreCase: true);
            Assert.Equal(restoreContentHash, restoreEvidence.RootElement.GetProperty("restoreContentHashBase64").GetString());
            Assert.Equal(Sha256(verifiedPackage), restoreEvidence.RootElement.GetProperty("verifiedPackageSha256").GetString());
            Assert.Equal(Sha256(restoredPackage), restoreEvidence.RootElement.GetProperty("restoredPackageSha256").GetString());
            var nugetConfig = XDocument.Load(config);
            Assert.Equal(source, nugetConfig.Descendants("packageSources").Elements("add").Single(element => (string?)element.Attribute("key") == "verified-candidate").Attribute("value")?.Value);
            Assert.Equal("ModelContextProtocol", nugetConfig.Descendants("packageSource").Single(element => (string?)element.Attribute("key") == "verified-candidate").Element("package")?.Attribute("pattern")?.Value);

            File.WriteAllText(restoredMetadata, JsonSerializer.Serialize(new { version = 2, contentHash = "not-a-sha512", source }));
            var invalidContentHashEvidenceArg = Path.Combine(root, "invalid-content-hash.evidence.json").Replace("'", "''", StringComparison.Ordinal);
            Assert.NotEqual(0, RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; New-RestorePackageEvidence -ProbeName 'normal-restore' -PackageVersion '1.4.1' -PackagesDirectory '{packagesArg}' -VerifiedPackagePath '{verifiedArg}' -CandidateSource '{sourceArg}' -ConfigPath '{configArg}' -PublishedHashBase64 '{publishedHash}' -EvidencePath '{invalidContentHashEvidenceArg}'").ExitCode);

            File.WriteAllText(restoredMetadata, JsonSerializer.Serialize(new { version = 2, contentHash = restoreContentHash, source }));
            File.WriteAllBytes(restoredPackage, [9, 9, 9]);
            var tamperedEvidenceArg = Path.Combine(root, "tampered.evidence.json").Replace("'", "''", StringComparison.Ordinal);
            Assert.NotEqual(0, RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; New-RestorePackageEvidence -ProbeName 'normal-restore' -PackageVersion '1.4.1' -PackagesDirectory '{packagesArg}' -VerifiedPackagePath '{verifiedArg}' -CandidateSource '{sourceArg}' -ConfigPath '{configArg}' -PublishedHashBase64 '{publishedHash}' -EvidencePath '{tamperedEvidenceArg}'").ExitCode);
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(root);
        }
    }

    [Fact]
    public void CandidateRunner_UsesIsolatedOutputAndDoesNotEditTrackedFiles()
    {
        using var contract = InvokeRunnerJson("Get-PlatformRunnerContract | ConvertTo-Json -Depth 8 -Compress");
        var root = contract.RootElement;

        Assert.Equal("temporary-copied-worktree", root.GetProperty("workspaceMode").GetString());
        Assert.False(root.GetProperty("editsCallerTrackedFiles").GetBoolean());
        Assert.Equal("CreateNew", root.GetProperty("resultFileMode").GetString());
    }

    [Fact]
    public async Task CandidateRunner_ReplacesOnlyMaterializedGlobalJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-global-{Guid.NewGuid():N}");
        var workspace = Path.Combine(directory, "worktree");
        Directory.CreateDirectory(workspace);
        var global = Path.Combine(workspace, "global.json");
        File.WriteAllText(global, "tracked selection");
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var workspaceArg = workspace.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $path = Write-CandidateGlobalJson -Workspace '{workspaceArg}' -Content 'candidate override'; [ordered]@{{ Path = $path; Content = [string](Get-Content -LiteralPath $path -Raw); FirstByte = [int](Get-Content -LiteralPath $path -Encoding Byte -TotalCount 1) }} | ConvertTo-Json -Compress");

            Assert.True(result.ExitCode == 0, $"Candidate global replacement failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var evidence = JsonDocument.Parse(result.Stdout);
            Assert.Equal(Path.GetFullPath(global), evidence.RootElement.GetProperty("Path").GetString());
            Assert.Equal("candidate override", evidence.RootElement.GetProperty("Content").GetString());
            Assert.Equal((int)'c', evidence.RootElement.GetProperty("FirstByte").GetInt32());

            var createNew = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Write-NewUtf8File -Path '{global.Replace("'", "''", StringComparison.Ordinal)}' -Content 'must fail'");
            Assert.NotEqual(0, createNew.ExitCode);
            Assert.Equal("candidate override", File.ReadAllText(global));
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_MaterializesCommittedHeadInsteadOfDirtyWorkingTree()
    {
        var repository = LocateRepoRoot();
        var directory = Path.Combine(repository, ".superpowers", $"platform-source-{Guid.NewGuid():N}");
        var destination = Path.Combine(directory, "materialized");
        var sentinelName = $"platform-untracked-{Guid.NewGuid():N}.sentinel";
        var sentinelPath = Path.Combine(repository, sentinelName);
        Directory.CreateDirectory(directory);
        try
        {
            var commit = RunProcess("git", ["rev-parse", "HEAD"], repository);
            Assert.Equal(0, commit.ExitCode);
            Assert.Matches("^[0-9a-f]{40}$", commit.Stdout);
            File.WriteAllText(sentinelPath, "must not be archived");

            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var repoArg = repository.Replace("'", "''", StringComparison.Ordinal);
            var destinationArg = destination.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Copy-TrackedCommit -RepositoryRoot '{repoArg}' -Commit '{commit.Stdout}' -Destination '{destinationArg}' | ConvertTo-Json -Compress");

            Assert.True(result.ExitCode == 0, $"Materialization failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var materialization = JsonDocument.Parse(result.Stdout);
            Assert.Equal(commit.Stdout, materialization.RootElement.GetProperty("commit").GetString());
            Assert.Equal(commit.Stdout, materialization.RootElement.GetProperty("archiveTreeish").GetString());
            foreach (var relativePath in new[] { "scripts/Test-PlatformCandidate.ps1", "eng/platform-candidates.v1.json" })
            {
                var expected = RunProcess("git", ["show", $"{commit.Stdout}:{relativePath}"], repository);
                Assert.Equal(0, expected.ExitCode);
                Assert.Equal(expected.Stdout, File.ReadAllText(Path.Combine(destination, relativePath.Replace('/', Path.DirectorySeparatorChar))).Trim());
            }
            Assert.False(File.Exists(Path.Combine(destination, sentinelName)));
            Assert.False(Directory.Exists(Path.Combine(destination, ".git")));
        }
        finally
        {
            File.Delete(sentinelPath);
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_TimesOutCommandsAndRetainsStageDiagnostics()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-timeout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var working = directory.Replace("'", "''", StringComparison.Ordinal);
            var log = Path.Combine(directory, "timeout").Replace("'", "''", StringComparison.Ordinal);
            var stopwatch = Stopwatch.StartNew();
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Invoke-CapturedCommand ping.exe @('127.0.0.1','-n','30','-w','1000') '{working}' '{log}' @{{}} -TimeoutSeconds 1 -Stage 'timeout-regression' | ConvertTo-Json -Compress");
            stopwatch.Stop();

            Assert.True(result.ExitCode == 0, $"Timeout probe failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15), $"Timeout was not bounded: {stopwatch.Elapsed}");
            using var evidence = JsonDocument.Parse(result.Stdout);
            Assert.Equal(124, evidence.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.True(evidence.RootElement.GetProperty("TimedOut").GetBoolean());
            Assert.Contains("timeout-regression timed out after 1 seconds", File.ReadAllText(Path.Combine(directory, "timeout.stderr.log")), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_CapturesRedirectedNativeExitCode()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-exit-code-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var working = directory.Replace("'", "''", StringComparison.Ordinal);
            var log = Path.Combine(directory, "exit-code").Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Invoke-CapturedCommand cmd.exe @('/d','/c','exit','7') '{working}' '{log}' @{{}} -Stage 'exit-code-regression' | ConvertTo-Json -Compress");

            Assert.True(result.ExitCode == 0, $"Exit-code probe failed. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var evidence = JsonDocument.Parse(result.Stdout);
            Assert.Equal(7, evidence.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.False(evidence.RootElement.GetProperty("TimedOut").GetBoolean());
            Assert.False(evidence.RootElement.GetProperty("StartFailed").GetBoolean());
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_RetainsStructuredFailureWhenProcessCannotStart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-start-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var working = directory.Replace("'", "''", StringComparison.Ordinal);
            var log = Path.Combine(directory, "start-failure").Replace("'", "''", StringComparison.Ordinal);
            var missing = Path.Combine(directory, "missing-command.exe").Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; Invoke-CapturedCommand '{missing}' @() '{working}' '{log}' @{{}} -Stage 'launch-regression' | ConvertTo-Json -Compress");
            Assert.Equal(0, result.ExitCode);
            using var evidence = JsonDocument.Parse(result.Stdout);
            Assert.Equal(125, evidence.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.True(evidence.RootElement.GetProperty("StartFailed").GetBoolean());
            Assert.Contains("launch-regression", File.ReadAllText(Path.Combine(directory, "start-failure.stderr.log")), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task CandidateRunner_RefusesToReuseEvidenceRoot()
    {
        var parent = Path.Combine(Path.GetTempPath(), $"platform-fresh-root-{Guid.NewGuid():N}");
        var directory = Path.Combine(parent, "candidate");
        Directory.CreateDirectory(parent);
        var sentinel = Path.Combine(directory, "sentinel.txt");
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var path = directory.Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; New-FreshDirectory '{path}'; Set-Content -LiteralPath '{path}\\sentinel.txt' -Value 'preserve'; try {{ New-FreshDirectory '{path}'; exit 9 }} catch {{ if (Test-Path -LiteralPath '{path}\\sentinel.txt') {{ exit 0 }} else {{ exit 8 }} }}");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("preserve", File.ReadAllText(sentinel).Trim());
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(parent);
        }
    }

    [Fact]
    public async Task CandidateValidator_RejectsArtifactThroughJunctionAncestor()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-reparse-{Guid.NewGuid():N}");
        var root = Path.Combine(directory, "candidate");
        var outside = Path.Combine(directory, "outside");
        var junction = Path.Combine(root, "junction");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "artifact.bin"), "outside");
        try
        {
            var created = RunProcess(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", ["/d", "/c", "mklink", "/J", junction, outside], directory);
            Assert.Equal(0, created.ExitCode);
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var rootArg = root.Replace("'", "''", StringComparison.Ordinal);
            var artifactArg = Path.Combine(junction, "artifact.bin").Replace("'", "''", StringComparison.Ordinal);
            var result = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; if (Test-ContainedPathWithoutReparsePoint -Path '{artifactArg}' -Root '{rootArg}') {{ exit 9 }} else {{ exit 0 }}");
            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public void CandidateResult_RecordsCommandsExitCodesHashesAndCommit()
    {
        using var contract = InvokeRunnerJson("Get-PlatformRunnerContract | ConvertTo-Json -Depth 8 -Compress");

        Assert.Equal(
        ["schemaVersion", "candidateId", "sdkVersion", "targetFramework", "mcpSdkVersion", "protocolRevision", "protocolProfile", "commit", "startedUtc", "completedUtc", "probes"],
            ReadStringArray(contract.RootElement, "resultFields"));
        Assert.Equal(
        ["name", "command", "exitCode", "stdoutSha256", "stderrSha256", "passed", "artifactSha256", "cases", "nuGetPackage"],
            ReadStringArray(contract.RootElement, "probeFields"));
        Assert.Equal(
        ["hostMode", "scenario", "command", "exitCode", "stdoutSha256", "stderrSha256", "passed", "failureStage", "artifactSha256"],
            ReadStringArray(contract.RootElement, "caseFields"));
    }

    [Fact]
    public void CandidateResult_RecordsExactProbeCasesAndRejectsMissingDuplicateOrExtraModes()
    {
        var validPath = WriteResultFixture(SdkSurfaceHostModes);
        var missingPath = WriteResultFixture(SdkSurfaceHostModes[..2]);
        var duplicatePath = WriteResultFixture([SdkSurfaceHostModes[0], SdkSurfaceHostModes[0], SdkSurfaceHostModes[2]]);
        var extraPath = WriteResultFixture([.. SdkSurfaceHostModes, "unexpected"]);

        try
        {
            Assert.Equal(0, InvokeResultValidation(validPath));
            Assert.NotEqual(0, InvokeResultValidation(missingPath));
            Assert.NotEqual(0, InvokeResultValidation(duplicatePath));
            Assert.NotEqual(0, InvokeResultValidation(extraPath));
        }
        finally
        {
            DeleteResultFixture(validPath);
            DeleteResultFixture(missingPath);
            DeleteResultFixture(duplicatePath);
            DeleteResultFixture(extraPath);
        }
    }

    [Fact]
    public void CandidateResult_RejectsIncompleteGoldenTraceEventEvidenceEvenWhenArtifactHashIsUpdated()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "golden-traceevent-reads")!;
            var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("golden-traceevent-reads.evidence.json", StringComparison.Ordinal));
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            evidence["fixtures"]!.AsArray().RemoveAt(5);
            File.WriteAllText(evidencePath, evidence.ToJsonString());
            var updatedHash = Sha256(evidencePath);
            probe["artifactSha256"]![evidencePath] = updatedHash;
            probe["cases"]![0]!["artifactSha256"]![evidencePath] = updatedHash;
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsNativeLoadPlaceholderEvenWhenArtifactHashIsUpdated()
    {
        var nativePath = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var nativeResult = JsonNode.Parse(File.ReadAllText(nativePath))!.AsObject();
            var nativeProbe = nativeResult["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "native-layout")!;
            var nativeEvidencePath = nativeProbe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("native-layout.evidence.json", StringComparison.Ordinal));
            var nativeEvidence = JsonNode.Parse(File.ReadAllText(nativeEvidencePath))!.AsObject();
            nativeEvidence["dependencies"]![0]!["loaded"] = false;
            File.WriteAllText(nativeEvidencePath, nativeEvidence.ToJsonString());
            var nativeHash = Sha256(nativeEvidencePath);
            nativeProbe["artifactSha256"]![nativeEvidencePath] = nativeHash;
            nativeProbe["cases"]![0]!["artifactSha256"]![nativeEvidencePath] = nativeHash;
            File.WriteAllText(nativePath, nativeResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(nativePath));
        }
        finally
        {
            DeleteResultFixture(nativePath);
        }
    }

    [Fact]
    public void CandidateResult_RejectsDiaRvaPlaceholderEvenWhenArtifactHashIsUpdated()
    {
        var diaPath = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var diaResult = JsonNode.Parse(File.ReadAllText(diaPath))!.AsObject();
            var diaProbe = diaResult["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "windows-dia-pdb-resolution")!;
            var diaEvidencePath = diaProbe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("windows-dia-pdb-resolution.evidence.json", StringComparison.Ordinal));
            var diaEvidence = JsonNode.Parse(File.ReadAllText(diaEvidencePath))!.AsObject();
            diaEvidence["resolvedName"] = string.Empty;
            File.WriteAllText(diaEvidencePath, diaEvidence.ToJsonString());
            var diaHash = Sha256(diaEvidencePath);
            diaProbe["artifactSha256"]![diaEvidencePath] = diaHash;
            diaProbe["cases"]![0]!["artifactSha256"]![diaEvidencePath] = diaHash;
            File.WriteAllText(diaPath, diaResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(diaPath));
        }
        finally
        {
            DeleteResultFixture(diaPath);
        }
    }

    [Fact]
    public void CandidateResult_RejectsDisconnectedArchitectureObservationEvenWhenArtifactHashIsUpdated()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "windows-architecture-matrix")!;
            var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("windows-architecture-matrix.evidence.json", StringComparison.Ordinal));
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            evidence["observations"]![0]!["processArchitecture"] = "Arm64";
            File.WriteAllText(evidencePath, evidence.ToJsonString());
            var updatedHash = Sha256(evidencePath);
            probe["artifactSha256"]![evidencePath] = updatedHash;
            probe["cases"]![0]!["artifactSha256"]![evidencePath] = updatedHash;
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsFailedArchitectureProbeWithPassingEvidence()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "windows-architecture-matrix")!;
            probe["exitCode"] = 1;
            probe["passed"] = false;
            probe["cases"]![0]!["exitCode"] = 1;
            probe["cases"]![0]!["passed"] = false;
            probe["cases"]![0]!["failureStage"] = "probe";
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateRunner_ValidatesArchitectureEvidenceBeforeCreateNewWrite()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "windows-architecture-matrix")!;
            var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("windows-architecture-matrix.evidence.json", StringComparison.Ordinal));
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var evidence = evidencePath.Replace("'", "''", StringComparison.Ordinal);
            var candidateRoot = Path.GetDirectoryName(evidencePath)!.Replace("'", "''", StringComparison.Ordinal);
            var validation = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $matrix = Get-PlatformMatrix; $value = Get-Content -LiteralPath '{evidence}' -Raw | ConvertFrom-Json; if (-not (Test-WindowsArchitectureEvidence -Evidence $value -CandidateRoot '{candidateRoot}' -Matrix $matrix)) {{ exit 3 }}; $value.observations[0].processArchitecture = 'Arm64'; if (Test-WindowsArchitectureEvidence -Evidence $value -CandidateRoot '{candidateRoot}' -Matrix $matrix) {{ exit 4 }}");

            Assert.True(validation.ExitCode == 0, $"In-memory architecture validation failed. stdout: {validation.Stdout} stderr: {validation.Stderr}");
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsEmptySuccessfulSdkAndSchemaAggregateArtifactMaps()
    {
        var sdkPath = WriteResultFixture(SdkSurfaceHostModes);
        var schemaPath = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var sdkResult = JsonNode.Parse(File.ReadAllText(sdkPath))!.AsObject();
            var sdkProbe = sdkResult["probes"]!.AsArray().First(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal))!;
            sdkProbe["artifactSha256"]!.AsObject().Clear();
            File.WriteAllText(sdkPath, sdkResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(sdkPath));

            var schemaResult = JsonNode.Parse(File.ReadAllText(schemaPath))!.AsObject();
            var schemaProbe = schemaResult["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "tools-list-output-schema")!;
            schemaProbe["artifactSha256"]!.AsObject().Clear();
            File.WriteAllText(schemaPath, schemaResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(schemaPath));
        }
        finally
        {
            DeleteResultFixture(sdkPath);
            DeleteResultFixture(schemaPath);
        }
    }

    [Fact]
    public void CandidateResult_RejectsNuGetMetadataThatDoesNotMatchTheCandidate()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            Assert.Equal(0, InvokeResultValidation(path));
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "nuget-package-existence-hash")!;
            probe["nuGetPackage"]!["packageVersion"] = "9.9.9";
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_AcceptsExactPlanNuGetPackageContractWithoutCatalogUrl()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "nuget-package-existence-hash")!;
            var verification = probe["nuGetPackage"]!.AsObject();
            Assert.False(verification.ContainsKey("catalogUrl"));
            Assert.Equal(
                [
                    "packageId", "packageVersion", "registrationUrl", "packageContentUrl", "hashAlgorithm",
                    "publishedHashBase64", "downloadedHashBase64", "observedUtc", "retrievalSource",
                ],
                verification.Select(property => property.Key));
            File.WriteAllText(path, result.ToJsonString());

            Assert.Equal(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsSuccessfulRestoreWhenNuGetVerificationProbeFailed()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "nuget-package-existence-hash")!;
            probe["exitCode"] = 1;
            probe["passed"] = false;
            probe["cases"]![0]!["exitCode"] = 1;
            probe["cases"]![0]!["passed"] = false;
            probe["cases"]![0]!["failureStage"] = "probe";
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsRestoreEvidenceDisconnectedFromTheVerifiedSourceEvenWhenRehashed()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            Assert.Equal(0, InvokeResultValidation(path));
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "normal-restore")!;
            var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("normal-restore.package.evidence.json", StringComparison.Ordinal));
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            evidence["metadataSource"] = Path.Combine(Path.GetDirectoryName(evidencePath)!, "unverified-source");
            File.WriteAllText(evidencePath, evidence.ToJsonString());
            var updatedHash = Sha256(evidencePath);
            probe["artifactSha256"]![evidencePath] = updatedHash;
            probe["cases"]![0]!["artifactSha256"]![evidencePath] = updatedHash;
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_AllowsDistinctRestoreContentHashButRejectsDisconnectedMetadataEvenWhenRehashed()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            Assert.Equal(0, InvokeResultValidation(path));
            var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "normal-restore")!;
            var probeCase = probe["cases"]![0]!;
            var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("normal-restore.package.evidence.json", StringComparison.Ordinal));
            var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
            var metadataPath = evidence["restoredMetadataPath"]!.GetValue<string>();
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
            var restoreContentHash = Convert.ToBase64String(SHA512.HashData([2, 7, 1, 8, 2, 8]));

            metadata["contentHash"] = restoreContentHash;
            evidence["restoreContentHashBase64"] = restoreContentHash;
            var ridProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "win-x64-restore")!;
            var ridEvidencePath = ridProbe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("win-x64-restore.package.evidence.json", StringComparison.Ordinal));
            var ridEvidence = JsonNode.Parse(File.ReadAllText(ridEvidencePath))!.AsObject();
            ridEvidence["restoreContentHashBase64"] = restoreContentHash;
            File.WriteAllText(metadataPath, metadata.ToJsonString());
            File.WriteAllText(evidencePath, evidence.ToJsonString());
            File.WriteAllText(ridEvidencePath, ridEvidence.ToJsonString());
            foreach (var artifactPath in new[] { metadataPath, evidencePath, ridEvidencePath })
            {
                var hash = Sha256(artifactPath);
                foreach (var resultProbe in result["probes"]!.AsArray())
                {
                    var aggregateArtifacts = resultProbe!["artifactSha256"]!.AsObject();
                    if (aggregateArtifacts.ContainsKey(artifactPath)) aggregateArtifacts[artifactPath] = hash;
                    foreach (var resultCase in resultProbe["cases"]!.AsArray())
                    {
                        var caseArtifacts = resultCase!["artifactSha256"]!.AsObject();
                        if (caseArtifacts.ContainsKey(artifactPath)) caseArtifacts[artifactPath] = hash;
                    }
                }
            }
            File.WriteAllText(path, result.ToJsonString());

            Assert.Equal(0, InvokeResultValidation(path));

            evidence["restoreContentHashBase64"] = Convert.ToBase64String(SHA512.HashData([1, 6, 1, 8, 0, 3]));
            File.WriteAllText(evidencePath, evidence.ToJsonString());
            var disconnectedHash = Sha256(evidencePath);
            probe["artifactSha256"]![evidencePath] = disconnectedHash;
            probeCase["artifactSha256"]![evidencePath] = disconnectedHash;
            File.WriteAllText(path, result.ToJsonString());

            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_RejectsCaseExitHashAndRetainedArtifactTampering()
    {
        var path = WriteResultFixture(SdkSurfaceHostModes);
        var original = File.ReadAllText(path);
        try
        {
            Assert.Equal(0, InvokeResultValidation(path));
            var result = JsonNode.Parse(original)!.AsObject();
            result["probes"]![0]!["cases"]![0]!["exitCode"] = 7;
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            result = JsonNode.Parse(original)!.AsObject();
            result["probes"]![0]!["cases"]![0]!["stdoutSha256"] = new string('f', 64);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(path, original);
            var artifact = JsonNode.Parse(original)!["probes"]![0]!["cases"]![0]!["artifactSha256"]!.AsObject().First().Key;
            File.Delete(artifact);
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(path, original);
            var externalArtifact = Path.Combine(Path.GetTempPath(), $"external-platform-artifact-{Guid.NewGuid():N}.bin");
            try
            {
                File.WriteAllBytes(artifact, [1, 2, 3, 4]);
                File.WriteAllBytes(externalArtifact, [5, 6, 7]);
                result = JsonNode.Parse(original)!.AsObject();
                var artifactMap = result["probes"]![0]!["cases"]![0]!["artifactSha256"]!.AsObject();
                artifactMap.Clear();
                artifactMap[externalArtifact] = Sha256(externalArtifact);
                File.WriteAllText(path, result.ToJsonString());
                Assert.NotEqual(0, InvokeResultValidation(path));
            }
            finally
            {
                File.Delete(externalArtifact);
            }

            File.WriteAllText(path, original);
            result = JsonNode.Parse(original)!.AsObject();
            var stdioProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "self-contained-stdio")!;
            var stdioEvidencePath = stdioProbe["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("self-contained-stdio.evidence.json", StringComparison.Ordinal));
            var originalStdioEvidence = File.ReadAllText(stdioEvidencePath);
            var stdioEvidence = JsonNode.Parse(originalStdioEvidence)!.AsObject();
            stdioEvidence["orderedMessageMethodTranscript"] = new JsonArray("initialize", "tools/list", "tools/call");
            File.WriteAllText(stdioEvidencePath, stdioEvidence.ToJsonString());
            var tamperedEvidenceHash = Sha256(stdioEvidencePath);
            stdioProbe["artifactSha256"]![stdioEvidencePath] = tamperedEvidenceHash;
            stdioProbe["cases"]![0]!["artifactSha256"]![stdioEvidencePath] = tamperedEvidenceHash;
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(stdioEvidencePath, originalStdioEvidence);
            File.WriteAllText(path, original);
            result = JsonNode.Parse(original)!.AsObject();
            var sdkProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
            var sdkCase = sdkProbe["cases"]![0]!;
            var sdkEvidencePath = sdkCase["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("selected-profile-handshake.normal.evidence.json", StringComparison.Ordinal));
            var originalSdkEvidence = File.ReadAllText(sdkEvidencePath);
            var sdkEvidence = JsonNode.Parse(originalSdkEvidence)!.AsObject();
            sdkEvidence["launchIdentity"]!["runtimeIdentity"]!["processId"] = 999;
            File.WriteAllText(sdkEvidencePath, sdkEvidence.ToJsonString());
            sdkCase["artifactSha256"]![sdkEvidencePath] = Sha256(sdkEvidencePath);
            RebuildAggregateArtifacts(sdkProbe);
            RefreshArchitectureEvidence(result);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(sdkEvidencePath, originalSdkEvidence);
            result = JsonNode.Parse(original)!.AsObject();
            sdkProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
            var selfContainedCase = sdkProbe["cases"]!.AsArray().Single(node => node!["hostMode"]!.GetValue<string>() == "win-x64-self-contained")!;
            var selfContainedEvidencePath = selfContainedCase["artifactSha256"]!.AsObject().Select(property => property.Key)
                .Single(candidate => candidate.EndsWith("selected-profile-handshake.win-x64-self-contained.evidence.json", StringComparison.Ordinal));
            var originalSelfContainedEvidence = File.ReadAllText(selfContainedEvidencePath);
            var selfContainedEvidence = JsonNode.Parse(originalSelfContainedEvidence)!.AsObject();
            selfContainedEvidence["launchIdentity"]!["runtimeIdentity"]!["loadedHostFxrPath"] = Path.Combine(Path.GetTempPath(), "outside-hostfxr.dll");
            File.WriteAllText(selfContainedEvidencePath, selfContainedEvidence.ToJsonString());
            selfContainedCase["artifactSha256"]![selfContainedEvidencePath] = Sha256(selfContainedEvidencePath);
            RebuildAggregateArtifacts(sdkProbe);
            RefreshArchitectureEvidence(result);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(selfContainedEvidencePath, originalSelfContainedEvidence);
            result = JsonNode.Parse(original)!.AsObject();
            sdkProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
            sdkCase = sdkProbe["cases"]![0]!;
            sdkEvidence = JsonNode.Parse(originalSdkEvidence)!.AsObject();
            sdkEvidence["launchIdentity"]!["runtimeIdentity"]!["loadedHostFxrSha256"] = new string('f', 64);
            File.WriteAllText(sdkEvidencePath, sdkEvidence.ToJsonString());
            sdkCase["artifactSha256"]![sdkEvidencePath] = Sha256(sdkEvidencePath);
            RebuildAggregateArtifacts(sdkProbe);
            RefreshArchitectureEvidence(result);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(sdkEvidencePath, originalSdkEvidence);
            result = JsonNode.Parse(original)!.AsObject();
            sdkProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
            sdkCase = sdkProbe["cases"]![0]!;
            sdkEvidence = JsonNode.Parse(originalSdkEvidence)!.AsObject();
            sdkEvidence["framingAndRequestIds"]!["productionFrameLimit"] = 99999;
            File.WriteAllText(sdkEvidencePath, sdkEvidence.ToJsonString());
            sdkCase["artifactSha256"]![sdkEvidencePath] = Sha256(sdkEvidencePath);
            RebuildAggregateArtifacts(sdkProbe);
            RefreshArchitectureEvidence(result);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));

            File.WriteAllText(sdkEvidencePath, originalSdkEvidence);
            result = JsonNode.Parse(original)!.AsObject();
            sdkProbe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
            var normalCase = sdkProbe["cases"]!.AsArray().Single(node => node!["hostMode"]!.GetValue<string>() == "normal")!;
            var frameworkCase = sdkProbe["cases"]!.AsArray().Single(node => node!["hostMode"]!.GetValue<string>() == "win-x64-framework-dependent")!;
            var normalManifestPath = normalCase["artifactSha256"]!.AsObject().Select(property => property.Key).Single(candidate => candidate.EndsWith("normal.json", StringComparison.Ordinal));
            var frameworkManifestPath = frameworkCase["artifactSha256"]!.AsObject().Select(property => property.Key).Single(candidate => candidate.EndsWith("win-x64-framework-dependent.json", StringComparison.Ordinal));
            var normalManifest = JsonNode.Parse(File.ReadAllText(normalManifestPath))!.AsObject();
            var frameworkManifest = JsonNode.Parse(File.ReadAllText(frameworkManifestPath))!.AsObject();
            var normalRoot = normalManifest["publishRoot"]!.GetValue<string>();
            var frameworkRoot = frameworkManifest["publishRoot"]!.GetValue<string>();
            foreach (var metadataName in new[] { "sdkcandidateprobe.deps.json", "sdkcandidateprobe.runtimeconfig.json" })
            {
                File.Copy(Path.Combine(normalRoot, metadataName), Path.Combine(frameworkRoot, metadataName), overwrite: true);
                var entry = frameworkManifest["files"]!.AsArray().Single(node => node!["relativePath"]!.GetValue<string>() == metadataName)!;
                entry["sha256"] = Sha256(Path.Combine(frameworkRoot, metadataName));
            }
            File.WriteAllText(frameworkManifestPath, frameworkManifest.ToJsonString());
            frameworkCase["artifactSha256"]![frameworkManifestPath] = Sha256(frameworkManifestPath);
            RebuildAggregateArtifacts(sdkProbe);
            File.WriteAllText(path, result.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(path));
        }
        finally
        {
            DeleteResultFixture(path);
        }
    }

    [Fact]
    public void CandidateResult_AcceptsRetainedPublishAndLaunchFailuresButRejectsFalsePassClaims()
    {
        var publishFailurePath = WriteResultFixture(SdkSurfaceHostModes);
        var launchFailurePath = WriteResultFixture(SdkSurfaceHostModes);
        var profileFailurePath = WriteResultFixture(SdkSurfaceHostModes);
        var sdkStartFailurePath = WriteResultFixture(SdkSurfaceHostModes);
        var sdkDeepLaunchFailurePath = WriteResultFixture(SdkSurfaceHostModes);
        try
        {
            var publishResult = JsonNode.Parse(File.ReadAllText(publishFailurePath))!.AsObject();
            string? failedPublishRoot = null;
            string? failedManifestPath = null;
            foreach (var probe in publishResult["probes"]!.AsArray().Where(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal)))
            {
                probe!["exitCode"] = 1;
                probe["passed"] = false;
                var failedCase = probe["cases"]!.AsArray().Single(node => node!["hostMode"]!.GetValue<string>() == "win-x64-framework-dependent")!;
                failedCase["exitCode"] = 1;
                failedCase["passed"] = false;
                failedCase["failureStage"] = "publish";
                var artifacts = failedCase["artifactSha256"]!.AsObject();
                failedPublishRoot ??= Path.GetDirectoryName(artifacts.Select(property => property.Key).Single(path => path.EndsWith("sdkcandidateprobe.exe", StringComparison.Ordinal)))!;
                failedManifestPath ??= artifacts.Select(property => property.Key).Single(path => path.EndsWith("win-x64-framework-dependent.json", StringComparison.Ordinal));
                foreach (var evidencePath in artifacts.Select(property => property.Key).Where(path => path.EndsWith(".evidence.json", StringComparison.Ordinal)).ToArray())
                {
                    File.Delete(evidencePath);
                }
                artifacts.Clear();
                RebuildAggregateArtifacts(probe);
            }
            RefreshSchemaAggregate(publishResult);
            Assert.NotNull(failedPublishRoot);
            Assert.NotNull(failedManifestPath);
            Directory.Delete(failedPublishRoot!, recursive: true);
            File.Delete(failedManifestPath!);
            RefreshArchitectureEvidence(publishResult);
            File.WriteAllText(publishFailurePath, publishResult.ToJsonString());
            Assert.Equal(0, InvokeResultValidation(publishFailurePath));

            var firstFailedCase = publishResult["probes"]!.AsArray()
                .First(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal))!["cases"]!.AsArray()
                .Single(node => node!["hostMode"]!.GetValue<string>() == "win-x64-framework-dependent")!;
            firstFailedCase["exitCode"] = 0;
            firstFailedCase["passed"] = true;
            firstFailedCase["failureStage"] = "none";
            File.WriteAllText(publishFailurePath, publishResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(publishFailurePath));

            var launchResult = JsonNode.Parse(File.ReadAllText(launchFailurePath))!.AsObject();
            var stdioProbe = launchResult["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "self-contained-stdio")!;
            var stdioCase = stdioProbe["cases"]![0]!;
            var stdioArtifacts = stdioCase["artifactSha256"]!.AsObject();
            var serverPath = stdioArtifacts.Select(property => property.Key).Single(path => path.EndsWith("WprMcp.exe", StringComparison.Ordinal));
            var serverHash = Sha256(serverPath);
            foreach (var retainedPath in stdioArtifacts.Select(property => property.Key).Where(path => path != serverPath).ToArray())
            {
                File.Delete(retainedPath);
            }
            stdioArtifacts.Clear();
            stdioArtifacts[serverPath] = serverHash;
            stdioProbe["artifactSha256"]!.AsObject().Clear();
            stdioProbe["artifactSha256"]![serverPath] = serverHash;
            stdioCase["exitCode"] = 1;
            stdioCase["passed"] = false;
            stdioCase["failureStage"] = "launch";
            stdioProbe["exitCode"] = 1;
            stdioProbe["passed"] = false;
            RefreshArchitectureEvidence(launchResult);
            File.WriteAllText(launchFailurePath, launchResult.ToJsonString());
            Assert.Equal(0, InvokeResultValidation(launchFailurePath));

            stdioCase["exitCode"] = 0;
            stdioCase["passed"] = true;
            stdioCase["failureStage"] = "none";
            File.WriteAllText(launchFailurePath, launchResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(launchFailurePath));

            var profileResult = JsonNode.Parse(File.ReadAllText(profileFailurePath))!.AsObject();
            var profileProbe = profileResult["probes"]!.AsArray()
                .First(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal))!;
            var profileCase = profileProbe["cases"]!.AsArray()
                .Single(node => node!["hostMode"]!.GetValue<string>() == "normal")!;
            var profileArtifacts = profileCase["artifactSha256"]!.AsObject();
            var profileEvidencePath = profileArtifacts.Select(property => property.Key)
                .Single(path => path.EndsWith("normal.evidence.json", StringComparison.Ordinal));
            var profileEvidence = JsonNode.Parse(File.ReadAllText(profileEvidencePath))!.AsObject();
            Assert.True(profileEvidence["launchIdentity"]!["passed"]!.GetValue<bool>());
            profileEvidence["passed"] = false;
            profileEvidence["cancellationProgress"]!["cancellationObserved"] = false;
            File.WriteAllText(profileEvidencePath, profileEvidence.ToJsonString());
            profileArtifacts[profileEvidencePath] = Sha256(profileEvidencePath);
            profileCase["exitCode"] = 1;
            profileCase["passed"] = false;
            profileCase["failureStage"] = "profile";
            profileProbe["exitCode"] = 1;
            profileProbe["passed"] = false;
            RebuildAggregateArtifacts(profileProbe);
            RefreshArchitectureEvidence(profileResult);
            File.WriteAllText(profileFailurePath, profileResult.ToJsonString());
            Assert.Equal(0, InvokeResultValidation(profileFailurePath));

            profileCase["failureStage"] = "publish";
            File.WriteAllText(profileFailurePath, profileResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(profileFailurePath));

            profileCase["failureStage"] = "launch";
            File.WriteAllText(profileFailurePath, profileResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(profileFailurePath));

            var startResult = JsonNode.Parse(File.ReadAllText(sdkStartFailurePath))!.AsObject();
            var startProbe = startResult["probes"]!.AsArray()
                .First(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal))!;
            var startCase = startProbe["cases"]!.AsArray()
                .Single(node => node!["hostMode"]!.GetValue<string>() == "normal")!;
            var startArtifacts = startCase["artifactSha256"]!.AsObject();
            var startEvidencePath = startArtifacts.Select(property => property.Key)
                .Single(path => path.EndsWith("normal.evidence.json", StringComparison.Ordinal));
            File.Delete(startEvidencePath);
            startArtifacts.Remove(startEvidencePath);
            startCase["exitCode"] = 1;
            startCase["passed"] = false;
            startCase["failureStage"] = "launch";
            startProbe["exitCode"] = 1;
            startProbe["passed"] = false;
            RebuildAggregateArtifacts(startProbe);
            RefreshArchitectureEvidence(startResult);
            File.WriteAllText(sdkStartFailurePath, startResult.ToJsonString());
            Assert.Equal(0, InvokeResultValidation(sdkStartFailurePath));

            var deepLaunchResult = JsonNode.Parse(File.ReadAllText(sdkDeepLaunchFailurePath))!.AsObject();
            var deepLaunchProbe = deepLaunchResult["probes"]!.AsArray()
                .First(node => SdkSurfaceProbeNames.Contains(node!["name"]!.GetValue<string>(), StringComparer.Ordinal))!;
            var deepLaunchCase = deepLaunchProbe["cases"]!.AsArray()
                .Single(node => node!["hostMode"]!.GetValue<string>() == "normal")!;
            var deepLaunchArtifacts = deepLaunchCase["artifactSha256"]!.AsObject();
            var deepLaunchEvidencePath = deepLaunchArtifacts.Select(property => property.Key)
                .Single(path => path.EndsWith("normal.evidence.json", StringComparison.Ordinal));
            var deepLaunchManifestPath = deepLaunchArtifacts.Select(property => property.Key)
                .Single(path => path.EndsWith("normal.json", StringComparison.Ordinal));
            var deepLaunchEvidence = JsonNode.Parse(File.ReadAllText(deepLaunchEvidencePath))!.AsObject();
            deepLaunchEvidence["passed"] = false;
            deepLaunchEvidence["launchIdentity"]!["runtimeIdentity"]!["loadedHostFxrSha256"] = new string('f', 64);
            File.WriteAllText(deepLaunchEvidencePath, deepLaunchEvidence.ToJsonString());
            deepLaunchArtifacts[deepLaunchEvidencePath] = Sha256(deepLaunchEvidencePath);
            deepLaunchArtifacts.Remove(deepLaunchManifestPath);
            foreach (var retainedRuntimePath in deepLaunchArtifacts.Select(property => property.Key)
                .Where(path => path.Contains($"{Path.DirectorySeparatorChar}framework-runtime{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .ToArray())
            {
                deepLaunchArtifacts.Remove(retainedRuntimePath);
            }
            Assert.True(File.Exists(deepLaunchManifestPath), "A later same-host case must be allowed to share the global manifest without backfilling this case map.");
            deepLaunchCase["exitCode"] = 1;
            deepLaunchCase["passed"] = false;
            deepLaunchCase["failureStage"] = "launch";
            deepLaunchProbe["exitCode"] = 1;
            deepLaunchProbe["passed"] = false;
            RebuildAggregateArtifacts(deepLaunchProbe);
            RefreshArchitectureEvidence(deepLaunchResult);
            File.WriteAllText(sdkDeepLaunchFailurePath, deepLaunchResult.ToJsonString());
            Assert.Equal(0, InvokeResultValidation(sdkDeepLaunchFailurePath));

            deepLaunchCase["failureStage"] = "publish";
            File.WriteAllText(sdkDeepLaunchFailurePath, deepLaunchResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(sdkDeepLaunchFailurePath));

            deepLaunchCase["failureStage"] = "launch";
            deepLaunchCase["passed"] = true;
            File.WriteAllText(sdkDeepLaunchFailurePath, deepLaunchResult.ToJsonString());
            Assert.NotEqual(0, InvokeResultValidation(sdkDeepLaunchFailurePath));
        }
        finally
        {
            DeleteResultFixture(publishFailurePath);
            DeleteResultFixture(launchFailurePath);
            DeleteResultFixture(profileFailurePath);
            DeleteResultFixture(sdkStartFailurePath);
            DeleteResultFixture(sdkDeepLaunchFailurePath);
        }
    }

    [Fact]
    public void SdkCandidateProbe_UsesSelectedRevisionProfileAndPublicSdkSeamsOnly()
    {
        var project = XDocument.Load(LocateRepoFile("tools", "sdkcandidateprobe", "sdkcandidateprobe.csproj"));
        var build = XDocument.Load(LocateRepoFile("Directory.Build.props"));
        var packages = XDocument.Load(LocateRepoFile("Directory.Packages.props"));
        var source = File.ReadAllText(LocateRepoFile("tools", "sdkcandidateprobe", "Program.cs"));

        Assert.Null(ProjectProperty(project, "TargetFramework"));
        Assert.Equal("$(WprMcpTargetFramework)", ProjectProperty(build, "TargetFramework"));
        Assert.Null(ProjectPackageVersion(project, "ModelContextProtocol"));
        Assert.Equal("$(WprMcpMcpSdkVersion)", CentralPackageVersion(packages, "ModelContextProtocol"));
        Assert.Contains("StreamServerTransport", source, StringComparison.Ordinal);
        Assert.Contains("AddIncomingFilter", source, StringComparison.Ordinal);
        Assert.Contains("McpServer.Create", source, StringComparison.Ordinal);
        Assert.Contains("RunAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("System.Reflection", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BindingFlags", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SdkCandidateProbe_DelegatesTypedStructuredToolWithoutCallToolResultDomainReturn()
    {
        var source = File.ReadAllText(LocateRepoFile("tools", "sdkcandidateprobe", "SdkProbeTool.cs"));

        Assert.Matches(new Regex(@"public\s+async\s+Task<ProbeOutput>\s+EchoAsync\s*\(", RegexOptions.CultureInvariant), source);
        Assert.Contains("UseStructuredContent = true", source, StringComparison.Ordinal);
        Assert.Contains("DelegatingMcpServerTool", source, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"public\s+(?:Task<)?CallToolResult", RegexOptions.CultureInvariant), source);
    }

    [Fact]
    public void SdkCandidateProbe_InjectsCancellationAndProgressWithoutInputSchemaProperties()
    {
        var toolSource = File.ReadAllText(LocateRepoFile("tools", "sdkcandidateprobe", "SdkProbeTool.cs"));
        var suiteSource = File.ReadAllText(LocateRepoFile("tools", "sdkcandidateprobe", "Program.cs"));

        Assert.Contains("CancellationToken cancellationToken", toolSource, StringComparison.Ordinal);
        Assert.Contains("IProgress<ProgressNotificationValue>? progress", toolSource, StringComparison.Ordinal);
        Assert.Contains("cancellationToken.ThrowIfCancellationRequested()", toolSource, StringComparison.Ordinal);
        Assert.Contains("progress?.Report", toolSource, StringComparison.Ordinal);
        Assert.Contains("InputSchemaPropertyNames", suiteSource, StringComparison.Ordinal);
        Assert.Contains("InvocationCount", suiteSource, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SdkCandidateProbe_CancelledRequestHasNoResponseAndServerDrainsAfterClientStopsWaiting()
    {
        var executable = LocateBuiltSdkCandidateProbe();
        await using var child = await SdkCandidateProbe.ProbeChild.StartAsync(
            executable,
            "2025-11-25",
            "stateful");

        using var initializeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var initializeResponse = await child.SendRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "initialize",
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "cancellation-regression", ["version"] = "1.0" },
            },
        }, "initialize", crlf: false, initializeTimeout.Token);
        Assert.Equal("initialize", initializeResponse["id"]?.GetValue<string>());

        using var notificationTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await child.SendNotificationAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, crlf: false, notificationTimeout.Token);
        var cancellation = await child.SendCancellableRequestAsync(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "cancel",
                ["method"] = "tools/call",
                ["params"] = new JsonObject
                {
                    ["name"] = "sdk_probe_echo",
                    ["arguments"] = new JsonObject { ["value"] = "cancel" },
                    ["_meta"] = new JsonObject { ["progressToken"] = "cancel-progress" },
                },
            },
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/cancelled",
                ["params"] = new JsonObject { ["requestId"] = "cancel", ["reason"] = "regression" },
            },
            "cancel",
            TimeSpan.FromSeconds(2),
            CancellationToken.None);

        Assert.True(cancellation.ProgressObserved);
        Assert.False(cancellation.ResponseObserved);
        using var cleanExitTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await child.CloseInputAndWaitAsync(cleanExitTimeout.Token);
        Assert.Equal(string.Empty, await child.ReadStandardErrorAsync());
    }

    [Fact]
    public async Task SdkCandidateProbe_RunSuiteRetainsPerStageProgressAndHandlerCancellationEvidence()
    {
        var assembly = LocateBuiltSdkCandidateProbe();
        var host = assembly;
        var evidence = Path.Combine(Path.GetTempPath(), $"sdkcandidateprobe-suite-{Guid.NewGuid():N}.json");
        try
        {
            var dotnetExecutable = LocateCurrentDotNetHost();
            var dotnetRoot = Path.GetDirectoryName(dotnetExecutable)!;
            var result = RunProcess(dotnetExecutable, [
                assembly,
                "--run-suite", "--host-mode", "normal", "--host-command", host,
                "--protocol-revision", "2025-11-25", "--protocol-profile", "stateful",
                "--evidence", evidence,
            ], LocateRepoRoot(), new Dictionary<string, string>
            {
                ["DOTNET_ROOT"] = dotnetRoot,
                ["DOTNET_ROOT_X64"] = dotnetRoot,
            });
            Assert.True(result.ExitCode == 1, $"Managed harness should fail only production launch identity, but exited {result.ExitCode}. stdout: {result.Stdout} stderr: {result.Stderr}");
            using var document = JsonDocument.Parse(File.ReadAllBytes(evidence));
            var progress = document.RootElement.GetProperty("cancellationProgress");
            Assert.Equal(1, progress.GetProperty("normalProgressNotificationCount").GetInt32());
            Assert.Equal(1, progress.GetProperty("cancellationProgressNotificationCount").GetInt32());
            Assert.Equal(2, progress.GetProperty("totalProgressNotificationCount").GetInt32());
            Assert.Equal(1, progress.GetProperty("handlerCancellationObservationCount").GetInt32());
            var runtimeIdentity = document.RootElement.GetProperty("launchIdentity").GetProperty("runtimeIdentity");
            var loadedHostFxrPath = runtimeIdentity.GetProperty("LoadedHostFxrPath").GetString()!;
            var loadedHostPolicyPath = runtimeIdentity.GetProperty("LoadedHostPolicyPath").GetString()!;
            Assert.True(File.Exists(loadedHostFxrPath));
            Assert.True(File.Exists(loadedHostPolicyPath));
            Assert.Equal(Sha256(loadedHostFxrPath), runtimeIdentity.GetProperty("LoadedHostFxrSha256").GetString());
            Assert.Equal(Sha256(loadedHostPolicyPath), runtimeIdentity.GetProperty("LoadedHostPolicySha256").GetString());
            Assert.False(document.RootElement.GetProperty("launchIdentity").GetProperty("passed").GetBoolean());
            Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
        }
        finally
        {
            File.Delete(evidence);
            await Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SdkCandidateProbe_RejectedRequestDoesNotReachIncomingNextOrHandler()
    {
        var executable = LocateBuiltSdkCandidateProbe();
        var directory = Path.Combine(Path.GetTempPath(), $"sdk-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "stdin.bin");
        var stdout = Path.Combine(directory, "stdout.bin");
        var stderr = Path.Combine(directory, "stderr.txt");
        var audit = Path.Combine(directory, "audit.json");
        var request = $"{{\"jsonrpc\":\"2.0\",\"id\":\"{new string('i', 129)}\",\"method\":\"tools/call\",\"params\":{{\"name\":\"sdk_probe_echo\",\"arguments\":{{\"value\":\"must-not-run\"}}}}}}\n";
        File.WriteAllText(input, request, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var command = $"\"\"{LocateCurrentDotNetHost()}\" \"{executable}\" --serve --protocol-revision \"2025-11-25\" --protocol-profile \"stateful\" --audit-path \"{audit}\" < \"{input}\" > \"{stdout}\" 2> \"{stderr}\"\"";
        Process? process = null;

        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(15_000), "Rejected request server did not drain within 15 seconds.");
            Assert.True(process.ExitCode == 2, $"Expected rejection exit 2, got {process.ExitCode}: {File.ReadAllText(stderr)}");
            Assert.Equal(0, new FileInfo(stdout).Length);
            Assert.Equal("sdkcandidateprobe: request id limit exceeded", File.ReadAllText(stderr).TrimEnd('\r', '\n'));

            using var evidence = JsonDocument.Parse(File.ReadAllBytes(audit));
            Assert.Equal(0, evidence.RootElement.GetProperty("incomingNextCount").GetInt32());
            Assert.Equal(0, evidence.RootElement.GetProperty("handlerInvocationCount").GetInt32());
        }
        finally
        {
            await KillProcessTreeIfRunningAsync(process);
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task SdkCandidateProbe_OversizedFrameIsInvisibleToParserAndHandler()
    {
        var executable = LocateBuiltSdkCandidateProbe();
        var directory = Path.Combine(Path.GetTempPath(), $"sdk-frame-rejection-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "stdin.bin");
        var stdout = Path.Combine(directory, "stdout.bin");
        var stderr = Path.Combine(directory, "stderr.txt");
        var audit = Path.Combine(directory, "audit.json");
        File.WriteAllBytes(input, CreateRawProbeCall("\"frame-100001\"", string.Empty, "\n", exactPayloadBytes: 100001));
        var command = $"\"\"{LocateCurrentDotNetHost()}\" \"{executable}\" --serve --protocol-revision \"2025-11-25\" --protocol-profile \"stateful\" --audit-path \"{audit}\" < \"{input}\" > \"{stdout}\" 2> \"{stderr}\"\"";
        Process? process = null;

        try
        {
            process = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /s /c {command}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(15_000), "Oversized-frame server did not drain within 15 seconds.");
            Assert.Equal(2, process.ExitCode);
            Assert.Equal(0, new FileInfo(stdout).Length);
            Assert.Equal("sdkcandidateprobe: frame limit exceeded", File.ReadAllText(stderr).TrimEnd('\r', '\n'));
            using var evidence = JsonDocument.Parse(File.ReadAllBytes(audit));
            Assert.Equal(0, evidence.RootElement.GetProperty("incomingNextCount").GetInt32());
            Assert.Equal(0, evidence.RootElement.GetProperty("handlerInvocationCount").GetInt32());
        }
        finally
        {
            await KillProcessTreeIfRunningAsync(process);
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public async Task SdkCandidateProbe_AcceptsExactIdsFramesAndExportsDelegatedObservations()
    {
        var executable = LocateBuiltSdkCandidateProbe();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await SdkCandidateProbe.ProbeChild.StartAsync(executable, "2025-11-25", "stateful");
        await child.SendRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "initialize",
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject { ["name"] = "boundary-regression", ["version"] = "1.0" },
            },
        }, "initialize", crlf: false, timeout.Token);
        await child.SendNotificationAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/initialized",
        }, crlf: true, timeout.Token);

        var list = await child.SendRequestAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = "list",
            ["method"] = "tools/list",
            ["params"] = new JsonObject(),
        }, "list", crlf: true, timeout.Token);
        var tool = list["result"]!["tools"]!.AsArray().Single(item => item!["name"]!.GetValue<string>() == "sdk_probe_echo")!;
        Assert.Equal(["value"], tool["inputSchema"]!["properties"]!.AsObject().Select(property => property.Key));
        Assert.NotNull(tool["outputSchema"]);
        Assert.NotNull(tool["annotations"]);

        var accepted = new (string IdJson, string ExpectedIdJson, string Value, string Ending)[]
        {
            ($"\"{new string('a', 127)}\"", $"\"{new string('a', 127)}\"", "ascii127", "\n"),
            ($"\"{new string('a', 128)}\"", $"\"{new string('a', 128)}\"", "ascii128", "\r\n"),
            ("\"é\"", "\"é\"", "direct", "\n"),
            ("\"\\u00e9\"", "\"é\"", "escaped", "\r\n"),
            (long.MinValue.ToString(), long.MinValue.ToString(), "min", "\n"),
            ("0", "0", "zero", "\r\n"),
            (long.MaxValue.ToString(), long.MaxValue.ToString(), "max", "\n"),
        };
        var invocation = 0;
        foreach (var item in accepted)
        {
            var response = await child.SendRawRequestAsync(CreateRawProbeCall(item.IdJson, item.Value, item.Ending), item.ExpectedIdJson, timeout.Token);
            var structured = response["result"]!["structuredContent"]!;
            invocation = structured["invocation"]!.GetValue<int>();
            Assert.True(structured["innerTextObserved"]!.GetValue<bool>());
            Assert.True(structured["innerStructuredObserved"]!.GetValue<bool>());
            Assert.False(structured["preservedIsError"]?.GetValue<bool>() ?? false);
        }

        var exactFrame = CreateRawProbeCall("\"frame-100000\"", string.Empty, "\r\n", exactPayloadBytes: 100000);
        Assert.Equal(100002, exactFrame.Length);
        var exactResponse = await child.SendRawRequestAsync(exactFrame, "\"frame-100000\"", timeout.Token);
        Assert.True(exactResponse["result"]!["structuredContent"]!["invocation"]!.GetValue<int>() > invocation);

        using var cleanExit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await child.CloseInputAndWaitAsync(cleanExit.Token);
        Assert.Equal(string.Empty, await child.ReadStandardErrorAsync());
    }

    [Fact]
    public void SdkCandidateProbe_ProvesConfiguredFrameAndDecodedRequestIdBoundariesBeforeDispatch()
    {
        var source = File.ReadAllText(LocateRepoFile("tools", "sdkcandidateprobe", "Program.cs"));

        Assert.Contains("FrameLimitStream", source, StringComparison.Ordinal);
        Assert.Contains("100000", source, StringComparison.Ordinal);
        Assert.Contains("Encoding.UTF8.GetByteCount", source, StringComparison.Ordinal);
        Assert.Contains("JsonRpcMessageWithId", source, StringComparison.Ordinal);
        Assert.Contains("request id limit exceeded", source, StringComparison.Ordinal);
        Assert.Contains("frame limit exceeded", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonDocument.Parse", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize<JsonRpcMessage", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CandidateRunner_UsesRealProductionStdioHandshakeAndRetainedLaunchIdentity()
    {
        var runner = File.ReadAllText(LocateRepoFile("scripts", "Test-PlatformCandidate.ps1"));
        var probe = File.ReadAllText(LocateRepoFile("tests", "WprMcp.Tests", "PlatformProductionStdioTests.cs"));

        Assert.DoesNotContain("$serverExe @('--help')", runner, StringComparison.Ordinal);
        Assert.Contains("PlatformProductionStdioTests", runner, StringComparison.Ordinal);
        Assert.Contains("WPRMCP_PLATFORM_SERVER_PATH", runner, StringComparison.Ordinal);
        Assert.Contains("WPRMCP_PLATFORM_EXPECTED_LAUNCH_SHA256", runner, StringComparison.Ordinal);
        Assert.Contains("WPRMCP_PLATFORM_EVIDENCE_PATH", runner, StringComparison.Ordinal);
        Assert.Contains("DOTNET_ROOT_X64", runner, StringComparison.Ordinal);
        Assert.Contains("MetaKeys.ProtocolVersion", probe, StringComparison.Ordinal);
        Assert.Contains("MetaKeys.ClientInfo", probe, StringComparison.Ordinal);
        Assert.Contains("MetaKeys.ClientCapabilities", probe, StringComparison.Ordinal);
        Assert.Contains("RequestMethods.ServerDiscover", probe, StringComparison.Ordinal);
        Assert.Contains("[\"initialize\", \"notifications/initialized\", \"tools/list\", \"tools/call\"]", probe, StringComparison.Ordinal);
        Assert.Contains("[RequestMethods.ServerDiscover, \"tools/list\", \"tools/call\"]", probe, StringComparison.Ordinal);
        Assert.Contains("FileMode.CreateNew", probe, StringComparison.Ordinal);
        Assert.Contains("expectedLaunchSha256", probe, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CandidateRunner_CreateNewPublishManifestCoversFullTreeAndRejectsReuse()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"platform-publish-manifest-{Guid.NewGuid():N}");
        var publish = Path.Combine(directory, "publish");
        Directory.CreateDirectory(Path.Combine(publish, "nested"));
        foreach (var relativePath in new[]
        {
            "sdkcandidateprobe.exe",
            "sdkcandidateprobe.dll",
            "sdkcandidateprobe.deps.json",
            "sdkcandidateprobe.runtimeconfig.json",
            "nested/native.dll",
        })
        {
            var file = Path.Combine(publish, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, relativePath);
        }
        var manifest = Path.Combine(directory, "publish-manifests", "normal.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        var dotnetRoot = Path.Combine(directory, "dotnet");
        var hostFxr8 = Path.Combine(dotnetRoot, "host", "fxr", "8.0.26", "hostfxr.dll");
        var hostFxr10 = Path.Combine(dotnetRoot, "host", "fxr", "10.0.10", "hostfxr.dll");
        var hostPolicy8 = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", "8.0.26", "hostpolicy.dll");
        var hostPolicy10 = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App", "10.0.10", "hostpolicy.dll");
        foreach (var path in new[] { hostFxr8, hostFxr10, hostPolicy8, hostPolicy10 })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }
        File.WriteAllText(hostFxr8, "hostfxr-8");
        File.WriteAllText(hostFxr10, "hostfxr-10-actually-loaded");
        File.WriteAllText(hostPolicy8, "hostpolicy-8");
        File.WriteAllText(hostPolicy10, "hostpolicy-10-actually-loaded");
        var runtimeEvidence = Path.Combine(directory, "normal.evidence.json");
        File.WriteAllText(runtimeEvidence, JsonSerializer.Serialize(new
        {
            hostMode = "normal",
            passed = false,
            launchIdentity = new
            {
                configuredDotNetRoot = dotnetRoot,
                configuredDotNetRootX64 = dotnetRoot,
                passed = true,
                runtimeIdentity = new
                {
                    LoadedHostFxrPath = hostFxr10,
                    LoadedHostFxrSha256 = Sha256(hostFxr10),
                    LoadedHostPolicyPath = hostPolicy10,
                    LoadedHostPolicySha256 = Sha256(hostPolicy10),
                },
            },
        }));
        var caseStdout = Path.Combine(directory, "profile.stdout.log");
        var caseStderr = Path.Combine(directory, "profile.stderr.log");
        var publishStderr = Path.Combine(directory, "publish.stderr.log");
        File.WriteAllText(caseStdout, string.Empty);
        File.WriteAllText(caseStderr, "profile contract failed");
        File.WriteAllText(publishStderr, string.Empty);
        try
        {
            var script = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
            var publishArg = publish.Replace("'", "''", StringComparison.Ordinal);
            var manifestArg = manifest.Replace("'", "''", StringComparison.Ordinal);
            var dotnetRootArg = dotnetRoot.Replace("'", "''", StringComparison.Ordinal);
            var runtimeEvidenceArg = runtimeEvidence.Replace("'", "''", StringComparison.Ordinal);
            var caseStdoutArg = caseStdout.Replace("'", "''", StringComparison.Ordinal);
            var caseStderrArg = caseStderr.Replace("'", "''", StringComparison.Ordinal);
            var publishStderrArg = publishStderr.Replace("'", "''", StringComparison.Ordinal);
            var first = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $publishResult = [ordered]@{{ ExitCode = 0; StderrPath = '{publishStderrArg}'; StderrSha256 = (Get-Sha256 '{publishStderrArg}') }}; $caseResult = [ordered]@{{ Command = 'sdkcandidateprobe --run-suite'; ExitCode = 1; StdoutPath = '{caseStdoutArg}'; StderrPath = '{caseStderrArg}'; StdoutSha256 = (Get-Sha256 '{caseStdoutArg}'); StderrSha256 = (Get-Sha256 '{caseStderrArg}'); StartFailed = $false }}; $completed = Complete-SdkCaseRuntimeManifest -HostMode 'normal' -PublishRoot '{publishArg}' -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $caseResult -ManifestPath '{manifestArg}' -RuntimeEvidencePath '{runtimeEvidenceArg}' -DotNetRoot '{dotnetRootArg}'; $artifacts = @{{}}; foreach ($path in @((Join-Path '{publishArg}' 'sdkcandidateprobe.exe'), '{runtimeEvidenceArg}', $completed.Path, $completed.Manifest.frameworkRuntime.retainedHostFxrPath, $completed.Manifest.frameworkRuntime.retainedHostPolicyPath)) {{ if ($path) {{ $artifacts[$path] = Get-Sha256 $path }} }}; $stage = Get-SdkCaseFailureStage -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $caseResult -HostManifest $completed; $case = New-CaseResult 'normal' 'selected-profile-handshake' $caseResult $artifacts $stage; $publishFailureStage = Get-SdkCaseFailureStage -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult ([ordered]@{{ ExitCode = 1 }}) -CaseResult $caseResult -HostManifest $null; $launchFailureStage = Get-SdkCaseFailureStage -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult ([ordered]@{{ ExitCode = 1; StartFailed = $true }}) -HostManifest $null; [ordered]@{{ Manifest = $completed; Case = $case; PublishFailureStage = $publishFailureStage; LaunchFailureStage = $launchFailureStage }} | ConvertTo-Json -Depth 10 -Compress");
            Assert.True(first.ExitCode == 0, $"Manifest creation failed. stdout: {first.Stdout} stderr: {first.Stderr}");
            using var completed = JsonDocument.Parse(first.Stdout);
            var retainedCase = completed.RootElement.GetProperty("Case");
            Assert.Equal(1, retainedCase.GetProperty("exitCode").GetInt32());
            Assert.False(retainedCase.GetProperty("passed").GetBoolean());
            Assert.Equal("profile", retainedCase.GetProperty("failureStage").GetString());
            Assert.Equal("publish", completed.RootElement.GetProperty("PublishFailureStage").GetString());
            Assert.Equal("launch", completed.RootElement.GetProperty("LaunchFailureStage").GetString());
            var retainedArtifactNames = retainedCase.GetProperty("artifactSha256").EnumerateObject().Select(property => property.Name).ToArray();
            Assert.Contains(runtimeEvidence, retainedArtifactNames, StringComparer.Ordinal);
            Assert.Contains(manifest, retainedArtifactNames, StringComparer.Ordinal);
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            Assert.Equal("normal", json.RootElement.GetProperty("hostMode").GetString());
            Assert.Equal(0, json.RootElement.GetProperty("publishExitCode").GetInt32());
            Assert.Equal(
                new[] { "nested/native.dll", "sdkcandidateprobe.deps.json", "sdkcandidateprobe.dll", "sdkcandidateprobe.exe", "sdkcandidateprobe.runtimeconfig.json" },
                json.RootElement.GetProperty("files").EnumerateArray().Select(file => file.GetProperty("relativePath").GetString()).ToArray());
            Assert.All(json.RootElement.GetProperty("files").EnumerateArray(), file => Assert.Matches("^[0-9a-f]{64}$", file.GetProperty("sha256").GetString()));
            var frameworkRuntime = json.RootElement.GetProperty("frameworkRuntime");
            Assert.Equal(hostFxr10, frameworkRuntime.GetProperty("sourceHostFxrPath").GetString());
            Assert.Equal(Sha256(hostFxr10), frameworkRuntime.GetProperty("sourceHostFxrSha256").GetString());
            Assert.Equal(hostPolicy10, frameworkRuntime.GetProperty("sourceHostPolicyPath").GetString());
            Assert.Equal(Sha256(hostPolicy10), frameworkRuntime.GetProperty("sourceHostPolicySha256").GetString());
            Assert.Equal(Sha256(hostFxr10), Sha256(frameworkRuntime.GetProperty("retainedHostFxrPath").GetString()!));
            Assert.Equal(Sha256(hostPolicy10), Sha256(frameworkRuntime.GetProperty("retainedHostPolicyPath").GetString()!));
            Assert.NotEqual(Sha256(hostFxr8), frameworkRuntime.GetProperty("sourceHostFxrSha256").GetString());
            Assert.NotEqual(Sha256(hostPolicy8), frameworkRuntime.GetProperty("sourceHostPolicySha256").GetString());

            var second = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $result = [ordered]@{{ ExitCode = 0 }}; New-PublishManifest -HostMode 'normal' -PublishRoot '{publishArg}' -PublishResult $result -ManifestPath '{manifestArg}' -RuntimeEvidencePath '{runtimeEvidenceArg}' -DotNetRoot '{dotnetRootArg}'");
            Assert.NotEqual(0, second.ExitCode);

            var deepFailureManifest = Path.Combine(directory, "publish-manifests", "win-x64-framework-dependent.json");
            var deepFailureEvidence = Path.Combine(directory, "framework.evidence.json");
            var deepFailureCaseStderr = Path.Combine(directory, "deep-runtime.stderr.log");
            var deepFailurePublishStderr = Path.Combine(directory, "deep-publish.stderr.log");
            File.WriteAllText(deepFailureCaseStderr, "profile contract failed");
            File.WriteAllText(deepFailurePublishStderr, string.Empty);
            var deepEvidence = JsonNode.Parse(File.ReadAllText(runtimeEvidence))!.AsObject();
            deepEvidence["hostMode"] = "win-x64-framework-dependent";
            deepEvidence["launchIdentity"]!["runtimeIdentity"]!["LoadedHostFxrSha256"] = new string('f', 64);
            File.WriteAllText(deepFailureEvidence, deepEvidence.ToJsonString());
            var deepManifestArg = deepFailureManifest.Replace("'", "''", StringComparison.Ordinal);
            var deepEvidenceArg = deepFailureEvidence.Replace("'", "''", StringComparison.Ordinal);
            var deepCaseStderrArg = deepFailureCaseStderr.Replace("'", "''", StringComparison.Ordinal);
            var deepPublishStderrArg = deepFailurePublishStderr.Replace("'", "''", StringComparison.Ordinal);
            var deepFailure = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $publishResult = [ordered]@{{ ExitCode = 0; StderrPath = '{deepPublishStderrArg}'; StderrSha256 = (Get-Sha256 '{deepPublishStderrArg}') }}; $invalidCase = [ordered]@{{ Command = 'invalid-runtime-suite'; ExitCode = 1; StderrPath = '{deepCaseStderrArg}'; StderrSha256 = (Get-Sha256 '{deepCaseStderrArg}'); StartFailed = $false }}; $invalidManifest = Complete-SdkCaseRuntimeManifest -HostMode 'win-x64-framework-dependent' -PublishRoot '{publishArg}' -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $invalidCase -ManifestPath '{deepManifestArg}' -RuntimeEvidencePath '{deepEvidenceArg}' -DotNetRoot '{dotnetRootArg}'; $invalidStage = Get-SdkCaseFailureStage -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $invalidCase -HostManifest $invalidManifest; $invalidPublishExit = $publishResult.ExitCode; $invalidCaseDiagnostic = Get-Content -LiteralPath '{deepCaseStderrArg}' -Raw; $runtimeEvidence = Get-Content -LiteralPath '{deepEvidenceArg}' -Raw | ConvertFrom-Json; $runtimeEvidence.launchIdentity.runtimeIdentity.LoadedHostFxrSha256 = Get-Sha256 $runtimeEvidence.launchIdentity.runtimeIdentity.LoadedHostFxrPath; Set-Content -LiteralPath '{deepEvidenceArg}' -Value ($runtimeEvidence | ConvertTo-Json -Depth 8) -Encoding UTF8 -NoNewline; $nextCase = [ordered]@{{ Command = 'valid-launch-profile-failure'; ExitCode = 1; StderrPath = '{deepCaseStderrArg}'; StderrSha256 = (Get-Sha256 '{deepCaseStderrArg}'); StartFailed = $false }}; $validManifest = Complete-SdkCaseRuntimeManifest -HostMode 'win-x64-framework-dependent' -PublishRoot '{publishArg}' -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $nextCase -ManifestPath '{deepManifestArg}' -RuntimeEvidencePath '{deepEvidenceArg}' -DotNetRoot '{dotnetRootArg}'; $validStage = Get-SdkCaseFailureStage -HostPath (Join-Path '{publishArg}' 'sdkcandidateprobe.exe') -PublishResult $publishResult -CaseResult $nextCase -HostManifest $validManifest; [ordered]@{{ InvalidManifestIsNull = $null -eq $invalidManifest; PublishExitAfterInvalid = $invalidPublishExit; InvalidStage = $invalidStage; InvalidCaseDiagnostic = $invalidCaseDiagnostic; CanRunNextCase = $publishResult.ExitCode -eq 0; ValidManifestExists = $null -ne $validManifest -and (Test-Path -LiteralPath $validManifest.Path -PathType Leaf); ValidStage = $validStage }} | ConvertTo-Json -Compress");
            Assert.Equal(0, deepFailure.ExitCode);
            using var deepFailureJson = JsonDocument.Parse(deepFailure.Stdout);
            Assert.True(deepFailureJson.RootElement.GetProperty("InvalidManifestIsNull").GetBoolean());
            Assert.Equal(0, deepFailureJson.RootElement.GetProperty("PublishExitAfterInvalid").GetInt32());
            Assert.Equal("launch", deepFailureJson.RootElement.GetProperty("InvalidStage").GetString());
            Assert.Contains("Runtime manifest finalization failed", deepFailureJson.RootElement.GetProperty("InvalidCaseDiagnostic").GetRawText(), StringComparison.Ordinal);
            Assert.True(deepFailureJson.RootElement.GetProperty("CanRunNextCase").GetBoolean());
            Assert.True(deepFailureJson.RootElement.GetProperty("ValidManifestExists").GetBoolean());
            Assert.Equal("profile", deepFailureJson.RootElement.GetProperty("ValidStage").GetString());

            var incomplete = Path.Combine(directory, "incomplete");
            Directory.CreateDirectory(incomplete);
            foreach (var name in new[] { "sdkcandidateprobe.exe", "sdkcandidateprobe.dll", "sdkcandidateprobe.deps.json" })
            {
                File.WriteAllText(Path.Combine(incomplete, name), name);
            }
            var incompleteManifest = Path.Combine(directory, "incomplete-manifest.json");
            var incompleteStderr = Path.Combine(directory, "incomplete.stderr.log");
            File.WriteAllText(incompleteStderr, string.Empty);
            var incompleteArg = incomplete.Replace("'", "''", StringComparison.Ordinal);
            var incompleteManifestArg = incompleteManifest.Replace("'", "''", StringComparison.Ordinal);
            var incompleteStderrArg = incompleteStderr.Replace("'", "''", StringComparison.Ordinal);
            var failed = RunPowerShell($". '{script}' -CandidateId net8-stable-stateful; $result = [ordered]@{{ ExitCode = 0; StderrPath = '{incompleteStderrArg}'; StderrSha256 = (Get-Sha256 '{incompleteStderrArg}') }}; $manifest = Complete-PublishManifestEvidence -HostMode 'normal' -PublishRoot '{incompleteArg}' -PublishResult $result -ManifestPath '{incompleteManifestArg}' -RuntimeEvidencePath '{runtimeEvidenceArg}' -DotNetRoot '{dotnetRootArg}'; [ordered]@{{ ExitCode = $result.ExitCode; ManifestIsNull = $null -eq $manifest; StderrSha256 = $result.StderrSha256 }} | ConvertTo-Json -Compress");
            Assert.Equal(0, failed.ExitCode);
            using var failedJson = JsonDocument.Parse(failed.Stdout);
            Assert.Equal(1, failedJson.RootElement.GetProperty("ExitCode").GetInt32());
            Assert.True(failedJson.RootElement.GetProperty("ManifestIsNull").GetBoolean());
            Assert.False(File.Exists(incompleteManifest));
            Assert.Contains("omitted required launch file", File.ReadAllText(incompleteStderr), StringComparison.Ordinal);
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(directory);
        }
    }

    [Fact]
    public void SelectedPlatform_ReferencesOnePassingCandidate()
    {
        var selected = ReadSelectedPlatformProperties();
        using var matrix = LoadMatrix();
        using var decision = LoadDecisionEvidence();
        var matches = matrix.RootElement.GetProperty("candidates").EnumerateArray()
            .Where(candidate => SelectedValuesMatchCandidate(selected, candidate))
            .ToArray();
        var candidate = Assert.Single(matches);
        var candidateId = candidate.GetProperty("id").GetString()!;
        var result = decision.RootElement.GetProperty("candidateResults").EnumerateArray()
            .Single(entry => entry.GetProperty("candidateId").GetString() == candidateId);

        Assert.Equal(candidateId, result.GetProperty("candidateId").GetString());
        Assert.All(result.GetProperty("probes").EnumerateArray(), probe =>
            Assert.True(probe.GetProperty("passed").GetBoolean(), $"Selected candidate probe failed: {probe.GetProperty("name").GetString()}"));
        AssertLocalCandidateResultMatchesIfPresent(result, validate: true);
    }

    [Fact]
    public void SelectedPlatform_ValuesExactlyMatchCandidateResult()
    {
        var selected = ReadSelectedPlatformProperties();
        var candidateId = FindSelectedCandidateId(selected);
        using var decision = LoadDecisionEvidence();
        var result = decision.RootElement.GetProperty("candidateResults").EnumerateArray()
            .Single(entry => entry.GetProperty("candidateId").GetString() == candidateId);

        Assert.Equal(result.GetProperty("sdkVersion").GetString(), selected["WprMcpSdkVersion"]);
        Assert.Equal(result.GetProperty("targetFramework").GetString(), selected["WprMcpTargetFramework"]);
        Assert.Equal(result.GetProperty("mcpSdkVersion").GetString(), selected["WprMcpMcpSdkVersion"]);
        Assert.Equal(result.GetProperty("protocolRevision").GetString(), selected["WprMcpProtocolRevision"]);
        Assert.Equal(result.GetProperty("protocolProfile").GetString(), selected["WprMcpProtocolProfile"]);
        AssertLocalCandidateResultMatchesIfPresent(result);
    }

    [Fact]
    public void DecisionRecord_ContainsEveryRequiredProbeCommandAndObservedOutcome()
    {
        using var decision = LoadDecisionEvidence();
        foreach (var candidateResult in decision.RootElement.GetProperty("candidateResults").EnumerateArray())
        {
            var indexedProbes = candidateResult.GetProperty("probes").EnumerateArray().ToArray();
            Assert.Equal(RequiredProbeNames, indexedProbes.Select(probe => probe.GetProperty("name").GetString()));
            foreach (var probe in indexedProbes)
            {
                Assert.False(string.IsNullOrWhiteSpace(probe.GetProperty("command").GetString()));
                Assert.True(probe.GetProperty("exitCode").TryGetInt32(out _));
                Assert.Contains(probe.GetProperty("passed").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
                AssertSha256(probe.GetProperty("stdoutSha256").GetString());
                AssertSha256(probe.GetProperty("stderrSha256").GetString());
            }
            AssertLocalCandidateResultMatchesIfPresent(candidateResult);
        }
    }

    [Fact]
    public void DecisionRecord_RecordsOfficialEvidenceUrlsAndNuGetVerificationUtc()
    {
        using var matrix = LoadMatrix();
        using var decision = LoadDecisionEvidence();
        var official = decision.RootElement.GetProperty("officialEvidence");
        foreach (var property in matrix.RootElement.GetProperty("planDateEvidence").EnumerateObject())
        {
            Assert.Equal(property.Value.GetRawText(), official.GetProperty(property.Name).GetRawText());
        }

        foreach (var candidate in decision.RootElement.GetProperty("candidateResults").EnumerateArray())
        {
            var verification = candidate.GetProperty("probes").EnumerateArray()
                .Single(probe => probe.GetProperty("name").GetString() == "nuget-package-existence-hash")
                .GetProperty("nuGetPackage");
            Assert.False(string.IsNullOrWhiteSpace(verification.GetProperty("observedUtc").GetString()));
            Assert.Equal("SHA512", verification.GetProperty("hashAlgorithm").GetString());
            Assert.Contains(verification.GetProperty("retrievalSource").GetString(), new[] { "Network", "VerifiedCache" });
        }
    }

    [Fact]
    public void DecisionRecord_ExplainsRejectedCandidatesAndPrereleaseRisk()
    {
        using var decision = LoadDecisionEvidence();
        var selectedId = decision.RootElement.GetProperty("selectedCandidateId").GetString();
        var decisions = decision.RootElement.GetProperty("decisions").EnumerateArray().ToArray();
        Assert.Equal(3, decisions.Length);
        Assert.Single(decisions, entry => entry.GetProperty("disposition").GetString() == "selected");
        foreach (var entry in decisions)
        {
            var candidateId = entry.GetProperty("candidateId").GetString();
            var disposition = entry.GetProperty("disposition").GetString();
            Assert.Equal(candidateId == selectedId ? "selected" : "rejected", disposition);
            Assert.NotEmpty(entry.GetProperty("reasons").EnumerateArray());
            if (disposition == "rejected")
            {
                var result = decision.RootElement.GetProperty("candidateResults").EnumerateArray()
                    .Single(candidate => candidate.GetProperty("candidateId").GetString() == candidateId);
                var failedNames = result.GetProperty("probes").EnumerateArray()
                    .Where(probe => !probe.GetProperty("passed").GetBoolean())
                    .Select(probe => probe.GetProperty("name").GetString()!)
                    .ToArray();
                var reasons = string.Join('\n', entry.GetProperty("reasons").EnumerateArray().Select(reason => reason.GetString()));
                foreach (var failedName in failedNames) Assert.Contains(failedName, reasons, StringComparison.Ordinal);
            }
        }
        Assert.Contains("prerelease", decision.RootElement.GetProperty("prereleaseRisk").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2.0.0-rc.1", decision.RootElement.GetProperty("prereleaseRisk").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DecisionRecord_DefinesExactProtocolE2eMatrix()
    {
        using var matrix = LoadMatrix();
        using var decision = LoadDecisionEvidence();
        var indexed = decision.RootElement.GetProperty("candidateResults").EnumerateArray().ToArray();
        foreach (var candidate in matrix.RootElement.GetProperty("candidates").EnumerateArray())
        {
            var entry = indexed.Single(result => result.GetProperty("candidateId").GetString() == candidate.GetProperty("id").GetString());
            foreach (var property in new[] { "sdkVersion", "targetFramework", "mcpSdkVersion", "protocolRevision", "protocolProfile" })
            {
                Assert.Equal(candidate.GetProperty(property).GetRawText(), entry.GetProperty(property).GetRawText());
            }
        }
    }

    [Fact]
    public void DecisionRecord_RecordsSdkSurfaceCasesForEveryHostModeAndCandidate()
    {
        using var decision = LoadDecisionEvidence();
        foreach (var candidate in decision.RootElement.GetProperty("candidateResults").EnumerateArray())
        {
            foreach (var probeName in SdkSurfaceProbeNames)
            {
                var probe = candidate.GetProperty("probes").EnumerateArray()
                    .Single(item => item.GetProperty("name").GetString() == probeName);
                Assert.Equal(SdkSurfaceHostModes, probe.GetProperty("cases").EnumerateArray()
                    .Select(@case => @case.GetProperty("hostMode").GetString()));
                Assert.All(probe.GetProperty("cases").EnumerateArray(), @case =>
                {
                    Assert.True(@case.TryGetProperty("passed", out _));
                    Assert.True(@case.TryGetProperty("failureStage", out _));
                    Assert.True(@case.TryGetProperty("artifactSha256", out _));
                });
            }
        }
    }

    [Fact]
    public void DecisionRecord_RecordsSelectedProfileStructuredInjectionAndGuardEvidence()
    {
        var markdown = File.ReadAllText(LocateRepoFile("docs", "decisions", "0001-platform-protocol.md"));
        Assert.Contains("delegated typed structured-output replacement", markdown, StringComparison.Ordinal);
        Assert.Contains("cancellation/progress injection without schema leakage", markdown, StringComparison.Ordinal);
        Assert.Contains("100000-byte frame boundary", markdown, StringComparison.Ordinal);
        Assert.Contains("127=accepted, 128=accepted, 129=rejected before dispatch", markdown, StringComparison.Ordinal);
        Assert.Contains("Int64 minimum/zero/maximum numeric IDs", markdown, StringComparison.Ordinal);
        using var decision = LoadDecisionEvidence();
        var selectedId = decision.RootElement.GetProperty("selectedCandidateId").GetString()!;
        var selected = decision.RootElement.GetProperty("candidateResults").EnumerateArray()
            .Single(candidate => candidate.GetProperty("candidateId").GetString() == selectedId);
        foreach (var probeName in SdkSurfaceProbeNames)
        {
            var probe = selected.GetProperty("probes").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == probeName);
            foreach (var @case in probe.GetProperty("cases").EnumerateArray())
            {
                var hostMode = @case.GetProperty("hostMode").GetString();
                var evidenceArtifact = @case.GetProperty("artifactSha256").EnumerateObject()
                    .Single(artifact => artifact.Name.EndsWith($"{probeName}.{hostMode}.evidence.json", StringComparison.Ordinal));
                AssertSha256(evidenceArtifact.Value.GetString());
                Assert.Contains(evidenceArtifact.Name, markdown, StringComparison.Ordinal);
                Assert.Contains(evidenceArtifact.Value.GetString()!, markdown, StringComparison.Ordinal);
                if (File.Exists(evidenceArtifact.Name))
                {
                    Assert.Equal(evidenceArtifact.Value.GetString(), Sha256(evidenceArtifact.Name), ignoreCase: true);
                    using var evidence = JsonDocument.Parse(File.ReadAllBytes(evidenceArtifact.Name));
                    Assert.Equal(selected.GetProperty("protocolRevision").GetString(), evidence.RootElement.GetProperty("protocolRevision").GetString());
                    Assert.Equal(selected.GetProperty("protocolProfile").GetString(), evidence.RootElement.GetProperty("protocolProfile").GetString());
                    Assert.True(evidence.RootElement.GetProperty("structuredOutput").GetProperty("structuredContentReplaced").GetBoolean());
                    Assert.True(evidence.RootElement.GetProperty("cancellationProgress").GetProperty("injectedParametersAbsentFromSchema").GetBoolean());
                    Assert.Equal(100000, evidence.RootElement.GetProperty("framingAndRequestIds").GetProperty("productionFrameLimit").GetInt32());
                    Assert.True(evidence.RootElement.GetProperty("framingAndRequestIds").GetProperty("oversizedIdRejectedBeforeDispatch").GetBoolean());
                }
            }
        }
        foreach (var seam in decision.RootElement.GetProperty("selectedPublicSdkSeams").EnumerateArray())
        {
            Assert.Contains(seam.GetString()!, markdown, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DecisionRecord_ListsSupportedWindowsAndArchitectureMatrix()
    {
        using var matrix = LoadMatrix();
        using var decision = LoadDecisionEvidence();
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse(matrix.RootElement.GetProperty("windowsArchitectureMatrix").GetRawText()),
            JsonNode.Parse(decision.RootElement.GetProperty("windowsArchitectureMatrix").GetRawText())));
        var selected = decision.RootElement.GetProperty("candidateResults").EnumerateArray()
            .Single(candidate => candidate.GetProperty("candidateId").GetString() == decision.RootElement.GetProperty("selectedCandidateId").GetString());
        var architecture = selected.GetProperty("probes").EnumerateArray()
            .Single(probe => probe.GetProperty("name").GetString() == "windows-architecture-matrix");
        Assert.True(architecture.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void DecisionRecord_ContainsNoUnverifiedPassOrReviewClaim()
    {
        var markdown = File.ReadAllText(LocateRepoFile("docs", "decisions", "0001-platform-protocol.md"));
        Assert.DoesNotContain('\r', markdown);
        Assert.DoesNotMatch(new Regex(@"\b(?:TBD|TODO|assumed pass|unverified pass|external review passed|independent review passed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), markdown);
        using var decision = LoadDecisionEvidence();
        var selectedId = decision.RootElement.GetProperty("selectedCandidateId").GetString();
        foreach (var indexed in decision.RootElement.GetProperty("candidateResults").EnumerateArray())
        {
            var candidateId = indexed.GetProperty("candidateId").GetString()!;
            AssertLocalCandidateResultMatchesIfPresent(indexed);
            if (candidateId == selectedId)
            {
                Assert.All(indexed.GetProperty("probes").EnumerateArray(), probe => Assert.True(probe.GetProperty("passed").GetBoolean()));
            }
        }
    }

    [Fact]
    public void DecisionArtifacts_AreCheckedOutWithLfLineEndings()
    {
        var attributes = File.ReadAllLines(LocateRepoFile(".gitattributes"))
            .Select(line => line.Trim())
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("docs/decisions/*.md text eol=lf", attributes);
        Assert.Contains("eng/*.json text eol=lf", attributes);
        Assert.Contains("scripts/*.ps1 text eol=lf", attributes);
    }

    [Fact]
    public void Freeze_RejectsMissingDuplicateExtraOrFailedRequiredProbe()
    {
        var paths = Enumerable.Range(0, 5).Select(_ => WriteResultFixture(SdkSurfaceHostModes)).ToArray();
        try
        {
            Assert.Equal(0, InvokeFreezeValidation(paths[0]));

            var missing = JsonNode.Parse(File.ReadAllText(paths[1]))!.AsObject();
            missing["probes"]!.AsArray().RemoveAt(0);
            File.WriteAllText(paths[1], missing.ToJsonString());
            Assert.NotEqual(0, InvokeFreezeValidation(paths[1]));

            var duplicate = JsonNode.Parse(File.ReadAllText(paths[2]))!.AsObject();
            duplicate["probes"]!.AsArray().Add(duplicate["probes"]![0]!.DeepClone());
            File.WriteAllText(paths[2], duplicate.ToJsonString());
            Assert.NotEqual(0, InvokeFreezeValidation(paths[2]));

            var extra = JsonNode.Parse(File.ReadAllText(paths[3]))!.AsObject();
            var extraProbe = extra["probes"]![0]!.DeepClone();
            extraProbe!["name"] = "extra-probe";
            extraProbe["cases"]![0]!["scenario"] = "extra-probe";
            extra["probes"]!.AsArray().Add(extraProbe);
            File.WriteAllText(paths[3], extra.ToJsonString());
            Assert.NotEqual(0, InvokeFreezeValidation(paths[3]));

            var failed = JsonNode.Parse(File.ReadAllText(paths[4]))!.AsObject();
            var failedProbe = failed["probes"]!.AsArray().Single(probe => probe!["name"]!.GetValue<string>() == "release-unit-tests")!;
            failedProbe["exitCode"] = 1;
            failedProbe["passed"] = false;
            failedProbe["cases"]![0]!["exitCode"] = 1;
            failedProbe["cases"]![0]!["passed"] = false;
            failedProbe["cases"]![0]!["failureStage"] = "probe";
            File.WriteAllText(paths[4], failed.ToJsonString());
            Assert.NotEqual(0, InvokeFreezeValidation(paths[4]));
        }
        finally
        {
            foreach (var path in paths) DeleteResultFixture(path);
        }
    }

    [Fact]
    public void Freeze_RejectsSdkSurfaceProbeWithMissingDuplicateExtraOrFailedHostMode()
    {
        var paths = Enumerable.Range(0, 5).Select(_ => WriteResultFixture(SdkSurfaceHostModes)).ToArray();
        try
        {
            Assert.Equal(0, InvokeFreezeValidation(paths[0]));
            foreach (var (path, mutation) in new[]
            {
                (paths[1], "missing"), (paths[2], "duplicate"), (paths[3], "extra"), (paths[4], "failed"),
            })
            {
                var result = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "delegated-typed-tool-structured-output")!;
                var cases = probe["cases"]!.AsArray();
                if (mutation == "missing") cases.RemoveAt(0);
                if (mutation == "duplicate") cases.Add(cases[0]!.DeepClone());
                if (mutation == "extra")
                {
                    var extra = cases[0]!.DeepClone();
                    extra!["hostMode"] = "unexpected";
                    cases.Add(extra);
                }
                if (mutation == "failed")
                {
                    cases[0]!["exitCode"] = 1;
                    cases[0]!["passed"] = false;
                    cases[0]!["failureStage"] = "profile";
                    probe["exitCode"] = 1;
                    probe["passed"] = false;
                }
                RebuildAggregateArtifacts(probe);
                File.WriteAllText(path, result.ToJsonString());
                Assert.NotEqual(0, InvokeFreezeValidation(path));
            }
        }
        finally
        {
            foreach (var path in paths) DeleteResultFixture(path);
        }
    }

    [Fact]
    public void Freeze_RejectsSdkEvidenceForWrongProfileRevisionBinaryOrBoundary()
    {
        var paths = Enumerable.Range(0, 5).Select(_ => WriteResultFixture(SdkSurfaceHostModes)).ToArray();
        try
        {
            Assert.Equal(0, InvokeFreezeValidation(paths[0]));
            var mutations = new Action<JsonObject>[]
            {
                evidence => evidence["protocolProfile"] = "stateless-discovery",
                evidence => evidence["protocolRevision"] = "1900-01-01",
                evidence => evidence["launchIdentity"]!["preLaunchSha256"] = new string('f', 64),
                evidence => evidence["framingAndRequestIds"]!["ascii128Bytes"] = 127,
            };
            for (var index = 0; index < mutations.Length; index++)
            {
                var result = JsonNode.Parse(File.ReadAllText(paths[index + 1]))!.AsObject();
                var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "selected-profile-handshake")!;
                var @case = probe["cases"]!.AsArray().Single(node => node!["hostMode"]!.GetValue<string>() == "normal")!;
                var evidencePath = @case["artifactSha256"]!.AsObject().Select(property => property.Key)
                    .Single(candidate => candidate.EndsWith("selected-profile-handshake.normal.evidence.json", StringComparison.Ordinal));
                var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
                mutations[index](evidence);
                File.WriteAllText(evidencePath, evidence.ToJsonString());
                var hash = Sha256(evidencePath);
                @case["artifactSha256"]![evidencePath] = hash;
                probe["artifactSha256"]![evidencePath] = hash;
                RefreshArchitectureEvidence(result);
                File.WriteAllText(paths[index + 1], result.ToJsonString());
                Assert.NotEqual(0, InvokeFreezeValidation(paths[index + 1]));
            }
        }
        finally
        {
            foreach (var path in paths) DeleteResultFixture(path);
        }
    }

    private static JsonDocument LoadMatrix() => JsonDocument.Parse(File.ReadAllBytes(LocateRepoFile("eng", "platform-candidates.v1.json")));

    private static readonly string[] SelectedPlatformPropertyNames =
    [
        "WprMcpSdkVersion",
        "WprMcpTargetFramework",
        "WprMcpMcpSdkVersion",
        "WprMcpProtocolRevision",
        "WprMcpProtocolProfile",
    ];

    private static Dictionary<string, string> ReadSelectedPlatformProperties()
    {
        var document = XDocument.Load(LocateRepoFile("eng", "SelectedPlatform.props"));
        var group = Assert.Single(document.Root!.Elements("PropertyGroup"));
        Assert.Equal(SelectedPlatformPropertyNames, group.Elements().Select(element => element.Name.LocalName));
        return group.Elements().ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
    }

    private static bool SelectedValuesMatchCandidate(IReadOnlyDictionary<string, string> selected, JsonElement candidate) =>
        selected["WprMcpSdkVersion"] == candidate.GetProperty("sdkVersion").GetString() &&
        selected["WprMcpTargetFramework"] == candidate.GetProperty("targetFramework").GetString() &&
        selected["WprMcpMcpSdkVersion"] == candidate.GetProperty("mcpSdkVersion").GetString() &&
        selected["WprMcpProtocolRevision"] == candidate.GetProperty("protocolRevision").GetString() &&
        selected["WprMcpProtocolProfile"] == candidate.GetProperty("protocolProfile").GetString();

    private static string FindSelectedCandidateId(IReadOnlyDictionary<string, string> selected)
    {
        using var matrix = LoadMatrix();
        return Assert.Single(matrix.RootElement.GetProperty("candidates").EnumerateArray()
            .Where(candidate => SelectedValuesMatchCandidate(selected, candidate)))
            .GetProperty("id").GetString()!;
    }

    private static string? TryCandidateResultPath(string candidateId)
    {
        var path = Path.Combine(LocateRepoRoot(), "artifacts", "platform-matrix", $"{candidateId}.result.json");
        return File.Exists(path) ? path : null;
    }

    private static void AssertLocalCandidateResultMatchesIfPresent(JsonElement indexed, bool validate = false)
    {
        var candidateId = indexed.GetProperty("candidateId").GetString()!;
        var path = TryCandidateResultPath(candidateId);
        if (path is null)
        {
            return;
        }

        using var actual = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(actual.RootElement.GetRawText()), JsonNode.Parse(indexed.GetRawText())));
        if (validate)
        {
            Assert.Equal(0, InvokeResultValidation(path, candidateId));
        }
    }

    private static void AssertSha256(string? value)
    {
        Assert.NotNull(value);
        Assert.Matches("^[0-9a-f]{64}$", value!);
    }

    private static JsonDocument LoadDecisionEvidence()
    {
        var markdown = File.ReadAllText(LocateRepoFile("docs", "decisions", "0001-platform-protocol.md"));
        const string startMarker = "<!-- BEGIN PLATFORM DECISION EVIDENCE -->";
        const string endMarker = "<!-- END PLATFORM DECISION EVIDENCE -->";
        var start = markdown.IndexOf(startMarker, StringComparison.Ordinal);
        var end = markdown.IndexOf(endMarker, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Decision record omitted its machine-readable evidence index.");
        var fenced = markdown[(start + startMarker.Length)..end];
        var jsonStart = fenced.IndexOf("```json", StringComparison.Ordinal);
        Assert.True(jsonStart >= 0, "Decision evidence index omitted its JSON fence.");
        jsonStart = fenced.IndexOf('\n', jsonStart) + 1;
        var jsonEnd = fenced.LastIndexOf("```", StringComparison.Ordinal);
        Assert.True(jsonStart > 0 && jsonEnd > jsonStart, "Decision evidence JSON fence was incomplete.");
        return JsonDocument.Parse(fenced[jsonStart..jsonEnd]);
    }

    private static string LocateBuiltSdkCandidateProbe()
    {
        var testOutput = new DirectoryInfo(AppContext.BaseDirectory);
        return LocateRepoFile(
            "tools",
            "sdkcandidateprobe",
            "bin",
            testOutput.Parent!.Name,
            testOutput.Name,
            "sdkcandidateprobe.dll");
    }

    private static string LocateCurrentDotNetHost()
    {
        var configured = Environment.GetEnvironmentVariable("WPRMCP_DOTNET_HOST")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return Path.GetFullPath(configured);
        }

        var discovered = RunProcess("where.exe", ["dotnet.exe"], LocateRepoRoot());
        Assert.Equal(0, discovered.ExitCode);
        var path = discovered.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0];
        Assert.True(File.Exists(path), $"Expected dotnet host at {path}");
        return Path.GetFullPath(path);
    }

    private static string LocateRuntimeBinary(string dotnetRoot, string relativeVersionsRoot, string majorPrefix, string fileName)
    {
        var versionsRoot = Path.Combine(dotnetRoot, relativeVersionsRoot);
        Assert.True(Directory.Exists(versionsRoot), $"Expected runtime versions under {versionsRoot}");
        var versionDirectory = Directory.GetDirectories(versionsRoot)
            .Where(path => Path.GetFileName(path).StartsWith(majorPrefix, StringComparison.Ordinal))
            .OrderDescending(StringComparer.Ordinal)
            .FirstOrDefault();
        Assert.NotNull(versionDirectory);
        return Assert.Single(Directory.GetFiles(versionDirectory!, fileName, SearchOption.AllDirectories));
    }

    private static byte[] CreateRawProbeCall(string idJson, string value, string lineEnding, int? exactPayloadBytes = null)
    {
        var prefix = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson},\"method\":\"tools/call\",\"params\":{{\"name\":\"sdk_probe_echo\",\"arguments\":{{\"value\":\"";
        const string suffix = "\"}}}";
        var fixedBytes = Encoding.UTF8.GetByteCount(prefix + suffix);
        var body = exactPayloadBytes.HasValue ? new string('v', exactPayloadBytes.Value - fixedBytes) : value;
        return Encoding.UTF8.GetBytes(prefix + body + suffix + lineEnding);
    }

    private static string[] ReadStringArray(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WprMcp.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string LocateRepoFile(params string[] parts)
    {
        var path = Path.Combine(new[] { LocateRepoRoot() }.Concat(parts).ToArray());
        Assert.True(File.Exists(path), $"Expected repository artifact at {path}");
        return path;
    }

    private static JsonDocument InvokeRunnerJson(string command)
    {
        var scriptPath = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1");
        var escapedScriptPath = scriptPath.Replace("'", "''", StringComparison.Ordinal);
        var result = RunPowerShell($". '{escapedScriptPath}' -CandidateId net8-stable-stateful; {command}");
        Assert.True(result.ExitCode == 0, $"Runner contract command failed. stdout: {result.Stdout} stderr: {result.Stderr}");
        return JsonDocument.Parse(result.Stdout);
    }

    private static int InvokeResultValidation(string path, string candidateId = "net10-stable-stateful")
    {
        var scriptPath = LocateRepoFile("scripts", "Test-PlatformCandidate.ps1").Replace("'", "''", StringComparison.Ordinal);
        var fixturePath = path.Replace("'", "''", StringComparison.Ordinal);
        return RunPowerShell($". '{scriptPath}' -CandidateId {candidateId}; if (Test-PlatformCandidateResult -Path '{fixturePath}' -CandidateId {candidateId}) {{ exit 0 }} else {{ exit 3 }}").ExitCode;
    }

    private static int InvokeFreezeValidation(string path)
    {
        var scriptPath = LocateRepoFile("scripts", "Freeze-PlatformDecision.ps1").Replace("'", "''", StringComparison.Ordinal);
        var fixturePath = path.Replace("'", "''", StringComparison.Ordinal);
        return RunPowerShell($". '{scriptPath}'; if (Test-PlatformDecisionInput -ResultPath '{fixturePath}' -CandidateId net10-stable-stateful -ExpectedCommit '{new string('d', 40)}') {{ exit 0 }} else {{ exit 3 }}").ExitCode;
    }

    private static (int ExitCode, string Stdout, string Stderr) RunPowerShell(
        string command,
        IReadOnlyDictionary<string, string>? inheritedEnvironment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = LocateRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(command)));
        if (inheritedEnvironment is not null)
        {
            foreach (var entry in inheritedEnvironment)
            {
                startInfo.Environment[entry.Key] = entry.Value;
            }
        }
        startInfo.Environment.Remove("PSModulePath");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(30_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(exited, $"PowerShell runner contract command timed out. stdout: {stdout} stderr: {stderr}");
        return (process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(
        string executable,
        string[] arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var entry in environment) startInfo.Environment[entry.Key] = entry.Value;
        }
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdoutTask = process!.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exited = process.WaitForExit(15_000);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.WaitForExit(5_000);
        }
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(exited, $"Process '{executable}' timed out. stdout: {stdout} stderr: {stderr}");
        return (process.ExitCode, stdout.Trim(), stderr.Trim());
    }

    private static string WriteResultFixture(IEnumerable<string> hostModes)
    {
        var fixtureRoot = Path.Combine(Path.GetTempPath(), $"platform-result-{Guid.NewGuid():N}");
        var retainedRoot = Path.Combine(fixtureRoot, "net10-stable-stateful");
        Directory.CreateDirectory(Path.Combine(retainedRoot, "logs"));
        var stdoutPath = Path.Combine(retainedRoot, "logs", "retained.stdout.log");
        var stderrPath = Path.Combine(retainedRoot, "logs", "retained.stderr.log");
        var artifactPath = Path.Combine(retainedRoot, "retained.bin");
        File.WriteAllText(stdoutPath, "stdout");
        File.WriteAllText(stderrPath, "stderr");
        File.WriteAllBytes(artifactPath, [1, 2, 3, 4]);
        var stdoutHash = Sha256(stdoutPath);
        var stderrHash = Sha256(stderrPath);
        var artifactHash = Sha256(artifactPath);
        const string packageVersion = "1.4.1";
        const string registrationUrl = "https://example.test/registration/1.4.1.json";
        const string catalogUrl = "https://example.test/catalog/1.4.1.json";
        const string packageContentUrl = "https://example.test/package/1.4.1.nupkg";
        const string observedUtc = "2026-07-29T00:00:00Z";
        var nugetRoot = Path.Combine(retainedRoot, "nuget-evidence");
        Directory.CreateDirectory(nugetRoot);
        var retainedPackagePath = Path.Combine(nugetRoot, "modelcontextprotocol.1.4.1.nupkg");
        var retainedRegistrationPath = Path.Combine(nugetRoot, "modelcontextprotocol.1.4.1.registration.json");
        var retainedCatalogPath = Path.Combine(nugetRoot, "modelcontextprotocol.1.4.1.catalog.json");
        var retainedVerificationPath = Path.Combine(nugetRoot, "modelcontextprotocol.1.4.1.verification.json");
        File.WriteAllBytes(retainedPackagePath, [7, 11, 13, 17]);
        var publishedHashBase64 = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(retainedPackagePath)));
        File.WriteAllText(retainedRegistrationPath, new JsonObject
        {
            ["@id"] = registrationUrl,
            ["listed"] = true,
            ["catalogEntry"] = catalogUrl,
            ["packageContent"] = packageContentUrl,
        }.ToJsonString());
        File.WriteAllText(retainedCatalogPath, new JsonObject
        {
            ["@id"] = catalogUrl,
            ["id"] = "ModelContextProtocol",
            ["version"] = packageVersion,
            ["listed"] = true,
            ["packageHashAlgorithm"] = "SHA512",
            ["packageHash"] = publishedHashBase64,
        }.ToJsonString());
        File.WriteAllText(retainedVerificationPath, JsonSerializer.Serialize(new
        {
            packageId = "ModelContextProtocol",
            packageVersion,
            registrationUrl,
            catalogUrl,
            packageContentUrl,
            hashAlgorithm = "SHA512",
            publishedHashBase64,
            downloadedHashBase64 = publishedHashBase64,
            observedUtc,
            retrievalSource = "Network",
        }));
        var nugetArtifacts = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [retainedPackagePath] = Sha256(retainedPackagePath),
            [retainedRegistrationPath] = Sha256(retainedRegistrationPath),
            [retainedCatalogPath] = Sha256(retainedCatalogPath),
            [retainedVerificationPath] = Sha256(retainedVerificationPath),
        };
        var candidateNuGetConfig = Path.Combine(retainedRoot, "NuGet.Config");
        File.WriteAllText(candidateNuGetConfig, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <packageSources>
                <clear />
                <add key="verified-candidate" value="{nugetRoot}" />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="verified-candidate"><package pattern="ModelContextProtocol" /></packageSource>
                <packageSource key="nuget.org"><package pattern="*" /></packageSource>
              </packageSourceMapping>
            </configuration>
            """);
        var installedPackageRoot = Path.Combine(retainedRoot, "packages", "modelcontextprotocol", packageVersion);
        Directory.CreateDirectory(installedPackageRoot);
        var restoredPackagePath = Path.Combine(installedPackageRoot, $"modelcontextprotocol.{packageVersion}.nupkg");
        var restoredSha512Path = Path.Combine(installedPackageRoot, $"modelcontextprotocol.{packageVersion}.nupkg.sha512");
        var restoredMetadataPath = Path.Combine(installedPackageRoot, ".nupkg.metadata");
        File.Copy(retainedPackagePath, restoredPackagePath);
        File.WriteAllText(restoredSha512Path, publishedHashBase64);
        File.WriteAllText(restoredMetadataPath, JsonSerializer.Serialize(new
        {
            version = 2,
            contentHash = publishedHashBase64,
            source = nugetRoot,
        }));
        var restoreArtifacts = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var restoreProbeName in new[] { "normal-restore", "win-x64-restore" })
        {
            var restoreEvidencePath = Path.Combine(retainedRoot, $"{restoreProbeName}.package.evidence.json");
            File.WriteAllText(restoreEvidencePath, JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                probeName = restoreProbeName,
                packageId = "ModelContextProtocol",
                packageVersion,
                candidateSource = nugetRoot,
                configPath = candidateNuGetConfig,
                configSha256 = Sha256(candidateNuGetConfig),
                verifiedPackagePath = retainedPackagePath,
                verifiedPackageSha256 = Sha256(retainedPackagePath),
                restoredPackagePath,
                restoredPackageSha256 = Sha256(restoredPackagePath),
                restoredSha512Path,
                restoredMetadataPath,
                metadataSource = nugetRoot,
                publishedHashBase64,
                restoreContentHashBase64 = publishedHashBase64,
                passed = true,
            }));
            restoreArtifacts[restoreProbeName] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [candidateNuGetConfig] = Sha256(candidateNuGetConfig),
                [retainedPackagePath] = Sha256(retainedPackagePath),
                [restoreEvidencePath] = Sha256(restoreEvidencePath),
                [restoredPackagePath] = Sha256(restoredPackagePath),
                [restoredSha512Path] = Sha256(restoredSha512Path),
                [restoredMetadataPath] = Sha256(restoredMetadataPath),
            };
        }
        var serverPublish = Path.Combine(retainedRoot, "publishes", "server-self-contained");
        Directory.CreateDirectory(serverPublish);
        var serverPath = Path.Combine(serverPublish, "WprMcp.exe");
        File.WriteAllBytes(serverPath, [9, 8, 7, 6]);
        File.WriteAllText(Path.Combine(serverPublish, "WprMcp.deps.json"), JsonSerializer.Serialize(new
        {
            runtimeTarget = new { name = ".NETCoreApp,Version=v10.0/win-x64" },
        }));
        foreach (var runtimeBinary in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
        {
            File.WriteAllText(Path.Combine(serverPublish, runtimeBinary), runtimeBinary);
        }
        var nativeDirectory = Path.Combine(serverPublish, "amd64");
        Directory.CreateDirectory(nativeDirectory);
        var msdiaPath = Path.Combine(nativeDirectory, "msdia140.dll");
        var kernelTraceControlPath = Path.Combine(nativeDirectory, "KernelTraceControl.dll");
        File.WriteAllText(msdiaPath, "fixture-msdia");
        File.WriteAllText(kernelTraceControlPath, "fixture-kernel-trace-control");
        var serverHash = Sha256(serverPath);
        var stdioEvidencePath = Path.Combine(retainedRoot, "self-contained-stdio.evidence.json");
        var stdioRawStdout = Path.Combine(retainedRoot, "self-contained-stdio.server.stdout.log");
        var stdioRawStderr = Path.Combine(retainedRoot, "self-contained-stdio.server.stderr.log");
        File.WriteAllText(stdioRawStdout, "{\"id\":\"initialize\"}\n{\"id\":\"list\"}\n{\"id\":\"unknown-call\"}\n");
        File.WriteAllText(stdioRawStderr, string.Empty);
        File.WriteAllText(stdioEvidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            protocolRevision = "2025-11-25",
            protocolProfile = "stateful",
            orderedMessageMethodTranscript = new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" },
            serializedMetadataKeysByMethod = new Dictionary<string, string[]>
            {
                ["tools/list"] = [],
                ["tools/call"] = [],
            },
            observedOutcomes = new
            {
                listedToolCount = 1,
                unknownCallTerminalError = true,
            },
            launch = new
            {
                path = serverPath,
                publishRoot = serverPublish,
                relativePath = "WprMcp.exe",
                expectedLaunchSha256 = serverHash,
                sha256Before = serverHash,
                sha256After = serverHash,
                processId = 123,
                childProcessArchitecture = "X64",
                observerOsArchitecture = "X64",
                requestedRuntimeIdentifier = "win-x64",
                publishRuntimeIdentifier = "win-x64",
            },
            correlatedResponseCount = 3,
            passed = true,
        }));
        var stdioArtifacts = new Dictionary<string, string>
        {
            [serverPath] = serverHash,
            [stdioEvidencePath] = Sha256(stdioEvidencePath),
            [stdioRawStdout] = Sha256(stdioRawStdout),
            [stdioRawStderr] = Sha256(stdioRawStderr),
        };
        var publishManifestRoot = Path.Combine(retainedRoot, "publish-manifests");
        Directory.CreateDirectory(publishManifestRoot);
        var dotnetExecutable = LocateCurrentDotNetHost();
        var fixtureDotNetRoot = Path.GetDirectoryName(dotnetExecutable)!;
        var fixtureHostFxr = LocateRuntimeBinary(fixtureDotNetRoot, Path.Combine("host", "fxr"), "10.", "hostfxr.dll");
        var fixtureHostPolicy = LocateRuntimeBinary(fixtureDotNetRoot, Path.Combine("shared", "Microsoft.NETCore.App"), "10.", "hostpolicy.dll");
        var sdkHosts = new Dictionary<string, (string Root, string Host, string Entry, string Manifest, string? RetainedHostFxr, string? RetainedHostPolicy)>(StringComparer.Ordinal);
        foreach (var hostMode in hostModes.Distinct(StringComparer.Ordinal))
        {
            var publishLeaf = hostMode switch
            {
                "normal" => "sdk-probe-normal",
                "win-x64-framework-dependent" => "sdk-probe-framework-dependent",
                "win-x64-self-contained" => "sdk-probe-self-contained",
                _ => $"sdk-probe-{hostMode}",
            };
            var publishRoot = Path.Combine(retainedRoot, "publishes", publishLeaf);
            Directory.CreateDirectory(publishRoot);
            foreach (var name in new[] { "sdkcandidateprobe.exe", "sdkcandidateprobe.dll" })
            {
                File.WriteAllText(Path.Combine(publishRoot, name), $"{hostMode}:{name}");
            }
            var ridTarget = hostMode == "normal" ? ".NETCoreApp,Version=v10.0" : ".NETCoreApp,Version=v10.0/win-x64";
            File.WriteAllText(Path.Combine(publishRoot, "sdkcandidateprobe.deps.json"), JsonSerializer.Serialize(new
            {
                runtimeTarget = new { name = ridTarget },
            }));
            var runtimeOptions = hostMode == "win-x64-self-contained"
                ? new JsonObject { ["includedFrameworks"] = new JsonArray(new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.0" }) }
                : new JsonObject { ["framework"] = new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = "10.0.0" } };
            File.WriteAllText(Path.Combine(publishRoot, "sdkcandidateprobe.runtimeconfig.json"), new JsonObject
            {
                ["runtimeOptions"] = runtimeOptions,
            }.ToJsonString());
            if (hostMode == "win-x64-self-contained")
            {
                foreach (var runtimeBinary in new[] { "coreclr.dll", "hostfxr.dll", "hostpolicy.dll" })
                {
                    File.WriteAllText(Path.Combine(publishRoot, runtimeBinary), $"{hostMode}:{runtimeBinary}");
                }
            }
            string? retainedHostFxr = null;
            string? retainedHostPolicy = null;
            if (hostMode != "win-x64-self-contained")
            {
                var retainedRuntimeRoot = Path.Combine(retainedRoot, "framework-runtime", hostMode);
                Directory.CreateDirectory(retainedRuntimeRoot);
                retainedHostFxr = Path.Combine(retainedRuntimeRoot, "hostfxr.dll");
                retainedHostPolicy = Path.Combine(retainedRuntimeRoot, "hostpolicy.dll");
                File.Copy(fixtureHostFxr, retainedHostFxr);
                File.Copy(fixtureHostPolicy, retainedHostPolicy);
            }
            var manifestPath = Path.Combine(publishManifestRoot, $"{hostMode}.json");
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                hostMode,
                publishRoot,
                publishExitCode = 0,
                frameworkRuntime = hostMode == "win-x64-self-contained" ? null : new
                {
                    dotnetRoot = fixtureDotNetRoot,
                    sourceHostFxrPath = fixtureHostFxr,
                    sourceHostFxrSha256 = Sha256(fixtureHostFxr),
                    retainedHostFxrPath = retainedHostFxr,
                    retainedHostFxrSha256 = Sha256(retainedHostFxr!),
                    sourceHostPolicyPath = fixtureHostPolicy,
                    sourceHostPolicySha256 = Sha256(fixtureHostPolicy),
                    retainedHostPolicyPath = retainedHostPolicy,
                    retainedHostPolicySha256 = Sha256(retainedHostPolicy!),
                },
                files = Directory.GetFiles(publishRoot, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal)
                    .Select(file => new
                    {
                        relativePath = Path.GetRelativePath(publishRoot, file).Replace('\\', '/'),
                        sha256 = Sha256(file),
                    }),
            }));
            sdkHosts[hostMode] = (publishRoot, Path.Combine(publishRoot, "sdkcandidateprobe.exe"), Path.Combine(publishRoot, "sdkcandidateprobe.dll"), manifestPath, retainedHostFxr, retainedHostPolicy);
        }

        Dictionary<string, string> CreateSdkArtifacts(string probeName, string hostMode)
        {
            var host = sdkHosts[hostMode];
            var hostHash = Sha256(host.Host);
            var entryHash = Sha256(host.Entry);
            var evidencePath = Path.Combine(retainedRoot, $"{probeName}.{hostMode}.evidence.json");
            if (!File.Exists(evidencePath))
            {
                File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    hostMode,
                    protocolRevision = "2025-11-25",
                    protocolProfile = "stateful",
                    offeredRevision = "2025-11-25",
                    acceptedRevision = "2025-11-25",
                    orderedMessageMethodTranscript = new[] { "initialize", "notifications/initialized", "tools/list", "tools/call" },
                    serializedMetadataKeysByMethod = new Dictionary<string, string[]>
                    {
                        ["initialize"] = [],
                        ["tools/list"] = [],
                        ["tools/call"] = ["progressToken"],
                    },
                    InputSchemaPropertyNames = new[] { "value" },
                    launchIdentity = new
                    {
                        retainedLaunchPath = host.Host,
                        preLaunchSha256 = hostHash,
                        postLaunchSha256 = hostHash,
                        childProcessId = 456,
                        childProcessPath = host.Host,
                        childProcessSha256 = hostHash,
                        childProcessArchitecture = "X64",
                        configuredDotNetRoot = fixtureDotNetRoot,
                        configuredDotNetRootX64 = fixtureDotNetRoot,
                        runtimeIdentity = new
                        {
                            processId = 456,
                            processPath = host.Host,
                            processPathSha256 = hostHash,
                            entryAssemblyPath = host.Entry,
                            entryAssemblySha256 = entryHash,
                            osPlatform = "Windows",
                            osArchitecture = "X64",
                            processArchitecture = "X64",
                            runtimeIdentifier = "win-x64",
                            is64BitOperatingSystem = true,
                            is64BitProcess = true,
                            loadedHostFxrPath = hostMode == "win-x64-self-contained" ? Path.Combine(host.Root, "hostfxr.dll") : fixtureHostFxr,
                            loadedHostFxrSha256 = Sha256(hostMode == "win-x64-self-contained" ? Path.Combine(host.Root, "hostfxr.dll") : fixtureHostFxr),
                            loadedHostPolicyPath = hostMode == "win-x64-self-contained" ? Path.Combine(host.Root, "hostpolicy.dll") : fixtureHostPolicy,
                            loadedHostPolicySha256 = Sha256(hostMode == "win-x64-self-contained" ? Path.Combine(host.Root, "hostpolicy.dll") : fixtureHostPolicy),
                        },
                        passed = true,
                    },
                    structuredOutput = new
                    {
                        textReplaced = true,
                        structuredContentReplaced = true,
                        innerTextObserved = true,
                        innerStructuredObserved = true,
                        inputSchemaPresent = true,
                        outputSchemaPresent = true,
                        annotationsPresent = true,
                        isError = (bool?)null,
                        preservedIsError = (bool?)null,
                    },
                    cancellationProgress = new
                    {
                        normalProgressNotificationCount = 1,
                        cancellationProgressNotificationCount = 1,
                        totalProgressNotificationCount = 2,
                        cancellationObserved = true,
                        handlerCancellationObservationCount = 1,
                        injectedParametersAbsentFromSchema = true,
                    },
                    framingAndRequestIds = new
                    {
                        productionFrameLimit = 100000,
                        decodedRequestIdLimit = 128,
                        ascii127Bytes = 127,
                        ascii128Bytes = 128,
                        directUtf8Bytes = 2,
                        escapedUtf8Bytes = 2,
                        numericIds = new[] { long.MinValue, 0, long.MaxValue },
                        acceptedIdCases = Enumerable.Repeat(true, 7).ToArray(),
                        exactProductionFrameAccepted = true,
                        oversizedIdRejectedBeforeDispatch = true,
                        oversizedFrameRejectedBeforeDeserialization = true,
                        oversizedFrameObservation = new
                        {
                            ExitCode = 2,
                            Stdout = string.Empty,
                            Stderr = "sdkcandidateprobe: frame limit exceeded",
                            IncomingNextCount = 0,
                            HandlerInvocationCount = 0,
                        },
                        loweredCapIsolatedCrRejected = true,
                        bomAtStartRejected = true,
                        bomAnywhereRejected = true,
                        lfAndCrLfAccepted = true,
                    },
                    InvocationCount = 9,
                    passed = true,
                }));
            }
            var artifacts = new Dictionary<string, string>
            {
                [host.Host] = hostHash,
                [host.Manifest] = Sha256(host.Manifest),
                [evidencePath] = Sha256(evidencePath),
            };
            if (host.RetainedHostFxr is not null) artifacts[host.RetainedHostFxr] = Sha256(host.RetainedHostFxr);
            if (host.RetainedHostPolicy is not null) artifacts[host.RetainedHostPolicy] = Sha256(host.RetainedHostPolicy);
            return artifacts;
        }
        var goldenEvidencePath = Path.Combine(retainedRoot, "golden-traceevent-reads.evidence.json");
        File.WriteAllText(goldenEvidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            probeName = "golden-traceevent-reads",
            processArchitecture = "X64",
            fixtures = new[]
            {
                "perfview_gcevents.etl",
                "small_cpu.etl",
                "small_fileio.etl",
                "small_memory.etl",
                "small_mmap.etl",
                "small_wait_bound.etl",
            }.Select((name, index) => new
            {
                name,
                sourceSha256 = new string((char)('a' + index), 64),
                copySha256 = new string((char)('a' + index), 64),
                eventCount = index + 1,
                durationTicks = index + 1,
                temporaryCopyUsed = true,
            }),
            passed = true,
        }));
        var goldenArtifacts = new Dictionary<string, string>
        {
            [goldenEvidencePath] = Sha256(goldenEvidencePath),
        };
        var nativeEvidencePath = Path.Combine(retainedRoot, "native-layout.evidence.json");
        File.WriteAllText(nativeEvidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            probeName = "native-layout",
            processArchitecture = "X64",
            publishRoot = serverPublish,
            serverPath,
            serverSha256 = Sha256(serverPath),
            dependencies = new[]
            {
                new { relativePath = "amd64/msdia140.dll", path = msdiaPath, sha256 = Sha256(msdiaPath), loaded = true },
                new { relativePath = "amd64/KernelTraceControl.dll", path = kernelTraceControlPath, sha256 = Sha256(kernelTraceControlPath), loaded = true },
            },
            passed = true,
        }));
        var nativeArtifacts = new Dictionary<string, string>
        {
            [serverPath] = Sha256(serverPath),
            [msdiaPath] = Sha256(msdiaPath),
            [kernelTraceControlPath] = Sha256(kernelTraceControlPath),
            [nativeEvidencePath] = Sha256(nativeEvidencePath),
        };
        var diaRoot = Path.Combine(retainedRoot, "dia-probe");
        Directory.CreateDirectory(diaRoot);
        var diaImagePath = Path.Combine(diaRoot, "platform-dia-probe.dll");
        var diaPdbPath = Path.Combine(diaRoot, "platform-dia-probe.pdb");
        File.WriteAllText(diaImagePath, "fixture-native-image");
        File.WriteAllText(diaPdbPath, "fixture-native-pdb");
        var diaEvidencePath = Path.Combine(retainedRoot, "windows-dia-pdb-resolution.evidence.json");
        File.WriteAllText(diaEvidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            probeName = "windows-dia-pdb-resolution",
            processArchitecture = "X64",
            msdiaPath,
            msdiaSha256 = Sha256(msdiaPath),
            nativeImagePath = diaImagePath,
            nativeImageSha256 = Sha256(diaImagePath),
            nativePdbPath = diaPdbPath,
            nativePdbSha256 = Sha256(diaPdbPath),
            functionCount = 1,
            enumeratedName = "PlatformDiaSentinel",
            symbolRva = 4096,
            resolvedName = "PlatformDiaSentinel",
            resolvedStartRva = 4096,
            passed = true,
        }));
        var diaArtifacts = new Dictionary<string, string>
        {
            [msdiaPath] = Sha256(msdiaPath),
            [diaImagePath] = Sha256(diaImagePath),
            [diaPdbPath] = Sha256(diaPdbPath),
            [diaEvidencePath] = Sha256(diaEvidencePath),
        };
        Dictionary<string, string> CreateSdkAggregateArtifacts(string probeName)
        {
            var aggregate = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var hostMode in hostModes)
            {
                foreach (var artifact in CreateSdkArtifacts(probeName, hostMode)) aggregate[artifact.Key] = artifact.Value;
            }
            return aggregate;
        }
        var architectureSources = new List<(string Component, string EvidencePath)>
        {
            ("golden-traceevent-reads", goldenEvidencePath),
            ("windows-dia-pdb-resolution", diaEvidencePath),
            ("native-layout", nativeEvidencePath),
            ("self-contained-stdio", stdioEvidencePath),
        };
        foreach (var hostMode in hostModes.Distinct(StringComparer.Ordinal))
        {
            var sdkEvidencePath = CreateSdkArtifacts("selected-profile-handshake", hostMode).Keys
                .Single(candidate => candidate.EndsWith($"{hostMode}.evidence.json", StringComparison.Ordinal));
            architectureSources.Add(($"sdk-{hostMode}", sdkEvidencePath));
        }
        var architectureEvidencePath = Path.Combine(retainedRoot, "windows-architecture-matrix.evidence.json");
        File.WriteAllText(architectureEvidencePath, JsonSerializer.Serialize(new
        {
            schemaVersion = "1.0",
            probeName = "windows-architecture-matrix",
            expected = new[]
            {
                new { id = "windows-x64", osPlatform = "Windows", osArchitecture = "X64", processArchitecture = "X64", runtimeIdentifier = "win-x64" },
            },
            runner = new { osPlatform = "Windows", osArchitecture = "X64", processArchitecture = "X64" },
            observations = architectureSources.Select(source => new
            {
                component = source.Component,
                evidencePath = source.EvidencePath,
                evidenceSha256 = Sha256(source.EvidencePath),
                processArchitecture = "X64",
            }),
            passed = true,
        }));
        var architectureArtifacts = architectureSources.ToDictionary(
            source => source.EvidencePath,
            source => Sha256(source.EvidencePath),
            StringComparer.Ordinal);
        architectureArtifacts[architectureEvidencePath] = Sha256(architectureEvidencePath);
        var probes = RequiredProbeNames.Select(name => new
        {
            name,
            command = name == "self-contained-publish" ? "dotnet publish -r win-x64 --self-contained true" : "command",
            exitCode = 0,
            stdoutSha256 = stdoutHash,
            stderrSha256 = stderrHash,
            passed = true,
            artifactSha256 = SdkSurfaceProbeNames.Contains(name, StringComparer.Ordinal)
                ? CreateSdkAggregateArtifacts(name)
                : name switch
                {
                    "nuget-package-existence-hash" => nugetArtifacts,
                    "normal-restore" => restoreArtifacts[name],
                    "win-x64-restore" => restoreArtifacts[name],
                    "self-contained-stdio" => stdioArtifacts,
                    "golden-traceevent-reads" => goldenArtifacts,
                    "native-layout" => nativeArtifacts,
                    "windows-dia-pdb-resolution" => diaArtifacts,
                    "tools-list-output-schema" => CreateSdkAggregateArtifacts("cancellation-progress-injection-schema"),
                    "windows-architecture-matrix" => architectureArtifacts,
                    _ => new Dictionary<string, string>(),
                },
            cases = SdkSurfaceProbeNames.Contains(name, StringComparer.Ordinal)
                ? hostModes.Select(hostMode => new
                {
                    hostMode,
                    scenario = name,
                    command = "command",
                    exitCode = 0,
                    stdoutSha256 = stdoutHash,
                    stderrSha256 = stderrHash,
                    passed = true,
                    failureStage = "none",
                    artifactSha256 = CreateSdkArtifacts(name, hostMode),
                }).ToArray()
                : [new
                {
                    hostMode = "candidate-worktree",
                    scenario = name,
                    command = "command",
                    exitCode = 0,
                    stdoutSha256 = stdoutHash,
                    stderrSha256 = stderrHash,
                    passed = true,
                    failureStage = "none",
                    artifactSha256 = name switch
                    {
                        "nuget-package-existence-hash" => nugetArtifacts,
                        "normal-restore" => restoreArtifacts[name],
                        "win-x64-restore" => restoreArtifacts[name],
                        "self-contained-stdio" => stdioArtifacts,
                        "golden-traceevent-reads" => goldenArtifacts,
                        "native-layout" => nativeArtifacts,
                        "windows-dia-pdb-resolution" => diaArtifacts,
                        "tools-list-output-schema" => CreateSdkAggregateArtifacts("cancellation-progress-injection-schema"),
                        "windows-architecture-matrix" => architectureArtifacts,
                        _ => new Dictionary<string, string> { [artifactPath] = artifactHash },
                    },
                }],
            nuGetPackage = name == "nuget-package-existence-hash" ? new
            {
                packageId = "ModelContextProtocol",
                packageVersion,
                registrationUrl,
                packageContentUrl,
                hashAlgorithm = "SHA512",
                publishedHashBase64,
                downloadedHashBase64 = publishedHashBase64,
                observedUtc,
                retrievalSource = "Network",
            } : null,
        }).ToArray();
        var fixture = new
        {
            schemaVersion = "1.0",
            candidateId = "net10-stable-stateful",
            sdkVersion = "10.0.302",
            targetFramework = "net10.0",
            mcpSdkVersion = "1.4.1",
            protocolRevision = "2025-11-25",
            protocolProfile = "stateful",
            commit = new string('d', 40),
            startedUtc = "2026-07-29T00:00:00Z",
            completedUtc = "2026-07-29T00:01:00Z",
            probes,
        };
        var path = Path.Combine(fixtureRoot, "net10-stable-stateful.result.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture));
        return path;
    }

    private static void DeleteResultFixture(string path)
    {
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void RebuildAggregateArtifacts(JsonNode probe)
    {
        var aggregate = new JsonObject();
        foreach (var @case in probe["cases"]!.AsArray())
        {
            foreach (var artifact in @case!["artifactSha256"]!.AsObject())
            {
                if (aggregate.TryGetPropertyValue(artifact.Key, out var existing))
                {
                    Assert.Equal(existing!.GetValue<string>(), artifact.Value!.GetValue<string>());
                }
                else
                {
                    aggregate[artifact.Key] = artifact.Value!.GetValue<string>();
                }
            }
        }
        probe["artifactSha256"] = aggregate;
    }

    private static void RefreshArchitectureEvidence(JsonObject result)
    {
        var probe = result["probes"]!.AsArray().Single(node => node!["name"]!.GetValue<string>() == "windows-architecture-matrix")!;
        var evidencePath = probe["artifactSha256"]!.AsObject().Select(property => property.Key)
            .Single(path => path.EndsWith("windows-architecture-matrix.evidence.json", StringComparison.Ordinal));
        var evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!.AsObject();
        var artifacts = new JsonObject();
        var passed = true;
        foreach (var observation in evidence["observations"]!.AsArray())
        {
            var sourcePath = observation!["evidencePath"]!.GetValue<string>();
            if (!File.Exists(sourcePath))
            {
                observation["evidenceSha256"] = string.Empty;
                observation["processArchitecture"] = string.Empty;
                passed = false;
                continue;
            }

            var source = JsonNode.Parse(File.ReadAllText(sourcePath))!;
            var component = observation["component"]!.GetValue<string>();
            var processArchitecture = component switch
            {
                "self-contained-stdio" => source["launch"]!["childProcessArchitecture"]!.GetValue<string>(),
                var sdkComponent when sdkComponent.StartsWith("sdk-", StringComparison.Ordinal) =>
                    source["launchIdentity"]!["runtimeIdentity"]!["processArchitecture"]!.GetValue<string>(),
                _ => source["processArchitecture"]!.GetValue<string>(),
            };
            var sourceHash = Sha256(sourcePath);
            observation["evidenceSha256"] = sourceHash;
            observation["processArchitecture"] = processArchitecture;
            artifacts[sourcePath] = sourceHash;
            passed &= processArchitecture == "X64";
        }
        evidence["passed"] = passed;
        File.WriteAllText(evidencePath, evidence.ToJsonString());
        artifacts[evidencePath] = Sha256(evidencePath);
        probe["artifactSha256"] = JsonNode.Parse(artifacts.ToJsonString());
        probe["cases"]![0]!["artifactSha256"] = JsonNode.Parse(artifacts.ToJsonString());
        probe["exitCode"] = passed ? 0 : 1;
        probe["passed"] = passed;
        probe["cases"]![0]!["exitCode"] = passed ? 0 : 1;
        probe["cases"]![0]!["passed"] = passed;
        probe["cases"]![0]!["failureStage"] = passed ? "none" : "probe";
    }

    private static void RefreshSchemaAggregate(JsonObject result)
    {
        var source = result["probes"]!.AsArray().Single(node =>
            node!["name"]!.GetValue<string>() == "cancellation-progress-injection-schema")!;
        var schema = result["probes"]!.AsArray().Single(node =>
            node!["name"]!.GetValue<string>() == "tools-list-output-schema")!;
        var aggregate = new JsonObject();
        var passed = true;
        foreach (var @case in source["cases"]!.AsArray())
        {
            passed &= @case!["passed"]!.GetValue<bool>();
            foreach (var artifact in @case["artifactSha256"]!.AsObject())
            {
                if (aggregate.TryGetPropertyValue(artifact.Key, out var existing))
                {
                    Assert.Equal(existing!.GetValue<string>(), artifact.Value!.GetValue<string>());
                }
                else
                {
                    aggregate[artifact.Key] = artifact.Value!.GetValue<string>();
                }
            }
        }
        schema["artifactSha256"] = JsonNode.Parse(aggregate.ToJsonString());
        schema["cases"]![0]!["artifactSha256"] = JsonNode.Parse(aggregate.ToJsonString());
        schema["exitCode"] = passed ? 0 : 1;
        schema["passed"] = passed;
        schema["cases"]![0]!["exitCode"] = passed ? 0 : 1;
        schema["cases"]![0]!["passed"] = passed;
        schema["cases"]![0]!["failureStage"] = passed ? "none" : "probe";
    }

    private static async Task DeleteDirectoryEventuallyAsync(string directory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }

    private static async Task KillProcessTreeIfRunningAsync(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static (string Directory, string RegistrationPath, string CatalogPath, string PackagePath, string PackageHash) WriteNuGetEvidenceFixture()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nuget-evidence-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var registrationPath = Path.Combine(directory, "registration.json");
        var catalogPath = Path.Combine(directory, "catalog.json");
        var packagePath = Path.Combine(directory, "package.nupkg");
        File.WriteAllBytes(packagePath, [5, 4, 3, 2, 1]);
        var packageHash = Convert.ToBase64String(SHA512.HashData(File.ReadAllBytes(packagePath)));
        File.WriteAllText(registrationPath, new JsonObject
        {
            ["@id"] = "https://example.test/registration/1.4.1.json",
            ["listed"] = true,
            ["catalogEntry"] = "https://example.test/catalog/1.4.1.json",
            ["packageContent"] = "https://example.test/package/1.4.1.nupkg",
        }.ToJsonString());
        File.WriteAllText(catalogPath, new JsonObject
        {
            ["@id"] = "https://example.test/catalog/1.4.1.json",
            ["id"] = "ModelContextProtocol",
            ["version"] = "1.4.1",
            ["listed"] = true,
            ["packageHashAlgorithm"] = "SHA512",
            ["packageHash"] = packageHash,
        }.ToJsonString());
        return (directory, registrationPath, catalogPath, packagePath, packageHash);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string? ProjectProperty(XDocument project, string name) =>
        project.Root?.Elements("PropertyGroup").Elements(name).Select(element => element.Value).FirstOrDefault();

    private static string? ProjectPackageVersion(XDocument project, string packageName) =>
        project.Root?.Elements("ItemGroup").Elements("PackageReference")
            .Where(element => (string?)element.Attribute("Include") == packageName)
            .Select(element => (string?)element.Attribute("Version"))
            .FirstOrDefault();

    private static string? CentralPackageVersion(XDocument project, string packageName) =>
        project.Root?.Elements("ItemGroup").Elements("PackageVersion")
            .Where(element => (string?)element.Attribute("Include") == packageName)
            .Select(element => (string?)element.Attribute("Version"))
            .FirstOrDefault();
}

public sealed class PlatformNonSdkRuntimeProbeTests
{
    private static readonly string[] GoldenFixtureNames =
    [
        "perfview_gcevents.etl",
        "small_cpu.etl",
        "small_fileio.etl",
        "small_memory.etl",
        "small_mmap.etl",
        "small_wait_bound.etl",
    ];

    [Fact]
    public async Task GoldenTraceEventReads_OpensEveryFixtureFromTemporaryCopy()
    {
        if (!PlatformProbeRequired()) return;
        var evidencePath = RequiredEnvironment("WPRMCP_PLATFORM_GOLDEN_EVIDENCE_PATH");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"platform-golden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryRoot);
        try
        {
            var fixtures = new List<object>();
            foreach (var name in GoldenFixtureNames)
            {
                var source = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
                Assert.True(File.Exists(source), $"Required golden fixture was not copied to test output: {name}");
                var copyDirectory = Path.Combine(temporaryRoot, Path.GetFileNameWithoutExtension(name));
                Directory.CreateDirectory(copyDirectory);
                var copy = Path.Combine(copyDirectory, name);
                File.Copy(source, copy);
                var sourceHash = Sha256(source);
                var copyHash = Sha256(copy);
                Assert.Equal(sourceHash, copyHash);
                using var trace = TraceLog.OpenOrConvert(copy);
                Assert.True(trace.EventCount > 0, $"Golden fixture contained no events: {name}");
                Assert.True(trace.SessionDuration.Ticks > 0, $"Golden fixture had no duration: {name}");
                fixtures.Add(new
                {
                    name,
                    sourceSha256 = sourceHash,
                    copySha256 = copyHash,
                    eventCount = trace.EventCount,
                    durationTicks = trace.SessionDuration.Ticks,
                    temporaryCopyUsed = true,
                });
            }
            WriteCreateNewJson(evidencePath, new
            {
                schemaVersion = "1.0",
                probeName = "golden-traceevent-reads",
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                fixtures,
                passed = true,
            });
        }
        finally
        {
            await DeleteDirectoryEventuallyAsync(temporaryRoot);
        }
    }

    [Fact]
    public void NativeLayout_LoadsExactProductionAmd64Dependencies()
    {
        if (!PlatformProbeRequired()) return;
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        var publishRoot = RequiredEnvironment("WPRMCP_PLATFORM_PUBLISH_ROOT");
        var evidencePath = RequiredEnvironment("WPRMCP_PLATFORM_NATIVE_EVIDENCE_PATH");
        var serverPath = Path.Combine(publishRoot, "WprMcp.exe");
        Assert.True(File.Exists(serverPath), $"Production publish omitted {serverPath}");
        var dependencies = new List<object>();
        foreach (var relativePath in new[] { "amd64/msdia140.dll", "amd64/KernelTraceControl.dll" })
        {
            var path = Path.Combine(publishRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Production publish omitted exact native dependency {relativePath}");
            var handle = NativeLibrary.Load(path);
            try
            {
                Assert.NotEqual(IntPtr.Zero, handle);
                dependencies.Add(new { relativePath, path = Path.GetFullPath(path), sha256 = Sha256(path), loaded = true });
            }
            finally
            {
                if (handle != IntPtr.Zero) NativeLibrary.Free(handle);
            }
        }
        WriteCreateNewJson(evidencePath, new
        {
            schemaVersion = "1.0",
            probeName = "native-layout",
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            publishRoot,
            serverPath = Path.GetFullPath(serverPath),
            serverSha256 = Sha256(serverPath),
            dependencies,
            passed = true,
        });
    }

    [Fact]
    public async Task WindowsDiaPdbResolution_EnumeratesFunctionAndResolvesItsRva()
    {
        if (!PlatformProbeRequired()) return;
        Assert.Equal(Architecture.X64, RuntimeInformation.ProcessArchitecture);
        var root = RequiredEnvironment("WPRMCP_PLATFORM_DIA_ROOT");
        var evidencePath = RequiredEnvironment("WPRMCP_PLATFORM_DIA_EVIDENCE_PATH");
        var msdiaPath = RequiredEnvironment("WPRMCP_PLATFORM_MSDIA_PATH");
        Directory.CreateDirectory(root);
        var sourcePath = Path.Combine(root, "platform-dia-probe.cpp");
        var imagePath = Path.Combine(root, "platform-dia-probe.dll");
        var pdbPath = Path.Combine(root, "platform-dia-probe.pdb");
        await File.WriteAllTextAsync(sourcePath,
            "extern \"C\" __declspec(dllexport) __declspec(noinline) int PlatformDiaSentinel(int value) { return value + 42; }\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        Assert.True(File.Exists(vswhere), $"vswhere.exe not found at {vswhere}");
        var discovery = await RunProcessAsync(vswhere,
            ["-latest", "-products", "*", "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath"],
            root, TimeSpan.FromSeconds(15));
        Assert.Equal(0, discovery.ExitCode);
        var installationPath = discovery.Stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Single();
        var vcvars = Path.Combine(installationPath, "VC", "Auxiliary", "Build", "vcvars64.bat");
        Assert.True(File.Exists(vcvars), $"vcvars64.bat not found at {vcvars}");
        var compileScript = Path.Combine(root, "compile.cmd");
        await File.WriteAllTextAsync(compileScript,
            $"@echo off\r\ncall \"{vcvars}\" >nul\r\nif errorlevel 1 exit /b %errorlevel%\r\ncl.exe /nologo /Zi /Od /LD /EHsc /Fe:\"{imagePath}\" /Fd:\"{pdbPath}\" \"{sourcePath}\" /link /DEBUG:FULL /INCREMENTAL:NO\r\n",
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var compile = await RunProcessAsync(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/c", "compile.cmd"], root, TimeSpan.FromSeconds(60));
        Assert.True(compile.ExitCode == 0, $"Native DIA fixture compilation failed. stdout: {compile.Stdout} stderr: {compile.Stderr}");
        Assert.True(File.Exists(imagePath));
        Assert.True(File.Exists(pdbPath));
        Assert.True(File.Exists(msdiaPath));

        var msdiaHandle = NativeLibrary.Load(msdiaPath);
        try
        {
            using var log = new StringWriter();
            using var reader = new SymbolReader(log, string.Empty);
            var module = reader.OpenNativeSymbolFile(pdbPath);
            Assert.NotNull(module);
            var functions = module!.GlobalSymbol.GetChildren(SymTagEnum.SymTagFunction).ToArray();
            Assert.NotEmpty(functions);
            var sentinel = functions.Single(symbol =>
                symbol.Name.Contains("PlatformDiaSentinel", StringComparison.Ordinal) ||
                symbol.UndecoratedName.Contains("PlatformDiaSentinel", StringComparison.Ordinal));
            Assert.True(sentinel.RVA > 0);
            uint resolvedStartRva = 0;
            var resolvedName = module.FindNameForRva(sentinel.RVA, ref resolvedStartRva);
            Assert.Contains("PlatformDiaSentinel", resolvedName, StringComparison.Ordinal);
            Assert.Equal(sentinel.RVA, resolvedStartRva);
            WriteCreateNewJson(evidencePath, new
            {
                schemaVersion = "1.0",
                probeName = "windows-dia-pdb-resolution",
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                msdiaPath = Path.GetFullPath(msdiaPath),
                msdiaSha256 = Sha256(msdiaPath),
                nativeImagePath = Path.GetFullPath(imagePath),
                nativeImageSha256 = Sha256(imagePath),
                nativePdbPath = Path.GetFullPath(pdbPath),
                nativePdbSha256 = Sha256(pdbPath),
                functionCount = functions.Length,
                enumeratedName = sentinel.Name,
                symbolRva = sentinel.RVA,
                resolvedName,
                resolvedStartRva,
                passed = true,
            });
        }
        finally
        {
            if (msdiaHandle != IntPtr.Zero) NativeLibrary.Free(msdiaHandle);
        }
    }

    private static bool PlatformProbeRequired() =>
        string.Equals(Environment.GetEnvironmentVariable("WPRMCP_PLATFORM_REQUIRED"), "1", StringComparison.Ordinal);

    private static string RequiredEnvironment(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Missing required platform probe environment variable {name}.");
        return Path.GetFullPath(value!);
    }

    private static void WriteCreateNewJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var stdout = process!.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"Process timed out after {timeout}: {executable}");
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private static async Task DeleteDirectoryEventuallyAsync(string directory)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
                return;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                await Task.Delay(25);
            }
        }
    }
}
