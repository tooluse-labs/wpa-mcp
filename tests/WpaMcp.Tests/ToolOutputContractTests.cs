using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolOutputContractTests
{
    [Fact]
    public void ActiveRegistry_IsCompleteClosedAndContentAddressedFromCanonicalUtf8()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var contracts = catalog.OutputContracts;

        Assert.Equal(62, catalog.Tools.Count);
        Assert.Equal(62, contracts.Count);
        Assert.True(contracts.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(
            catalog.Tools.Select(tool => tool.ToolName)));

        foreach (var tool in catalog.Tools)
        {
            var contract = contracts[tool.ToolName];
            Assert.Same(tool.OutputContract, contract);
            Assert.Equal(tool.ToolName, contract.ToolName);
            Assert.Equal(ToolContractVersions.V2, contract.ContractVersion);
            Assert.Equal(ToolOutputContract.Draft202012, contract.SchemaDialect);
            Assert.Equal(ToolOutputContract.ContractMediaType, contract.MediaType);

            var canonicalUtf8 = Encoding.UTF8.GetBytes(contract.CanonicalJson);
            var canonicalSha256 = Convert.ToHexString(SHA256.HashData(canonicalUtf8))
                .ToLowerInvariant();
            Assert.Equal(canonicalUtf8.Length, contract.Utf8Bytes);
            Assert.Equal(canonicalSha256, contract.Sha256);
            Assert.Matches("^[0-9a-f]{64}$", contract.Sha256);
            Assert.Equal(
                $"wpa://contracts/tools/{tool.ToolName}/{contract.Sha256}",
                contract.SchemaUri);

            var schema = contract.ParseSchema();
            Assert.Equal(contract.SchemaDialect, schema["$schema"]!.GetValue<string>());
            Assert.False(schema["additionalProperties"]!.GetValue<bool>());
            Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
            Assert.Equal(
                contract,
                ToolOutputSchemaFactory.CreateContract(tool.ToolName, tool.OutputDataType));
        }

        Assert.Equal(62, contracts.Values.Select(contract => contract.SchemaUri)
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryContractWrapper_AdvertisesOnlyAnExactOutputContractReference()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();

        var wrappers = catalog.CreateServerTools(provider);
        var contracts = catalog.OutputContracts;

        Assert.Equal(62, wrappers.Count);
        Assert.All(wrappers, wrapper => Assert.IsType<ContractMcpServerTool>(wrapper));
        foreach (var wrapper in wrappers)
        {
            var contract = contracts[wrapper.ProtocolTool.Name];
            Assert.Null(wrapper.ProtocolTool.OutputSchema);
            var metadata = Assert.IsType<JsonObject>(
                wrapper.ProtocolTool.Meta?[ToolOutputContract.MetadataKey]);

            Assert.Equal(contract.ContractVersion,
                metadata["contractVersion"]!.GetValue<string>());
            Assert.Equal(contract.SchemaUri, metadata["uri"]!.GetValue<string>());
            Assert.Equal(contract.Sha256, metadata["sha256"]!.GetValue<string>());
            Assert.Equal(contract.Utf8Bytes, metadata["utf8Bytes"]!.GetValue<int>());
            Assert.Equal(contract.SchemaDialect,
                metadata["schemaDialect"]!.GetValue<string>());
            Assert.Equal(contract.MediaType, metadata["mediaType"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(
                contract.ToDiscoveryMetadata(),
                metadata));
        }
    }

    [Fact]
    public void AddingContractDiscovery_PreservesEveryReviewedToolSchemaHash()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(
            LocateRepositoryRoot(),
            "eng",
            "contract-baselines",
            "pre-resource-output-schema-hashes.v1.json")));
        Assert.Equal(
            "pre-resource-output-schema-hashes.v1",
            baseline.RootElement.GetProperty("formatVersion").GetString());
        Assert.Equal(60, baseline.RootElement.GetProperty("toolCount").GetInt32());
        var reviewedHashes = baseline.RootElement.GetProperty("tools")
            .EnumerateArray()
            .ToDictionary(
                tool => tool.GetProperty("toolName").GetString()!,
                tool => tool.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);

        Assert.Equal(60, reviewedHashes.Count);
        Assert.Equal(
            ["get_tool_contract", "thread_compare_windows"],
            catalog.OutputContracts.Keys.Except(reviewedHashes.Keys, StringComparer.Ordinal));
        Assert.All(reviewedHashes, reviewed => Assert.Equal(
            reviewed.Value,
            catalog.OutputContracts[reviewed.Key].Sha256));
    }

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
