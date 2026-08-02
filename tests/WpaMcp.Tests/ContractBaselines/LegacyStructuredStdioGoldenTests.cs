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
using WpaMcp.Output;

namespace WpaMcp.Tests.ContractBaselines;

public sealed class LegacyStructuredStdioGoldenTests
{
    private const string ProtocolVersion = "2025-11-25";
    private const string FixtureRelativePath = "tests/WpaMcp.Tests/fixtures/small_wait_bound.etl";
    private const string FixtureSha256 = "6f65fad5e6b25e1bf6d28c6ae0e27a002421df5a13f05692f9391e8fcbfcc95a";
    private const string StructuredTextMarker = "<JSON_TEXT_EQUALS_STRUCTURED_CONTENT>";

    private static readonly string[] ExpectedLegacyStructuredTools =
    [
        "diagnose_high_wait",
        "diagnose_window",
        "inspect_trace",
        "unload_trace",
        "wait_analysis",
    ];

    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions CompactJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ReviewedLegacyGolden_IsCompleteJsonAndLabelsKnownIncorrectEvidence()
    {
        var baselinePath = LegacyBaselinePath();
        var bytes = File.ReadAllBytes(baselinePath);
        Assert.NotEmpty(bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.Equal(
            "055be9ddde2c21effad7a9d3c27c6630977b600c7c4fc1be3f891392a662e2b7",
            Sha256(bytes));

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(JsonValueKind.Object, root.ValueKind);
        Assert.Equal("legacy-structured-stdio.v1", root.GetProperty("formatVersion").GetString());
        Assert.Equal(
            "compatibility_evidence_not_correctness_approval",
            root.GetProperty("contractDisposition").GetString());
        Assert.Contains(
            "corrected active runtime golden",
            root.GetProperty("migrationRule").GetString(),
            StringComparison.Ordinal);

        var defect = Assert.Single(root.GetProperty("knownIncorrectDefects").EnumerateArray());
        Assert.Equal("LEGACY-STDIO-SCHEMA-001", defect.GetProperty("id").GetString());
        Assert.Equal(
            "known_incorrect_must_change",
            defect.GetProperty("disposition").GetString());
        Assert.Equal(
            "active-structured-stdio.v1.json",
            defect.GetProperty("correctedGoldenName").GetString());

        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        Assert.Equal(
            ExpectedLegacyStructuredTools,
            tools.Select(tool => tool.GetProperty("name").GetString()));
        Assert.All(tools, tool =>
        {
            Assert.Equal(
                "success_result",
                tool.GetProperty("success").GetProperty("terminal").GetString());
            Assert.Equal(
                "tool_error_result",
                tool.GetProperty("failure").GetProperty("terminal").GetString());
            Assert.Equal(
                "success_result",
                tool.GetProperty("boundary").GetProperty("terminal").GetString());
        });
    }

    [Fact]
    public async Task ProductionStdioStructuredTools_MatchCorrectedActiveRuntimeGolden()
    {
        var actual = await BuildCanonicalSnapshotAsync();
        var baselinePath = ActiveBaselinePath();

        if (!File.Exists(baselinePath))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                $"The corrected production stdio runtime golden is missing: {baselinePath}. " +
                $"Review the captured candidate at {actualPath}, then add the baseline deliberately.");
        }

        var expected = File.ReadAllText(baselinePath, Encoding.UTF8)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            var actualPath = WriteMismatchArtifact(actual);
            Assert.Fail(
                "The corrected production stdio runtime contract changed. This golden is separate " +
                "from the immutable known-incorrect legacy evidence and the tools/list schema snapshot. " +
                $"Candidate: {actualPath}; baseline: {baselinePath}.");
        }
    }

    [Fact]
    public Task ProductionStdio_LoadTraceReturnsContract2TraceIdBeforeFullCatalogGolden()
        => RunProductionLoadTraceGateAsync(
            "wpa-mcp-load-trace-stdio-gate",
            "load-trace-contract2-gate",
            requireSuccess: true);

    [Fact]
    public Task ProductionStdio_LoadTraceBoundaryIsClosedWhenHostDeniesArtifactAcl()
        => RunProductionLoadTraceGateAsync(
            "wpa-mcp-load-trace-boundary-gate",
            "load-trace-boundary-gate",
            requireSuccess: false);

    private static async Task RunProductionLoadTraceGateAsync(
        string scenarioName,
        string requestId,
        bool requireSuccess)
    {
        var repoRoot = LocateRepoRoot();
        var sourceFixture = Path.Combine(
            repoRoot,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var scenarioRoot = Path.Combine(
            Path.GetTempPath(),
            scenarioName,
            Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(scenarioRoot, "source");
        var artifactRoot = Path.Combine(scenarioRoot, "trace-artifacts");
        Directory.CreateDirectory(sourceRoot);
        var tracePath = Path.Combine(sourceRoot, "fixture.etl");
        File.Copy(sourceFixture, tracePath);

        try
        {
            var catalog = ActiveToolCatalog.LoadAndValidate();
            var tools = catalog.CreateProtocolTools(new DeferredCatalogServiceProvider());
            var frameCap = ToolsListPageFitter.Preflight(
                tools,
                ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;
            var loadSchema = JsonNode.Parse(tools.Single(tool => tool.Name == "load_trace")
                .OutputSchema!.Value.GetRawText())!.AsObject();
            await using var client = await ProductionStdioClient.StartAsync(
                repoRoot,
                scenarioRoot,
                sourceRoot,
                artifactRoot,
                frameCap,
                privacyProfile: "off");
            _ = await client.InitializeAsync(ProtocolVersion);
            var arguments = new JsonObject { ["path"] = tracePath };
            var response = await client.SendToolCallAsync(
                requestId,
                "load_trace",
                arguments);
            var structured = RequireStructuredContent(response);
            var status = structured["status"]?.GetValue<string>();
            if (requireSuccess && status != "succeeded")
            {
                var sourceState = DescribeDirectoryState(sourceRoot);
                var artifactState = DescribeDirectoryState(artifactRoot);
                var failedExit = await client.CompleteAsync();
                Assert.Fail(
                    "Production load_trace did not reach the Contract 2.0 success path. " +
                    $"Fixture: {tracePath}. Source state: {sourceState}. " +
                    $"Artifact state: {artifactState}. Stderr: {failedExit.Stderr}. " +
                    $"Response: {response.ToJsonString(IndentedJson)}");
            }

            if (status == "succeeded")
            {
                var traceId = structured["data"]?["traceId"]?.GetValue<string>();
                Assert.NotNull(traceId);
                Assert.Matches("^trc_[0-9a-f]{32}$", traceId);
                Assert.NotEmpty(Directory.EnumerateFiles(
                    artifactRoot,
                    "*.etlx",
                    SearchOption.AllDirectories));
            }
            else
            {
                Assert.False(requireSuccess);
                Assert.Equal("failed", status);
                Assert.Equal(
                    "trace_access_denied",
                    structured["error"]?["code"]?.GetValue<string>());
                Assert.Null(structured["data"]);
                Assert.True(response["result"]?["isError"]?.GetValue<bool>());
                Assert.Empty(Directory.EnumerateFiles(
                    artifactRoot,
                    "*",
                    SearchOption.AllDirectories));
            }
            Assert.Equal(FixtureSha256, Sha256(File.ReadAllBytes(tracePath)));
            _ = CaptureCase(
                status == "succeeded"
                    ? "load_trace_contract2_success_gate"
                    : "load_trace_contract2_access_denied_gate",
                arguments,
                response,
                loadSchema,
                tracePath,
                scenarioRoot,
                repoRoot,
                expectedContractEnvelope: true);
            Assert.True(client.LastResponseFrameBytes <= frameCap);
            var exit = await client.CompleteAsync();
            Assert.Equal(0, exit.ExitCode);
            Assert.Equal(2, exit.StdoutLineCount);
        }
        finally
        {
            if (Directory.Exists(scenarioRoot))
                Directory.Delete(scenarioRoot, recursive: true);
        }
    }

    private static string DescribeDirectoryState(string path)
    {
        if (!Directory.Exists(path))
            return $"missing:{path}";

        var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.Ordinal)
            .Select(file =>
            {
                var info = new FileInfo(file);
                return $"{Path.GetRelativePath(path, file)}:{info.Length}";
            });
        return $"exists:{path}:[{string.Join(',', files)}]";
    }

    private static async Task<string> BuildCanonicalSnapshotAsync()
    {
        var repoRoot = LocateRepoRoot();
        var sourceFixture = Path.Combine(
            repoRoot,
            FixtureRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(sourceFixture), $"Missing stdio golden fixture: {sourceFixture}");
        Assert.Equal(FixtureSha256, Sha256(File.ReadAllBytes(sourceFixture)));

        var scenarioRoot = Path.Combine(
            Path.GetTempPath(),
            "wpa-mcp-structured-stdio-golden",
            Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(scenarioRoot, "source");
        var artifactRoot = Path.Combine(scenarioRoot, "trace-artifacts");
        var privacyArtifactRoot = Path.Combine(scenarioRoot, "privacy-trace-artifacts");
        Directory.CreateDirectory(sourceRoot);
        var tracePath = Path.Combine(sourceRoot, "fixture.etl");
        File.Copy(sourceFixture, tracePath);

        try
        {
            var runtimeCatalog = ActiveToolCatalog.LoadAndValidate();
            var runtimeTools = runtimeCatalog.CreateProtocolTools(
                new DeferredCatalogServiceProvider());
            var toolsListFrameCap = ToolsListPageFitter.Preflight(
                runtimeTools,
                ToolsListPaginationOptions.HardMaxResponseFrameBytes).MinimumViableFrameBytes;
            await using var client = await ProductionStdioClient.StartAsync(
                repoRoot,
                scenarioRoot,
                sourceRoot,
                artifactRoot,
                toolsListFrameCap,
                privacyProfile: "off");
            var initialize = await client.InitializeAsync(ProtocolVersion);
            var catalog = new JsonArray();
            var catalogPages = new JsonArray();
            string? catalogCursor = null;
            var catalogPageIndex = 0;
            do
            {
                var listResponse = await client.SendRequestAsync(
                    $"tools-list-{catalogPageIndex}",
                    RequestMethods.ToolsList,
                    catalogCursor is null
                        ? new JsonObject()
                        : new JsonObject { ["cursor"] = catalogCursor });
                var pageResult = RequireObject(listResponse, "result");
                var pageTools = RequireObject(pageResult, "tools");
                Assert.NotEmpty(pageTools);
                foreach (var tool in pageTools)
                    catalog.Add(tool!.DeepClone());

                catalogCursor = pageResult["nextCursor"]?.GetValue<string>();
                if (catalogCursor is not null)
                    Assert.Matches("^tlc_[0-9a-f]{32}$", catalogCursor);
                catalogPages.Add(new JsonObject
                {
                    ["pageIndex"] = catalogPageIndex,
                    ["returnedTools"] = pageTools.Count,
                    ["firstTool"] = pageTools[0]!["name"]!.GetValue<string>(),
                    ["lastTool"] = pageTools[^1]!["name"]!.GetValue<string>(),
                    ["hasMore"] = catalogCursor is not null,
                    ["frameBytes"] = client.LastResponseFrameBytes,
                });
                Assert.True(client.LastResponseFrameBytes <= toolsListFrameCap);
                catalogPageIndex++;
                Assert.InRange(catalogPageIndex, 1, runtimeTools.Count);
            }
            while (catalogCursor is not null);

            var aggregateListResult = new JsonObject { ["tools"] = catalog.DeepClone() };
            var listResultBytes = Encoding.UTF8.GetBytes(
                aggregateListResult.ToJsonString(CompactJson));
            var activeCatalog = LegacyActiveToolSnapshotBuilder.Build();
            Assert.Equal(activeCatalog.ToolCount, catalog.Count);
            Assert.Equal(activeCatalog.CatalogBytes, listResultBytes.Length);
            Assert.Equal(activeCatalog.CatalogSha256, Sha256(listResultBytes));
            var schemas = BuildStructuredSchemaMap(catalog);
            var inputSchemas = BuildInputSchemaMap(catalog);
            var activeToolNames = catalog
                .Select(node => node!["name"]!.GetValue<string>())
                .ToArray();
            Assert.Equal(activeToolNames, schemas.Keys.OrderBy(
                name => Array.IndexOf(activeToolNames, name)));
            Assert.Equal(activeToolNames, inputSchemas.Keys.OrderBy(
                name => Array.IndexOf(activeToolNames, name)));
            Assert.Equal(activeToolNames.Length, schemas.Count);

            var cases = activeToolNames.ToDictionary(
                name => name,
                _ => new List<JsonObject>(),
                StringComparer.Ordinal);

            async Task<JsonNode> Capture(
                string tool,
                string kind,
                string caseName,
                JsonObject arguments)
            {
                var response = await client.SendToolCallAsync($"{tool}-{kind}", tool, arguments);
                Assert.True(
                    client.LastResponseFrameBytes <= toolsListFrameCap,
                    $"{tool}/{kind} exceeded the configured complete-frame cap.");
                try
                {
                    cases[tool].Add(CaptureCase(
                        caseName,
                        arguments,
                        response,
                        schemas[tool],
                        tracePath,
                        scenarioRoot,
                        repoRoot,
                        expectedContractEnvelope: true));
                }
                catch (Exception exception)
                {
                    var diagnostic = WriteMismatchArtifact(new JsonObject
                    {
                        ["case"] = $"{tool}/{kind}",
                        ["wireSizes"] = BuildWireSizeDiagnostic(response),
                        ["response"] = response.DeepClone(),
                    }.ToJsonString(IndentedJson));
                    throw new InvalidOperationException(
                        $"Active stdio case '{tool}/{kind}' violated its wire contract. " +
                        $"Raw response: {diagnostic}",
                        exception);
                }
                return response;
            }

            await Capture(
                "list_capabilities",
                "primary",
                "complete_declared_capability_page",
                new JsonObject());
            var loadResponse = await Capture(
                "load_trace",
                "primary",
                "load_fixture_into_owned_artifact_store",
                new JsonObject { ["path"] = tracePath });
            var traceId = RequireStructuredContent(loadResponse)["data"]?["traceId"]?.GetValue<string>();
            if (traceId is null)
            {
                var failedExit = await client.CompleteAsync();
                throw new JsonException(
                    "load_trace did not return data.traceId. Stderr: " + failedExit.Stderr +
                    ". Response: " + loadResponse.ToJsonString(IndentedJson));
            }
            Assert.Matches("^trc_[0-9a-f]{32}$", traceId);

            var inspectResponse = await Capture(
                "inspect_trace",
                "primary",
                "inspect_loaded_fixture_by_trace_id",
                BuildPrimaryArguments("inspect_trace", inputSchemas["inspect_trace"], traceId, null, null));
            var durationUs = ReadExactInt64(
                    RequireStructuredContent(inspectResponse)["data"]?["trace"]?["durationUs"])
                ?? 499_998L;
            var processesResponse = await Capture(
                "list_processes",
                "primary",
                "list_loaded_fixture_process_instances",
                BuildPrimaryArguments("list_processes", inputSchemas["list_processes"], traceId, null, null));
            var firstProcess = RequireStructuredContent(processesResponse)["data"]?["rows"]?
                .AsArray()
                .FirstOrDefault()
                ?.AsObject()
                ?? throw new JsonException("The fixture contains no process rows.");
            var pid = firstProcess["pid"]?.GetValue<int>()
                ?? throw new JsonException("The first process row omitted pid.");
            var processStartUs = ReadExactInt64(firstProcess["startUs"]);

            foreach (var tool in activeToolNames)
            {
                if (tool is "list_capabilities" or "load_trace" or "inspect_trace" or
                    "list_processes" or "unload_trace")
                {
                    continue;
                }

                await Capture(
                    tool,
                    "primary",
                    "loaded_fixture_contract_case",
                    BuildPrimaryArguments(
                        tool,
                        inputSchemas[tool],
                        traceId,
                        pid,
                        processStartUs,
                        durationUs));
            }

            await Capture(
                "unload_trace",
                "primary",
                "retire_loaded_trace_handle",
                new JsonObject { ["traceId"] = traceId });

            const string unknownTraceId = "trc_11111111111111111111111111111111";
            foreach (var tool in activeToolNames)
            {
                var arguments = BuildFailureArguments(
                    tool,
                    inputSchemas[tool],
                    sourceRoot,
                    unknownTraceId,
                    pid,
                    processStartUs,
                    durationUs);
                await Capture(tool, "failure", "stable_failed_boundary", arguments);
            }

            var exit = await client.CompleteAsync();
            Assert.Equal(0, exit.ExitCode);
            Assert.Equal(1 + catalogPageIndex + activeToolNames.Length * 2, exit.StdoutLineCount);

            var tools = new JsonArray();
            foreach (var tool in activeToolNames)
            {
                var schemaBytes = Encoding.UTF8.GetBytes(schemas[tool].ToJsonString(CompactJson));
                tools.Add(new JsonObject
                {
                    ["name"] = tool,
                    ["outputSchemaBytes"] = schemaBytes.Length,
                    ["outputSchemaSha256"] = Sha256(schemaBytes),
                    ["cases"] = new JsonArray(cases[tool]
                        .Select(item => (JsonNode?)item)
                        .ToArray()),
                });
            }

            var privacyScenario = await CapturePrivacyScenarioAsync(
                repoRoot,
                scenarioRoot,
                sourceRoot,
                privacyArtifactRoot,
                tracePath,
                toolsListFrameCap,
                schemas["inspect_trace"]);

            var observedStatuses = cases.Values
                .SelectMany(toolCases => toolCases)
                .Select(item => item["contractStatus"]?.GetValue<string>())
                .Where(status => status is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(status => status, StringComparer.Ordinal)
                .ToArray();
            var noDataReasons = cases.Values
                .SelectMany(toolCases => toolCases)
                .Select(item => item["noDataReason"]?.GetValue<string>())
                .Where(reason => reason is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(reason => reason, StringComparer.Ordinal)
                .ToArray();
            var toolsWithHasMore = cases
                .Where(pair => pair.Value.Any(item => item["hasMore"]?.GetValue<bool>() == true))
                .Select(pair => pair.Key)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var normalizedInitialize = NormalizeNode(
                initialize,
                tracePath,
                scenarioRoot,
                repoRoot,
                propertyName: null);
            var snapshot = new JsonObject
            {
                ["formatVersion"] = "active-structured-stdio.v1",
                ["baselineKind"] = "corrected_active_production_stdio_runtime_golden",
                ["contractDisposition"] = "active_correctness_gate",
                ["correctsDefects"] = new JsonArray("WIRE-SCHEMA-001", "LEGACY-STDIO-SCHEMA-001"),
                ["fixture"] = new JsonObject
                {
                    ["repoRelativePath"] = FixtureRelativePath,
                    ["sha256"] = FixtureSha256,
                    ["bytes"] = new FileInfo(sourceFixture).Length,
                    ["runtimeCopy"] = "<TRACE_PATH>",
                },
                ["validationScope"] = new JsonObject
                {
                    ["catalogSchemaBaseline"] = "active-tools.v1.json",
                    ["legacyCatalogMigrationEvidence"] = "legacy-active-tools.v1.json",
                    ["runtimeGolden"] = "actual WpaMcp.Program production stdio JSON-RPC",
                    ["asserted"] = new JsonArray(
                        "newline-delimited JSON-RPC framing",
                        "complete paged Active Catalog traversal",
                        "every active tool has two schema-valid terminating Contract 2.0 wire cases",
                        "ID-only analysis uses load_trace and canonical TraceId",
                        "structuredContent presence for succeeded, partial, no-data, and failed outcomes when observed",
                        "text JSON semantic equality with structuredContent",
                        "recursive outputSchema required-property presence",
                        "recursive outputSchema nullability and JSON primitive/container types",
                        "array items and object additionalProperties schema traversal",
                        "complete response frame remains within the configured hard cap",
                        "strict privacy profile production-wire scenario"),
                    ["notClaimed"] = new JsonArray(
                        "evaluation of JSON Schema keywords outside the generated type/required/items/additionalProperties subset",
                        "that every terminal status is applicable to every tool",
                        "machine-specific performance or large-trace behavior",
                        "exact planner phase timings; planner phase durationUs values are normalized to zero",
                        "process-keyed privacy alias tokens; each token is normalized while preserving its sensitive-field kind"),
                    ["observedContractStatuses"] = new JsonArray(observedStatuses
                        .Select(status => (JsonNode?)JsonValue.Create(status))
                        .ToArray()),
                    ["observedNoDataReasons"] = new JsonArray(noDataReasons
                        .Select(reason => (JsonNode?)JsonValue.Create(reason))
                        .ToArray()),
                    ["toolsWithHasMore"] = new JsonArray(toolsWithHasMore
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray()),
                },
                ["transport"] = new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["profile"] = "stateful",
                    ["framing"] = "utf8_json_object_per_line",
                    ["productionEntryPoint"] = "WpaMcp.Program",
                    ["initializeResponse"] = normalizedInitialize,
                    ["toolsList"] = new JsonObject
                    {
                        ["toolCount"] = catalog.Count,
                        ["pageCount"] = catalogPageIndex,
                        ["configuredFrameCapBytes"] = toolsListFrameCap,
                        ["maximumObservedPageFrameBytes"] = catalogPages
                            .Select(page => page!["frameBytes"]!.GetValue<int>())
                            .Max(),
                        ["pages"] = catalogPages,
                        ["structuredToolNames"] = new JsonArray(
                            activeToolNames
                                .Select(name => (JsonNode?)JsonValue.Create(name))
                                .ToArray()),
                        ["resultBytes"] = listResultBytes.Length,
                        ["resultSha256"] = Sha256(listResultBytes),
                    },
                    ["stdoutLineCount"] = exit.StdoutLineCount,
                    ["allStdoutLinesWereJson"] = true,
                    ["exitCode"] = exit.ExitCode,
                },
                ["privacyScenario"] = privacyScenario,
                ["tools"] = tools,
            };

            return snapshot.ToJsonString(IndentedJson)
                .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
        }
        finally
        {
            if (Directory.Exists(scenarioRoot))
                Directory.Delete(scenarioRoot, recursive: true);
        }
    }

    private static JsonObject BuildPrimaryArguments(
        string tool,
        JsonObject inputSchema,
        string traceId,
        int? pid,
        long? processStartUs,
        long durationUs = 499_998)
    {
        var arguments = new JsonObject();
        var required = inputSchema["required"] as JsonArray ?? new JsonArray();
        foreach (var requiredNode in required)
        {
            var name = requiredNode?.GetValue<string>()
                ?? throw new JsonException($"{tool} has a non-string required input property.");
            arguments[name] = name switch
            {
                "path" or "traceId" => traceId,
                "focusFunction" or "function" => "__wpa_mcp_missing_focus__",
                "providerName" => "__wpa_mcp_missing_provider__",
                "nameSubstring" => "__wpa_mcp_missing_marker__",
                "pids" => new JsonArray(pid ?? int.MaxValue),
                "pid" or "parentPid" => pid ?? int.MaxValue,
                "startUs" => 0L,
                "endUs" => Math.Max(1L, durationUs),
                _ => throw new InvalidOperationException(
                    $"The active stdio candidate builder has no reviewed value for {tool}.{name}."),
            };
        }

        if (inputSchema["properties"] is JsonObject properties)
        {
            SetIfPresent(properties, arguments, "top", 1);
            SetIfPresent(properties, arguments, "maxCandidates", 1);
            SetIfPresent(properties, arguments, "topStacks", 1);
            SetIfPresent(properties, arguments, "topReadyStacks", 1);
            SetIfPresent(properties, arguments, "includeReadyStacks", false);
            SetIfPresent(properties, arguments, "timeBudgetMs", 100_000);
            if (processStartUs.HasValue && arguments.ContainsKey("pid"))
                SetIfPresent(properties, arguments, "processStartUs", processStartUs.Value);
            if (processStartUs.HasValue && tool == "cpu_top_functions_batch")
                SetIfPresent(properties, arguments, "processStartUs", new JsonArray(processStartUs.Value));
        }

        return arguments;
    }

    private static JsonObject BuildFailureArguments(
        string tool,
        JsonObject inputSchema,
        string sourceRoot,
        string unknownTraceId,
        int pid,
        long? processStartUs,
        long durationUs)
    {
        if (tool == "list_capabilities")
            return new JsonObject { ["cursor"] = "cpc_11111111111111111111111111111111" };
        if (tool == "load_trace")
            return new JsonObject { ["path"] = Path.Combine(sourceRoot, "missing.etl") };

        return BuildPrimaryArguments(
            tool,
            inputSchema,
            unknownTraceId,
            pid,
            processStartUs,
            durationUs);
    }

    private static void SetIfPresent(
        JsonObject properties,
        JsonObject arguments,
        string name,
        JsonNode? value)
    {
        if (properties.ContainsKey(name))
            arguments[name] = value;
    }

    private static JsonObject RequireStructuredContent(JsonNode response)
        => response["result"]?["structuredContent"]?.AsObject()
            ?? throw new JsonException("Production tool response omitted structuredContent.");

    private static long? ReadExactInt64(JsonNode? node)
    {
        if (node is null)
            return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var numeric))
                return numeric;
            if (value.TryGetValue<string>(out var text) &&
                long.TryParse(
                    text,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsed))
            {
                return parsed;
            }
        }
        throw new JsonException("Expected an exact signed 64-bit integer string or number.");
    }

    private static JsonObject BuildWireSizeDiagnostic(JsonNode response)
    {
        var structured = RequireStructuredContent(response);
        var structuredJson = structured.ToJsonString(CompactJson);
        var fullTextProjection = response.DeepClone();
        var fullTextItems = fullTextProjection["result"]?["content"]?.AsArray()
            ?? throw new JsonException("Tool result omitted content while computing wire sizes.");
        var fullTextItem = fullTextItems
            .Select(Assert.IsType<JsonObject>)
            .Single(item => item["type"]?.GetValue<string>() == "text");
        fullTextItem["text"] = structuredJson;

        var structuredOnlyProjection = response.DeepClone();
        structuredOnlyProjection["result"]!["content"] = new JsonArray();
        return new JsonObject
        {
            ["currentCompleteFrameBytes"] = JsonLineBytes(response),
            ["structuredContentChars"] = structuredJson.Length,
            ["structuredContentUtf8Bytes"] = Encoding.UTF8.GetByteCount(structuredJson),
            ["fullJsonTextAndStructuredFrameBytes"] = JsonLineBytes(fullTextProjection),
            ["structuredOnlyEmptyContentFrameBytes"] = JsonLineBytes(structuredOnlyProjection),
        };
    }

    private static int JsonLineBytes(JsonNode node)
        => checked(Encoding.UTF8.GetByteCount(node.ToJsonString(CompactJson)) + 1);

    private static async Task<JsonObject> CapturePrivacyScenarioAsync(
        string repoRoot,
        string scenarioRoot,
        string sourceRoot,
        string artifactRoot,
        string tracePath,
        int frameCap,
        JsonObject inspectSchema)
    {
        await using var client = await ProductionStdioClient.StartAsync(
            repoRoot,
            scenarioRoot,
            sourceRoot,
            artifactRoot,
            frameCap,
            privacyProfile: "strict");
        _ = await client.InitializeAsync(ProtocolVersion);
        var load = await client.SendToolCallAsync(
            "privacy-load",
            "load_trace",
            new JsonObject { ["path"] = tracePath });
        var traceId = RequireStructuredContent(load)["data"]?["traceId"]?.GetValue<string>()
            ?? throw new JsonException("Strict-profile load_trace omitted traceId.");
        var inspectArguments = new JsonObject { ["path"] = traceId };
        var inspect = await client.SendToolCallAsync(
            "privacy-inspect",
            "inspect_trace",
            inspectArguments);
        Assert.True(client.LastResponseFrameBytes <= frameCap);
        var evidence = CaptureCase(
            "strict_privacy_profile",
            inspectArguments,
            inspect,
            inspectSchema,
            tracePath,
            scenarioRoot,
            repoRoot,
            expectedContractEnvelope: true);
        var exit = await client.CompleteAsync();
        Assert.Equal(0, exit.ExitCode);
        Assert.Equal(3, exit.StdoutLineCount);
        evidence["profile"] = "strict";
        evidence["exitCode"] = exit.ExitCode;
        return evidence;
    }

    private static SortedDictionary<string, JsonObject> BuildStructuredSchemaMap(JsonArray tools)
    {
        var result = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in tools)
        {
            var tool = Assert.IsType<JsonObject>(node);
            if (tool["outputSchema"] is not JsonObject schema)
                continue;

            var name = tool["name"]?.GetValue<string>()
                ?? throw new JsonException("Catalog tool omitted name.");
            result.Add(name, (JsonObject)schema.DeepClone());
        }

        return result;
    }

    private static SortedDictionary<string, JsonObject> BuildInputSchemaMap(JsonArray tools)
    {
        var result = new SortedDictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var node in tools)
        {
            var tool = Assert.IsType<JsonObject>(node);
            var name = tool["name"]?.GetValue<string>()
                ?? throw new JsonException("Catalog tool omitted name.");
            var schema = Assert.IsType<JsonObject>(tool["inputSchema"]);
            result.Add(name, (JsonObject)schema.DeepClone());
        }

        return result;
    }

    private static JsonObject CaptureCase(
        string caseName,
        JsonObject arguments,
        JsonNode response,
        JsonObject outputSchema,
        string tracePath,
        string tempRoot,
        string repoRoot,
        bool expectedContractEnvelope)
    {
        IReadOnlyList<string>? recursiveSchemaViolations = null;
        if (expectedContractEnvelope &&
            response["result"] is JsonObject rawResult)
        {
            var rawStructured = Assert.IsType<JsonObject>(rawResult["structuredContent"]);
            recursiveSchemaViolations = ToolWireSchemaValidator
                .Validate(rawStructured, outputSchema)
                .Select(failure => $"{failure.Path}: {failure.Message}")
                .ToArray();
            Assert.Empty(recursiveSchemaViolations);
        }

        var normalized = NormalizeNode(
            response,
            tracePath,
            tempRoot,
            repoRoot,
            propertyName: null);
        var normalizedObject = Assert.IsType<JsonObject>(normalized);
        Assert.Equal("2.0", normalizedObject["jsonrpc"]?.GetValue<string>());
        NormalizeEmbeddedJsonText(
            normalizedObject,
            tracePath,
            tempRoot,
            repoRoot);

        var compactFrame = normalizedObject.ToJsonString(CompactJson);
        var compactFrameBytes = Encoding.UTF8.GetBytes(compactFrame);
        var baselineResponse = (JsonObject)normalizedObject.DeepClone();
        var terminal = "unknown";
        var textMatchesStructured = false;
        IReadOnlyList<string>? missingTopLevelRequiredProperties = null;

        if (baselineResponse["result"] is JsonObject result)
        {
            var isError = result["isError"]?.GetValue<bool>() == true;
            terminal = isError ? "tool_error_result" : "success_result";
            if (expectedContractEnvelope)
            {
                var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
                missingTopLevelRequiredProperties = MissingTopLevelRequiredProperties(
                    structured,
                    outputSchema);

                var content = Assert.IsType<JsonArray>(result["content"]);
                var textItems = content
                    .Select(Assert.IsType<JsonObject>)
                    .Where(item => item["type"]?.GetValue<string>() == "text")
                    .ToArray();
                var textItem = Assert.Single(textItems);
                var text = textItem["text"]?.GetValue<string>()
                    ?? throw new JsonException("Structured success omitted text content.");
                var textJson = JsonNode.Parse(text)
                    ?? throw new JsonException("Structured success text was not JSON.");
                textMatchesStructured = JsonNode.DeepEquals(textJson, structured);
                if (!textMatchesStructured)
                {
                    var mismatchPath = WriteMismatchArtifact(new JsonObject
                    {
                        ["textJson"] = textJson.DeepClone(),
                        ["structuredContent"] = structured.DeepClone(),
                    }.ToJsonString(IndentedJson));
                    Assert.Fail(
                        $"Structured success text disagrees with structuredContent. Diagnostic: {mismatchPath}");
                }
                textItem["text"] = StructuredTextMarker;
            }
            else
            {
                Assert.True(isError, "Expected a terminal tool failure result.");
            }
        }
        else if (baselineResponse["error"] is JsonObject)
        {
            terminal = "jsonrpc_error";
            Assert.False(expectedContractEnvelope, "Expected a contract envelope, received JSON-RPC error.");
        }
        else
        {
            Assert.Fail("Production response had neither result nor error.");
        }

        var normalizedArguments = Assert.IsType<JsonObject>(NormalizeNode(
            arguments,
            tracePath,
            tempRoot,
            repoRoot,
            propertyName: null));
        return new JsonObject
        {
            ["case"] = caseName,
            ["arguments"] = normalizedArguments,
            ["terminal"] = terminal,
            ["contractStatus"] = baselineResponse["result"]?["structuredContent"]?["status"]?.DeepClone(),
            ["noDataReason"] = baselineResponse["result"]?["structuredContent"]?["noData"]?["reason"]?.DeepClone(),
            ["hasMore"] = baselineResponse["result"]?["structuredContent"]?["hasMore"]?.DeepClone(),
            ["textJsonEqualsStructuredContent"] = expectedContractEnvelope
                ? textMatchesStructured
                : null,
            ["topLevelRequiredPropertiesPresent"] = expectedContractEnvelope
                ? missingTopLevelRequiredProperties!.Count == 0
                : null,
            ["missingTopLevelRequiredProperties"] = expectedContractEnvelope
                ? new JsonArray(missingTopLevelRequiredProperties!
                    .Select(name => (JsonNode?)JsonValue.Create(name))
                    .ToArray())
                : null,
            ["recursiveSchemaValid"] = expectedContractEnvelope
                ? recursiveSchemaViolations!.Count == 0
                : null,
            ["recursiveSchemaViolations"] = expectedContractEnvelope
                ? new JsonArray(recursiveSchemaViolations!
                    .Select(violation => (JsonNode?)JsonValue.Create(violation))
                    .ToArray())
                : null,
            ["normalizedCompactFrameBytes"] = compactFrameBytes.Length,
            ["normalizedCompactFrameSha256"] = Sha256(compactFrameBytes),
            ["response"] = expectedContractEnvelope
                ? BuildStructuredSuccessEvidence(baselineResponse)
                : baselineResponse,
        };
    }

    private static JsonObject BuildStructuredSuccessEvidence(JsonObject response)
    {
        var result = Assert.IsType<JsonObject>(response["result"]);
        var structured = Assert.IsType<JsonObject>(result["structuredContent"]);
        var structuredBytes = Encoding.UTF8.GetBytes(structured.ToJsonString(CompactJson));
        var sections = new JsonArray();
        foreach (var property in structured)
        {
            var value = property.Value;
            var payload = value?.ToJsonString(CompactJson) ?? "null";
            var payloadBytes = Encoding.UTF8.GetBytes(payload);
            sections.Add(new JsonObject
            {
                ["name"] = property.Key,
                ["kind"] = value?.GetValueKind().ToString().ToLowerInvariant() ?? "null",
                ["itemCount"] = value is JsonArray array ? array.Count : null,
                ["propertyCount"] = value is JsonObject obj ? obj.Count : null,
                ["bytes"] = payloadBytes.Length,
                ["sha256"] = Sha256(payloadBytes),
            });
        }

        var content = Assert.IsType<JsonArray>(result["content"]);
        var contentShape = new JsonArray(content
            .Select(item =>
            {
                var obj = Assert.IsType<JsonObject>(item);
                return (JsonNode?)new JsonObject
                {
                    ["properties"] = new JsonArray(obj
                        .Select(property => (JsonNode?)JsonValue.Create(property.Key))
                        .ToArray()),
                    ["type"] = obj["type"]?.DeepClone(),
                    ["text"] = obj["text"]?.DeepClone(),
                };
            })
            .ToArray());

        return new JsonObject
        {
            ["jsonrpc"] = response["jsonrpc"]?.DeepClone(),
            ["id"] = response["id"]?.DeepClone(),
            ["responseProperties"] = new JsonArray(response
                .Select(property => (JsonNode?)JsonValue.Create(property.Key))
                .ToArray()),
            ["resultProperties"] = new JsonArray(result
                .Select(property => (JsonNode?)JsonValue.Create(property.Key))
                .ToArray()),
            ["contentShape"] = contentShape,
            ["structuredContentBytes"] = structuredBytes.Length,
            ["structuredContentSha256"] = Sha256(structuredBytes),
            ["structuredTopLevelSections"] = sections,
            ["selectedFacts"] = BuildSelectedFacts(structured),
        };
    }

    private static JsonObject BuildSelectedFacts(JsonObject structured)
    {
        var facts = new JsonObject();
        foreach (var path in SelectedFactPaths)
        {
            if (TryResolvePath(structured, path, out var value))
                facts[path] = value?.DeepClone();
        }

        foreach (var property in structured)
        {
            if (property.Value is JsonArray array)
                facts[$"{property.Key}.$count"] = array.Count;
        }
        return facts;
    }

    private static bool TryResolvePath(JsonObject root, string path, out JsonNode? value)
    {
        value = root;
        foreach (var segment in path.Split('.'))
        {
            if (value is not JsonObject obj || !obj.TryGetPropertyValue(segment, out value))
                return false;
        }
        return true;
    }

    private static readonly string[] SelectedFactPaths =
    [
        "contractVersion",
        "status",
        "error.code",
        "error.retryable",
        "hasMore",
        "scope.status",
        "scope.mode",
        "scope.pidReuseObserved",
        "scope.identityUnresolved",
        "completeness.status",
        "completeness.requestedSectionCount",
        "completeness.sectionsWithData",
        "completeness.failedSectionCount",
        "completeness.hasMore",
        "noData.reason",
        "noData.boundaryCode",
        "trace.durationUs",
        "trace.eventCount",
        "trace.eventsLost",
        "trace.processCount",
        "capabilities.hasCSwitch",
        "capabilities.hasCSwitchStacks",
        "capabilities.hasReadyThread",
        "capabilities.hasReadyThreadStacks",
        "symbolQuality.frameResolutionMeasurementState",
        "windowStartUs",
        "windowEndUs",
        "durationUs",
        "pid",
        "totalBlockedUs",
        "scopeMode",
        "pidReuseObserved",
        "scopeStatus",
        "capabilityStatus",
        "matchedEventCount",
        "matchedIntervalCount",
        "noDataReason",
        "cacheEntryRetired",
        "nextLoadForcesEtlxRefresh",
        "refreshRequestedForCurrentServerProcess",
        "refreshRequestLifetime",
    ];

    private static void NormalizeEmbeddedJsonText(
        JsonObject response,
        string tracePath,
        string tempRoot,
        string repoRoot)
    {
        if (response["result"]?["content"] is not JsonArray content)
            return;

        foreach (var item in content.OfType<JsonObject>())
        {
            if (item["type"]?.GetValue<string>() != "text" ||
                item["text"]?.GetValue<string>() is not { } text)
            {
                continue;
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                continue;
            }

            if (parsed is null)
                continue;
            item["text"] = NormalizeNode(
                parsed,
                tracePath,
                tempRoot,
                repoRoot,
                propertyName: null).ToJsonString(CompactJson);
        }
    }

    private static IReadOnlyList<string> MissingTopLevelRequiredProperties(
        JsonObject structured,
        JsonObject outputSchema)
    {
        Assert.Equal("object", outputSchema["type"]?.GetValue<string>());
        var required = Assert.IsType<JsonArray>(outputSchema["required"]);
        return required
            .Select(property => property?.GetValue<string>()
                ?? throw new JsonException("Output schema required entry was not a string."))
            .Where(name => !structured.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ValidateGeneratedSchema(
        JsonNode? value,
        JsonObject schema)
    {
        var violations = new List<string>();
        ValidateGeneratedSchema(value, schema, schema, "$", violations);
        return violations;
    }

    private static void ValidateGeneratedSchema(
        JsonNode? value,
        JsonObject schema,
        JsonObject rootSchema,
        string path,
        List<string> violations)
    {
        schema = ResolveLocalReference(schema, rootSchema);
        var allowedTypes = SchemaTypes(schema);
        if (allowedTypes.Count != 0 && !allowedTypes.Any(type => NodeMatchesType(value, type)))
        {
            violations.Add(
                $"{path}: expected [{string.Join(',', allowedTypes)}], " +
                $"observed {NodeType(value)}");
            return;
        }

        if (value is null)
            return;

        if (value is JsonObject obj)
        {
            if (schema["required"] is JsonArray required)
            {
                foreach (var requiredNode in required)
                {
                    var requiredName = requiredNode?.GetValue<string>()
                        ?? throw new JsonException("Output schema required entry was not a string.");
                    if (!obj.ContainsKey(requiredName))
                        violations.Add($"{path}.{requiredName}: missing required property");
                }
            }

            var properties = schema["properties"] as JsonObject;
            var additionalProperties = schema["additionalProperties"] as JsonObject;
            foreach (var property in obj)
            {
                var propertySchema = properties?[property.Key] as JsonObject
                    ?? additionalProperties;
                if (propertySchema is not null)
                {
                    ValidateGeneratedSchema(
                        property.Value,
                        propertySchema,
                        rootSchema,
                        $"{path}.{property.Key}",
                        violations);
                }
            }

            return;
        }

        if (value is JsonArray array && schema["items"] is JsonObject itemSchema)
        {
            for (var index = 0; index < array.Count; index++)
            {
                ValidateGeneratedSchema(
                    array[index],
                    itemSchema,
                    rootSchema,
                    $"{path}[{index}]",
                    violations);
            }
        }
    }

    private static JsonObject ResolveLocalReference(
        JsonObject schema,
        JsonObject rootSchema)
    {
        if (schema["$ref"]?.GetValue<string>() is not { } reference)
            return schema;
        if (!reference.StartsWith("#/", StringComparison.Ordinal))
            throw new JsonException($"Only local output schema references are supported: {reference}");

        JsonNode? resolved = rootSchema;
        foreach (var encodedSegment in reference[2..].Split('/'))
        {
            var segment = encodedSegment
                .Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            resolved = (resolved as JsonObject)?[segment]
                ?? throw new JsonException($"Output schema $ref could not be resolved: {reference}");
        }

        return Assert.IsType<JsonObject>(resolved);
    }

    private static IReadOnlyList<string> FindUnsupportedCompositionKeywords(
        JsonNode node,
        string path = "$")
    {
        var findings = new List<string>();
        if (node is JsonObject obj)
        {
            foreach (var property in obj)
            {
                var childPath = $"{path}.{property.Key}";
                if (property.Key is "oneOf" or "anyOf" or "allOf")
                    findings.Add(childPath);
                if (property.Value is not null)
                    findings.AddRange(FindUnsupportedCompositionKeywords(property.Value, childPath));
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                if (array[index] is { } item)
                    findings.AddRange(FindUnsupportedCompositionKeywords(item, $"{path}[{index}]"));
            }
        }

        return findings;
    }

    private static IReadOnlyList<string> SchemaTypes(JsonObject schema)
        => schema["type"] switch
        {
            JsonValue value => new[]
            {
                value.GetValue<string>(),
            },
            JsonArray array => array
                .Select(node => node?.GetValue<string>()
                    ?? throw new JsonException("Output schema type entry was not a string."))
                .ToArray(),
            null => Array.Empty<string>(),
            _ => throw new JsonException("Output schema type was neither a string nor an array."),
        };

    private static bool NodeMatchesType(JsonNode? value, string schemaType)
        => schemaType switch
        {
            "null" => value is null,
            "object" => value is JsonObject,
            "array" => value is JsonArray,
            "string" => value?.GetValueKind() == JsonValueKind.String,
            "boolean" => value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
            "integer" => value is JsonValue integer && integer.TryGetValue<long>(out _),
            "number" => value?.GetValueKind() == JsonValueKind.Number,
            _ => true,
        };

    private static string NodeType(JsonNode? value)
        => value?.GetValueKind().ToString().ToLowerInvariant() ?? "null";

    private static JsonNode NormalizeNode(
        JsonNode node,
        string tracePath,
        string tempRoot,
        string repoRoot,
        string? propertyName)
    {
        if (node is JsonObject obj)
        {
            var copy = new JsonObject();
            foreach (var property in obj)
            {
                copy[property.Key] = property.Value is null
                    ? null
                    : NormalizeNode(
                        property.Value,
                        tracePath,
                        tempRoot,
                        repoRoot,
                        property.Key);
            }

            // Planner phase timings are deliberately current-call stopwatch
            // measurements. Preserve their phase names and integer schema shape,
            // but do not turn machine scheduling noise into a wire-contract golden.
            if (obj["phase"]?.GetValueKind() == JsonValueKind.String &&
                obj["durationUs"] is JsonNode duration &&
                duration.GetValueKind() is JsonValueKind.Number or JsonValueKind.String &&
                obj.Count == 2)
            {
                copy["durationUs"] = duration.GetValueKind() == JsonValueKind.String
                    ? JsonValue.Create("0")
                    : JsonValue.Create(0);
            }
            return copy;
        }

        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (var item in array)
                copy.Add(item is null
                    ? null
                    : NormalizeNode(item, tracePath, tempRoot, repoRoot, propertyName));
            return copy;
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (IsOpaqueLocator(text, "trc_"))
                return JsonValue.Create("<TRACE_ID>")!;
            if (IsOpaqueLocator(text, "sym_"))
                return JsonValue.Create("<SYMBOL_CONTEXT_ID>")!;
            if (IsOpaqueLocator(text, "cpc_"))
                return JsonValue.Create("<CAPABILITY_CURSOR>")!;
            if (IsOpaqueLocator(text, "qrc_"))
                return JsonValue.Create("<QUERY_CURSOR>")!;
            if (IsOpaqueLocator(text, "tlc_"))
                return JsonValue.Create("<TIMELINE_CURSOR>")!;
            if (IsOpaqueLocator(text, "tgen_"))
                return JsonValue.Create("<TRACE_GENERATION_ID>")!;
            if (string.Equals(propertyName, "cacheDir", StringComparison.OrdinalIgnoreCase))
                return JsonValue.Create("<SYMBOL_CACHE_DIR>")!;
            if (string.Equals(propertyName, "machineName", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(text))
            {
                return JsonValue.Create("<FIXTURE_MACHINE_NAME>")!;
            }

            return JsonValue.Create(NormalizeString(text, tracePath, tempRoot, repoRoot))!;
        }

        return node.DeepClone();
    }

    private static bool IsOpaqueLocator(string value, string prefix)
    {
        if (value.Length != prefix.Length + 32 ||
            !value.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var character in value.AsSpan(prefix.Length))
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private static string NormalizeString(
        string value,
        string tracePath,
        string tempRoot,
        string repoRoot)
    {
        value = NormalizePrivacyAliases(value);
        value = ReplacePath(value, tracePath, "<TRACE_PATH>");
        value = ReplacePath(value, tempRoot, "<TRACE_DIR>");
        return ReplacePath(value, repoRoot, "<REPO_ROOT>");
    }

    private static string NormalizePrivacyAliases(string value)
    {
        foreach (var kind in Enum.GetValues<SensitiveFieldKind>())
        {
            var kindToken = SensitiveFieldKinds.Token(kind);
            var prefix = "alias_" + kindToken + "_";
            var searchFrom = 0;
            while (true)
            {
                var start = value.IndexOf(prefix, searchFrom, StringComparison.Ordinal);
                if (start < 0)
                    break;

                var tokenStart = start + prefix.Length;
                const int tokenLength = 22;
                if (tokenStart + tokenLength > value.Length ||
                    value.AsSpan(tokenStart, tokenLength).ContainsAnyExcept(
                        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"))
                {
                    searchFrom = tokenStart;
                    continue;
                }

                var marker = "<PRIVACY_ALIAS:" + kindToken + ">";
                value = string.Concat(
                    value.AsSpan(0, start),
                    marker,
                    value.AsSpan(tokenStart + tokenLength));
                searchFrom = start + marker.Length;
            }
        }

        return value;
    }

    private static string ReplacePath(string value, string path, string replacement)
    {
        value = value.Replace(path, replacement, StringComparison.OrdinalIgnoreCase);
        var forward = path.Replace('\\', '/');
        return value.Replace(forward, replacement, StringComparison.OrdinalIgnoreCase);
    }

    private static JsonArray RequireObject(JsonObject parent, string name)
        => Assert.IsType<JsonArray>(parent[name]);

    private static JsonObject RequireObject(JsonNode parent, string name)
        => Assert.IsType<JsonObject>(parent[name]);

    private static string WriteMismatchArtifact(string actual)
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"wpa-mcp-active-structured-stdio-{Guid.NewGuid():N}.actual.json");
        File.WriteAllText(path, actual, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
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

    private static string LegacyBaselinePath() => Path.Combine(
        LocateRepoRoot(),
        "tests",
        "WpaMcp.Tests",
        "ContractBaselines",
        "legacy-structured-stdio.v1.json");

    private static string ActiveBaselinePath() => Path.Combine(
        LocateRepoRoot(),
        "tests",
        "WpaMcp.Tests",
        "ContractBaselines",
        "active-structured-stdio.v1.json");

    private static string Sha256(byte[] payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private sealed class ProductionStdioClient : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly CancellationTokenSource _timeout;
        private readonly Task<string> _stderrTask;
        private int _stdoutLineCount;
        private bool _completed;

        private ProductionStdioClient(Process process)
        {
            _process = process;
            _timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            _stderrTask = process.StandardError.ReadToEndAsync(_timeout.Token);
        }

        internal static Task<ProductionStdioClient> StartAsync(
            string repoRoot,
            string workingDirectory,
            string sourceRoot,
            string artifactRoot,
            int toolsListFrameCap,
            string privacyProfile)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
                ?? throw new InvalidOperationException("Could not determine test build configuration.");
            var serverAssembly = Path.Combine(
                repoRoot,
                "src",
                "WpaMcp",
                "bin",
                configuration,
                "net10.0",
                "WpaMcp.dll");
            Assert.True(File.Exists(serverAssembly), $"Production server assembly is missing: {serverAssembly}");

            var startInfo = new ProcessStartInfo
            {
                FileName = LocateCurrentDotNetHost(),
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add(serverAssembly);
            startInfo.Environment["WPAMCP_TELEMETRY"] = "0";
            startInfo.Environment["WPAMCP_CONTRACT_MODE"] = ToolContractVersions.V2;
            startInfo.Environment[
                ToolsListPaginationOptions.MaxResponseFrameBytesEnvironmentVariable] =
                toolsListFrameCap.ToString(System.Globalization.CultureInfo.InvariantCulture);
            startInfo.Environment[TraceRuntimeOptions.AccessModeEnvironmentVariable] = "id_only";
            startInfo.Environment[TraceRuntimeOptions.AllowedRootsEnvironmentVariable] = sourceRoot;
            startInfo.Environment[TraceRuntimeOptions.ArtifactRootEnvironmentVariable] = artifactRoot;
            startInfo.Environment[ToolPrivacyOptions.EnvironmentVariable] = privacyProfile;
            startInfo.Environment.Remove("_NT_SYMBOL_PATH");

            var process = new Process { StartInfo = startInfo };
            Assert.True(process.Start());
            return Task.FromResult(new ProductionStdioClient(process));
        }

        internal async Task<JsonNode> InitializeAsync(string protocolVersion)
        {
            var response = await SendRequestAsync(
                "initialize",
                "initialize",
                new JsonObject
                {
                    ["protocolVersion"] = protocolVersion,
                    ["capabilities"] = new JsonObject(),
                    ["clientInfo"] = new JsonObject
                    {
                        ["name"] = "legacy-structured-stdio-golden",
                        ["version"] = "1.0",
                    },
                });
            Assert.Equal(
                protocolVersion,
                response["result"]?["protocolVersion"]?.GetValue<string>());
            await SendNotificationAsync("notifications/initialized", new JsonObject());
            return response;
        }

        internal Task<JsonNode> SendToolCallAsync(
            string id,
            string tool,
            JsonObject arguments)
            => SendRequestAsync(
                id,
                "tools/call",
                new JsonObject
                {
                    ["name"] = tool,
                    ["arguments"] = arguments.DeepClone(),
                });

        internal async Task<JsonNode> SendRequestAsync(
            string id,
            string method,
            JsonObject parameters)
        {
            await SendMessageAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters,
            });

            while (true)
            {
                var line = await _process.StandardOutput.ReadLineAsync(_timeout.Token)
                    ?? throw new EndOfStreamException(
                        $"Production server closed stdout before response '{id}'. Stderr: {await _stderrTask}");
                _stdoutLineCount++;
                var response = JsonNode.Parse(line)
                    ?? throw new JsonException("Production server emitted an empty stdout JSON line.");
                if (response["id"]?.GetValue<string>() == id)
                {
                    LastResponseFrameBytes = Encoding.UTF8.GetByteCount(line) + 1;
                    return response;
                }
            }
        }

        internal int LastResponseFrameBytes { get; private set; }

        internal Task SendNotificationAsync(string method, JsonObject parameters)
            => SendMessageAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = method,
                ["params"] = parameters,
            });

        internal async Task<ProcessExit> CompleteAsync()
        {
            if (_completed)
                throw new InvalidOperationException("Production stdio client already completed.");
            _completed = true;
            _process.StandardInput.Close();
            await _process.WaitForExitAsync(_timeout.Token);
            var stderr = await _stderrTask;
            return new ProcessExit(_process.ExitCode, _stdoutLineCount, stderr);
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
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString(CompactJson) + "\n");
            await _process.StandardInput.BaseStream.WriteAsync(bytes, _timeout.Token);
            await _process.StandardInput.BaseStream.FlushAsync(_timeout.Token);
        }

        private static string LocateCurrentDotNetHost()
        {
            var configured = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
                return configured;

            var runtimeDirectory = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory());
            var dotnetRoot = runtimeDirectory.Parent?.Parent?.Parent?.FullName;
            var candidate = dotnetRoot is null
                ? null
                : Path.Combine(dotnetRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            if (candidate is not null && File.Exists(candidate))
                return candidate;

            throw new FileNotFoundException(
                "Could not locate the dotnet host that is running the production stdio golden test.");
        }
    }

    private sealed record ProcessExit(int ExitCode, int StdoutLineCount, string Stderr);
}
