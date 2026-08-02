using System.Security.Cryptography;
using System.Text.Json;
using WpaMcp.Tests.ContractBaselines;

namespace WpaMcp.Tests;

public sealed class LegacyDtoInventorySnapshotTests
{
    private const string LegacyBaselineSha256 =
        "90b5530a60bf5a98d71377a4a4db6d290e32be6f559479bd4a118b79488f529a";

    [Fact]
    public void ReviewedLegacyDtoContract_IsImmutableMigrationEvidence()
    {
        var baselinePath = BaselinePath("legacy-dto-inventory.v1.json");
        var bytes = File.ReadAllBytes(baselinePath);

        Assert.Equal(LegacyBaselineSha256, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        Assert.Equal(233_524, bytes.Length);

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal("legacy-dto-inventory.v1", root.GetProperty("formatVersion").GetString());
        Assert.Equal("reviewed_compatibility_floor", root.GetProperty("baselineKind").GetString());
        Assert.Equal("WpaMcp.Output", root.GetProperty("namespace").GetString());
        Assert.Equal(132, root.GetProperty("typeCount").GetInt32());
        Assert.Equal(44, root.GetProperty("responseTypeCount").GetInt32());
        Assert.Equal(1_618, root.GetProperty("propertyCount").GetInt32());
        Assert.Equal(
            "2c3e9c32b8b413891a98206238f1c7fa8a5ebd65ddc1c03d3ae81cec105bc944",
            root.GetProperty("contractSha256").GetString());
    }

    [Fact]
    public void ActivePublicOutputDtoContract_MatchesReviewedBaseline()
    {
        var snapshot = LegacyDtoInventorySnapshotBuilder.Build();
        var actual = LegacyDtoInventorySnapshotBuilder.BuildCanonicalJson();
        var baselinePath = BaselinePath("active-dto-inventory.v1.json");
        if (!File.Exists(baselinePath))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                $"The reviewed current DTO inventory golden is missing: {baselinePath}. " +
                $"Review the captured candidate at {actualPath}, then add it deliberately.");
        }

        var expected = File.ReadAllText(baselinePath)
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal("active-dto-inventory.v1", snapshot.FormatVersion);
        Assert.Equal("reviewed_current_active_dto_contract", snapshot.BaselineKind);
        var activeCatalog = WpaMcp.Core.Catalog.ActiveToolCatalog.LoadAndValidate();
        Assert.Equal(activeCatalog.Tools.Count, snapshot.ActiveToolCount);
        Assert.Equal(snapshot.ActiveToolCount, snapshot.ActiveToolOutputs.Count);
        Assert.Equal(
            snapshot.ActiveToolCount,
            snapshot.ActiveToolOutputs.Select(binding => binding.ToolName)
                .Distinct(StringComparer.Ordinal)
                .Count());
        AssertSortedUnique(
            snapshot.ActiveToolOutputs.Select(binding => binding.ToolName),
            "active tool DTO bindings");
        Assert.Equal(
            snapshot.ActiveDataTypeCount,
            snapshot.ActiveToolOutputs.Select(binding => binding.DataType)
                .Distinct(StringComparer.Ordinal)
                .Count());
        var inventoriedTypes = snapshot.Types.Select(type => type.Name)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(snapshot.ActiveToolOutputs, binding =>
        {
            Assert.Contains(binding.DataType, inventoriedTypes);
            Assert.Equal(
                $"WpaMcp.Output.ToolEnvelope<{binding.DataType}>",
                binding.EnvelopeType);
        });
        Assert.Equal(snapshot.TypeCount, snapshot.Types.Count);
        Assert.Equal(
            snapshot.TypeCount,
            snapshot.Types.Select(type => type.Name).Distinct(StringComparer.Ordinal).Count());
        AssertSortedUnique(snapshot.Types.Select(type => type.Name), "DTO type set");
        foreach (var type in snapshot.Types)
            AssertSortedUnique(type.Properties.Select(property => property.Name), $"{type.Name} properties");
        Assert.Equal(
            snapshot.PropertyCount,
            snapshot.Types.Sum(type => type.Properties.Count));
        Assert.Equal(
            snapshot.PropertyCount,
            snapshot.Types
                .SelectMany(type => type.Properties.Select(property => $"{type.Name}.{property.Name}"))
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.Equal(snapshot.ResponseTypeCount, snapshot.Candidates.ResponseTypes.Count);
        Assert.Equal(
            snapshot.ResponseTypeCount,
            snapshot.Types.Count(type => type.Name.EndsWith("Response", StringComparison.Ordinal)));
        AssertSortedUnique(snapshot.Candidates.PublicUlongProperties, "public ulong properties");
        AssertSortedUnique(snapshot.Candidates.IdLikeIntegerProperties, "ID-like integer properties");
        AssertSortedUnique(snapshot.Candidates.CollectionProperties, "collection properties");
        AssertSortedUnique(snapshot.Candidates.TopNCandidates, "Top-N candidates");
        AssertSortedUnique(snapshot.Candidates.TimelineCandidates, "timeline candidates");
        AssertSortedUnique(snapshot.Candidates.ResponseTypes, "response types");
        Assert.Equal(64, snapshot.TypeSetSha256.Length);
        Assert.Equal(64, snapshot.PropertySetSha256.Length);
        Assert.Equal(64, snapshot.ContractSha256.Length);
        Assert.Equal(actual, LegacyDtoInventorySnapshotBuilder.BuildCanonicalJson());
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                "The reviewed current public WpaMcp.Output DTO contract changed. Review the exact "
                + "type/property metadata and collection, identifier, metric, Top-N, and timeline "
                + $"candidate diff, then update {baselinePath} deliberately with apply_patch. "
                + $"Candidate: {actualPath}. Actual contract SHA-256: {snapshot.ContractSha256}. "
                + "This test never overwrites the reviewed baseline.");
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
            $"wpa-mcp-active-dto-inventory-{Guid.NewGuid():N}.actual.json");
        File.WriteAllText(path, actual);
        return path;
    }

    private static void AssertSortedUnique(IEnumerable<string> values, string label)
    {
        var actual = values.ToArray();
        var expected = actual
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            expected.SequenceEqual(actual, StringComparer.Ordinal),
            $"{label} must be ordinal-sorted and unique.");
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
