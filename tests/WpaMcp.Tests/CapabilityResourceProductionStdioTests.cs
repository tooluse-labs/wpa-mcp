using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class CapabilityResourceProductionStdioTests
{
    private const string ProtocolVersion = "2025-06-18";

    [Fact]
    public async Task FourKiBServer_FailsClosedBeforeAdvertisingUnreadableResources()
    {
        await using var client = await StdioClient.StartAsync(
            FindRepositoryRoot(),
            ToolResponseBudgetOptions.MinimumResponseFrameBytes);
        var failure = await client.WaitForStartupFailureAsync();

        Assert.NotEqual(0, failure.ExitCode);
        Assert.Empty(failure.Stdout);
        Assert.Contains("tools/list startup preflight failed", failure.Stderr, StringComparison.Ordinal);
        Assert.Contains("response cap 4096 bytes", failure.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionResourcesRead_FramesAreCompleteUtf8AndExactUnion()
    {
        var root = FindRepositoryRoot();
        await using var client = await StdioClient.StartAsync(
            root,
            ToolResponseBudgetOptions.HardMaxResponseFrameBytes);
        await client.InitializeAsync();

        var listed = await client.SendRequestAsync(
            JsonValue.Create("resources")!,
            "resources/list",
            new JsonObject());
        Assert.True(
            listed.Utf8FrameBytes <= ToolResponseBudgetOptions.HardMaxResponseFrameBytes,
            $"resources/list emitted {listed.Utf8FrameBytes} bytes.");
        Assert.Null(listed.Message["error"]);

        var expectedCatalog = WpaMcp.Core.Catalog.ActiveToolCatalog.LoadAndValidate();
        var capabilityIds = new List<string>();
        var toolNames = new List<string>();
        var workflowIds = new List<string>();
        var sectionLinks = new Dictionary<string, string>(StringComparer.Ordinal);
        var sectionPointers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var useUnicodeId = false;

        async Task<JsonObject> ReadJson(string uri)
        {
            useUnicodeId = !useUnicodeId;
            var id = useUnicodeId
                ? JsonValue.Create(new string('界', 21))!
                : JsonValue.Create(new string('r', 126))!;
            Assert.Equal(128, Encoding.UTF8.GetByteCount(id.ToJsonString()));
            var response = await client.SendRequestAsync(
                id,
                "resources/read",
                new JsonObject { ["uri"] = uri });
            Assert.True(
                response.Utf8FrameBytes <= ToolResponseBudgetOptions.HardMaxResponseFrameBytes,
                $"{uri} emitted {response.Utf8FrameBytes} bytes.");
            Assert.Null(response.Message["error"]);
            var text = response.Message["result"]!["contents"]![0]!["text"]!.GetValue<string>();
            return JsonNode.Parse(text)!.AsObject();
        }

        var capabilityIndex = await ReadJson("wpa://capabilities/server");
        foreach (var shard in capabilityIndex["shards"]!.AsArray())
        {
            var domain = shard!["key"]!.GetValue<string>();
            var pageIndex = await ReadJson(shard["uri"]!.GetValue<string>());
            foreach (var page in pageIndex["pages"]!.AsArray())
            {
                var pageData = await ReadJson(page!["uri"]!.GetValue<string>());
                capabilityIds.AddRange(pageData["capabilities"]!.AsArray()
                    .Select(row => row!["capabilityId"]!.GetValue<string>()));
                Assert.Equal(
                    pageData["returnedCapabilities"]!.GetValue<int>(),
                    pageData["capabilities"]!.AsArray().Count);
                Assert.Equal(domain, pageData["domain"]!.GetValue<string>());
            }
        }

        var toolIndex = await ReadJson("wpa://tools/server");
        foreach (var shard in toolIndex["shards"]!.AsArray())
        {
            var domain = shard!["key"]!.GetValue<string>();
            var pageIndex = await ReadJson(shard["uri"]!.GetValue<string>());
            foreach (var page in pageIndex["pages"]!.AsArray())
            {
                var pageData = await ReadJson(page!["uri"]!.GetValue<string>());
                foreach (var row in pageData["tools"]!.AsArray())
                {
                    var toolName = row!["toolName"]!.GetValue<string>();
                    toolNames.Add(toolName);
                    sectionLinks.Add(
                        toolName,
                        row["sectionContractsResourceUri"]!.GetValue<string>());
                    Assert.Equal(
                        "complete_in_linked_resource",
                        row["sectionContractCompleteness"]!.GetValue<string>());
                }
                Assert.Equal(
                    pageData["returnedTools"]!.GetValue<int>(),
                    pageData["tools"]!.AsArray().Count);
                Assert.Equal(domain, pageData["domain"]!.GetValue<string>());
            }
        }

        foreach (var tool in expectedCatalog.Tools)
        {
            var index = await ReadJson(sectionLinks[tool.ToolName]);
            Assert.Equal("tool_section_contracts", index["resourceKind"]!.GetValue<string>());
            Assert.Equal(tool.ToolName, index["key"]!.GetValue<string>());
            var pointers = new List<string>();
            foreach (var page in index["pages"]!.AsArray())
            {
                var pageData = await ReadJson(page!["uri"]!.GetValue<string>());
                Assert.Equal(tool.ToolName, pageData["toolName"]!.GetValue<string>());
                pointers.AddRange(pageData["sectionContracts"]!.AsArray()
                    .Select(section => section!["sectionPointer"]!.GetValue<string>()));
            }
            sectionPointers.Add(tool.ToolName, pointers);
        }

        var workflowIndex = await ReadJson("wpa://workflows/server");
        foreach (var shard in workflowIndex["shards"]!.AsArray())
        {
            var workflow = await ReadJson(shard!["uri"]!.GetValue<string>());
            workflowIds.Add(workflow["workflowId"]!.GetValue<string>());
        }

        Assert.Equal(
            expectedCatalog.Capabilities.OrderBy(capability => capability.Domain, StringComparer.Ordinal)
                .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
                .Select(capability => capability.CapabilityId),
            capabilityIds);
        Assert.Equal(expectedCatalog.Tools.Select(tool => tool.ToolName), toolNames);
        Assert.Equal(
            expectedCatalog.Workflows.OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .Select(workflow => workflow.WorkflowId),
            workflowIds);
        Assert.Equal(capabilityIds.Count, capabilityIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(toolNames.Count, toolNames.Distinct(StringComparer.Ordinal).Count());
        Assert.All(expectedCatalog.Tools, tool => Assert.Equal(
            tool.PageableSections.Order(StringComparer.Ordinal),
            sectionPointers[tool.ToolName]));
        Assert.Equal(0, await client.CompleteAsync());
    }

    private static string FindRepositoryRoot()
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
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromMinutes(3));
        private readonly Task<string> _stderr;
        private bool _completed;

        private StdioClient(Process process)
        {
            _process = process;
            _stderr = process.StandardError.ReadToEndAsync(_timeout.Token);
        }

        internal static Task<StdioClient> StartAsync(string root, int maxResponseBytes)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine build configuration.");
            var assembly = Path.Combine(
                root, "src", "WpaMcp", "bin", configuration, "net10.0", "WpaMcp.dll");
            Assert.True(File.Exists(assembly), $"Production assembly is missing: {assembly}");
            var start = new ProcessStartInfo
            {
                FileName = LocateDotNetHost(),
                WorkingDirectory = root,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(assembly);
            start.Environment["WPAMCP_TELEMETRY"] = "0";
            start.Environment[ToolsListPaginationOptions.MaxResponseFrameBytesEnvironmentVariable] =
                maxResponseBytes.ToString(CultureInfo.InvariantCulture);
            var process = new Process { StartInfo = start };
            Assert.True(process.Start());
            return Task.FromResult(new StdioClient(process));
        }

        internal async Task InitializeAsync()
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
                        ["name"] = "capability-resource-production-test",
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

        internal async Task<ResponseFrame> SendRequestAsync(
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
                if (!string.Equals(message["id"]?.ToJsonString(), id.ToJsonString(), StringComparison.Ordinal))
                    continue;
                return new ResponseFrame(message, Encoding.UTF8.GetByteCount(line) + 1);
            }
        }

        internal async Task<int> CompleteAsync()
        {
            _completed = true;
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(_timeout.Token);
            _ = await _stderr;
            return _process.ExitCode;
        }

        internal async Task<StartupFailure> WaitForStartupFailureAsync()
        {
            _completed = true;
            var stdout = _process.StandardOutput.ReadToEndAsync(_timeout.Token);
            await _process.WaitForExitAsync(_timeout.Token);
            return new StartupFailure(_process.ExitCode, await stdout, await _stderr);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_completed && !_process.HasExited)
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
            var root = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
            var candidate = root is null
                ? null
                : Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            return candidate is not null && File.Exists(candidate)
                ? candidate
                : throw new FileNotFoundException("Could not locate the current dotnet host.");
        }
    }

    private sealed record ResponseFrame(JsonNode Message, int Utf8FrameBytes);
    private sealed record StartupFailure(int ExitCode, string Stdout, string Stderr);
}
