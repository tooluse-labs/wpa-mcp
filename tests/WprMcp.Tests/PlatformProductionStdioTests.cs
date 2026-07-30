using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.InteropServices;
#if MCP_STATELESS_DISCOVERY
using ModelContextProtocol.Protocol;
#endif

namespace WprMcp.Tests;

public sealed class PlatformProductionStdioTests
{
    [Fact]
    public async Task ProductionSelfContainedHostCompletesExactProfileHandshake()
    {
        var serverPath = Environment.GetEnvironmentVariable("WPRMCP_PLATFORM_SERVER_PATH");
        if (string.IsNullOrWhiteSpace(serverPath))
        {
            if (Environment.GetEnvironmentVariable("WPRMCP_PLATFORM_REQUIRED") == "1")
            {
                throw new InvalidOperationException("WPRMCP_PLATFORM_SERVER_PATH is required in runner probe mode.");
            }
            return;
        }

        var publishRoot = RequireEnvironment("WPRMCP_PLATFORM_PUBLISH_ROOT");
        var protocolRevision = RequireEnvironment("WPRMCP_PLATFORM_PROTOCOL_REVISION");
        var protocolProfile = RequireEnvironment("WPRMCP_PLATFORM_PROTOCOL_PROFILE");
        var evidencePath = RequireEnvironment("WPRMCP_PLATFORM_EVIDENCE_PATH");
        var rawStdoutPath = RequireEnvironment("WPRMCP_PLATFORM_RAW_STDOUT_PATH");
        var rawStderrPath = RequireEnvironment("WPRMCP_PLATFORM_RAW_STDERR_PATH");
        var expectedLaunchSha256 = RequireEnvironment("WPRMCP_PLATFORM_EXPECTED_LAUNCH_SHA256");
        var requestedRuntimeIdentifier = RequireEnvironment("WPRMCP_PLATFORM_REQUESTED_RID");
        var publishRuntimeIdentifier = RequireEnvironment("WPRMCP_PLATFORM_PUBLISH_RID");

        serverPath = Path.GetFullPath(serverPath);
        publishRoot = Path.GetFullPath(publishRoot);
        var relativeLaunchPath = Path.GetRelativePath(publishRoot, serverPath);
        Assert.False(Path.IsPathRooted(relativeLaunchPath));
        Assert.False(relativeLaunchPath.StartsWith("..", StringComparison.Ordinal));
        Assert.True(File.Exists(serverPath), $"Production launch path is missing: {serverPath}");
        var launchShaBefore = Sha256(serverPath);
        Assert.Equal(expectedLaunchSha256, launchShaBefore);

        string[] expectedTranscript = protocolProfile switch
        {
            "stateful" => ["initialize", "notifications/initialized", "tools/list", "tools/call"],
#if MCP_STATELESS_DISCOVERY
            "stateless-discovery" => [RequestMethods.ServerDiscover, "tools/list", "tools/call"],
#else
            "stateless-discovery" => throw new InvalidOperationException("Stateless profile was compiled without the rc public SDK surface."),
#endif
            _ => throw new InvalidOperationException($"Unknown protocol profile '{protocolProfile}'."),
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverPath,
                WorkingDirectory = publishRoot,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        Assert.True(process.Start());
        var launchedProcessArchitecture = GetProcessArchitecture(process);
        Assert.Equal(Architecture.X64, launchedProcessArchitecture);
        Assert.Equal(Architecture.X64, RuntimeInformation.OSArchitecture);
        Assert.Equal("win-x64", RuntimeInformation.RuntimeIdentifier);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var receivedLines = new List<string>();
        var sentMethods = new List<string>();
        var metadataKeysByMethod = new Dictionary<string, string[]>(StringComparer.Ordinal);

        try
        {
            if (protocolProfile == "stateful")
            {
                var initialize = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "initialize",
                    ["method"] = "initialize",
                    ["params"] = new JsonObject
                    {
                        ["protocolVersion"] = protocolRevision,
                        ["capabilities"] = new JsonObject(),
                        ["clientInfo"] = new JsonObject { ["name"] = "platform-production-probe", ["version"] = "1.0" },
                    },
                };
                var initializeResponse = await SendRequestAsync(process, initialize, "initialize", sentMethods, receivedLines, timeout.Token);
                Assert.Equal(protocolRevision, initializeResponse["result"]?["protocolVersion"]?.GetValue<string>());
                await SendMessageAsync(process, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/initialized",
                }, sentMethods, timeout.Token);
            }
            else
            {
#if MCP_STATELESS_DISCOVERY
                var discoverParams = CreateStatelessParams(protocolRevision);
                metadataKeysByMethod[RequestMethods.ServerDiscover] = ReadMetadataKeys(discoverParams);
                var discoverResponse = await SendRequestAsync(process, new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = "discover",
                    ["method"] = RequestMethods.ServerDiscover,
                    ["params"] = discoverParams,
                }, "discover", sentMethods, receivedLines, timeout.Token);
                var supportedVersions = discoverResponse["result"]?["supportedVersions"]?.AsArray()
                    .Select(item => item?.GetValue<string>()).ToArray() ?? [];
                Assert.Contains(protocolRevision, supportedVersions);
#else
                throw new InvalidOperationException("Stateless profile was compiled without the rc public SDK surface.");
#endif
            }

            var listParams = CreateProfileParams(protocolProfile, protocolRevision);
            metadataKeysByMethod["tools/list"] = ReadMetadataKeys(listParams);
            var listResponse = await SendRequestAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "list",
                ["method"] = "tools/list",
                ["params"] = listParams,
            }, "list", sentMethods, receivedLines, timeout.Token);
            var listedToolCount = listResponse["result"]?["tools"]?.AsArray().Count ?? 0;
            Assert.True(listedToolCount > 0);

            var callParams = CreateProfileParams(protocolProfile, protocolRevision);
            metadataKeysByMethod["tools/call"] = ReadMetadataKeys(callParams);
            callParams["name"] = "__platform_probe_unknown_tool__";
            callParams["arguments"] = new JsonObject();
            var callResponse = await SendRequestAsync(process, new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "unknown-call",
                ["method"] = "tools/call",
                ["params"] = callParams,
            }, "unknown-call", sentMethods, receivedLines, timeout.Token);
            var unknownCallTerminalError = callResponse["error"] is not null || callResponse["result"]?["isError"]?.GetValue<bool>() == true;
            Assert.True(unknownCallTerminalError,
                "Unknown production tool call did not produce a terminal JSON-RPC error result.");

            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            var stderr = await stderrTask;
            var launchShaAfter = Sha256(serverPath);
            Assert.Equal(launchShaBefore, launchShaAfter);
            Assert.Equal(expectedTranscript, sentMethods);

            WriteCreateNew(rawStdoutPath, string.Join("\n", receivedLines) + "\n");
            WriteCreateNew(rawStderrPath, stderr);
            await using var evidenceStream = new FileStream(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(evidenceStream, new
            {
                schemaVersion = "1.0",
                protocolRevision,
                protocolProfile,
                orderedMessageMethodTranscript = sentMethods,
                serializedMetadataKeysByMethod = metadataKeysByMethod,
                observedOutcomes = new
                {
                    listedToolCount,
                    unknownCallTerminalError,
                },
                launch = new
                {
                    path = serverPath,
                    publishRoot,
                    relativePath = relativeLaunchPath,
                    expectedLaunchSha256,
                    sha256Before = launchShaBefore,
                    sha256After = launchShaAfter,
                    processId = process.Id,
                    childProcessArchitecture = launchedProcessArchitecture.ToString(),
                    observerOsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
                    requestedRuntimeIdentifier,
                    publishRuntimeIdentifier,
                },
                correlatedResponseCount = receivedLines.Count,
                passed = listedToolCount > 0 && unknownCallTerminalError,
            }, new JsonSerializerOptions { WriteIndented = true }, timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private static async Task<JsonNode> SendRequestAsync(
        Process process,
        JsonObject request,
        string expectedId,
        List<string> sentMethods,
        List<string> receivedLines,
        CancellationToken cancellationToken)
    {
        await SendMessageAsync(process, request, sentMethods, cancellationToken);
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken)
                ?? throw new EndOfStreamException($"Production server closed stdout before response '{expectedId}'.");
            receivedLines.Add(line);
            var response = JsonNode.Parse(line) ?? throw new JsonException("Production server emitted empty JSON.");
            if (response["id"]?.GetValue<string>() == expectedId)
            {
                return response;
            }
        }
    }

    private static async Task SendMessageAsync(
        Process process,
        JsonObject message,
        List<string> sentMethods,
        CancellationToken cancellationToken)
    {
        sentMethods.Add(message["method"]?.GetValue<string>() ?? throw new JsonException("Outgoing message omitted method."));
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
        await process.StandardInput.BaseStream.WriteAsync(bytes, cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    private static JsonObject CreateProfileParams(string profile, string revision)
    {
        if (profile == "stateful")
        {
            return new JsonObject();
        }
#if MCP_STATELESS_DISCOVERY
        return CreateStatelessParams(revision);
#else
        throw new InvalidOperationException("Stateless profile was compiled without the rc public SDK surface.");
#endif
    }

#if MCP_STATELESS_DISCOVERY
    private static JsonObject CreateStatelessParams(string revision) => new()
    {
        ["_meta"] = new JsonObject
        {
            [MetaKeys.ProtocolVersion] = revision,
            [MetaKeys.ClientInfo] = new JsonObject { ["name"] = "platform-production-probe", ["version"] = "1.0" },
            [MetaKeys.ClientCapabilities] = new JsonObject(),
        },
    };
#endif

    private static string[] ReadMetadataKeys(JsonObject parameters) =>
        parameters["_meta"] is JsonObject metadata
            ? metadata.Select(property => property.Key).ToArray()
            : [];

    private static string RequireEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required for the production stdio probe.");

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void WriteCreateNew(string path, string content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static Architecture GetProcessArchitecture(Process process)
    {
        Assert.True(IsWow64Process2(process.Handle, out var processMachine, out var nativeMachine));
        var machine = processMachine == ImageFileMachineUnknown ? nativeMachine : processMachine;
        return machine switch
        {
            ImageFileMachineAmd64 => Architecture.X64,
            ImageFileMachineArm64 => Architecture.Arm64,
            ImageFileMachineI386 => Architecture.X86,
            _ => throw new InvalidOperationException($"Unsupported launched process machine 0x{machine:x4}."),
        };
    }

    private const ushort ImageFileMachineUnknown = 0;
    private const ushort ImageFileMachineI386 = 0x014c;
    private const ushort ImageFileMachineAmd64 = 0x8664;
    private const ushort ImageFileMachineArm64 = 0xaa64;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
