using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;
using Xunit.Abstractions;

namespace WpaMcp.Tests;

public sealed class LlmOverclaimAdversarialGateTests
{
    private const string BenchmarkPath = "benchmarks/llm-overclaim-adversarial.v1.json";
    private const int MaximumRouteCallsPerScenario = 2;

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly JsonSerializerOptions ReportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly IReadOnlyDictionary<string, string[]> ExpectedScenarios =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["pid-reuse-process-instance"] = ["COR-SCOPE-001"],
            ["target-domain-no-stacks"] = ["COR-STACK-001"],
            ["symbol-frame-resolution-unmeasured"] = ["COR-SYMBOL-001"],
            ["symbol-frame-resolution-low"] = ["COR-SYMBOL-001"],
            ["security-scan-heuristic"] = ["COR-HEURISTIC-001"],
            ["response-budget-partial-visible"] = ["COR-PAGING-001"],
            ["response-budget-terminal"] = ["COR-PAGING-001"],
            ["unsafe-64-bit-identifier"] = ["COR-ID-001"],
            ["trace-without-mcp-self-attribution"] = ["COR-ATTRIBUTION-001"],
            ["empty-result-no-data-reason"] = ["COR-NODATA-001"],
        };

    private readonly ITestOutputHelper _output;

    public LlmOverclaimAdversarialGateTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Manifest_IsVersionedFrozenAndAuditable()
    {
        var (suite, raw) = LoadSuite();

        Assert.Equal("llm-overclaim-adversarial.v1", suite.SchemaVersion);
        Assert.Equal("1.0.0", suite.SuiteVersion);
        Assert.Equal("deterministic_contract_proxy", suite.EvaluationKind);
        Assert.Equal("none", suite.ModelExecution);
        Assert.Contains("not a named-model quality evaluation", suite.ClaimScope, StringComparison.Ordinal);
        Assert.DoesNotContain("GPT", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Claude", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Gemini", raw, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(0, suite.ScoringContract.Thresholds.MaximumWrongToolProxyRate);
        Assert.Equal(0, suite.ScoringContract.Thresholds.MaximumOverclaimProxyRate);
        Assert.Equal(0, suite.ScoringContract.Thresholds.MaximumMissingCaveatProxyRate);
        Assert.Equal(2, suite.ScoringContract.Thresholds.MaximumMeanDeclaredRouteCallsProxy);
        Assert.Equal(
            new[]
            {
                "meanDeclaredRouteCallsProxy",
                "missingCaveatProxyRate",
                "overclaimProxyRate",
                "wrongToolProxyRate",
            },
            suite.ScoringContract.MetricDefinitions.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(ExpectedScenarios.Keys, suite.Scenarios.Select(scenario => scenario.ScenarioId));
        Assert.Equal(57, suite.Scenarios.Sum(scenario => scenario.EvidenceRequirements.Count));
        Assert.Equal(20, suite.Scenarios.Sum(scenario => scenario.ForbiddenConclusions.Count));
        Assert.Equal(10, suite.Scenarios.Sum(scenario => scenario.RequiredCaveats.Count));
        Assert.All(suite.Scenarios, scenario =>
        {
            Assert.Equal(ExpectedScenarios[scenario.ScenarioId], scenario.IssueIds);
            Assert.NotEmpty(scenario.Condition);
            Assert.NotEmpty(scenario.ExpectedTools);
            Assert.InRange(scenario.ExpectedTools.Count, 1, MaximumRouteCallsPerScenario);
            Assert.Equal(
                scenario.ExpectedTools.Count,
                scenario.ExpectedTools.Distinct(StringComparer.Ordinal).Count());
            Assert.NotEmpty(scenario.EvidenceRequirements);
            Assert.NotEmpty(scenario.ForbiddenConclusions);
            Assert.NotEmpty(scenario.RequiredCaveats);

            var requirementIds = scenario.EvidenceRequirements
                .Select(requirement => requirement.RequirementId)
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(scenario.EvidenceRequirements.Count, requirementIds.Count);
            Assert.All(scenario.EvidenceRequirements, requirement =>
            {
                Assert.Contains(requirement.ToolName, scenario.ExpectedTools);
                Assert.Contains(requirement.Surface, AllowedSurfaces);
                Assert.NotEmpty(requirement.Selector);
                if (requirement.Surface == "contract")
                {
                    Assert.Contains(requirement.Selector, AllowedContractSelectors);
                    Assert.NotEmpty(requirement.ExpectedValues);
                }
                else
                {
                    Assert.Empty(requirement.ExpectedValues);
                }
            });

            var referencedRequirements = new HashSet<string>(StringComparer.Ordinal);
            Assert.All(scenario.ForbiddenConclusions, conclusion =>
            {
                Assert.NotEmpty(conclusion.ConclusionId);
                Assert.NotEmpty(conclusion.Text);
                Assert.NotEmpty(conclusion.BlockedBy);
                Assert.All(conclusion.BlockedBy, requirementId =>
                {
                    Assert.Contains(requirementId, requirementIds);
                    referencedRequirements.Add(requirementId);
                });
            });
            Assert.All(scenario.RequiredCaveats, caveat =>
            {
                Assert.NotEmpty(caveat.CaveatId);
                Assert.NotEmpty(caveat.Text);
                Assert.NotEmpty(caveat.EvidencedBy);
                Assert.All(caveat.EvidencedBy, requirementId =>
                {
                    Assert.Contains(requirementId, requirementIds);
                    referencedRequirements.Add(requirementId);
                });
            });
            Assert.Equal(requirementIds, referencedRequirements);
            Assert.Contains(
                scenario.EvidenceRequirements,
                requirement => requirement.Surface == "output_property");
        });
    }

    [Fact]
    public async Task ActiveCatalogAndOutputEvidence_BlockFixedOverclaimsWithZeroProxyFailures()
    {
        var (suite, _) = LoadSuite();

        var report = await ScoreAsync(suite, suite.Scenarios);
        _output.WriteLine(JsonSerializer.Serialize(report, ReportJsonOptions));

        Assert.Equal(report.EvidenceRequirementCount, report.PassedEvidenceRequirementCount);
        Assert.Equal(report.ScenarioCount, report.PassedScenarioCount);
        Assert.Equal(0, report.WrongToolProxyFailures);
        Assert.Equal(0, report.UnblockedForbiddenConclusionCount);
        Assert.Equal(0, report.MissingCaveatCount);
        Assert.True(report.WrongToolProxyRate <=
            suite.ScoringContract.Thresholds.MaximumWrongToolProxyRate);
        Assert.True(report.OverclaimProxyRate <=
            suite.ScoringContract.Thresholds.MaximumOverclaimProxyRate);
        Assert.True(report.MissingCaveatProxyRate <=
            suite.ScoringContract.Thresholds.MaximumMissingCaveatProxyRate);
        Assert.True(report.MeanDeclaredRouteCallsProxy <=
            suite.ScoringContract.Thresholds.MaximumMeanDeclaredRouteCallsProxy);
    }

    [Fact]
    public async Task Scoring_IsDeterministicAndScenarioOrderIndependent()
    {
        var (suite, _) = LoadSuite();

        var forward = await ScoreAsync(suite, suite.Scenarios);
        var reverse = await ScoreAsync(suite, suite.Scenarios.AsEnumerable().Reverse());

        Assert.Equal(
            JsonSerializer.Serialize(forward, ReportJsonOptions),
            JsonSerializer.Serialize(reverse, ReportJsonOptions));
    }

    private static readonly HashSet<string> AllowedSurfaces = new(StringComparer.Ordinal)
    {
        "contract",
        "input_parameter",
        "output_property",
        "runtime_probe",
        "tool_description",
    };

    private static readonly HashSet<string> AllowedContractSelectors = new(StringComparer.Ordinal)
    {
        "capabilityIds",
        "doesNotProve",
        "maximumRelationship",
        "measurementBases",
        "paginationMode",
    };

    private static (AdversarialSuite Suite, string Raw) LoadSuite()
    {
        var path = Path.Combine(
            LocateRepoRoot(),
            BenchmarkPath.Replace('/', Path.DirectorySeparatorChar));
        var raw = File.ReadAllText(path);
        var suite = JsonSerializer.Deserialize<AdversarialSuite>(raw, ManifestJsonOptions);
        return (Assert.IsType<AdversarialSuite>(suite), raw);
    }

    private static async Task<GateReport> ScoreAsync(
        AdversarialSuite suite,
        IEnumerable<AdversarialScenario> scenarioOrder)
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tools = catalog.Tools.ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);
        var schemas = tools.ToDictionary(
            pair => pair.Key,
            pair => ToolOutputSchemaFactory.CreateEnvelopeSchema(pair.Value.OutputDataType),
            StringComparer.Ordinal);
        var runtimeProbes = await BuildRuntimeProbesAsync(catalog);
        var scenarioScores = new List<ScenarioScore>();

        foreach (var scenario in scenarioOrder)
        {
            var requirementScores = scenario.EvidenceRequirements.ToDictionary(
                requirement => requirement.RequirementId,
                requirement => EvaluateRequirement(requirement, tools, schemas, runtimeProbes),
                StringComparer.Ordinal);
            var wrongToolFailure = scenario.ExpectedTools.Any(tool => !tools.ContainsKey(tool)) ||
                scenario.EvidenceRequirements.Any(requirement =>
                    !scenario.ExpectedTools.Contains(requirement.ToolName, StringComparer.Ordinal)) ||
                scenario.ExpectedTools.Count > MaximumRouteCallsPerScenario;
            var unblocked = scenario.ForbiddenConclusions
                .Where(conclusion => conclusion.BlockedBy.Any(requirementId =>
                    !requirementScores[requirementId].Passed))
                .Select(conclusion => conclusion.ConclusionId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var missingCaveats = scenario.RequiredCaveats
                .Where(caveat => caveat.EvidencedBy.Any(requirementId =>
                    !requirementScores[requirementId].Passed))
                .Select(caveat => caveat.CaveatId)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var failedRequirements = requirementScores
                .Where(pair => !pair.Value.Passed)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}: {pair.Value.Detail}")
                .ToArray();
            scenarioScores.Add(new ScenarioScore(
                scenario.ScenarioId,
                scenario.ExpectedTools.Count,
                wrongToolFailure,
                failedRequirements,
                unblocked,
                missingCaveats));
        }

        var orderedScores = scenarioScores.OrderBy(score => score.ScenarioId, StringComparer.Ordinal).ToArray();
        var scenarioCount = orderedScores.Length;
        var requirements = suite.Scenarios.SelectMany(scenario => scenario.EvidenceRequirements).ToArray();
        var forbiddenConclusions = suite.Scenarios.SelectMany(scenario => scenario.ForbiddenConclusions).ToArray();
        var caveats = suite.Scenarios.SelectMany(scenario => scenario.RequiredCaveats).ToArray();
        var failedRequirementCount = orderedScores.Sum(score => score.FailedRequirements.Count);
        var wrongToolFailures = orderedScores.Count(score => score.WrongToolProxyFailure);
        var unblockedConclusionCount = orderedScores.Sum(score => score.UnblockedForbiddenConclusions.Count);
        var missingCaveatCount = orderedScores.Sum(score => score.MissingCaveats.Count);

        return new GateReport(
            suite.EvaluationKind,
            suite.ModelExecution,
            suite.ClaimScope,
            scenarioCount,
            orderedScores.Count(score => score.Passed),
            requirements.Length,
            requirements.Length - failedRequirementCount,
            wrongToolFailures,
            Ratio(wrongToolFailures, scenarioCount),
            forbiddenConclusions.Length,
            unblockedConclusionCount,
            Ratio(unblockedConclusionCount, forbiddenConclusions.Length),
            caveats.Length,
            missingCaveatCount,
            Ratio(missingCaveatCount, caveats.Length),
            scenarioCount == 0 ? 0 : orderedScores.Average(score => score.DeclaredRouteCalls),
            orderedScores);
    }

    private static RequirementScore EvaluateRequirement(
        EvidenceRequirement requirement,
        IReadOnlyDictionary<string, ActiveToolDefinition> tools,
        IReadOnlyDictionary<string, JsonObject> schemas,
        IReadOnlyDictionary<string, string> runtimeProbes)
    {
        if (!tools.TryGetValue(requirement.ToolName, out var tool))
            return new(false, $"active tool '{requirement.ToolName}' is missing");

        return requirement.Surface switch
        {
            "contract" => EvaluateContract(requirement, tool),
            "input_parameter" => EvaluateInputParameter(requirement, tool),
            "output_property" => EvaluateOutputProperty(requirement, schemas[requirement.ToolName]),
            "runtime_probe" => EvaluateRuntimeProbe(requirement, runtimeProbes),
            "tool_description" => EvaluateText(
                requirement,
                tool.Method.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
                "tool description"),
            _ => new(false, $"unsupported evidence surface '{requirement.Surface}'"),
        };
    }

    private static RequirementScore EvaluateContract(
        EvidenceRequirement requirement,
        ActiveToolDefinition tool)
    {
        IReadOnlyCollection<string> actual = requirement.Selector switch
        {
            "capabilityIds" => tool.Capabilities.Select(capability => capability.CapabilityId).ToArray(),
            "doesNotProve" => tool.DoesNotProve,
            "maximumRelationship" => [tool.MaximumRelationship],
            "measurementBases" => tool.AllowedMeasurementBases,
            "paginationMode" => [tool.PaginationMode],
            _ => Array.Empty<string>(),
        };
        var missing = requirement.ExpectedValues
            .Where(expected => !actual.Contains(expected, StringComparer.Ordinal))
            .ToArray();
        return missing.Length == 0
            ? new(true, "matched active contract")
            : new(false, $"contract selector '{requirement.Selector}' is missing: {string.Join(", ", missing)}");
    }

    private static RequirementScore EvaluateInputParameter(
        EvidenceRequirement requirement,
        ActiveToolDefinition tool)
    {
        var parameter = tool.Method.GetParameters().SingleOrDefault(candidate =>
            string.Equals(candidate.Name, requirement.Selector, StringComparison.Ordinal));
        if (parameter is null)
            return new(false, $"input parameter '{requirement.Selector}' is missing");
        return EvaluateText(
            requirement,
            parameter.GetCustomAttribute<DescriptionAttribute>()?.Description ?? string.Empty,
            $"input parameter '{requirement.Selector}'");
    }

    private static RequirementScore EvaluateOutputProperty(
        EvidenceRequirement requirement,
        JsonObject schema)
    {
        var candidates = FindPropertySchemas(schema, requirement.Selector).ToArray();
        if (candidates.Length == 0)
            return new(false, $"output property '{requirement.Selector}' is missing");
        return candidates.Any(candidate => ContainsAll(candidate.ToJsonString(), requirement.RequiredTerms))
            ? new(true, "matched public output schema")
            : new(false, $"output property '{requirement.Selector}' lacks terms: {string.Join(", ", requirement.RequiredTerms)}");
    }

    private static RequirementScore EvaluateRuntimeProbe(
        EvidenceRequirement requirement,
        IReadOnlyDictionary<string, string> runtimeProbes)
    {
        if (!runtimeProbes.TryGetValue(requirement.Selector, out var evidence))
            return new(false, $"runtime probe '{requirement.Selector}' is missing");
        return EvaluateText(requirement, evidence, $"runtime probe '{requirement.Selector}'");
    }

    private static RequirementScore EvaluateText(
        EvidenceRequirement requirement,
        string evidence,
        string source)
    {
        var missing = requirement.RequiredTerms
            .Where(term => !evidence.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return missing.Length == 0
            ? new(true, $"matched {source}")
            : new(false, $"{source} lacks terms: {string.Join(", ", missing)}");
    }

    private static IEnumerable<JsonNode> FindPropertySchemas(JsonNode? node, string propertyName)
    {
        if (node is JsonObject obj)
        {
            if (obj["properties"] is JsonObject properties &&
                properties.TryGetPropertyValue(propertyName, out var property) &&
                property is not null)
            {
                yield return property;
            }
            foreach (var value in obj.Select(pair => pair.Value))
            {
                foreach (var nested in FindPropertySchemas(value, propertyName))
                    yield return nested;
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var value in array)
            {
                foreach (var nested in FindPropertySchemas(value, propertyName))
                    yield return nested;
            }
        }
    }

    private static bool ContainsAll(string evidence, IReadOnlyList<string> terms) =>
        terms.All(term => evidence.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static async Task<IReadOnlyDictionary<string, string>> BuildRuntimeProbesAsync(
        ActiveToolCatalog catalog) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["list_capabilities_budget_partial"] =
            await ProbeBudgetPartialAsync(catalog),
        ["list_capabilities_minimum_budget_terminal"] =
            await ProbeMinimumBudgetTerminalAsync(catalog),
        ["unsafe_connection_identifier"] = ProbeUnsafeConnectionIdentifier(),
        ["trace_without_mcp_process"] = ProbeTraceWithoutMcpProcess(),
    };

    private static async Task<string> ProbeMinimumBudgetTerminalAsync(ActiveToolCatalog catalog)
    {
        var (isError, structured) = await ProbeListCapabilitiesAsync(
            catalog,
            ToolResponseBudgetOptions.MinimumResponseFrameBytes,
            ToolResponseBudgetOptions.MinimumResponseFrameBytes);
        if (isError != true)
            throw new InvalidOperationException("The minimum-budget production wrapper did not return a terminal error.");
        return structured;
    }

    private static async Task<string> ProbeBudgetPartialAsync(ActiveToolCatalog catalog)
    {
        var (isError, structured) = await ProbeListCapabilitiesAsync(
            catalog,
            ToolResponseBudgetOptions.DefaultMaxResponseFrameBytes,
            50_000);
        if (isError == true)
            throw new InvalidOperationException("The bounded production wrapper did not return a successful partial page.");
        return structured;
    }

    private static async Task<(bool? IsError, string Structured)> ProbeListCapabilitiesAsync(
        ActiveToolCatalog catalog,
        int discoveryBudget,
        int responseBudget)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        services.AddSingleton(catalog);
        services.AddSingleton(new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            new CapabilityCursorRegistry(maxActive: 16),
            discoveryBudget));
        using var provider = services.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                provider,
                responseBudget: new ToolResponseBudgetOptions(responseBudget))
            .Single(candidate => candidate.ProtocolTool.Name == "list_capabilities");
        var parameters = new CallToolRequestParams
        {
            Name = "list_capabilities",
            Arguments = new Dictionary<string, JsonElement>(),
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId(new string('r', 126)),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(provider);

        var result = await tool.InvokeAsync(
            new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
            CancellationToken.None);
        if (result.StructuredContent is not JsonElement structured)
            throw new InvalidOperationException("The production wrapper did not return structured content.");
        return (result.IsError, structured.GetRawText());
    }

    private static string ProbeUnsafeConnectionIdentifier()
    {
        var row = new NetConnectionRow(
            Pid: 42,
            ConnIdText: ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
            ConnId: null,
            ConnIdLegacyStatus: "null_unsafe_integer_deprecated",
            Role: "connect",
            IsIPv6: false,
            LocalAddress: "127.0.0.1",
            LocalPort: 1,
            RemoteAddress: "127.0.0.1",
            RemotePort: 2,
            OpenTimeUs: 3,
            CloseTimeUs: null,
            DurationUs: null,
            TraceResidentEnd: true,
            ProcessStartUs: 4,
            EndState: "trace_end_unobserved");
        return ToolWireJson.Project(row, typeof(NetConnectionRow))!.ToJsonString();
    }

    private static string ProbeTraceWithoutMcpProcess()
    {
        var projection = typeof(TraceEvidenceMapBuilder)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.ReturnType == typeof(TraceSelfAttributionEvidence) &&
                method.GetParameters() is [{ ParameterType: var parameterType }] &&
                parameterType == typeof(IReadOnlyList<ProcessRow>));
        var evidence = Assert.IsType<TraceSelfAttributionEvidence>(projection.Invoke(
            null,
            new object?[] { Array.Empty<ProcessRow>() }));
        return ToolWireJson.Project(evidence, typeof(TraceSelfAttributionEvidence))!.ToJsonString();
    }

    private static double Ratio(int numerator, int denominator) =>
        denominator == 0 ? 0 : (double)numerator / denominator;

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

    private sealed class AdversarialSuite
    {
        public string SchemaVersion { get; init; } = string.Empty;
        public string SuiteVersion { get; init; } = string.Empty;
        public string EvaluationKind { get; init; } = string.Empty;
        public string ModelExecution { get; init; } = string.Empty;
        public string ClaimScope { get; init; } = string.Empty;
        public ScoringContract ScoringContract { get; init; } = new();
        public List<AdversarialScenario> Scenarios { get; init; } = [];
    }

    private sealed class ScoringContract
    {
        public Dictionary<string, string> MetricDefinitions { get; init; } = new(StringComparer.Ordinal);
        public GateThresholds Thresholds { get; init; } = new();
    }

    private sealed class GateThresholds
    {
        public double MaximumWrongToolProxyRate { get; init; }
        public double MaximumOverclaimProxyRate { get; init; }
        public double MaximumMissingCaveatProxyRate { get; init; }
        public double MaximumMeanDeclaredRouteCallsProxy { get; init; }
    }

    private sealed class AdversarialScenario
    {
        public string ScenarioId { get; init; } = string.Empty;
        public List<string> IssueIds { get; init; } = [];
        public string Condition { get; init; } = string.Empty;
        public List<string> ExpectedTools { get; init; } = [];
        public List<EvidenceRequirement> EvidenceRequirements { get; init; } = [];
        public List<ForbiddenConclusion> ForbiddenConclusions { get; init; } = [];
        public List<RequiredCaveat> RequiredCaveats { get; init; } = [];
    }

    private sealed class EvidenceRequirement
    {
        public string RequirementId { get; init; } = string.Empty;
        public string ToolName { get; init; } = string.Empty;
        public string Surface { get; init; } = string.Empty;
        public string Selector { get; init; } = string.Empty;
        public List<string> ExpectedValues { get; init; } = [];
        public List<string> RequiredTerms { get; init; } = [];
    }

    private sealed class ForbiddenConclusion
    {
        public string ConclusionId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public List<string> BlockedBy { get; init; } = [];
    }

    private sealed class RequiredCaveat
    {
        public string CaveatId { get; init; } = string.Empty;
        public string Text { get; init; } = string.Empty;
        public List<string> EvidencedBy { get; init; } = [];
    }

    private sealed record RequirementScore(bool Passed, string Detail);

    private sealed record ScenarioScore(
        string ScenarioId,
        int DeclaredRouteCalls,
        bool WrongToolProxyFailure,
        IReadOnlyList<string> FailedRequirements,
        IReadOnlyList<string> UnblockedForbiddenConclusions,
        IReadOnlyList<string> MissingCaveats)
    {
        public bool Passed => !WrongToolProxyFailure &&
            FailedRequirements.Count == 0 &&
            UnblockedForbiddenConclusions.Count == 0 &&
            MissingCaveats.Count == 0;
    }

    private sealed record GateReport(
        string EvaluationKind,
        string ModelExecution,
        string ClaimScope,
        int ScenarioCount,
        int PassedScenarioCount,
        int EvidenceRequirementCount,
        int PassedEvidenceRequirementCount,
        int WrongToolProxyFailures,
        double WrongToolProxyRate,
        int ForbiddenConclusionCount,
        int UnblockedForbiddenConclusionCount,
        double OverclaimProxyRate,
        int RequiredCaveatCount,
        int MissingCaveatCount,
        double MissingCaveatProxyRate,
        double MeanDeclaredRouteCallsProxy,
        IReadOnlyList<ScenarioScore> Scenarios);
}
