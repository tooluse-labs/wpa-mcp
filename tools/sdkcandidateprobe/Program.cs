using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SdkCandidateProbe;

internal static class Program
{
    private const int ProductionFrameLimit = 100000;
    private const int RequestIdLimit = 128;
    private const string FrameLimitMessage = "sdkcandidateprobe: frame limit exceeded";
    private const string RequestIdLimitMessage = "sdkcandidateprobe: request id limit exceeded";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var arguments = ProbeArguments.Parse(args);
            if (arguments.Mode == ProbeMode.Serve)
            {
                return await RunServerAsync(arguments).ConfigureAwait(false);
            }

            using var suiteTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            return await RunSuiteAsync(arguments, suiteTimeout.Token).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            await Console.Error.WriteLineAsync($"sdkcandidateprobe: {exception.Message}").ConfigureAwait(false);
            return 64;
        }
        catch (OperationCanceledException)
        {
            await Console.Error.WriteLineAsync("sdkcandidateprobe: suite timed out").ConfigureAwait(false);
            return 124;
        }
    }

    private static async Task<int> RunServerAsync(ProbeArguments arguments)
    {
        var limitState = new LimitState();
        var tool = new SdkProbeTool();
        var delegatingTool = tool.CreateDelegatingTool();
        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        tools.Add(delegatingTool);

        var options = new McpServerOptions
        {
            ProtocolVersion = arguments.ProtocolRevision,
            InitializationTimeout = TimeSpan.FromSeconds(3),
            ServerInfo = new Implementation { Name = "sdkcandidateprobe", Version = "1.0" },
            Capabilities = new ServerCapabilities { Tools = new ToolsCapability() },
            ToolCollection = tools,
        };
        AddIncomingFilter(options, limitState);

        await using var guardedInput = new FrameLimitStream(Console.OpenStandardInput(), arguments.FrameLimit, limitState.Reject);
        await using var transport = new StreamServerTransport(
            guardedInput,
            Console.OpenStandardOutput(),
            "sdkcandidateprobe");
        await using var server = McpServer.Create(transport, options);
        await server.RunAsync().ConfigureAwait(false);
        if (arguments.AuditPath is not null)
        {
            await using var auditStream = new FileStream(arguments.AuditPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await JsonSerializer.SerializeAsync(auditStream, new
            {
                incomingNextCount = limitState.IncomingNextCount,
                handlerInvocationCount = tool.InvocationCount,
                handlerCancellationObservationCount = tool.CancellationObservationCount,
            }).ConfigureAwait(false);
        }
        if (limitState.Message is not null)
        {
            await Console.Error.WriteLineAsync(limitState.Message).ConfigureAwait(false);
            return 2;
        }

        return 0;
    }

    private static void AddIncomingFilter(McpServerOptions options, LimitState limitState)
    {
        options.Filters.Message.IncomingFilters.Add(next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcMessageWithId { Id.Id: string requestId } &&
                Encoding.UTF8.GetByteCount(requestId) > RequestIdLimit)
            {
                limitState.Reject(RequestIdLimitMessage);
                return;
            }

            limitState.RecordIncomingNext();
            await next(context, cancellationToken).ConfigureAwait(false);
        });
    }

    private static async Task<int> RunSuiteAsync(ProbeArguments arguments, CancellationToken cancellationToken)
    {
        var transcript = new List<string>();
        var metadataKeysByMethod = new Dictionary<string, string[]>(StringComparer.Ordinal);
        await using var child = await ProbeChild.StartAsync(
            arguments.HostCommand!,
            arguments.ProtocolRevision,
            arguments.ProtocolProfile).ConfigureAwait(false);

        string acceptedRevision;
        if (arguments.ProtocolProfile == "stateful")
        {
            var initialize = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "initialize",
                ["method"] = "initialize",
                ["params"] = new JsonObject
                {
                    ["protocolVersion"] = arguments.ProtocolRevision,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject { ["name"] = "sdkcandidateprobe-suite", ["version"] = "1.0" },
                },
            };
            transcript.Add("initialize");
            var initializeResponse = await child.SendRequestAsync(initialize, "initialize", crlf: false, cancellationToken).ConfigureAwait(false);
            acceptedRevision = initializeResponse["result"]?["protocolVersion"]?.GetValue<string>() ?? string.Empty;
            metadataKeysByMethod["initialize"] = [];
            transcript.Add("notifications/initialized");
            await child.SendNotificationAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "notifications/initialized",
            }, crlf: true, cancellationToken).ConfigureAwait(false);
        }
        else if (arguments.ProtocolProfile == "stateless-discovery")
        {
#if MCP_STATELESS_DISCOVERY
            var discoverParams = new DiscoverRequestParams { Meta = CreateRequestMeta(arguments.ProtocolRevision) };
            metadataKeysByMethod[RequestMethods.ServerDiscover] = discoverParams.Meta!.Select(property => property.Key).ToArray();
            transcript.Add(RequestMethods.ServerDiscover);
            var discoverResponse = await child.SendRequestAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = "discover",
                ["method"] = RequestMethods.ServerDiscover,
                ["params"] = JsonSerializer.SerializeToNode(discoverParams),
            }, "discover", crlf: false, cancellationToken).ConfigureAwait(false);
            var versions = discoverResponse["result"]?["supportedVersions"]?.AsArray();
            acceptedRevision = versions?.Select(node => node?.GetValue<string>()).FirstOrDefault(version => version == arguments.ProtocolRevision) ?? string.Empty;
#else
            throw new InvalidOperationException("The selected stable SDK does not expose stateless discovery APIs.");
#endif
        }
        else
        {
            throw new ArgumentException($"Unsupported protocol profile '{arguments.ProtocolProfile}'.");
        }

        transcript.Add("tools/list");
        var listParams = CreateProfileParams(arguments.ProtocolProfile, arguments.ProtocolRevision);
        metadataKeysByMethod["tools/list"] = ReadMetaKeys(listParams);
        var listResponse = await child.SendRequestAsync(CreateRequest(
            "list",
            "tools/list",
            listParams), "list", crlf: true, cancellationToken).ConfigureAwait(false);
        var listedTool = listResponse["result"]?["tools"]?.AsArray()
            .Single(node => node?["name"]?.GetValue<string>() == "sdk_probe_echo");
        var inputProperties = listedTool?["inputSchema"]?["properties"]?.AsObject().Select(property => property.Key).ToArray() ?? [];

        transcript.Add("tools/call");
        var callParams = CreateProfileParams(arguments.ProtocolProfile, arguments.ProtocolRevision);
        callParams["name"] = "sdk_probe_echo";
        callParams["arguments"] = new JsonObject { ["value"] = "hello" };
        EnsureMeta(callParams)["progressToken"] = "progress-1";
        metadataKeysByMethod["tools/call"] = ReadMetaKeys(callParams);
        var callResponse = await child.SendRequestAsync(CreateRequest("call", "tools/call", callParams), "call", crlf: false, cancellationToken).ConfigureAwait(false);
        var text = callResponse["result"]?["content"]?[0]?["text"]?.GetValue<string>();
        var structured = callResponse["result"]?["structuredContent"]?["value"]?.GetValue<string>();
        var delegatedInvocation = callResponse["result"]?["structuredContent"]?["invocation"]?.GetValue<int>() ?? 0;
        var innerTextObserved = callResponse["result"]?["structuredContent"]?["innerTextObserved"]?.GetValue<bool>() == true;
        var innerStructuredObserved = callResponse["result"]?["structuredContent"]?["innerStructuredObserved"]?.GetValue<bool>() == true;
        var preservedIsError = callResponse["result"]?["structuredContent"]?["preservedIsError"]?.GetValue<bool?>();
        var runtimeIdentity = callResponse["result"]?["structuredContent"]?["runtimeIdentity"]?
            .Deserialize<ProbeRuntimeIdentity>(new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var acceptedIdCases = new List<bool>();
        var acceptedIds = new (string IdJson, string ExpectedIdJson, string Value, bool Crlf)[]
        {
            ($"\"{new string('a', 127)}\"", $"\"{new string('a', 127)}\"", "ascii-127", false),
            ($"\"{new string('a', 128)}\"", $"\"{new string('a', 128)}\"", "ascii-128", true),
            ("\"é\"", "\"é\"", "direct-utf8", false),
            ("\"\\u00e9\"", "\"é\"", "escaped-utf8", true),
            (long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), long.MinValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "numeric-min", false),
            ("0", "0", "numeric-zero", true),
            (long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), "numeric-max", false),
        };
        foreach (var accepted in acceptedIds)
        {
            var response = await child.SendRawRequestAsync(
                CreateRawToolCallFrame(accepted.IdJson, accepted.Value, accepted.Crlf ? "\r\n" : "\n", arguments.ProtocolProfile, arguments.ProtocolRevision),
                accepted.ExpectedIdJson,
                cancellationToken).ConfigureAwait(false);
            acceptedIdCases.Add(response["result"]?["structuredContent"]?["invocation"]?.GetValue<int>() > delegatedInvocation);
        }

        var exactLimitFrame = CreateRawToolCallFrame("\"frame-100000\"", string.Empty, "\r\n", arguments.ProtocolProfile, arguments.ProtocolRevision, ProductionFrameLimit);
        var exactLimitResponse = await child.SendRawRequestAsync(exactLimitFrame, "\"frame-100000\"", cancellationToken).ConfigureAwait(false);
        var exactLimitAccepted = exactLimitResponse["result"]?["structuredContent"]?["invocation"]?.GetValue<int>() > delegatedInvocation;

        var cancelParams = CreateProfileParams(arguments.ProtocolProfile, arguments.ProtocolRevision);
        cancelParams["name"] = "sdk_probe_echo";
        cancelParams["arguments"] = new JsonObject { ["value"] = "cancel" };
        EnsureMeta(cancelParams)["progressToken"] = "cancel-progress";
        var cancellationResult = await child.SendCancellableRequestAsync(
            CreateRequest("cancel", "tools/call", cancelParams),
            new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "notifications/cancelled",
            ["params"] = new JsonObject { ["requestId"] = "cancel", ["reason"] = "probe" },
        },
            "cancel",
            TimeSpan.FromSeconds(2),
            cancellationToken).ConfigureAwait(false);

        await child.CloseInputAndWaitAsync(cancellationToken).ConfigureAwait(false);
        var childAudit = child.ReadAudit();
        var requestedShaAfter = child.GetRequestedSha256After();
        var processSha = child.GetProcessSha256();
        var configuredDotNetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var configuredDotNetRootX64 = Environment.GetEnvironmentVariable("DOTNET_ROOT_X64");
        static bool IsPathUnder(string path, string root)
        {
            var fullPath = Path.GetFullPath(path);
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        var loadedRuntimeModulesPassed = runtimeIdentity is not null &&
            File.Exists(runtimeIdentity.LoadedHostFxrPath) && File.Exists(runtimeIdentity.LoadedHostPolicyPath) &&
            runtimeIdentity.LoadedHostFxrSha256 == ProbeChild.Sha256(runtimeIdentity.LoadedHostFxrPath) &&
            runtimeIdentity.LoadedHostPolicySha256 == ProbeChild.Sha256(runtimeIdentity.LoadedHostPolicyPath) &&
            (arguments.HostMode == "win-x64-self-contained"
                ? IsPathUnder(runtimeIdentity.LoadedHostFxrPath, Path.GetDirectoryName(child.RequestedPath)!) &&
                  IsPathUnder(runtimeIdentity.LoadedHostPolicyPath, Path.GetDirectoryName(child.RequestedPath)!)
                : configuredDotNetRoot is not null && configuredDotNetRootX64 is not null &&
                  string.Equals(Path.GetFullPath(configuredDotNetRoot), Path.GetFullPath(configuredDotNetRootX64), StringComparison.OrdinalIgnoreCase) &&
                  IsPathUnder(runtimeIdentity.LoadedHostFxrPath, configuredDotNetRootX64) &&
                  IsPathUnder(runtimeIdentity.LoadedHostPolicyPath, configuredDotNetRootX64));
        var launchIdentityPassed = runtimeIdentity is not null &&
            runtimeIdentity.ProcessId == child.ProcessId &&
            string.Equals(Path.GetFullPath(arguments.HostCommand!), child.RequestedPath, StringComparison.OrdinalIgnoreCase) &&
            child.RequestedSha256Before == requestedShaAfter &&
            string.Equals(Path.GetFullPath(runtimeIdentity.ProcessPath), Path.GetFullPath(child.ProcessPath), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(runtimeIdentity.ProcessPath), child.RequestedPath, StringComparison.OrdinalIgnoreCase) &&
            runtimeIdentity.ProcessPathSha256 == processSha && runtimeIdentity.ProcessPathSha256 == child.RequestedSha256Before &&
            string.Equals(
                Path.GetFullPath(runtimeIdentity.EntryAssemblyPath),
                Path.Combine(Path.GetDirectoryName(child.RequestedPath)!, "sdkcandidateprobe.dll"),
                StringComparison.OrdinalIgnoreCase) &&
            runtimeIdentity.EntryAssemblySha256 == ProbeChild.Sha256(runtimeIdentity.EntryAssemblyPath) &&
            runtimeIdentity.OsPlatform == "Windows" &&
            runtimeIdentity.OsArchitecture == "X64" && runtimeIdentity.ProcessArchitecture == "X64" &&
            child.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64 &&
            runtimeIdentity.RuntimeIdentifier == "win-x64" &&
            runtimeIdentity.Is64BitOperatingSystem && runtimeIdentity.Is64BitProcess &&
            loadedRuntimeModulesPassed;
        var negativeId = await RunNegativeChildAsync(arguments,
            CreateRawToolCallFrame($"\"{new string('i', 129)}\"", "oversized-id", "\n", arguments.ProtocolProfile, arguments.ProtocolRevision), cancellationToken).ConfigureAwait(false);
        var oversizedFrame = await RunNegativeChildAsync(arguments,
            CreateRawToolCallFrame("\"frame-100001\"", string.Empty, "\n", arguments.ProtocolProfile, arguments.ProtocolRevision, ProductionFrameLimit + 1), cancellationToken).ConfigureAwait(false);
        var loweredFrame = CreateRawToolCallFrame("\"isolated-cr\"", "isolated-cr", "\r\n", arguments.ProtocolProfile, arguments.ProtocolRevision);
        var loweredCap = loweredFrame.Length - 1;
        var isolatedCrFrame = loweredFrame[..^2].Concat([(byte)'\r', (byte)' ', (byte)'\n']).ToArray();
        var isolatedCr = await RunNegativeChildAsync(arguments, isolatedCrFrame, cancellationToken, loweredCap).ConfigureAwait(false);
        var validBomTarget = CreateRawToolCallFrame("\"bom\"", "bom", "\n", arguments.ProtocolProfile, arguments.ProtocolRevision);
        var bomAtStart = await RunNegativeChildAsync(arguments, [0xEF, 0xBB, 0xBF, .. validBomTarget], cancellationToken).ConfigureAwait(false);
        var bomAnywhere = await RunNegativeChildAsync(arguments,
            [.. validBomTarget[..10], 0xEF, 0xBB, 0xBF, .. validBomTarget[10..]], cancellationToken).ConfigureAwait(false);

        var evidence = new
        {
            schemaVersion = "1.0",
            hostMode = arguments.HostMode,
            protocolRevision = arguments.ProtocolRevision,
            protocolProfile = arguments.ProtocolProfile,
            launchIdentity = new
            {
                retainedLaunchPath = child.RequestedPath,
                preLaunchSha256 = child.RequestedSha256Before,
                postLaunchSha256 = requestedShaAfter,
                childProcessId = child.ProcessId,
                childProcessPath = child.ProcessPath,
                childProcessSha256 = processSha,
                childProcessArchitecture = child.ProcessArchitecture.ToString(),
                configuredDotNetRoot,
                configuredDotNetRootX64,
                runtimeIdentity,
                passed = launchIdentityPassed,
            },
            offeredRevision = arguments.ProtocolRevision,
            acceptedRevision,
            serializedMetadataKeysByMethod = metadataKeysByMethod,
            orderedMessageMethodTranscript = transcript,
            InputSchemaPropertyNames = inputProperties,
            structuredOutput = new
            {
                textReplaced = text == "delegated-text",
                structuredContentReplaced = structured == "delegated-structured",
                innerTextObserved,
                innerStructuredObserved,
                inputSchemaPresent = listedTool?["inputSchema"] is not null,
                outputSchemaPresent = listedTool?["outputSchema"] is not null,
                annotationsPresent = listedTool?["annotations"] is not null,
                isError = callResponse["result"]?["isError"]?.GetValue<bool?>(),
                preservedIsError,
            },
            cancellationProgress = new
            {
                normalProgressNotificationCount = child.GetProgressNotificationCount("progress-1"),
                cancellationProgressNotificationCount = child.GetProgressNotificationCount("cancel-progress"),
                totalProgressNotificationCount = child.ProgressNotificationCount,
                cancellationObserved = cancellationResult.ProgressObserved && !cancellationResult.ResponseObserved,
                handlerCancellationObservationCount = childAudit.HandlerCancellationObservationCount,
                injectedParametersAbsentFromSchema = inputProperties.SequenceEqual(["value"], StringComparer.Ordinal),
            },
            framingAndRequestIds = new
            {
                productionFrameLimit = ProductionFrameLimit,
                decodedRequestIdLimit = RequestIdLimit,
                ascii127Bytes = Encoding.UTF8.GetByteCount(new string('a', 127)),
                ascii128Bytes = Encoding.UTF8.GetByteCount(new string('a', 128)),
                directUtf8Bytes = Encoding.UTF8.GetByteCount("é"),
                escapedUtf8Bytes = Encoding.UTF8.GetByteCount("\u00e9"),
                numericIds = new[] { long.MinValue, 0, long.MaxValue },
                acceptedIdCases,
                exactProductionFrameAccepted = exactLimitAccepted,
                oversizedIdRejectedBeforeDispatch = negativeId.IsExact(RequestIdLimitMessage),
                oversizedFrameRejectedBeforeDeserialization = oversizedFrame.IsExact(FrameLimitMessage),
                oversizedFrameObservation = oversizedFrame,
                loweredCapIsolatedCrRejected = isolatedCr.IsExact(FrameLimitMessage),
                bomAtStartRejected = bomAtStart.IsExact(FrameLimitMessage),
                bomAnywhereRejected = bomAnywhere.IsExact(FrameLimitMessage),
                lfAndCrLfAccepted = acceptedIdCases.Count == acceptedIds.Length,
            },
            InvocationCount = exactLimitResponse["result"]?["structuredContent"]?["invocation"]?.GetValue<int>() ?? 0,
            passed = arguments.ProtocolRevision == acceptedRevision &&
                transcript.SequenceEqual(arguments.ProtocolProfile == "stateful"
                    ? ["initialize", "notifications/initialized", "tools/list", "tools/call"]
                    : ["server/discover", "tools/list", "tools/call"], StringComparer.Ordinal) &&
                MetadataContractPassed(arguments.ProtocolProfile, metadataKeysByMethod) &&
                inputProperties.SequenceEqual(["value"], StringComparer.Ordinal) &&
                listedTool?["inputSchema"] is not null && listedTool?["outputSchema"] is not null && listedTool?["annotations"] is not null &&
                text == "delegated-text" &&
                structured == "delegated-structured" &&
                launchIdentityPassed &&
                innerTextObserved && innerStructuredObserved &&
                callResponse["result"]?["isError"]?.GetValue<bool?>() != true && preservedIsError != true &&
                callResponse["result"]?["isError"]?.GetValue<bool?>() == preservedIsError &&
                child.GetProgressNotificationCount("progress-1") == 1 &&
                child.GetProgressNotificationCount("cancel-progress") == 1 &&
                child.ProgressNotificationCount == 2 &&
                cancellationResult.ProgressObserved && !cancellationResult.ResponseObserved &&
                childAudit.HandlerCancellationObservationCount == 1 &&
                acceptedIdCases.All(value => value) && exactLimitAccepted &&
                negativeId.IsExact(RequestIdLimitMessage) &&
                oversizedFrame.IsExact(FrameLimitMessage) &&
                isolatedCr.IsExact(FrameLimitMessage) &&
                bomAtStart.IsExact(FrameLimitMessage) && bomAnywhere.IsExact(FrameLimitMessage) &&
                Encoding.UTF8.GetByteCount(new string('a', 127)) == 127 &&
                Encoding.UTF8.GetByteCount(new string('a', 128)) == RequestIdLimit &&
                Encoding.UTF8.GetByteCount("é") == 2 && Encoding.UTF8.GetByteCount("\u00e9") == 2 &&
                exactLimitFrame.Length - Encoding.UTF8.GetByteCount("\r\n") == ProductionFrameLimit &&
                exactLimitResponse["result"]?["structuredContent"]?["invocation"]?.GetValue<int>() > 0,
        };

        await using var evidenceStream = new FileStream(arguments.EvidencePath!, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(evidenceStream, evidence, new JsonSerializerOptions { WriteIndented = true }).ConfigureAwait(false);
        return evidence.passed ? 0 : 1;
    }

    private static JsonObject CreateRequest(string id, string method, JsonObject parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = parameters,
    };

    private static byte[] CreateRawToolCallFrame(
        string idJson,
        string value,
        string lineEnding,
        string profile,
        string revision,
        int? exactPayloadBytes = null)
    {
        var profileParameters = CreateProfileParams(profile, revision);
        var serializedMeta = profileParameters["_meta"] is JsonObject meta ? $"\"_meta\":{meta.ToJsonString()}," : string.Empty;
        var prefixAfterId = $",\"method\":\"tools/call\",\"params\":{{{serializedMeta}\"name\":\"sdk_probe_echo\",\"arguments\":{{\"value\":\"";
        const string suffix = "\"}}}";
        var prefix = $"{{\"jsonrpc\":\"2.0\",\"id\":{idJson}{prefixAfterId}";
        var fixedPayloadBytes = Encoding.UTF8.GetByteCount(prefix) + Encoding.UTF8.GetByteCount(suffix);
        var requestedValueBytes = exactPayloadBytes.HasValue ? exactPayloadBytes.Value - fixedPayloadBytes : Encoding.UTF8.GetByteCount(value);
        if (requestedValueBytes < 0 || (exactPayloadBytes.HasValue && value.Length != 0))
        {
            throw new ArgumentOutOfRangeException(nameof(exactPayloadBytes));
        }
        var payloadValue = exactPayloadBytes.HasValue ? new string('v', requestedValueBytes) : value;
        var frame = Encoding.UTF8.GetBytes(prefix + payloadValue + suffix + lineEnding);
        if (exactPayloadBytes.HasValue && frame.Length - Encoding.UTF8.GetByteCount(lineEnding) != exactPayloadBytes.Value)
        {
            throw new InvalidOperationException("Exact frame payload construction failed.");
        }
        return frame;
    }

    private static string[] ReadMetaKeys(JsonObject parameters) =>
        parameters["_meta"] is JsonObject meta ? meta.Select(property => property.Key).ToArray() : [];

    private static bool MetadataContractPassed(string profile, IReadOnlyDictionary<string, string[]> keysByMethod)
    {
        if (profile == "stateful")
        {
            return keysByMethod.TryGetValue("initialize", out var initialize) && initialize.Length == 0 &&
                keysByMethod.TryGetValue("tools/list", out var statefulList) && statefulList.Length == 0 &&
                keysByMethod.TryGetValue("tools/call", out var statefulCall) && statefulCall.SequenceEqual(["progressToken"], StringComparer.Ordinal);
        }

#if MCP_STATELESS_DISCOVERY
        var profileKeys = CreateRequestMeta(string.Empty).Select(property => property.Key).ToArray();
        return keysByMethod.TryGetValue(RequestMethods.ServerDiscover, out var discover) && discover.SequenceEqual(profileKeys, StringComparer.Ordinal) &&
            keysByMethod.TryGetValue("tools/list", out var list) && list.SequenceEqual(profileKeys, StringComparer.Ordinal) &&
            keysByMethod.TryGetValue("tools/call", out var call) && call.SequenceEqual([.. profileKeys, "progressToken"], StringComparer.Ordinal);
#else
        return false;
#endif
    }

    private static JsonObject CreateProfileParams(string profile, string revision)
    {
        var parameters = new JsonObject();
        if (profile == "stateless-discovery")
        {
#if MCP_STATELESS_DISCOVERY
            parameters["_meta"] = CreateRequestMeta(revision);
#else
            throw new InvalidOperationException("Stateless discovery requires the rc.1 public SDK surface.");
#endif
        }

        return parameters;
    }

    private static JsonObject EnsureMeta(JsonObject parameters)
    {
        if (parameters["_meta"] is not JsonObject meta)
        {
            meta = new JsonObject();
            parameters["_meta"] = meta;
        }

        return meta;
    }

#if MCP_STATELESS_DISCOVERY
    private static JsonObject CreateRequestMeta(string revision) => new()
    {
        [MetaKeys.ProtocolVersion] = revision,
        [MetaKeys.ClientInfo] = new JsonObject { ["name"] = "sdkcandidateprobe-suite", ["version"] = "1.0" },
        [MetaKeys.ClientCapabilities] = new JsonObject(),
    };
#endif

    private static async Task<NegativeResult> RunNegativeChildAsync(
        ProbeArguments arguments,
        byte[] input,
        CancellationToken cancellationToken,
        int? frameLimit = null)
    {
        var captureDirectory = Path.GetDirectoryName(arguments.EvidencePath!)!;
        var capturePrefix = Path.Combine(captureDirectory, $"sdkcandidateprobe-{Guid.NewGuid():N}");
        var inputPath = capturePrefix + ".stdin";
        var outputPath = capturePrefix + ".stdout";
        var errorPath = capturePrefix + ".stderr";
        var auditPath = capturePrefix + ".audit.json";
        await File.WriteAllBytesAsync(inputPath, input, cancellationToken).ConfigureAwait(false);

        using var process = CreateNegativeProcess(arguments, auditPath, frameLimit);
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException($"Negative probe child did not start: {process.StartInfo.FileName}");
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.BaseStream.WriteAsync(input, cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
            using var childTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            childTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(childTimeout.Token).ConfigureAwait(false);

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = (await stderrTask.ConfigureAwait(false)).TrimEnd('\r', '\n');
            await File.WriteAllTextAsync(outputPath, stdout, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(errorPath, stderr, cancellationToken).ConfigureAwait(false);
            var audit = JsonNode.Parse(await File.ReadAllTextAsync(auditPath, cancellationToken).ConfigureAwait(false))
                ?? throw new JsonException("Negative audit was empty JSON.");
            return new NegativeResult(
                process.ExitCode,
                stdout,
                stderr,
                audit["incomingNextCount"]!.GetValue<int>(),
                audit["handlerInvocationCount"]!.GetValue<int>());
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            File.Delete(inputPath);
            File.Delete(outputPath);
            File.Delete(errorPath);
            File.Delete(auditPath);
        }
    }

    private static Process CreateNegativeProcess(
        ProbeArguments arguments,
        string auditPath,
        int? frameLimit)
    {
        var requestedPath = Path.GetFullPath(arguments.HostCommand!);
        var managedAssembly = string.Equals(Path.GetExtension(requestedPath), ".dll", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = managedAssembly ? ProbeChild.ResolveDotNetHost() : requestedPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        if (managedAssembly) { startInfo.ArgumentList.Add(requestedPath); }
        startInfo.ArgumentList.Add("--serve");
        startInfo.ArgumentList.Add("--protocol-revision");
        startInfo.ArgumentList.Add(arguments.ProtocolRevision);
        startInfo.ArgumentList.Add("--protocol-profile");
        startInfo.ArgumentList.Add(arguments.ProtocolProfile);
        startInfo.ArgumentList.Add("--audit-path");
        startInfo.ArgumentList.Add(auditPath);
        if (frameLimit.HasValue)
        {
            startInfo.ArgumentList.Add("--frame-limit");
            startInfo.ArgumentList.Add(frameLimit.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return new Process { StartInfo = startInfo };
    }

    private sealed record NegativeResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        int IncomingNextCount,
        int HandlerInvocationCount)
    {
        public bool IsExact(string expectedStderr) =>
            ExitCode == 2 && Stdout.Length == 0 && Stderr == expectedStderr &&
            IncomingNextCount == 0 && HandlerInvocationCount == 0;
    }

    private sealed class LimitState
    {
        public string? Message { get; private set; }

        public int IncomingNextCount { get; private set; }

        public void RecordIncomingNext() => IncomingNextCount++;

        public void Reject(string message)
        {
            Message ??= message;
        }
    }

    private enum ProbeMode { Serve, RunSuite }

    private sealed record ProbeArguments(
        ProbeMode Mode,
        string? HostMode,
        string? HostCommand,
        string ProtocolRevision,
        string ProtocolProfile,
        string? EvidencePath,
        int FrameLimit,
        string? AuditPath)
    {
        public static ProbeArguments Parse(string[] args)
        {
            if (args.Length == 0 || (args[0] != "--serve" && args[0] != "--run-suite"))
            {
                throw new ArgumentException("mode must be --serve or --run-suite");
            }

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var index = 1; index < args.Length; index += 2)
            {
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException($"missing value for '{args[index]}'");
                }

                if (!values.TryAdd(args[index], args[index + 1]))
                {
                    throw new ArgumentException($"duplicate option '{args[index]}'");
                }
            }

            string Require(string name) => values.TryGetValue(name, out var value) && value.Length > 0
                ? value
                : throw new ArgumentException($"{name} is required");
            var mode = args[0] == "--serve" ? ProbeMode.Serve : ProbeMode.RunSuite;
            var revision = Require("--protocol-revision");
            var profile = Require("--protocol-profile");
            if (profile is not ("stateful" or "stateless-discovery"))
            {
                throw new ArgumentException("--protocol-profile must be stateful or stateless-discovery");
            }

            if (mode == ProbeMode.Serve)
            {
                var frameLimit = values.TryGetValue("--frame-limit", out var value) && int.TryParse(value, out var parsed) ? parsed : ProductionFrameLimit;
                var auditPath = values.TryGetValue("--audit-path", out var requestedAuditPath)
                    ? Path.GetFullPath(requestedAuditPath)
                    : null;
                return new(mode, null, null, revision, profile, null, frameLimit, auditPath);
            }

            var hostMode = Require("--host-mode");
            if (hostMode is not ("normal" or "win-x64-framework-dependent" or "win-x64-self-contained"))
            {
                throw new ArgumentException("--host-mode is invalid");
            }

            var hostCommand = Path.GetFullPath(Require("--host-command"));
            var evidence = Path.GetFullPath(Require("--evidence"));
            if (!Path.IsPathFullyQualified(hostCommand) || !Path.IsPathFullyQualified(evidence))
            {
                throw new ArgumentException("--host-command and --evidence must be absolute paths");
            }

            return new(mode, hostMode, hostCommand, revision, profile, evidence, ProductionFrameLimit, null);
        }
    }

    private sealed class FrameLimitStream(Stream inner, int payloadLimit, Action<string> reject) : Stream
    {
        private readonly byte[] _inputBuffer = new byte[8192];
        private readonly MemoryStream _pendingFrame = new();
        private byte[]? _approvedFrame;
        private int _approvedOffset;
        private int _inputOffset;
        private int _inputCount;
        private int _payloadBytes;
        private bool _pendingCarriageReturn;
        private byte _bomFirst;
        private byte _bomSecond;
        private bool _rejected;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            if (offset < 0 || count < 0 || offset + count > buffer.Length) { throw new ArgumentOutOfRangeException(); }
            while (!_rejected && !HasApprovedBytes())
            {
                if (_inputOffset == _inputCount)
                {
                    _inputCount = inner.Read(_inputBuffer, 0, _inputBuffer.Length);
                    _inputOffset = 0;
                    if (_inputCount == 0) { ApproveEndOfInput(); break; }
                }
                ProcessBufferedInput();
            }
            return CopyApprovedBytes(buffer.AsSpan(offset, count));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (!_rejected && !HasApprovedBytes())
            {
                if (_inputOffset == _inputCount)
                {
                    _inputCount = await inner.ReadAsync(_inputBuffer, cancellationToken).ConfigureAwait(false);
                    _inputOffset = 0;
                    if (_inputCount == 0) { ApproveEndOfInput(); break; }
                }
                ProcessBufferedInput();
            }
            return CopyApprovedBytes(buffer.Span);
        }

        private bool HasApprovedBytes() => _approvedFrame is not null && _approvedOffset < _approvedFrame.Length;

        private int CopyApprovedBytes(Span<byte> destination)
        {
            if (_rejected || !HasApprovedBytes() || destination.IsEmpty) { return 0; }
            var count = Math.Min(destination.Length, _approvedFrame!.Length - _approvedOffset);
            _approvedFrame.AsSpan(_approvedOffset, count).CopyTo(destination);
            _approvedOffset += count;
            if (_approvedOffset == _approvedFrame.Length)
            {
                _approvedFrame = null;
                _approvedOffset = 0;
            }
            return count;
        }

        private void ProcessBufferedInput()
        {
            while (_inputOffset < _inputCount && !_rejected && !HasApprovedBytes())
            {
                var value = _inputBuffer[_inputOffset++];
                _pendingFrame.WriteByte(value);
                InspectByte(value);
                if (value == (byte)'\n' && !_rejected) { ApprovePendingFrame(); }
            }
        }

        private void InspectByte(byte value)
        {
            if (_bomFirst == 0xEF && _bomSecond == 0xBB && value == 0xBF)
            {
                Reject();
                return;
            }
            _bomFirst = _bomSecond;
            _bomSecond = value;

            if (value == (byte)'\n')
            {
                _pendingCarriageReturn = false;
                return;
            }
            if (_pendingCarriageReturn) { CountPayloadByte(); }
            _pendingCarriageReturn = value == (byte)'\r';
            if (!_pendingCarriageReturn) { CountPayloadByte(); }
        }

        private void ApprovePendingFrame()
        {
            _approvedFrame = _pendingFrame.ToArray();
            _approvedOffset = 0;
            _pendingFrame.SetLength(0);
            _payloadBytes = 0;
            _pendingCarriageReturn = false;
        }

        private void ApproveEndOfInput()
        {
            if (_pendingCarriageReturn) { CountPayloadByte(); _pendingCarriageReturn = false; }
            if (!_rejected && _pendingFrame.Length > 0) { ApprovePendingFrame(); }
        }

        private void CountPayloadByte()
        {
            _payloadBytes++;
            if (_payloadBytes > payloadLimit)
            {
                Reject();
            }
        }

        private void Reject()
        {
            _rejected = true;
            _pendingFrame.SetLength(0);
            _approvedFrame = null;
            reject(FrameLimitMessage);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
