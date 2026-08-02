using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

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
                toolNames.Add(tool["name"]!.GetValue<string>());
                Assert.IsType<JsonObject>(tool["inputSchema"]);
                Assert.IsType<JsonObject>(tool["outputSchema"]);
            }
            toolsCursor = result["nextCursor"]?.GetValue<string>();
            toolPage++;
            Assert.InRange(toolPage, 1, expected.Tools.Count);
        }
        while (toolsCursor is not null);

        Assert.Equal(expected.Tools.Select(tool => tool.ToolName), toolNames);
        Assert.Equal(toolNames.Count, toolNames.Distinct(StringComparer.Ordinal).Count());

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
