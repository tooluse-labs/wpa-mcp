using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class ToolsListPaginationBaselineTests
{
    [Fact]
    public void ReviewedPhase2PaginationBudget_MatchesActiveCatalogAndExactPageFitting()
    {
        using var artifact = JsonDocument.Parse(File.ReadAllBytes(ArtifactPath()));
        var root = artifact.RootElement;
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var serverTools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        IReadOnlyList<Tool> tools = serverTools.Select(tool => tool.ProtocolTool).ToArray();
        var preflight = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        var contractPreflight = ToolContractDiscoveryPreflight.Measure(catalog, serverTools);
        var aggregate = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult { Tools = tools.ToArray() },
            McpJsonUtilities.DefaultOptions);
        var candidate = BuildCandidate(
            root,
            catalog,
            tools,
            preflight,
            contractPreflight,
            aggregate);
        var reviewed = JsonNode.Parse(root.GetRawText());
        if (!JsonNode.DeepEquals(reviewed, candidate))
        {
            var candidatePath = Path.Combine(
                Path.GetTempPath(),
                $"wpa-mcp-tools-list-pagination-{Guid.NewGuid():N}.actual.json");
            File.WriteAllText(
                candidatePath,
                candidate.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            Assert.Fail(
                "The reviewed tools/list pagination measurements changed. Review the exact catalog, " +
                $"preflight, and page-frame diff, then update {ArtifactPath()} deliberately. " +
                $"Candidate: {candidatePath}.");
        }

        var server = root.GetProperty("server");
        Assert.Equal(catalog.CatalogVersion, server.GetProperty("catalogVersion").GetString());
        Assert.Equal(tools.Count, server.GetProperty("toolCount").GetInt32());

        var reviewedPreflight = root.GetProperty("startupPreflight");
        Assert.Equal(preflight.MinimumSuccessFrameBytes,
            reviewedPreflight.GetProperty("minimumSuccessFrameBytes").GetInt32());
        Assert.Equal(preflight.MinimumViableFrameBytes,
            reviewedPreflight.GetProperty("minimumViableResponseBytes").GetInt32());
        Assert.Equal(preflight.LargestSingleToolName,
            reviewedPreflight.GetProperty("largestSingleTool").GetString());
        Assert.Equal(preflight.LargestSingleToolFrameBytes,
            reviewedPreflight.GetProperty("largestSingleToolFrameBytes").GetInt32());

        var reviewedContractPreflight = root.GetProperty("contractDiscoveryPreflight");
        Assert.Equal(contractPreflight.FixedPageUtf8Bytes,
            reviewedContractPreflight.GetProperty("fixedPageUtf8Bytes").GetInt32());
        Assert.Equal(contractPreflight.PageCount,
            reviewedContractPreflight.GetProperty("pageCount").GetInt32());
        Assert.Equal(contractPreflight.MaximumToolFrameBytes,
            reviewedContractPreflight.GetProperty("maximumToolFrameBytes").GetInt32());
        Assert.Equal(contractPreflight.MaximumResourceFrameBytes,
            reviewedContractPreflight.GetProperty("maximumResourceFrameBytes").GetInt32());
        Assert.Equal(contractPreflight.MinimumViableFrameBytes,
            reviewedContractPreflight.GetProperty("minimumViableResponseBytes").GetInt32());

        var reviewedAggregate = root.GetProperty("aggregateCatalog");
        Assert.Equal(aggregate.Length, reviewedAggregate.GetProperty("bytes").GetInt32());
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(aggregate)).ToLowerInvariant(),
            reviewedAggregate.GetProperty("sha256").GetString());

        var traversal = root.GetProperty("exactMinimumProductionStdioTraversal");
        var cap = traversal.GetProperty("maxJsonRpcResponseBytes").GetInt32();
        var expectedFrames = traversal.GetProperty("pageFrameBytes")
            .EnumerateArray()
            .Select(value => value.GetInt32())
            .ToArray();
        var actualFrames = new List<int>();
        var index = 0;
        var pageIndex = 0;
        while (index < tools.Count)
        {
            RequestId id = pageIndex % 2 == 0
                ? new RequestId(100 + pageIndex)
                : new RequestId($"目录页-{pageIndex}");
            var page = ToolsListPageFitter.FitProtocolPage(tools, index, id, cap);
            Assert.True(page.NextIndex > index);
            actualFrames.Add(page.FrameBytes);
            index = page.NextIndex;
            pageIndex++;
        }

        Assert.Equal(expectedFrames, actualFrames);
        Assert.Equal(pageIndex, traversal.GetProperty("pageCount").GetInt32());
        Assert.Equal(actualFrames.Max(),
            traversal.GetProperty("maximumObservedPageFrameBytes").GetInt32());
        Assert.False(root.GetProperty("reviewBoundary")
            .GetProperty("pagingReducesAggregatePromptCost").GetBoolean());
        Assert.True(root.GetProperty("reviewBoundary")
            .GetProperty("leanProjectionReducesAggregatePromptCost").GetBoolean());
        Assert.False(root.GetProperty("exactMinimumProductionStdioTraversal")
            .GetProperty("embeddedOutputSchemas").GetBoolean());
        Assert.All(tools, tool => Assert.Null(tool.OutputSchema));
    }

    private static JsonObject BuildCandidate(
        JsonElement reviewed,
        ActiveToolCatalog catalog,
        IReadOnlyList<Tool> tools,
        ToolsListPagingPreflight preflight,
        ToolContractDiscoveryPreflightResult contractPreflight,
        byte[] aggregate)
    {
        var candidate = JsonNode.Parse(reviewed.GetRawText())!.AsObject();
        candidate["server"]!["catalogVersion"] = catalog.CatalogVersion;
        candidate["server"]!["toolCount"] = tools.Count;
        candidate["aggregateCatalog"]!["bytes"] = aggregate.Length;
        candidate["aggregateCatalog"]!["sha256"] =
            Convert.ToHexString(SHA256.HashData(aggregate)).ToLowerInvariant();
        candidate["startupPreflight"]!["minimumSuccessFrameBytes"] =
            preflight.MinimumSuccessFrameBytes;
        candidate["startupPreflight"]!["minimumViableResponseBytes"] =
            preflight.MinimumViableFrameBytes;
        candidate["startupPreflight"]!["largestSingleTool"] =
            preflight.LargestSingleToolName;
        candidate["startupPreflight"]!["largestSingleToolFrameBytes"] =
            preflight.LargestSingleToolFrameBytes;
        candidate["startupPreflight"]!["scope"] = "tools_list_only";
        candidate["contractDiscoveryPreflight"] = new JsonObject
        {
            ["serializedRequestIdBytes"] = ToolRequestIdPolicy.MaxSerializedBytes,
            ["fixedPageUtf8Bytes"] = contractPreflight.FixedPageUtf8Bytes,
            ["toolCount"] = contractPreflight.ToolCount,
            ["pageCount"] = contractPreflight.PageCount,
            ["maximumToolFrameBytes"] = contractPreflight.MaximumToolFrameBytes,
            ["maximumToolFrameToolName"] = contractPreflight.MaximumToolFrameToolName,
            ["maximumToolFramePage"] = contractPreflight.MaximumToolFramePage,
            ["maximumResourceFrameBytes"] = contractPreflight.MaximumResourceFrameBytes,
            ["maximumResourceFrameToolName"] = contractPreflight.MaximumResourceFrameToolName,
            ["maximumResourceFramePage"] = contractPreflight.MaximumResourceFramePage,
            ["minimumViableResponseBytes"] = contractPreflight.MinimumViableFrameBytes,
            ["capMinusOneBehavior"] = "startup_exit_78_before_stdin",
        };

        var cap = Math.Max(
            preflight.MinimumViableFrameBytes,
            contractPreflight.MinimumViableFrameBytes);
        var frames = new List<int>();
        var index = 0;
        var pageIndex = 0;
        while (index < tools.Count)
        {
            RequestId id = pageIndex % 2 == 0
                ? new RequestId(100 + pageIndex)
                : new RequestId($"目录页-{pageIndex}");
            var page = ToolsListPageFitter.FitProtocolPage(tools, index, id, cap);
            frames.Add(page.FrameBytes);
            index = page.NextIndex;
            pageIndex++;
        }
        candidate["exactMinimumProductionStdioTraversal"]!["maxJsonRpcResponseBytes"] = cap;
        candidate["exactMinimumProductionStdioTraversal"]!["pageCount"] = frames.Count;
        candidate["exactMinimumProductionStdioTraversal"]!["pageFrameBytes"] =
            new JsonArray(frames.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        candidate["exactMinimumProductionStdioTraversal"]!["maximumObservedPageFrameBytes"] =
            frames.Max();
        candidate["exactMinimumProductionStdioTraversal"]!["closure"] =
            $"all_{tools.Count}_tools_exactly_once_in_validated_order";
        return candidate;
    }

    private static string ArtifactPath() => Path.Combine(
        LocateRepoRoot(),
        "eng",
        "contract-baselines",
        "tools-list-pagination.v1.json");

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
