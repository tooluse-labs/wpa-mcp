using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ReleasePackageStdioTests
{
    private const string ServerPathVariable = "WPAMCP_RELEASE_SERVER_PATH";
    private const string RequiredVariable = "WPAMCP_RELEASE_REQUIRED";
    private const string EvidencePathVariable = "WPAMCP_RELEASE_EVIDENCE_PATH";

    [Fact]
    [Trait("Category", "Package")]
    public async Task PublishedExecutable_ExposesCompleteToolAndCapabilityCatalogs()
    {
        var serverPath = ReleaseServerPathOrSkip();
        if (serverPath is null)
            return;

        var expected = ActiveToolCatalog.LoadAndValidate();
        var expectedProtocolTools = expected
            .CreateProtocolTools(new DeferredCatalogServiceProvider())
            .ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        var executableHashBefore = Sha256(serverPath);
        await using var server = await PackageServer.StartAsync(serverPath);

        var initializeId = new string('i', 126);
        var initialize = await server.RequestAsync(
            initializeId,
            "initialize",
            new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "wpa-mcp-release-package-gate",
                    ["version"] = "1.0",
                },
            });
        Assert.Equal("2025-11-25", initialize.Node["result"]?["protocolVersion"]?.GetValue<string>());
        await server.NotifyAsync("notifications/initialized", new JsonObject());

        var toolNames = new List<string>();
        var toolNodes = new List<JsonNode>();
        var advertisedContracts = new List<PackagedContractAdvertisement>();
        string? toolsCursor = null;
        var toolPage = 0;
        do
        {
            var response = await server.RequestAsync(
                $"tools-{toolPage}",
                "tools/list",
                toolsCursor is null
                    ? new JsonObject()
                    : new JsonObject { ["cursor"] = toolsCursor });
            var result = Assert.IsType<JsonObject>(response.Node["result"]);
            var tools = Assert.IsType<JsonArray>(result["tools"]);
            Assert.NotEmpty(tools);
            foreach (var item in tools)
            {
                var tool = Assert.IsType<JsonObject>(item);
                var toolName = tool["name"]!.GetValue<string>();
                var contract = expected.OutputContracts[toolName];
                toolNames.Add(toolName);
                toolNodes.Add(tool.DeepClone());
                Assert.True(JsonNode.DeepEquals(
                    JsonSerializer.SerializeToNode(
                        expectedProtocolTools[toolName],
                        McpJsonUtilities.DefaultOptions),
                    tool));
                Assert.IsType<JsonObject>(tool["inputSchema"]);
                Assert.Null(tool["outputSchema"]);
                var metadata = Assert.IsType<JsonObject>(
                    tool["_meta"]?[ToolOutputContract.MetadataKey]);
                Assert.Equal(contract.SchemaUri, metadata["uri"]!.GetValue<string>());
                Assert.Equal(contract.Sha256, metadata["sha256"]!.GetValue<string>());
                Assert.Equal(contract.Utf8Bytes, metadata["utf8Bytes"]!.GetValue<int>());
                Assert.True(JsonNode.DeepEquals(contract.ToDiscoveryMetadata(), metadata));
                var advertised = new PackagedContractAdvertisement(
                    toolName,
                    metadata["contractVersion"]!.GetValue<string>(),
                    metadata["schemaDialect"]!.GetValue<string>(),
                    metadata["uri"]!.GetValue<string>(),
                    metadata["sha256"]!.GetValue<string>(),
                    metadata["mediaType"]!.GetValue<string>(),
                    metadata["utf8Bytes"]!.GetValue<int>());
                Assert.Equal(ToolContractVersions.V2, advertised.ContractVersion);
                Assert.Equal(ToolOutputContract.Draft202012, advertised.SchemaDialect);
                Assert.Equal(ToolOutputContract.ContractMediaType, advertised.MediaType);
                Assert.Equal("utf8_json_pages", metadata["representation"]!.GetValue<string>());
                Assert.Matches("^[0-9a-f]{64}$", advertised.Sha256);
                Assert.Equal(
                    $"wpa://contracts/tools/{toolName}/{advertised.Sha256}",
                    advertised.SchemaUri);
                Assert.InRange(advertised.Utf8Bytes, 1, int.MaxValue);
                advertisedContracts.Add(advertised);
            }
            toolsCursor = result["nextCursor"]?.GetValue<string>();
            toolPage++;
            Assert.InRange(toolPage, 1, expected.Tools.Count);
        }
        while (toolsCursor is not null);

        Assert.Equal(expected.Tools.Select(tool => tool.ToolName), toolNames);
        Assert.Equal(62, toolNames.Count);
        Assert.Equal(toolNames.Count, toolNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(toolNames, advertisedContracts.Select(contract => contract.ToolName));
        var aggregateToolsList = new JsonObject
        {
            ["tools"] = new JsonArray(toolNodes.Select(tool => tool.DeepClone()).ToArray()),
        };
        var aggregateToolsListBytes = Encoding.UTF8.GetByteCount(
            aggregateToolsList.ToJsonString());
        Assert.InRange(
            aggregateToolsListBytes,
            1,
            ToolListPayload.DefaultMaxPayloadBytes);

        var outputContractResourcePageCount = 0;
        var outputContractToolPageCount = 0;
        var outputContractCanonicalBytes = 0;
        for (var index = 0; index < advertisedContracts.Count; index++)
        {
            var advertised = advertisedContracts[index];
            var resourceProjection = await ReadContractResourceAsync(server, advertised, index);
            var toolProjection = await ReadContractToolAsync(server, advertised, index);

            Assert.True(
                resourceProjection.CanonicalUtf8.AsSpan().SequenceEqual(toolProjection.CanonicalUtf8),
                $"Resource and tools-only contract projections differ for '{advertised.ToolName}'.");
            Assert.Equal(resourceProjection.Pages, toolProjection.Pages);
            Assert.Equal(advertised.Utf8Bytes, resourceProjection.CanonicalUtf8.Length);
            Assert.Equal(advertised.Sha256, Sha256Bytes(resourceProjection.CanonicalUtf8));
            Assert.Equal(
                expected.OutputContracts[advertised.ToolName].CanonicalJson,
                Encoding.UTF8.GetString(resourceProjection.CanonicalUtf8));

            outputContractResourcePageCount += resourceProjection.PageCount;
            outputContractToolPageCount += toolProjection.PageCount;
            outputContractCanonicalBytes += resourceProjection.CanonicalUtf8.Length;
        }
        Assert.Equal(62, advertisedContracts.Count);
        Assert.InRange(outputContractResourcePageCount, advertisedContracts.Count, int.MaxValue);
        Assert.Equal(outputContractResourcePageCount, outputContractToolPageCount);
        Assert.Equal(
            expected.OutputContracts.Values.Sum(contract =>
                (contract.Utf8Bytes + CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes - 1) /
                CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes),
            outputContractResourcePageCount);
        Assert.Equal(
            expected.OutputContracts.Values.Sum(contract => contract.Utf8Bytes),
            outputContractCanonicalBytes);

        var capabilityIds = new List<string>();
        string? capabilityCursor = null;
        var capabilityPage = 0;
        do
        {
            var arguments = capabilityCursor is null
                ? new JsonObject()
                : new JsonObject { ["cursor"] = capabilityCursor };
            var response = await server.RequestAsync(
                $"capabilities-{capabilityPage}",
                "tools/call",
                new JsonObject
                {
                    ["name"] = "list_capabilities",
                    ["arguments"] = arguments,
                });
            var result = Assert.IsType<JsonObject>(response.Node["result"]);
            Assert.False(result["isError"]?.GetValue<bool>() ?? false);
            var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
            var content = Assert.IsType<JsonArray>(result["content"]);
            var text = Assert.IsType<JsonObject>(Assert.Single(content));
            Assert.Equal("text", text["type"]?.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(structured, JsonNode.Parse(text["text"]!.GetValue<string>())));

            var data = Assert.IsType<JsonObject>(structured["data"]);
            var capabilities = Assert.IsType<JsonArray>(data["capabilities"]);
            Assert.NotEmpty(capabilities);
            capabilityIds.AddRange(capabilities.Select(item =>
                item!["capabilityId"]!.GetValue<string>()));
            capabilityCursor = data["nextCursor"]?.GetValue<string>();
            capabilityPage++;
            Assert.InRange(capabilityPage, 1, expected.Capabilities.Count);
        }
        while (capabilityCursor is not null);

        var expectedCapabilityIds = expected.Capabilities
            .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => capability.CapabilityId);
        Assert.Equal(expectedCapabilityIds, capabilityIds);
        Assert.Equal(capabilityIds.Count, capabilityIds.Distinct(StringComparer.Ordinal).Count());

        var resourceResponse = await server.RequestAsync(
            "capability-resource",
            "resources/read",
            new JsonObject { ["uri"] = "wpa://capabilities/server" });
        var resourceContents = Assert.IsType<JsonArray>(resourceResponse.Node["result"]?["contents"]);
        var resource = Assert.IsType<JsonObject>(Assert.Single(resourceContents));
        var resourceIndex = Assert.IsType<JsonObject>(JsonNode.Parse(resource["text"]!.GetValue<string>()));
        Assert.Equal(expected.CatalogVersion, resourceIndex["catalogVersion"]?.GetValue<string>());
        Assert.Equal(expected.Capabilities.Count, resourceIndex["totalItems"]?.GetValue<int>());

        var runtimeResponse = await server.RequestAsync(
            "runtime-profile-resource",
            "resources/read",
            new JsonObject { ["uri"] = "wpa://runtime/profile" });
        var runtimeContents = Assert.IsType<JsonArray>(runtimeResponse.Node["result"]?["contents"]);
        var runtimeContent = Assert.IsType<JsonObject>(Assert.Single(runtimeContents));
        var runtimeProfile = Assert.IsType<JsonObject>(
            JsonNode.Parse(runtimeContent["text"]!.GetValue<string>()));
        Assert.Equal("runtime-profile.v1", runtimeProfile["schemaVersion"]?.GetValue<string>());
        Assert.Equal("startup_immutable", runtimeProfile["selectionScope"]?.GetValue<string>());
        Assert.Equal("2.0", runtimeProfile["contractMode"]?.GetValue<string>());
        Assert.Equal("id_only", runtimeProfile["traceReferenceMode"]?.GetValue<string>());
        Assert.Equal(
            RuntimeCompatibilityPolicy.OutputSchemaDialect,
            runtimeProfile["outputSchemaDialect"]?.GetValue<string>());
        Assert.Equal(
            RuntimeCompatibilityPolicy.OutputSchemaReferenceProfile,
            runtimeProfile["outputSchemaReferenceProfile"]?.GetValue<string>());
        Assert.Equal(
            RuntimeCompatibilityPolicy.OutputSchemaReferenceRequirement,
            runtimeProfile["outputSchemaReferenceRequirement"]?.GetValue<string>());
        Assert.Equal(
            RuntimeCompatibilityPolicy.OutputSchemaExternalReferencePolicy,
            runtimeProfile["outputSchemaExternalReferencePolicy"]?.GetValue<string>());
        Assert.False(runtimeProfile["contractModeExplicit"]?.GetValue<bool>() ?? true);
        Assert.False(runtimeProfile["traceReferenceModeExplicit"]?.GetValue<bool>() ?? true);

        var exit = await server.CompleteAsync();
        Assert.Equal(0, exit);
        Assert.Equal(executableHashBefore, Sha256(serverPath));

        var evidencePath = Environment.GetEnvironmentVariable(EvidencePathVariable);
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            var evidence = new JsonObject
            {
                ["schemaVersion"] = "release-package-stdio.v1",
                ["commit"] = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                ["serverSha256"] = executableHashBefore,
                ["runtimeVersion"] = runtimeProfile["runtimeVersion"]?.GetValue<string>(),
                ["releaseLine"] = runtimeProfile["releaseLine"]?.GetValue<string>(),
                ["contractMode"] = runtimeProfile["contractMode"]?.GetValue<string>(),
                ["traceReferenceMode"] = runtimeProfile["traceReferenceMode"]?.GetValue<string>(),
                ["runtimeProfileReleaseStatus"] = runtimeProfile["releaseStatus"]?.GetValue<string>(),
                ["catalogVersion"] = expected.CatalogVersion,
                ["toolCount"] = toolNames.Count,
                ["toolPageCount"] = toolPage,
                ["toolsListAggregateBytes"] = aggregateToolsListBytes,
                ["outputContractCount"] = advertisedContracts.Count,
                ["outputContractResourcePageCount"] = outputContractResourcePageCount,
                ["outputContractToolPageCount"] = outputContractToolPageCount,
                ["outputContractCanonicalBytes"] = outputContractCanonicalBytes,
                ["capabilityCount"] = capabilityIds.Count,
                ["capabilityPageCount"] = capabilityPage,
                ["maxResponseFrameBytes"] = server.MaxResponseFrameBytes,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(evidencePath))!);
            await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
        }
    }

    [Fact]
    [Trait("Category", "Package")]
    public async Task PublishedExecutable_RejectsOversizedFirstIdBeforeMutableSideEffects()
    {
        var serverPath = ReleaseServerPathOrSkip();
        if (serverPath is null)
            return;

        var sandbox = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-release-ingress-" + Guid.NewGuid().ToString("N"));
        var telemetryFile = Path.Combine(sandbox, "telemetry", "events.jsonl");
        var startInfo = PackageServer.StartInfo(serverPath);
        startInfo.Environment["WPAMCP_TELEMETRY"] = "1";
        startInfo.Environment["WPAMCP_TELEMETRY_DEST"] = "file";
        startInfo.Environment["WPAMCP_TELEMETRY_FILE"] = telemetryFile;
        startInfo.Environment[TraceRuntimeOptions.AllowedRootsEnvironmentVariable] =
            Path.Combine(sandbox, "trace-source");
        startInfo.Environment[TraceRuntimeOptions.ArtifactRootEnvironmentVariable] =
            Path.Combine(sandbox, "trace-artifacts");
        startInfo.Environment[SymbolRuntimeOptions.LocalRootsEnvironmentVariable] =
            Path.Combine(sandbox, "symbol-source");
        startInfo.Environment[SymbolRuntimeOptions.StoreRootEnvironmentVariable] =
            Path.Combine(sandbox, "symbol-store");

        using var process = new Process { StartInfo = startInfo };
        try
        {
            Assert.True(process.Start());
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var frame = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = new string('r', 127),
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = "2025-11-25",
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "release-hostile-id", ["version"] = "1.0" },
                },
            };
            await process.StandardInput.WriteLineAsync(frame.ToJsonString().AsMemory(), timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);

            Assert.Equal(Program.RequestIdLimitExitCode, process.ExitCode);
            Assert.Equal(string.Empty, await stdout);
            Assert.Equal(
                JsonRpcFrameLimitingStream.RequestIdRejectionMessage + Environment.NewLine,
                await stderr);
            Assert.False(File.Exists(telemetryFile));
            Assert.False(Directory.Exists(sandbox));
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            if (Directory.Exists(sandbox))
                Directory.Delete(sandbox, recursive: true);
        }
    }

    private static string? ReleaseServerPathOrSkip()
    {
        var path = Environment.GetEnvironmentVariable(ServerPathVariable);
        if (string.IsNullOrWhiteSpace(path))
        {
            if (Environment.GetEnvironmentVariable(RequiredVariable) == "1")
                throw new InvalidOperationException($"{ServerPathVariable} is required in the release package lane.");
            return null;
        }

        path = Path.GetFullPath(path);
        if (!File.Exists(path))
            throw new FileNotFoundException("The published release executable is missing.", path);
        return path;
    }

    private static string Sha256(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string Sha256Bytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static async Task<ContractProjection> ReadContractResourceAsync(
        PackageServer server,
        PackagedContractAdvertisement advertised,
        int contractIndex)
    {
        var indexResponse = await server.RequestAsync(
            $"contract-resource-{contractIndex}-index",
            "resources/read",
            new JsonObject { ["uri"] = advertised.SchemaUri });
        var indexContents = Assert.IsType<JsonArray>(indexResponse.Node["result"]?["contents"]);
        var indexContent = Assert.IsType<JsonObject>(Assert.Single(indexContents));
        Assert.Equal(advertised.SchemaUri, indexContent["uri"]?.GetValue<string>());
        Assert.Equal("application/json", indexContent["mimeType"]?.GetValue<string>());
        var resourceIndex = Assert.IsType<JsonObject>(
            JsonNode.Parse(indexContent["text"]!.GetValue<string>()));
        Assert.Equal(advertised.ToolName, resourceIndex["toolName"]?.GetValue<string>());
        Assert.Equal(advertised.ContractVersion, resourceIndex["contractVersion"]?.GetValue<string>());
        Assert.Equal(advertised.SchemaUri, resourceIndex["schemaUri"]?.GetValue<string>());
        Assert.Equal(advertised.Sha256, resourceIndex["sha256"]?.GetValue<string>());
        Assert.Equal(advertised.MediaType, resourceIndex["mediaType"]?.GetValue<string>());
        Assert.Equal(advertised.Utf8Bytes, resourceIndex["utf8Bytes"]?.GetValue<int>());
        var pageCount = resourceIndex["pageCount"]?.GetValue<int>()
            ?? throw new JsonException("Contract resource index omitted pageCount.");
        Assert.InRange(pageCount, 1, int.MaxValue);
        Assert.Equal(
            $"{advertised.SchemaUri}/pages/{{page}}",
            resourceIndex["pageUriTemplate"]?.GetValue<string>());
        Assert.Equal("page_asc_start_utf8_byte_asc", resourceIndex["ordering"]?.GetValue<string>());

        using var assembled = new MemoryStream(advertised.Utf8Bytes);
        var boundaries = new List<ContractPageBoundary>(pageCount);
        var nextStart = 0;
        for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
        {
            var pageUri = $"{advertised.SchemaUri}/pages/{pageNumber}";
            var pageResponse = await server.RequestAsync(
                $"contract-resource-{contractIndex}-{pageNumber}",
                "resources/read",
                new JsonObject { ["uri"] = pageUri });
            var contents = Assert.IsType<JsonArray>(pageResponse.Node["result"]?["contents"]);
            var content = Assert.IsType<JsonObject>(Assert.Single(contents));
            Assert.Equal(pageUri, content["uri"]?.GetValue<string>());
            Assert.Equal("application/json", content["mimeType"]?.GetValue<string>());
            var page = Assert.IsType<JsonObject>(JsonNode.Parse(content["text"]!.GetValue<string>()));
            Assert.Equal(advertised.ToolName, page["toolName"]?.GetValue<string>());
            Assert.Equal(advertised.Sha256, page["sha256"]?.GetValue<string>());
            Assert.Equal(pageNumber, page["page"]?.GetValue<int>());
            Assert.Equal(pageCount, page["pageCount"]?.GetValue<int>());
            Assert.Equal(nextStart, page["startUtf8Byte"]?.GetValue<int>());

            var fragment = page["schemaFragment"]?.GetValue<string>()
                ?? throw new JsonException("Contract resource page omitted schemaFragment.");
            var fragmentBytes = Encoding.UTF8.GetBytes(fragment);
            Assert.Equal(fragmentBytes.Length, page["returnedUtf8Bytes"]?.GetValue<int>());
            Assert.InRange(
                fragmentBytes.Length,
                1,
                CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes);
            boundaries.Add(new ContractPageBoundary(
                pageNumber,
                nextStart,
                fragmentBytes.Length));
            assembled.Write(fragmentBytes);
            nextStart = checked(nextStart + fragmentBytes.Length);
        }

        Assert.Equal(advertised.Utf8Bytes, nextStart);
        return new ContractProjection(assembled.ToArray(), boundaries);
    }

    private static async Task<ContractProjection> ReadContractToolAsync(
        PackageServer server,
        PackagedContractAdvertisement advertised,
        int contractIndex)
    {
        using var assembled = new MemoryStream(advertised.Utf8Bytes);
        var boundaries = new List<ContractPageBoundary>();
        var pageNumber = 1;
        int? pageCount = null;
        var nextStart = 0;
        while (true)
        {
            var response = await server.RequestAsync(
                $"contract-tool-{contractIndex}-{pageNumber}",
                "tools/call",
                new JsonObject
                {
                    ["name"] = "get_tool_contract",
                    ["arguments"] = new JsonObject
                    {
                        ["toolName"] = advertised.ToolName,
                        ["page"] = pageNumber,
                    },
                });
            var result = Assert.IsType<JsonObject>(response.Node["result"]);
            Assert.False(result["isError"]?.GetValue<bool>() ?? false);
            var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
            var content = Assert.IsType<JsonArray>(result["content"]);
            var text = Assert.IsType<JsonObject>(Assert.Single(content));
            Assert.Equal("text", text["type"]?.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(
                structured,
                JsonNode.Parse(text["text"]!.GetValue<string>())));
            var data = Assert.IsType<JsonObject>(structured["data"]);

            Assert.Equal(advertised.ToolName, data["toolName"]?.GetValue<string>());
            Assert.Equal(advertised.ContractVersion, data["contractVersion"]?.GetValue<string>());
            Assert.Equal(advertised.SchemaUri, data["schemaUri"]?.GetValue<string>());
            Assert.Equal(advertised.Sha256, data["sha256"]?.GetValue<string>());
            Assert.Equal(advertised.MediaType, data["mediaType"]?.GetValue<string>());
            Assert.Equal(advertised.Utf8Bytes, data["utf8Bytes"]?.GetValue<int>());
            Assert.Equal(pageNumber, data["page"]?.GetValue<int>());
            var currentPageCount = data["pageCount"]?.GetValue<int>()
                ?? throw new JsonException("get_tool_contract omitted data.pageCount.");
            Assert.InRange(currentPageCount, 1, int.MaxValue);
            pageCount ??= currentPageCount;
            Assert.Equal(pageCount.Value, currentPageCount);
            Assert.Equal(nextStart, data["startUtf8Byte"]?.GetValue<int>());

            var fragment = data["schemaFragment"]?.GetValue<string>()
                ?? throw new JsonException("get_tool_contract omitted data.schemaFragment.");
            var fragmentBytes = Encoding.UTF8.GetBytes(fragment);
            Assert.Equal(fragmentBytes.Length, data["returnedUtf8Bytes"]?.GetValue<int>());
            Assert.InRange(
                fragmentBytes.Length,
                1,
                CapabilityDiscoveryRuntime.ToolContractPageUtf8Bytes);
            boundaries.Add(new ContractPageBoundary(
                pageNumber,
                nextStart,
                fragmentBytes.Length));
            assembled.Write(fragmentBytes);
            nextStart = checked(nextStart + fragmentBytes.Length);

            var nextPage = data["nextPage"]?.GetValue<int?>();
            Assert.Equal(pageNumber < currentPageCount ? pageNumber + 1 : null, nextPage);
            if (nextPage is null)
                break;
            pageNumber = nextPage.Value;
        }

        Assert.NotNull(pageCount);
        Assert.Equal(pageCount.Value, pageNumber);
        Assert.Equal(advertised.Utf8Bytes, nextStart);
        return new ContractProjection(assembled.ToArray(), boundaries);
    }

    private sealed record PackagedContractAdvertisement(
        string ToolName,
        string ContractVersion,
        string SchemaDialect,
        string SchemaUri,
        string Sha256,
        string MediaType,
        int Utf8Bytes);

    private sealed record ContractProjection(
        byte[] CanonicalUtf8,
        IReadOnlyList<ContractPageBoundary> Pages)
    {
        internal int PageCount => Pages.Count;
    }

    private sealed record ContractPageBoundary(
        int Page,
        int StartUtf8Byte,
        int ReturnedUtf8Bytes);

    private sealed record ReceivedFrame(JsonObject Node, int Utf8Bytes);

    private sealed class PackageServer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
        private readonly Task<string> _stderr;
        private bool _completed;

        private PackageServer(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync(_timeout.Token);
        }

        internal int MaxResponseFrameBytes { get; private set; }

        internal static ProcessStartInfo StartInfo(string serverPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                WorkingDirectory = Path.GetDirectoryName(serverPath)!,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.Environment["WPAMCP_TELEMETRY"] = "0";
            startInfo.Environment[ToolsListPaginationOptions.MaxResponseFrameBytesEnvironmentVariable] =
                ToolResponseBudgetOptions.HardMaxResponseFrameBytes.ToString();
            startInfo.Environment.Remove(RuntimeCompatibilityPolicy.ContractModeEnvironmentVariable);
            startInfo.Environment.Remove(TraceRuntimeOptions.AccessModeEnvironmentVariable);
            return startInfo;
        }

        internal static Task<PackageServer> StartAsync(string serverPath)
        {
            var process = new Process { StartInfo = StartInfo(serverPath) };
            Assert.True(process.Start());
            return Task.FromResult(new PackageServer(process));
        }

        internal async Task<ReceivedFrame> RequestAsync(string id, string method, JsonObject @params)
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = @params,
            });
            var line = await _process.StandardOutput.ReadLineAsync(_timeout.Token);
            if (line is null)
                throw new InvalidOperationException("Published server closed stdout. Stderr: " + await _stderr);
            var bytes = Encoding.UTF8.GetByteCount(line) + 1;
            MaxResponseFrameBytes = Math.Max(MaxResponseFrameBytes, bytes);
            Assert.InRange(bytes, 1, ToolResponseBudgetOptions.HardMaxResponseFrameBytes);
            var node = Assert.IsType<JsonObject>(JsonNode.Parse(line));
            Assert.Equal(JsonValue.Create(id)?.ToJsonString(), node["id"]?.ToJsonString());
            Assert.Null(node["error"]);
            return new ReceivedFrame(node, bytes);
        }

        internal Task NotifyAsync(string method, JsonObject @params) => WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params,
        });

        private async Task WriteAsync(JsonObject frame)
        {
            await _process.StandardInput.WriteLineAsync(frame.ToJsonString().AsMemory(), _timeout.Token);
            await _process.StandardInput.FlushAsync(_timeout.Token);
        }

        internal async Task<int> CompleteAsync()
        {
            if (!_completed)
            {
                _completed = true;
                _process.StandardInput.Close();
                await _process.WaitForExitAsync(_timeout.Token);
            }
            var stderr = await _stderr;
            Assert.DoesNotContain("Unhandled exception", stderr, StringComparison.OrdinalIgnoreCase);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_completed && !_process.HasExited)
                    _process.Kill(entireProcessTree: true);
                if (!_process.HasExited)
                    await _process.WaitForExitAsync(CancellationToken.None);
            }
            finally
            {
                _timeout.Dispose();
                _process.Dispose();
            }
        }
    }
}
