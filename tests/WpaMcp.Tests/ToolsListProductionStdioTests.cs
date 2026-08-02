using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using Xunit.Abstractions;

namespace WpaMcp.Tests;

public sealed class ToolsListProductionStdioTests
{
    private const string ProtocolVersion = "2025-11-25";
    private readonly ITestOutputHelper _output;

    public ToolsListProductionStdioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task ProductionProgram_PagesCompleteCatalogWithinUnifiedDiscoveryMinimum()
    {
        var repoRoot = LocateRepoRoot();
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var serverTools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        var tools = serverTools.Select(tool => tool.ProtocolTool).ToArray();
        var preflight = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        var contractPreflight = ToolContractDiscoveryPreflight.Measure(catalog, serverTools);
        var minimum = Math.Max(
            preflight.MinimumViableFrameBytes,
            contractPreflight.MinimumViableFrameBytes);
        Assert.Equal(62, tools.Length);
        foreach (var tool in tools)
        {
            Assert.Null(tool.OutputSchema);
            AssertExactContractMetadata(
                catalog,
                tool.Name,
                tool.Meta?[ToolOutputContract.MetadataKey]);
        }

        await using var firstServer = await StdioClient.StartAsync(repoRoot, minimum);
        await firstServer.InitializeAsync();

        var observedNames = new List<string>();
        var pageBytes = new List<int>();
        var pageToolNodes = new List<JsonArray>();
        string? cursor = null;
        string? firstCursor = null;
        var pageIndex = 0;
        do
        {
            JsonNode id = pageIndex % 2 == 0
                ? JsonValue.Create(100 + pageIndex)!
                : JsonValue.Create($"目录页-{pageIndex}")!;
            var frame = await firstServer.SendRequestAsync(
                id,
                RequestMethods.ToolsList,
                cursor is null
                    ? new JsonObject()
                    : new JsonObject { ["cursor"] = cursor });
            Assert.Null(frame.Message["error"]);
            var result = Assert.IsType<JsonObject>(frame.Message["result"]);
            var pageTools = Assert.IsType<JsonArray>(result["tools"]);
            Assert.NotEmpty(pageTools);
            Assert.True(
                frame.Utf8FrameBytes <= minimum,
                $"Page {pageIndex} was {frame.Utf8FrameBytes} bytes with cap {minimum}.");
            foreach (var pageTool in pageTools)
            {
                var toolObject = Assert.IsType<JsonObject>(pageTool);
                var toolName = toolObject["name"]!.GetValue<string>();
                Assert.IsType<JsonObject>(toolObject["inputSchema"]);
                Assert.Null(toolObject["outputSchema"]);
                AssertExactContractMetadata(
                    catalog,
                    toolName,
                    toolObject["_meta"]?[ToolOutputContract.MetadataKey]);
            }
            pageBytes.Add(frame.Utf8FrameBytes);
            pageToolNodes.Add((JsonArray)pageTools.DeepClone());
            observedNames.AddRange(pageTools.Select(tool => tool!["name"]!.GetValue<string>()));
            cursor = result["nextCursor"]?.GetValue<string>();
            if (cursor is not null)
            {
                Assert.Matches("^tlc_[0-9a-f]{32}$", cursor);
                firstCursor ??= cursor;
            }

            pageIndex++;
            Assert.InRange(pageIndex, 1, tools.Length);
        }
        while (cursor is not null);

        Assert.True(pageIndex >= 3);
        Assert.Equal(tools.Select(tool => tool.Name), observedNames);
        Assert.Equal(62, observedNames.Count);
        Assert.Equal(tools.Length, observedNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("prepare_symbols", observedNames);
        Assert.DoesNotContain("set_symbol_path", observedNames);
        Assert.DoesNotContain("add_symbol_server", observedNames);
        Assert.DoesNotContain("diagnose_symbols", observedNames);
        Assert.Equal(
            SymbolToolSchemaOverlay.ExpectedToolCount,
            pageToolNodes
                .SelectMany(page => page)
                .Count(tool => tool?["inputSchema"]?["properties"]?["symbolContextId"] is not null));
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(tools[0], McpJsonUtilities.DefaultOptions),
            pageToolNodes[0][0]));
        Assert.True(JsonNode.DeepEquals(
            JsonSerializer.SerializeToNode(tools[^1], McpJsonUtilities.DefaultOptions),
            pageToolNodes[^1][^1]));

        var aggregateResult = new JsonObject
        {
            ["tools"] = new JsonArray(
                pageToolNodes.SelectMany(page => page)
                    .Select(tool => tool!.DeepClone())
                    .ToArray()),
        };
        var aggregateBytes = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult { Tools = tools.ToArray() },
            McpJsonUtilities.DefaultOptions);
        Assert.Equal(
            aggregateBytes.Length,
            Encoding.UTF8.GetByteCount(
                aggregateResult.ToJsonString(McpJsonUtilities.DefaultOptions)));
        Assert.InRange(
            aggregateBytes.Length,
            1,
            ToolListPayload.DefaultMaxPayloadBytes);
        Assert.True(pageBytes.Sum(value => (long)value) > 0);
        _output.WriteLine(
            "catalogVersion={0}; unifiedMinimum={1}; toolsListMinimum={2}; minimumSuccess={3}; " +
            "largestSingleTool={4}; largestSingleToolFrameBytes={5}; pageCount={6}; " +
            "maxPageBytes={7}; pageBytes=[{8}]; aggregateResultBytes={9}; " +
            "aggregateResultSha256={10}",
            catalog.CatalogVersion,
            minimum,
            preflight.MinimumViableFrameBytes,
            preflight.MinimumSuccessFrameBytes,
            preflight.LargestSingleToolName,
            preflight.LargestSingleToolFrameBytes,
            pageIndex,
            pageBytes.Max(),
            string.Join(',', pageBytes),
            aggregateBytes.Length,
            Convert.ToHexString(SHA256.HashData(aggregateBytes)).ToLowerInvariant());

        Assert.NotNull(firstCursor);
        var retry = await firstServer.SendRequestAsync(
            JsonValue.Create("retry-after-response-loss")!,
            RequestMethods.ToolsList,
            new JsonObject { ["cursor"] = firstCursor });
        Assert.True(JsonNode.DeepEquals(
            pageToolNodes[1],
            retry.Message["result"]!["tools"]));

        await AssertInvalidCursorAsync(firstServer, "", "empty");
        await AssertInvalidCursorAsync(
            firstServer,
            firstCursor[..^1] + (firstCursor[^1] == '0' ? '1' : '0'),
            "tampered");
        await AssertInvalidCursorAsync(firstServer, firstCursor.ToUpperInvariant(), "uppercase");
        await AssertInvalidCursorAsync(
            firstServer,
            "tlc_00000000000000000000000000000000",
            "unknown");

        await using var secondServer = await StdioClient.StartAsync(repoRoot, minimum);
        await secondServer.InitializeAsync();
        await AssertInvalidCursorAsync(secondServer, firstCursor, "cross-instance");

        // A cross-instance probe must not revoke the cursor in its owning server.
        var ownerRetry = await firstServer.SendRequestAsync(
            JsonValue.Create("owner-still-valid")!,
            RequestMethods.ToolsList,
            new JsonObject { ["cursor"] = firstCursor });
        Assert.True(JsonNode.DeepEquals(
            pageToolNodes[1],
            ownerRetry.Message["result"]!["tools"]));

        Assert.Equal(0, await secondServer.CompleteAsync());
        Assert.Equal(0, await firstServer.CompleteAsync());
    }

    [Fact]
    public async Task ProductionProgram_RejectsCapBelowContractDiscoveryMinimumBeforeReadingStdin()
    {
        var repoRoot = LocateRepoRoot();
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var serverTools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        var minimum = ToolContractDiscoveryPreflight.Measure(catalog, serverTools)
            .MinimumViableFrameBytes;

        await using var server = await StdioClient.StartAsync(repoRoot, minimum - 1);
        var exit = await server.WaitForStartupFailureAsync();

        Assert.Equal(Program.StartupConfigurationErrorExitCode, exit.ExitCode);
        Assert.Equal(string.Empty, exit.Stdout);
        Assert.Contains("output-contract discovery preflight", exit.Stderr, StringComparison.Ordinal);
        Assert.Contains(minimum.ToString(CultureInfo.InvariantCulture), exit.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionProgram_ExactContractDiscoveryMinimumReassemblesWorstContract()
    {
        var repoRoot = LocateRepoRoot();
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var serverTools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        var preflight = ToolContractDiscoveryPreflight.Measure(catalog, serverTools);
        var contract = catalog.OutputContracts[preflight.MaximumToolFrameToolName];
        await using var server = await StdioClient.StartAsync(
            repoRoot,
            preflight.MinimumViableFrameBytes);
        await server.InitializeAsync();

        var assembled = new StringBuilder(contract.CanonicalJson.Length);
        var page = 1;
        var nextStart = 0;
        var observedFrames = new List<int>();
        while (true)
        {
            var requestId = $"contract-page-{page}".PadRight(126, 'r');
            Assert.Equal(
                ToolRequestIdPolicy.MaxSerializedBytes,
                Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(requestId)));
            var response = await server.SendRequestAsync(
                JsonValue.Create(requestId)!,
                RequestMethods.ToolsCall,
                new JsonObject
                {
                    ["name"] = "get_tool_contract",
                    ["arguments"] = new JsonObject
                    {
                        ["toolName"] = contract.ToolName,
                        ["page"] = page,
                    },
                });
            observedFrames.Add(response.Utf8FrameBytes);
            Assert.InRange(response.Utf8FrameBytes, 1, preflight.MinimumViableFrameBytes);
            Assert.Null(response.Message["error"]);
            var result = Assert.IsType<JsonObject>(response.Message["result"]);
            Assert.NotEqual(true, result["isError"]?.GetValue<bool>());
            var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
            var text = JsonNode.Parse(result["content"]![0]!["text"]!.GetValue<string>());
            Assert.True(JsonNode.DeepEquals(structured, text));
            var data = Assert.IsType<JsonObject>(structured["data"]);
            Assert.Equal(contract.ToolName, data["toolName"]?.GetValue<string>());
            Assert.Equal(page, data["page"]?.GetValue<int>());
            Assert.Equal(nextStart, data["startUtf8Byte"]?.GetValue<int>());
            var fragment = data["schemaFragment"]?.GetValue<string>()
                ?? throw new JsonException("get_tool_contract omitted schemaFragment.");
            var returned = data["returnedUtf8Bytes"]?.GetValue<int>()
                ?? throw new JsonException("get_tool_contract omitted returnedUtf8Bytes.");
            Assert.Equal(returned, Encoding.UTF8.GetByteCount(fragment));
            assembled.Append(fragment);
            nextStart = checked(nextStart + returned);

            var nextPage = data["nextPage"]?.GetValue<int?>();
            if (nextPage is null)
                break;
            Assert.Equal(page + 1, nextPage.Value);
            page = nextPage.Value;
        }

        Assert.Equal(preflight.MaximumToolFrameBytes, observedFrames.Max());
        Assert.Equal(contract.Utf8Bytes, nextStart);
        Assert.Equal(contract.CanonicalJson, assembled.ToString());
        Assert.Equal(contract.Sha256, Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(assembled.ToString()))).ToLowerInvariant());
        Assert.Equal(0, await server.CompleteAsync());
    }

    [Fact]
    public async Task ProductionProgram_RejectsCapBelowMeasuredMinimumBeforeReadingStdin()
    {
        var repoRoot = LocateRepoRoot();
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tools = catalog.CreateProtocolTools(new DeferredCatalogServiceProvider());
        var minimum = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;

        await using var server = await StdioClient.StartAsync(repoRoot, minimum - 1);
        var exit = await server.WaitForStartupFailureAsync();

        Assert.Equal(Program.StartupConfigurationErrorExitCode, exit.ExitCode);
        Assert.Equal(string.Empty, exit.Stdout);
        Assert.Contains("startup validation failed", exit.Stderr, StringComparison.Ordinal);
        Assert.Contains(minimum.ToString(CultureInfo.InvariantCulture), exit.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionProgram_MalformedTracePathReturnsStableInvalidArgument()
    {
        var repoRoot = LocateRepoRoot();
        await using var server = await StdioClient.StartAsync(
            repoRoot,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        await server.InitializeAsync();
        JsonNode?[] malformed =
        [
            JsonValue.Create(123),
            new JsonObject { ["nested"] = true },
            null,
        ];

        for (var index = 0; index < malformed.Length; index++)
        {
            var response = await server.SendRequestAsync(
                JsonValue.Create($"malformed-path-{index}")!,
                RequestMethods.ToolsCall,
                new JsonObject
                {
                    ["name"] = "inspect_trace",
                    ["arguments"] = new JsonObject
                    {
                        ["path"] = malformed[index]?.DeepClone(),
                    },
                });
            Assert.Null(response.Message["error"]);
            Assert.True(response.Message["result"]?["isError"]?.GetValue<bool>());
            var code = response.Message["result"]?["structuredContent"]?["error"]?["code"]?.GetValue<string>();
            Assert.True(
                string.Equals(code, "invalid_argument", StringComparison.Ordinal),
                response.Message.ToJsonString());
        }

        Assert.Equal(0, await server.CompleteAsync());
    }

    [Fact]
    public async Task PolicyDisabledProfile_FiltersToolsAndRetainsCapabilityEvidence()
    {
        var repoRoot = LocateRepoRoot();
        var fullCatalog = ActiveToolCatalog.LoadAndValidate();
        var protocolTools = fullCatalog.CreateProtocolTools(
            new DeferredCatalogServiceProvider());
        var disabledCapabilityIds = new HashSet<string>(StringComparer.Ordinal)
        {
            "cpu.sampled.stacks",
        };
        foreach (var protocolTool in protocolTools)
        {
            try
            {
                _ = ToolsListPageFitter.Preflight(
                    [protocolTool],
                    ToolsListPaginationOptions.HardMaxResponseFrameBytes);
            }
            catch (ToolsListStartupValidationException)
            {
                var definition = Assert.Single(fullCatalog.Tools, tool =>
                    tool.ToolName == protocolTool.Name);
                disabledCapabilityIds.Add(
                    Assert.Single(definition.Capabilities).CapabilityId);
            }
        }
        Assert.DoesNotContain("catalog.capability.list", disabledCapabilityIds);
        Assert.DoesNotContain("trace.capability.inspect", disabledCapabilityIds);
        var expectedNames = fullCatalog.Tools.Where(tool =>
                !tool.Capabilities.Any(capability =>
                    disabledCapabilityIds.Contains(capability.CapabilityId)))
            .Select(tool => tool.ToolName)
            .ToArray();
        await using var server = await StdioClient.StartAsync(
            repoRoot,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes,
            disabledCapabilities: string.Join(",", disabledCapabilityIds.Order(
                StringComparer.Ordinal)));
        await server.InitializeAsync();

        var names = new List<string>();
        string? cursor = null;
        do
        {
            var response = await server.SendRequestAsync(
                JsonValue.Create($"policy-tools-{names.Count}")!,
                RequestMethods.ToolsList,
                cursor is null
                    ? new JsonObject()
                    : new JsonObject { ["cursor"] = cursor });
            Assert.Null(response.Message["error"]);
            var result = response.Message["result"]!.AsObject();
            foreach (var toolNode in result["tools"]!.AsArray())
            {
                var toolObject = Assert.IsType<JsonObject>(toolNode);
                var toolName = toolObject["name"]!.GetValue<string>();
                Assert.Null(toolObject["outputSchema"]);
                AssertExactContractMetadata(
                    fullCatalog,
                    toolName,
                    toolObject["_meta"]?[ToolOutputContract.MetadataKey]);
                names.Add(toolName);
            }
            cursor = result["nextCursor"]?.GetValue<string>();
        }
        while (cursor is not null);

        Assert.Equal(expectedNames, names);
        Assert.Contains("list_capabilities", names);
        Assert.Contains("inspect_trace", names);
        Assert.DoesNotContain("cpu_caller_callee", names);
        Assert.DoesNotContain("cpu_top_functions", names);
        Assert.DoesNotContain("cpu_top_functions_batch", names);

        var capabilityResponse = await server.SendRequestAsync(
            JsonValue.Create("policy-capabilities")!,
            RequestMethods.ToolsCall,
            new JsonObject
            {
                ["name"] = "list_capabilities",
                ["arguments"] = new JsonObject { ["domain"] = "cpu" },
            });
        Assert.Null(capabilityResponse.Message["error"]);
        var data = capabilityResponse.Message["result"]!["structuredContent"]!["data"]!;
        Assert.Equal(
            "restricted",
            data["capabilityPolicy"]!["profileName"]!.GetValue<string>());
        var disabled = Assert.Single(data["capabilities"]!.AsArray(), capability =>
            capability!["capabilityId"]!.GetValue<string>() == "cpu.sampled.stacks");
        Assert.Equal(
            "disabled_by_policy",
            disabled!["availabilityState"]!.GetValue<string>());
        Assert.Empty(disabled["callableToolNames"]!.AsArray());
        Assert.Equal(3, disabled["disabledByPolicyToolNames"]!.AsArray().Count);

        Assert.Equal(0, await server.CompleteAsync());
    }

    private static async Task AssertInvalidCursorAsync(
        StdioClient client,
        string cursor,
        string caseName)
    {
        var response = await client.SendRequestAsync(
            JsonValue.Create($"invalid-{caseName}")!,
            RequestMethods.ToolsList,
            new JsonObject { ["cursor"] = cursor });
        Assert.Null(response.Message["result"]);
        Assert.Equal((int)McpErrorCode.InvalidParams, response.Message["error"]!["code"]!.GetValue<int>());
        Assert.Equal("Invalid tools/list cursor.", response.Message["error"]!["message"]!.GetValue<string>());
    }

    private static void AssertExactContractMetadata(
        ActiveToolCatalog catalog,
        string toolName,
        JsonNode? metadataNode)
    {
        var contract = catalog.OutputContracts[toolName];
        var metadata = Assert.IsType<JsonObject>(metadataNode);
        Assert.Equal(contract.SchemaUri, metadata["uri"]!.GetValue<string>());
        Assert.Equal(contract.Sha256, metadata["sha256"]!.GetValue<string>());
        Assert.Equal(contract.Utf8Bytes, metadata["utf8Bytes"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(contract.ToDiscoveryMetadata(), metadata));
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

    private sealed class StdioClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(2));
        private readonly Task<string> _stderr;
        private bool _completed;

        private StdioClient(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync(_timeout.Token);
        }

        public static Task<StdioClient> StartAsync(
            string repoRoot,
            int maxResponseFrameBytes,
            string? disabledCapabilities = null)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine build configuration.");
            var assembly = Path.Combine(
                repoRoot,
                "src",
                "WpaMcp",
                "bin",
                configuration,
                "net10.0",
                "WpaMcp.dll");
            Assert.True(File.Exists(assembly), $"Production assembly is missing: {assembly}");

            var startInfo = new ProcessStartInfo
            {
                FileName = LocateDotNetHost(),
                WorkingDirectory = repoRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(assembly);
            startInfo.Environment["WPAMCP_TELEMETRY"] = "0";
            startInfo.Environment[
                ToolsListPaginationOptions.MaxResponseFrameBytesEnvironmentVariable] =
                maxResponseFrameBytes.ToString(CultureInfo.InvariantCulture);
            startInfo.Environment.Remove(
                CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable);
            if (disabledCapabilities is not null)
            {
                startInfo.Environment[
                    CapabilityPolicyProfile.DisabledCapabilitiesEnvironmentVariable] =
                    disabledCapabilities;
            }

            var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start());
            return Task.FromResult(new StdioClient(process));
        }

        public async Task InitializeAsync()
        {
            var response = await SendRequestAsync(
                JsonValue.Create("initialize")!,
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "tools-list-production-test",
                        ["version"] = "1.0",
                    },
                });
            Assert.Equal(
                ProtocolVersion,
                response.Message["result"]!["protocolVersion"]!.GetValue<string>());
            await SendMessageAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
                ["params"] = new JsonObject(),
            });
        }

        public async Task<ResponseFrame> SendRequestAsync(
            JsonNode id,
            string method,
            JsonObject parameters)
        {
            await SendMessageAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id.DeepClone(),
                ["method"] = method,
                ["params"] = parameters.DeepClone(),
            });

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_timeout.Token)
                    ?? throw new EndOfStreamException(
                        $"Server closed stdout before response {id}. Stderr: {await _stderr}");
                var message = JsonNode.Parse(line)
                    ?? throw new JsonException("Server emitted an empty JSON line.");
                if (!string.Equals(
                        message["id"]?.ToJsonString(),
                        id.ToJsonString(),
                        StringComparison.Ordinal))
                    continue;
                return new ResponseFrame(
                    message,
                    Encoding.UTF8.GetByteCount(line) + 1);
            }
        }

        public async Task<int> CompleteAsync()
        {
            if (_completed)
                throw new InvalidOperationException("stdio client is already complete.");
            _completed = true;
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(_timeout.Token);
            _ = await _stderr;
            return _process.ExitCode;
        }

        public async Task<StartupFailure> WaitForStartupFailureAsync()
        {
            if (_completed)
                throw new InvalidOperationException("stdio client is already complete.");
            _completed = true;
            // Keep stdin open: a valid startup preflight failure must exit before transport
            // construction rather than waiting for input or relying on EOF.
            var stdoutTask = _process.StandardOutput.ReadToEndAsync(_timeout.Token);
            await _process.WaitForExitAsync(_timeout.Token);
            return new StartupFailure(
                _process.ExitCode,
                await stdoutTask,
                await _stderr);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
            _process.Dispose();
            _timeout.Dispose();
        }

        private async Task SendMessageAsync(JsonObject message)
        {
            var payload = Encoding.UTF8.GetBytes(
                message.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)) + "\n");
            await _process.StandardInput.BaseStream.WriteAsync(payload, _timeout.Token);
            await _process.StandardInput.BaseStream.FlushAsync(_timeout.Token);
        }

        private static string LocateDotNetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
            var candidate = dotnetRoot is null
                ? null
                : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            return candidate is not null && File.Exists(candidate)
                ? candidate
                : throw new FileNotFoundException("Could not locate the current dotnet host.");
        }
    }

    private sealed record ResponseFrame(JsonNode Message, int Utf8FrameBytes);
    private sealed record StartupFailure(int ExitCode, string Stdout, string Stderr);
}
