using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core;

namespace WpaMcp.Tests;

public sealed class JsonRpcIngressTests
{
    [Theory]
    [InlineData(99_999, true)]
    [InlineData(100_000, true)]
    [InlineData(100_001, false)]
    public async Task FrameLimiter_UsesCompleteLfTerminatedFrameBytes(
        int frameBytes,
        bool accepted)
    {
        var frame = BuildExactFrame(frameBytes, "\n");

        var result = await ReadGuardedAsync(frame);

        Assert.Equal(accepted ? frame : [], result.Bytes);
        Assert.Equal(
            accepted ? JsonRpcIngressRejection.None : JsonRpcIngressRejection.FrameLimit,
            result.Rejection);
    }

    [Theory]
    [InlineData(125, 127, true)]
    [InlineData(126, 128, true)]
    [InlineData(127, 129, false)]
    public async Task FrameLimiter_UsesCanonicalSerializedRequestIdBytes(
        int valueChars,
        int serializedBytes,
        bool accepted)
    {
        var id = new string('r', valueChars);
        Assert.Equal(serializedBytes, ToolRequestIdPolicy.SerializedBytes(new RequestId(id)));
        var frame = Encoding.UTF8.GetBytes(
            new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "ping",
            }.ToJsonString() + "\n");

        var result = await ReadGuardedAsync(frame);

        Assert.Equal(accepted ? frame : [], result.Bytes);
        Assert.Equal(
            accepted ? JsonRpcIngressRejection.None : JsonRpcIngressRejection.RequestIdLimit,
            result.Rejection);
    }

    [Fact]
    public async Task FrameLimiter_DecodesEscapedRequestIdBeforeApplyingLimit()
    {
        var decoded = new string('a', 126);
        Assert.Equal(128, ToolRequestIdPolicy.SerializedBytes(new RequestId(decoded)));
        var escaped = string.Concat(Enumerable.Repeat("\\u0061", decoded.Length));
        var frame = Encoding.UTF8.GetBytes(
            $"{{\"jsonrpc\":\"2.0\",\"id\":\"{escaped}\",\"method\":\"ping\"}}\n");

        var result = await ReadGuardedAsync(frame);

        Assert.Equal(frame, result.Bytes);
        Assert.Equal(JsonRpcIngressRejection.None, result.Rejection);
    }

    [Fact]
    public async Task FrameLimiter_AppliesLimitToMultibyteRequestIdCanonicalForm()
    {
        var acceptedId = LongestPrefixWithinRequestIdLimit("界");
        var rejectedId = acceptedId + "界";
        Assert.True(ToolRequestIdPolicy.SerializedBytes(new RequestId(acceptedId)) <= 128);
        Assert.True(ToolRequestIdPolicy.SerializedBytes(new RequestId(rejectedId)) > 128);

        var acceptedFrame = RequestFrame(acceptedId);
        var rejectedFrame = RequestFrame(rejectedId);

        Assert.Equal(JsonRpcIngressRejection.None, (await ReadGuardedAsync(acceptedFrame)).Rejection);
        Assert.Equal(JsonRpcIngressRejection.RequestIdLimit, (await ReadGuardedAsync(rejectedFrame)).Rejection);
    }

    [Fact]
    public async Task ProductionProgram_FirstOversizedRequestIdHasStableTransportFailure()
    {
        var id = new string('r', 127);
        Assert.Equal(129, ToolRequestIdPolicy.SerializedBytes(new RequestId(id)));

        var result = await RunProductionAsync(InitializeFrame(id));

        Assert.Equal(Program.RequestIdLimitExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(
            JsonRpcFrameLimitingStream.RequestIdRejectionMessage + Environment.NewLine,
            result.Stderr);
    }

    [Fact]
    public async Task ProductionProgram_RejectedFirstFrameCreatesNoConfiguredRuntimeArtifacts()
    {
        var sandbox = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-rejected-ingress-" + Guid.NewGuid().ToString("N"));
        var telemetryFile = Path.Combine(sandbox, "telemetry", "events.jsonl");
        var traceRoot = Path.Combine(sandbox, "trace-source");
        var traceArtifacts = Path.Combine(sandbox, "trace-artifacts");
        var symbolRoot = Path.Combine(sandbox, "symbol-source");
        var symbolStore = Path.Combine(sandbox, "symbol-store");
        var id = new string('r', 127);

        var result = await RunProductionAsync(
            InitializeFrame(id),
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["WPAMCP_TELEMETRY"] = "1",
                ["WPAMCP_TELEMETRY_DEST"] = "file",
                ["WPAMCP_TELEMETRY_FILE"] = telemetryFile,
                [TraceRuntimeOptions.AllowedRootsEnvironmentVariable] = traceRoot,
                [TraceRuntimeOptions.ArtifactRootEnvironmentVariable] = traceArtifacts,
                [SymbolRuntimeOptions.LocalRootsEnvironmentVariable] = symbolRoot,
                [SymbolRuntimeOptions.StoreRootEnvironmentVariable] = symbolStore,
            });

        Assert.Equal(Program.RequestIdLimitExitCode, result.ExitCode);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(
            JsonRpcFrameLimitingStream.RequestIdRejectionMessage + Environment.NewLine,
            result.Stderr);
        Assert.False(File.Exists(telemetryFile));
        Assert.False(Directory.Exists(sandbox));
    }

    [Theory]
    [InlineData(125, 127)]
    [InlineData(126, 128)]
    public async Task ProductionProgram_AcceptsRequestIdAtAndBelowBoundary(
        int valueChars,
        int serializedBytes)
    {
        var id = new string('r', valueChars);
        Assert.Equal(serializedBytes, ToolRequestIdPolicy.SerializedBytes(new RequestId(id)));

        var result = await RunProductionAsync(InitializeFrame(id));

        Assert.True(result.ExitCode == 0, result.Stderr);
        var response = JsonNode.Parse(result.Stdout.Trim());
        Assert.Equal(JsonValue.Create(id)?.ToJsonString(), response?["id"]?.ToJsonString());
        Assert.Equal("2025-11-25", response?["result"]?["protocolVersion"]?.GetValue<string>());
    }

    [Fact]
    public async Task ProductionProgram_ExtraTraceRootValueFailsAtStartupWithActionableError()
    {
        // Reproduces the issue-18 configuration: one --trace-root followed by
        // three paths used to register only C:\Temp and silently drop the rest.
        var result = await RunProductionAsync(
            [],
            arguments: ["--trace-root", "C:\\Temp", "C:\\tmp", "c:\\unsynced"]);

        Assert.Equal(Program.StartupConfigurationErrorExitCode, result.ExitCode);
        Assert.Contains("Unrecognized argument 'C:\\tmp'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("--trace-root", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProductionProgram_RepeatedTraceRootsStartCleanly()
    {
        var result = await RunProductionAsync(
            InitializeFrame("init-1"),
            arguments: ["--trace-root", "C:\\Temp", "--trace-root", "C:\\tmp"]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        var response = JsonNode.Parse(result.Stdout.Trim());
        Assert.Equal("2025-11-25", response?["result"]?["protocolVersion"]?.GetValue<string>());
    }

    private static byte[] RequestFrame(string id) => Encoding.UTF8.GetBytes(
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "ping",
        }.ToJsonString() + "\n");

    private static byte[] InitializeFrame(string id) => Encoding.UTF8.GetBytes(
        new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-11-25",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "json-rpc-ingress-test",
                    ["version"] = "1.0",
                },
            },
        }.ToJsonString() + "\n");

    private static string LongestPrefixWithinRequestIdLimit(string token)
    {
        var result = string.Empty;
        while (ToolRequestIdPolicy.SerializedBytes(new RequestId(result + token)) <=
               ToolRequestIdPolicy.MaxSerializedBytes)
        {
            result += token;
        }
        return result;
    }

    private static byte[] BuildExactFrame(int frameBytes, string lineEnding)
    {
        var prefix = Encoding.UTF8.GetBytes(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"params\":{\"padding\":\"");
        var suffix = Encoding.UTF8.GetBytes("\"}}" + lineEnding);
        var padding = frameBytes - prefix.Length - suffix.Length;
        Assert.True(padding >= 0);
        return [.. prefix, .. Enumerable.Repeat((byte)'x', padding), .. suffix];
    }

    private static async Task<(byte[] Bytes, JsonRpcIngressRejection Rejection)> ReadGuardedAsync(
        byte[] frame)
    {
        await using var source = new MemoryStream(frame, writable: false);
        await using var guarded = new JsonRpcFrameLimitingStream(
            source,
            new JsonRpcRequestFrameOptions(JsonRpcRequestFrameOptions.HardMaxFrameBytes));
        await using var output = new MemoryStream();
        await guarded.CopyToAsync(output);
        return (output.ToArray(), guarded.Rejection);
    }

    private static async Task<ProcessResult> RunProductionAsync(
        byte[] input,
        IReadOnlyDictionary<string, string?>? environment = null,
        IReadOnlyList<string>? arguments = null)
    {
        var repoRoot = LocateRepoRoot();
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
        if (arguments is not null)
        {
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);
        }
        startInfo.Environment["WPAMCP_TELEMETRY"] = "0";
        if (environment is not null)
        {
            foreach (var variable in environment)
                startInfo.Environment[variable.Key] = variable.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.StandardInput.BaseStream.WriteAsync(input, timeout.Token);
        await process.StandardInput.BaseStream.FlushAsync(timeout.Token);
        var firstLineTask = process.StandardOutput.ReadLineAsync(timeout.Token).AsTask();
        if (await Task.WhenAny(firstLineTask, Task.Delay(TimeSpan.FromSeconds(10), timeout.Token)) !=
            firstLineTask)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException(
                "Production server emitted no response or EOF within 10 seconds. Stderr: " +
                await stderr);
        }
        var firstLine = await firstLineTask;
        if (!process.HasExited)
            process.StandardInput.Close();
        var remainingStdout = await process.StandardOutput.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        var stdout = firstLine is null
            ? remainingStdout
            : firstLine + Environment.NewLine + remainingStdout;
        return new(process.ExitCode, stdout, await stderr);
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

    private sealed record ProcessResult(int ExitCode, string Stdout, string Stderr);
}
