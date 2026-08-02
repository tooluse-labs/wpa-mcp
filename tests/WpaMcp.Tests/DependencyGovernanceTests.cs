using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WpaMcp.Tests;

public sealed class DependencyGovernanceTests
{
    private static readonly string[] ExpectedProjects =
    [
        "src/WpaMcp/WpaMcp.csproj",
        "tests/WpaMcp.Tests/WpaMcp.Tests.csproj",
        "tools/etlshrink/etlshrink.csproj",
        "tools/interruptfixture/interruptfixture.csproj",
        "tools/sdkcandidateprobe/sdkcandidateprobe.csproj",
    ];

    private static readonly (string Id, string Version)[] FixedPackageVersions =
    [
        ("Microsoft.Diagnostics.Tracing.TraceEvent", "3.2.2"),
        ("Microsoft.Extensions.Hosting", "10.0.7"),
        ("ModelContextProtocol", "selected"),
        ("coverlet.collector", "6.0.0"),
        ("Microsoft.NET.Test.Sdk", "17.8.0"),
        ("Moq", "4.20.72"),
        ("xunit", "2.5.3"),
        ("xunit.runner.visualstudio", "2.5.3"),
    ];

    private static readonly (string Repository, string Tag)[] ApprovedActionPins =
    [
        ("actions/checkout", "v4"),
        ("actions/setup-dotnet", "v4"),
        ("actions/cache", "v4"),
        ("actions/upload-artifact", "v4"),
        ("actions/download-artifact", "v4"),
        ("actions/attest-build-provenance", "v3"),
        ("softprops/action-gh-release", "v2"),
    ];

    [Fact]
    public void GlobalJson_MatchesSelectedSdkAndDisablesRollForward()
    {
        var selected = ReadSelectedPlatform();
        using var global = ReadJson("global.json");
        var sdk = global.RootElement.GetProperty("sdk");
        var selectedSdk = selected["WpaMcpSdkVersion"];

        Assert.Equal(selectedSdk, sdk.GetProperty("version").GetString());
        Assert.Equal("disable", sdk.GetProperty("rollForward").GetString());
        Assert.Equal(selectedSdk.Contains('-', StringComparison.Ordinal), sdk.GetProperty("allowPrerelease").GetBoolean());
    }

    [Fact]
    public void EveryProject_UsesSelectedTargetFramework()
    {
        var selected = ReadSelectedPlatform();
        var build = ReadXml("Directory.Build.props");
        var import = Assert.Single(build.Root!.Elements("Import"));
        Assert.Equal("eng/SelectedPlatform.props", NormalizePath(import.Attribute("Project")!.Value));
        Assert.Equal("$(WpaMcpTargetFramework)", Assert.Single(build.Descendants("TargetFramework")).Value);
        Assert.Equal("true", Assert.Single(build.Descendants("RestorePackagesWithLockFile")).Value);
        Assert.Equal("true", Assert.Single(build.Descendants("Deterministic")).Value);
        Assert.Equal("true", Assert.Single(build.Descendants("DeterministicSourcePaths")).Value);
        Assert.Equal("enable", Assert.Single(build.Descendants("Nullable")).Value);
        Assert.Equal("true", Assert.Single(build.Descendants("ContinuousIntegrationBuild")).Value);
        Assert.Equal("'$(CI)' == 'true'", Assert.Single(build.Descendants("ContinuousIntegrationBuild")).Attribute("Condition")!.Value);
        Assert.Equal("true", Assert.Single(build.Descendants("TreatWarningsAsErrors")).Value);
        Assert.Equal("'$(CI)' == 'true'", Assert.Single(build.Descendants("TreatWarningsAsErrors")).Attribute("Condition")!.Value);
        Assert.Equal("net10.0", selected["WpaMcpTargetFramework"]);

        Assert.Equal(ExpectedProjects, EnumerateCurrentProjects());
        foreach (var projectPath in ExpectedProjects)
        {
            var project = ReadXml(projectPath);
            Assert.Empty(project.Descendants("TargetFramework"));
            Assert.Empty(project.Descendants("TargetFrameworks"));
            Assert.Empty(project.Descendants("WpaMcpTargetFramework"));
        }
    }

    [Fact]
    public void EveryPackageVersion_IsExactAndCentral()
    {
        var selected = ReadSelectedPlatform();
        var packages = ReadXml("Directory.Packages.props");
        Assert.Equal("true", Assert.Single(packages.Descendants("ManagePackageVersionsCentrally")).Value);

        var expected = FixedPackageVersions
            .Select(package => (package.Id, Version: package.Version == "selected" ? "$(WpaMcpMcpSdkVersion)" : package.Version))
            .ToArray();
        var actual = packages.Descendants("PackageVersion")
            .Select(package => (Id: package.Attribute("Include")!.Value, Version: package.Attribute("Version")!.Value))
            .ToArray();
        Assert.Equal(expected, actual);
        Assert.All(actual.Where(package => package.Id != "ModelContextProtocol"), package =>
            Assert.Matches(new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant), package.Version));
        Assert.Matches(new Regex(@"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant), selected["WpaMcpMcpSdkVersion"]);

        var centralIds = actual.Select(package => package.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var projectPath in ExpectedProjects)
        {
            foreach (var reference in ReadXml(projectPath).Descendants("PackageReference"))
            {
                var id = reference.Attribute("Include")!.Value;
                Assert.Contains(id, centralIds);
                Assert.Null(reference.Attribute("Version"));
                Assert.Empty(reference.Elements("Version"));
            }
        }
    }

    [Fact]
    public void Moq_IsPinnedTo42072NotWildcard()
    {
        var packages = ReadXml("Directory.Packages.props");
        var moq = Assert.Single(packages.Descendants("PackageVersion"), package => package.Attribute("Include")?.Value == "Moq");
        Assert.Equal("4.20.72", moq.Attribute("Version")!.Value);
        Assert.DoesNotContain('*', moq.Attribute("Version")!.Value);

        var testProject = ReadXml("tests/WpaMcp.Tests/WpaMcp.Tests.csproj");
        var reference = Assert.Single(testProject.Descendants("PackageReference"), package => package.Attribute("Include")?.Value == "Moq");
        Assert.Null(reference.Attribute("Version"));
    }

    [Fact]
    public void EveryCurrentProject_HasNormalLockFile()
    {
        var selected = ReadSelectedPlatform();
        var targetFramework = selected["WpaMcpTargetFramework"];
        var central = ReadCentralVersions();

        foreach (var projectPath in ExpectedProjects)
        {
            var project = ReadXml(projectPath);
            var lockPath = NormalizePath(Path.Combine(Path.GetDirectoryName(projectPath)!, "packages.lock.json"));
            using var lockFile = ReadJson(lockPath);
            Assert.Equal(2, lockFile.RootElement.GetProperty("version").GetInt32());
            var graphs = lockFile.RootElement.GetProperty("dependencies").EnumerateObject().ToArray();
            var graph = Assert.Single(graphs);
            Assert.Equal(targetFramework, graph.Name);
            AssertDirectDependencies(project, graph.Value, central);
        }
    }

    [Fact]
    public void Server_HasSeparateWinX64LockFile()
    {
        var selected = ReadSelectedPlatform();
        var server = ReadXml("src/WpaMcp/WpaMcp.csproj");
        var central = ReadCentralVersions();
        using var normal = ReadJson("src/WpaMcp/packages.lock.json");
        using var rid = ReadJson("src/WpaMcp/packages.win-x64.lock.json");

        Assert.Equal(selected["WpaMcpTargetFramework"], Assert.Single(normal.RootElement.GetProperty("dependencies").EnumerateObject()).Name);
        var ridGraphs = rid.RootElement.GetProperty("dependencies").EnumerateObject().ToArray();
        Assert.Equal(
            [selected["WpaMcpTargetFramework"], $"{selected["WpaMcpTargetFramework"]}/win-x64"],
            ridGraphs.Select(graph => graph.Name));
        AssertDirectDependencies(server, ridGraphs[0].Value, central);
        Assert.NotEmpty(ridGraphs[1].Value.EnumerateObject());
        Assert.All(ridGraphs[1].Value.EnumerateObject(), dependency =>
            Assert.Equal("Transitive", dependency.Value.GetProperty("type").GetString()));
        Assert.NotEqual(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepoPath("src/WpaMcp/packages.lock.json")))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(RepoPath("src/WpaMcp/packages.win-x64.lock.json")))));
    }

    [Fact]
    public async Task NormalAndRidLockedRestore_DoNotChangeLockFiles()
    {
        var lockPaths = ExpectedProjects
            .Select(project => NormalizePath(Path.Combine(Path.GetDirectoryName(project)!, "packages.lock.json")))
            .Append("src/WpaMcp/packages.win-x64.lock.json")
            .ToArray();
        var before = lockPaths.ToDictionary(path => path, path => SHA256.HashData(File.ReadAllBytes(RepoPath(path))), StringComparer.Ordinal);

        var normal = await RunDotNetAsync(
            "restore", "WpaMcp.sln", "--locked-mode", "-m:1", "-nr:false",
            "-p:RestoreDisableParallel=true", "-p:NuGetAudit=false");
        Assert.True(normal.ExitCode == 0, $"Normal locked restore failed.{Environment.NewLine}{normal.Output}");

        var rid = await RunDotNetAsync(
            "restore", "src/WpaMcp/WpaMcp.csproj", "-r", "win-x64", "--locked-mode",
            "-p:NuGetLockFilePath=packages.win-x64.lock.json", "-m:1", "-nr:false",
            "-p:RestoreDisableParallel=true", "-p:NuGetAudit=false");
        Assert.True(rid.ExitCode == 0, $"RID locked restore failed.{Environment.NewLine}{rid.Output}");

        foreach (var path in lockPaths)
        {
            Assert.Equal(before[path], SHA256.HashData(File.ReadAllBytes(RepoPath(path))));
        }
    }

    [Fact]
    public void SelectedSdkProbeProject_UsesSelectedTfmMcpPackageAndNormalLock()
    {
        var selected = ReadSelectedPlatform();
        var project = ReadXml("tools/sdkcandidateprobe/sdkcandidateprobe.csproj");
        Assert.Empty(project.Descendants("WpaMcpTargetFramework"));
        Assert.Empty(project.Descendants("WpaMcpMcpSdkVersion"));
        Assert.Empty(project.Descendants("WpaMcpProtocolProfile"));
        var reference = Assert.Single(project.Descendants("PackageReference"));
        Assert.Equal("ModelContextProtocol", reference.Attribute("Include")!.Value);
        Assert.Null(reference.Attribute("Version"));

        using var lockFile = ReadJson("tools/sdkcandidateprobe/packages.lock.json");
        var graph = Assert.Single(lockFile.RootElement.GetProperty("dependencies").EnumerateObject());
        Assert.Equal(selected["WpaMcpTargetFramework"], graph.Name);
        var mcp = graph.Value.GetProperty("ModelContextProtocol");
        Assert.Equal("Direct", mcp.GetProperty("type").GetString());
        Assert.Equal(selected["WpaMcpMcpSdkVersion"], mcp.GetProperty("resolved").GetString());
    }

    [Fact]
    public void EveryThirdPartyAction_UsesFullFortyHexCommitSha()
    {
        var actionUses = EnumerateThirdPartyActionUses().ToArray();

        Assert.NotEmpty(actionUses);
        Assert.All(actionUses, action =>
            Assert.Matches(new Regex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant), action.Revision));
    }

    [Fact]
    public void ActionPinInputs_ContainOnlyApprovedRepositoryAndMajorTagPairs()
    {
        using var inputs = ReadJson("eng/action-pin-inputs.v1.json");
        Assert.Equal(
            ["actions", "schemaVersion"],
            inputs.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, inputs.RootElement.GetProperty("schemaVersion").GetInt32());

        var actions = inputs.RootElement.GetProperty("actions").EnumerateArray().ToArray();
        Assert.Equal(ApprovedActionPins.Length, actions.Length);
        Assert.Equal(
            ApprovedActionPins,
            actions.Select(action =>
            {
                Assert.Equal(
                    ["repository", "tag"],
                    action.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
                return (
                    action.GetProperty("repository").GetString()!,
                    action.GetProperty("tag").GetString()!);
            }));

        var resolver = ReadRepoText("scripts/Resolve-ActionPins.ps1");
        Assert.Contains("[string]$InputPath = 'eng/action-pin-inputs.v1.json'", resolver, StringComparison.Ordinal);
        Assert.Contains("[string]$OutputPath = 'artifacts/action-pins.candidate.json'", resolver, StringComparison.Ordinal);
        Assert.Contains("git ls-remote", resolver, StringComparison.Ordinal);
        Assert.Contains("refs/tags/$tag refs/tags/$tag^{}", resolver, StringComparison.Ordinal);
        Assert.Contains("$attempt -le 2", resolver, StringComparison.Ordinal);
        Assert.Contains("^[0-9a-fA-F]{40}$", resolver, StringComparison.Ordinal);
        Assert.Contains("retrievalUtc", resolver, StringComparison.Ordinal);
        Assert.Contains("command", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void ActionPinComments_RecordResolvedRepositoryAndTag()
    {
        var approved = ApprovedActionPins.ToDictionary(action => action.Repository, action => action.Tag, StringComparer.Ordinal);
        var actionUses = EnumerateThirdPartyActionUses().ToArray();

        Assert.NotEmpty(actionUses);
        Assert.All(actionUses, action =>
        {
            Assert.True(approved.TryGetValue(action.Repository, out var tag), $"Unapproved action repository in {action.File}: {action.Repository}");
            Assert.Equal($"{action.Repository}@{tag}", action.Comment);
        });
    }

    [Fact]
    public void WorkflowSdk_EqualsGlobalJsonExactly()
    {
        using var global = ReadJson("global.json");
        var expectedSdk = Assert.IsType<string>(global.RootElement.GetProperty("sdk").GetProperty("version").GetString());
        var workflowVersions = EnumerateWorkflowFiles()
            .SelectMany(path => Regex.Matches(File.ReadAllText(path), @"(?m)^\s*dotnet-version:\s*['""]?(?<version>[^'""\s#]+)")
                .Select(match => match.Groups["version"].Value))
            .ToArray();

        Assert.Equal([expectedSdk], workflowVersions);
        Assert.Contains("uses: ./.github/actions/setup-wpamcp", ReadRepoText(".github/workflows/quality.yml"), StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/actions/setup-wpamcp", ReadRepoText(".github/workflows/release.yml"), StringComparison.Ordinal);
    }

    [Fact]
    public void Ci_CallsReusableQualityWorkflowWithoutDuplicatingBuildSteps()
    {
        var ci = ReadRepoText(".github/workflows/ci.yml");

        Assert.Contains("push:", ci, StringComparison.Ordinal);
        Assert.Contains("pull_request:", ci, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
        Assert.Contains("contents: read", ci, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/quality.yml", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("runs-on:", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("steps:", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet ", ci, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyQuality_RunsNormalAndWinX64LockedRestoreBuildAndNonPackageTests()
    {
        var quality = ReadRepoText(".github/workflows/quality.yml");

        Assert.Contains("workflow_call:", quality, StringComparison.Ordinal);
        Assert.Contains("workflow_dispatch:", quality, StringComparison.Ordinal);
        Assert.Contains("contents: read", quality, StringComparison.Ordinal);
        Assert.Contains("runs-on: windows-latest", quality, StringComparison.Ordinal);
        Assert.Contains("uses: actions/checkout@", quality, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/actions/setup-wpamcp", quality, StringComparison.Ordinal);
        Assert.Contains("dotnet restore WpaMcp.sln --locked-mode", quality, StringComparison.Ordinal);
        Assert.Contains("dotnet restore src/WpaMcp/WpaMcp.csproj -r win-x64 --locked-mode -p:NuGetLockFilePath=packages.win-x64.lock.json", quality, StringComparison.Ordinal);
        Assert.Contains("dotnet build WpaMcp.sln -c Release --no-restore -warnaserror", quality, StringComparison.Ordinal);
        Assert.Contains("dotnet test WpaMcp.sln -c Release --no-build --filter \"Category!=Package\"", quality, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyQuality_ReservesPackageCategoryFor11BArtifactStage()
    {
        var quality = ReadRepoText(".github/workflows/quality.yml");
        var solutionTests = Regex.Matches(quality, @"(?m)^\s*run:\s*(?<command>dotnet test WpaMcp\.sln[^\r\n]*)")
            .Select(match => match.Groups["command"].Value)
            .ToArray();

        Assert.NotEmpty(solutionTests);
        Assert.All(solutionTests, command => Assert.Contains("--filter \"Category!=Package\"", command, StringComparison.Ordinal));
        Assert.DoesNotContain("dotnet pack", quality, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("dotnet publish", quality, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("actions/upload-artifact", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/download-artifact", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/attest-build-provenance", quality, StringComparison.Ordinal);
    }

    [Fact]
    public void Release_DependsOnQualityAndBindsAssetsToVersionedManifestCommit()
    {
        var release = ReadRepoText(".github/workflows/release.yml");

        Assert.Contains("uses: ./.github/workflows/quality.yml", release, StringComparison.Ordinal);
        Assert.Contains("needs: quality", release, StringComparison.Ordinal);
        Assert.Contains("$env:GITHUB_REF_NAME -cne $expectedTag", release, StringComparison.Ordinal);
        Assert.Contains("github.sha", release, StringComparison.Ordinal);
        Assert.Contains("capabilityManifestSha256", release, StringComparison.Ordinal);
        Assert.Contains("toolContractManifestSha256", release, StringComparison.Ordinal);
        Assert.Contains("activeToolSnapshotSha256", release, StringComparison.Ordinal);
        Assert.Contains("activeDtoSnapshotSha256", release, StringComparison.Ordinal);
        Assert.Contains("activeStdioSnapshotSha256", release, StringComparison.Ordinal);
        Assert.Contains("--runtime-profile", release, StringComparison.Ordinal);
        Assert.Contains("--validate-release-profile", release, StringComparison.Ordinal);
        Assert.Contains("runtimeProfileSha256", release, StringComparison.Ordinal);
        Assert.Contains("runtime-profile.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("selectionScope -cne 'startup_immutable'", release, StringComparison.Ordinal);
        Assert.Contains("contractMode -cne '2.0'", release, StringComparison.Ordinal);
        Assert.Contains("traceReferenceMode -cne 'id_only'", release, StringComparison.Ordinal);
        Assert.Contains("tool-list-payload-budget.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("tools-list-pagination.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("tool-output-contract-registry.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("pre-resource-output-schema-hashes.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("artifact-materialization-budget.v1.json", release, StringComparison.Ordinal);
        Assert.DoesNotContain("supported-client-matrix.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("opaqueConverterTransientPeakProven", release, StringComparison.Ordinal);
        Assert.Contains("$package.commit -cne '${{ github.sha }}'", release, StringComparison.Ordinal);
        Assert.Contains("$package.contractMode -cne $profile.contractMode", release, StringComparison.Ordinal);
        Assert.Contains("$package.outputContractCount", release, StringComparison.Ordinal);
        Assert.Contains("$registry.toolCount", release, StringComparison.Ordinal);
        Assert.Contains("$package.outputContractCanonicalBytes", release, StringComparison.Ordinal);
        Assert.Contains("$registry.totalCanonicalUtf8Bytes", release, StringComparison.Ordinal);
        Assert.Contains("WPAMCP_RELEASE_SERVER_PATH", release, StringComparison.Ordinal);
        Assert.Contains("--filter \"Category=Package\"", release, StringComparison.Ordinal);
        Assert.Contains("release-package-stdio.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("release/release-evidence.v1.json", release, StringComparison.Ordinal);
        Assert.Contains("Published host reported", release, StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ReadSelectedPlatform()
    {
        var selected = ReadXml("eng/SelectedPlatform.props");
        return Assert.Single(selected.Root!.Elements("PropertyGroup")).Elements()
            .ToDictionary(element => element.Name.LocalName, element => element.Value, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> ReadCentralVersions()
    {
        var selected = ReadSelectedPlatform();
        return ReadXml("Directory.Packages.props").Descendants("PackageVersion")
            .ToDictionary(
                package => package.Attribute("Include")!.Value,
                package => package.Attribute("Version")!.Value == "$(WpaMcpMcpSdkVersion)"
                    ? selected["WpaMcpMcpSdkVersion"]
                    : package.Attribute("Version")!.Value,
                StringComparer.Ordinal);
    }

    private static void AssertDirectDependencies(XDocument project, JsonElement graph, IReadOnlyDictionary<string, string> central)
    {
        foreach (var reference in project.Descendants("PackageReference"))
        {
            var id = reference.Attribute("Include")!.Value;
            var dependency = graph.GetProperty(id);
            Assert.Equal("Direct", dependency.GetProperty("type").GetString());
            Assert.Equal(central[id], dependency.GetProperty("resolved").GetString());
        }
    }

    private static string[] EnumerateCurrentProjects()
    {
        var projects = new List<string>();
        foreach (var root in new[] { "src", "tests", "tools" })
        {
            CollectProjects(RepoPath(root), projects);
        }
        return projects.Order(StringComparer.Ordinal).ToArray();
    }

    private static void CollectProjects(string directory, ICollection<string> projects)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.csproj", SearchOption.TopDirectoryOnly))
        {
            projects.Add(NormalizePath(Path.GetRelativePath(LocateRepoRoot(), path)));
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (name is "bin" or "obj" or "artifacts") continue;
            CollectProjects(child, projects);
        }
    }

    private static XDocument ReadXml(string relativePath)
    {
        var path = RepoPath(relativePath);
        Assert.True(File.Exists(path), $"Expected XML file at {path}");
        return XDocument.Load(path);
    }

    private static JsonDocument ReadJson(string relativePath)
    {
        var path = RepoPath(relativePath);
        Assert.True(File.Exists(path), $"Expected JSON file at {path}");
        return JsonDocument.Parse(File.ReadAllBytes(path));
    }

    private static string RepoPath(string relativePath) => Path.Combine(LocateRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string ReadRepoText(string relativePath)
    {
        var path = RepoPath(relativePath);
        Assert.True(File.Exists(path), $"Expected repository file at {path}");
        return File.ReadAllText(path);
    }

    private static string[] EnumerateWorkflowFiles() =>
        Directory.EnumerateFiles(RepoPath(".github"), "*.yml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(RepoPath(".github"), "*.yaml", SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static IEnumerable<ActionUse> EnumerateThirdPartyActionUses()
    {
        var pattern = new Regex(@"^\s*uses:\s*(?<target>[^\s#]+)(?:\s+#\s*(?<comment>.+?))?\s*$", RegexOptions.CultureInvariant);
        foreach (var path in EnumerateWorkflowFiles())
        {
            foreach (var line in File.ReadLines(path))
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;
                var target = match.Groups["target"].Value;
                if (target.StartsWith("./", StringComparison.Ordinal)) continue;
                var separator = target.LastIndexOf('@');
                Assert.True(separator > 0, $"Third-party action use in {path} omitted a revision: {target}");
                yield return new ActionUse(
                    NormalizePath(Path.GetRelativePath(LocateRepoRoot(), path)),
                    target[..separator],
                    target[(separator + 1)..],
                    match.Groups["comment"].Success ? match.Groups["comment"].Value.Trim() : null);
            }
        }
    }

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("WPAMCP_DOTNET_HOST")
                ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
                ?? "dotnet",
            WorkingDirectory = LocateRepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), "Failed to start dotnet restore.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} exceeded three minutes.");
        }

        return (process.ExitCode, $"{await stdout}{Environment.NewLine}{await stderr}");
    }

    private sealed record ActionUse(string File, string Repository, string Revision, string? Comment);
}
