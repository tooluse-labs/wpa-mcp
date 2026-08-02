using System.Security.Cryptography;
using System.Text.Json;
using WpaMcp.Tests.ContractBaselines;

namespace WpaMcp.Tests;

public sealed class LegacyActiveToolSnapshotTests
{
    private const string LegacyBaselineSha256 =
        "77d0ada2342f4af4e4c84cf297af816524356e6973229730a8ee707c846a960e";

    private static readonly string[] ExpectedStructuredTools =
    [
        "diagnose_high_wait",
        "diagnose_window",
        "inspect_trace",
        "unload_trace",
        "wait_analysis",
    ];

    [Fact]
    public void ReviewedLegacyToolContract_IsImmutableMigrationEvidence()
    {
        var baselinePath = BaselinePath("legacy-active-tools.v1.json");
        var bytes = File.ReadAllBytes(baselinePath);

        Assert.Equal(LegacyBaselineSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(217_472, bytes.Length);

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal("legacy-active-tools.v1", root.GetProperty("formatVersion").GetString());
        Assert.Equal("reviewed_legacy_compatibility_floor", root.GetProperty("baselineKind").GetString());
        Assert.Equal(61, root.GetProperty("toolCount").GetInt32());
        Assert.Equal(179_107, root.GetProperty("catalogBytes").GetInt32());
        Assert.Equal(
            ExpectedStructuredTools,
            root.GetProperty("structuredToolNames")
                .EnumerateArray()
                .Select(name => name.GetString()));
        Assert.Equal("2dfb459", root.GetProperty("preRefactorObservation").GetProperty("commit").GetString());
    }

    [Fact]
    public void ActiveToolContract_MatchesReviewedBaseline()
    {
        var snapshot = LegacyActiveToolSnapshotBuilder.Build();
        var activeCatalog = WpaMcp.Core.Catalog.ActiveToolCatalog.LoadAndValidate();
        var actual = LegacyActiveToolSnapshotBuilder.BuildCanonicalJson();
        var baselinePath = BaselinePath("active-tools.v1.json");
        if (!File.Exists(baselinePath))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                $"The reviewed current catalog golden is missing: {baselinePath}. " +
                $"Review the captured candidate at {actualPath}, then add it deliberately.");
        }

        var expected = File.ReadAllText(baselinePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal("active-tools.v1", snapshot.FormatVersion);
        Assert.Equal("reviewed_current_active_catalog", snapshot.BaselineKind);
        Assert.Equal(activeCatalog.Tools.Count, snapshot.ToolCount);
        Assert.Equal(snapshot.ToolCount, snapshot.Tools.Select(tool => tool.Name).Distinct().Count());
        Assert.Equal(snapshot.ToolCount, snapshot.StructuredToolNames.Count);
        Assert.Equal(snapshot.Tools.Select(tool => tool.Name), snapshot.StructuredToolNames);
        Assert.All(snapshot.Tools, tool =>
        {
            Assert.True(tool.UseStructuredContent, $"{tool.Name} is missing structured content.");
            Assert.True(tool.OutputSchemaBytes > 0, $"{tool.Name} is missing its output schema.");
            Assert.NotNull(tool.OutputSchemaSha256);
        });
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                "The active MCP catalog changed. Review the tool metadata and wire-schema diff, " +
                $"then update {baselinePath} deliberately with apply_patch. " +
                $"Candidate: {actualPath}. Actual catalog SHA-256: {snapshot.CatalogSha256} " +
                $"({snapshot.CatalogBytes} bytes). This test never overwrites the reviewed baseline.");
        }
    }

    private static string BaselinePath(string fileName) => Path.Combine(
        LocateRepoRoot(),
        "tests",
        "WpaMcp.Tests",
        "ContractBaselines",
        fileName);

    private static string WriteMismatchArtifact(string actual)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"wpa-mcp-active-tools-{Guid.NewGuid():N}.actual.json");
        File.WriteAllText(path, actual);
        return path;
    }

    private static string LocateRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
