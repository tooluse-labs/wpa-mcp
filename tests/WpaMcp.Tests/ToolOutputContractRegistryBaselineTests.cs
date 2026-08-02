using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class ToolOutputContractRegistryBaselineTests
{
    private const string FormatVersion = "tool-output-contract-registry.v1";

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        WriteIndented = false,
    };

    [Fact]
    public void ReviewedRegistry_MatchesCompleteActiveOutputContractCatalog()
    {
        var artifactPath = ArtifactPath();
        using var artifact = JsonDocument.Parse(File.ReadAllBytes(artifactPath));
        var root = artifact.RootElement;
        var measuredAt = root.GetProperty("measuredAt").GetString();
        Assert.True(
            DateOnly.TryParseExact(
                measuredAt,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _),
            "measuredAt must be a reviewed ISO calendar date (yyyy-MM-dd).");

        var catalog = ActiveToolCatalog.LoadAndValidate();
        var candidate = BuildCandidate(catalog, measuredAt!);
        var reviewed = JsonNode.Parse(root.GetRawText());
        if (!JsonNode.DeepEquals(reviewed, candidate))
        {
            var candidatePath = Path.Combine(
                Path.GetTempPath(),
                $"wpa-mcp-tool-output-contract-registry-{Guid.NewGuid():N}.actual.json");
            File.WriteAllText(
                candidatePath,
                candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) +
                Environment.NewLine);
            Assert.Fail(
                "The reviewed complete output-contract registry changed. Review every descriptor, " +
                $"aggregate byte count, and registry hash before deliberately updating {artifactPath}. " +
                $"Candidate: {candidatePath}.");
        }

        var tools = root.GetProperty("tools");
        Assert.Equal(FormatVersion, root.GetProperty("formatVersion").GetString());
        Assert.Equal(catalog.OutputContracts.Count, root.GetProperty("toolCount").GetInt32());
        Assert.Equal(catalog.OutputContracts.Count, tools.GetArrayLength());
        Assert.Equal(
            catalog.OutputContracts.Values.Sum(contract => (long)contract.Utf8Bytes),
            root.GetProperty("totalCanonicalUtf8Bytes").GetInt64());
        Assert.Equal(
            Sha256(CompactUtf8(tools)),
            root.GetProperty("registrySha256").GetString());
    }

    private static JsonObject BuildCandidate(ActiveToolCatalog catalog, string measuredAt)
    {
        var descriptors = new JsonArray(catalog.OutputContracts.Values
            .OrderBy(contract => contract.ToolName, StringComparer.Ordinal)
            .Select(contract => (JsonNode?)new JsonObject
            {
                ["toolName"] = contract.ToolName,
                ["contractVersion"] = contract.ContractVersion,
                ["schemaDialect"] = contract.SchemaDialect,
                ["schemaUri"] = contract.SchemaUri,
                ["sha256"] = contract.Sha256,
                ["mediaType"] = contract.MediaType,
                ["utf8Bytes"] = contract.Utf8Bytes,
            })
            .ToArray());

        return new JsonObject
        {
            ["formatVersion"] = FormatVersion,
            ["measuredAt"] = measuredAt,
            ["registrySha256Canonicalization"] = new JsonObject
            {
                ["hashAlgorithm"] = "SHA-256",
                ["hexCase"] = "lowercase",
                ["encoding"] = "UTF-8 without BOM",
                ["payload"] = "compact JSON array of tools descriptors; no trailing newline",
                ["jsonEscaping"] = "System.Text.Json default encoder",
                ["toolOrdering"] = "toolName ordinal ascending",
                ["descriptorPropertyOrder"] = new JsonArray(
                    "toolName",
                    "contractVersion",
                    "schemaDialect",
                    "schemaUri",
                    "sha256",
                    "mediaType",
                    "utf8Bytes"),
            },
            ["toolCount"] = catalog.OutputContracts.Count,
            ["totalCanonicalUtf8Bytes"] = catalog.OutputContracts.Values
                .Sum(contract => (long)contract.Utf8Bytes),
            ["registrySha256"] = Sha256(CompactUtf8(descriptors)),
            ["tools"] = descriptors,
        };
    }

    private static byte[] CompactUtf8(JsonArray descriptors) =>
        Encoding.UTF8.GetBytes(descriptors.ToJsonString(CompactJson));

    private static byte[] CompactUtf8(JsonElement descriptors) =>
        JsonSerializer.SerializeToUtf8Bytes(descriptors, CompactJson);

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string ArtifactPath() => Path.Combine(
        LocateRepositoryRoot(),
        "eng",
        "contract-baselines",
        "tool-output-contract-registry.v1.json");

    private static string LocateRepositoryRoot()
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
