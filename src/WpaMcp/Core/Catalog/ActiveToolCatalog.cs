using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core.Catalog;

internal sealed class ActiveToolCatalog
{
    private const string ContractVersion = "2.0";
    private static readonly Regex CapabilityIdPattern = new(
        "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions ManifestJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private static readonly HashSet<string> MeasurementBases =
        ["direct", "derived", "heuristic", "metadata", "unmeasured"];
    private static readonly HashSet<string> Relationships =
        ["descriptive", "temporal", "association", "attribution", "causal"];
    private static readonly HashSet<string> Scopes =
        ["server", "trace", "process", "thread", "time_window", "provider", "focus_frame"];
    private static readonly HashSet<string> Maturities = ["supported", "preview", "gap"];
    private static readonly HashSet<string> SymbolRequirements =
        ["none", "optional", "metadata_only", "explicit_context_required"];
    private static readonly HashSet<string> PaginationModes = ["none", "top_n", "cursor"];
    private static readonly HashSet<string> CostClasses =
        ["bootstrap", "single_scan", "stack_scan", "composite", "local_probe", "process_state_mutation"];
    private static readonly HashSet<string> SideEffectClasses =
        ["none", "raw_trace_query", "raw_trace_stack_query", "loaded_trace_query", "loaded_trace_stack_query", "owned_trace_artifact_write", "trace_handle_retirement", "diagnose_local_symbols", "trace_cache_retirement", "symbol_path_configuration", "symbol_server_configuration", "symbol_context_preparation"];
    private static readonly HashSet<string> EvaluatorKinds =
        ["server", "event", "event_requirements", "event_count", "evidence_completion", "process_inventory", "logical_events", "query_dependent", "gap"];
    private static readonly HashSet<string> ConclusionStatuses =
        ["observed", "supported", "partial", "not_concluded", "not_applicable"];
    private static readonly Regex StableSemanticIdPattern = new(
        "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly ImmutableArray<ActiveToolDefinition> _tools;
    private readonly ImmutableArray<ActiveToolDefinition> _allTools;
    private readonly ImmutableArray<CapabilityDefinition> _capabilities;
    private readonly ImmutableArray<CapabilityGoalDefinition> _goals;
    private readonly ImmutableArray<CapabilityWorkflowDefinition> _workflows;
    private readonly ImmutableArray<CapabilityEvaluatorDefinition> _evaluators;
    private readonly CapabilityEvaluatorRegistry _evaluatorRegistry;

    private ActiveToolCatalog(
        ImmutableArray<ActiveToolDefinition> tools,
        ImmutableArray<ActiveToolDefinition> allTools,
        ImmutableArray<CapabilityDefinition> capabilities,
        ImmutableArray<CapabilityGoalDefinition> goals,
        ImmutableArray<CapabilityWorkflowDefinition> workflows,
        ImmutableArray<CapabilityEvaluatorDefinition> evaluators,
        string catalogVersion,
        string catalogScope,
        bool exhaustiveForWpa,
        string unlistedCapabilityMeaning,
        CapabilityPolicyProfile capabilityPolicy)
    {
        _tools = tools;
        _allTools = allTools;
        _capabilities = capabilities;
        _goals = goals;
        _workflows = workflows;
        _evaluators = evaluators;
        _evaluatorRegistry = new CapabilityEvaluatorRegistry(evaluators);
        CatalogVersion = catalogVersion;
        CatalogScope = catalogScope;
        ExhaustiveForWpa = exhaustiveForWpa;
        UnlistedCapabilityMeaning = unlistedCapabilityMeaning;
        CapabilityPolicy = capabilityPolicy;
    }

    public string CatalogVersion { get; }
    public string CatalogScope { get; }
    public bool ExhaustiveForWpa { get; }
    public string UnlistedCapabilityMeaning { get; }
    public IReadOnlyList<ActiveToolDefinition> Tools => _tools;
    public IReadOnlyList<ActiveToolDefinition> AllTools => _allTools;
    public IReadOnlyList<CapabilityDefinition> Capabilities => _capabilities;
    public IReadOnlyList<CapabilityGoalDefinition> Goals => _goals;
    public IReadOnlyList<CapabilityWorkflowDefinition> Workflows => _workflows;
    public IReadOnlyList<CapabilityEvaluatorDefinition> Evaluators => _evaluators;
    internal IReadOnlyDictionary<string, ToolOutputContract> OutputContracts =>
        _tools.ToDictionary(tool => tool.ToolName, tool => tool.OutputContract, StringComparer.Ordinal);
    internal CapabilityPolicyProfile CapabilityPolicy { get; }
    internal CapabilityEvaluatorRegistry EvaluatorRegistry => _evaluatorRegistry;

    public static ActiveToolCatalog LoadAndValidate(
        IServiceProvider? services = null,
        Assembly? toolAssembly = null)
    {
        toolAssembly ??= typeof(Program).Assembly;
        var capabilityJson = CatalogManifestLoader.Read(
            toolAssembly,
            "eng/capabilities.v1.json",
            "WpaMcp.Manifests.eng.capabilities.v1.json");
        var toolJson = CatalogManifestLoader.Read(
            toolAssembly,
            "eng/tool-contracts.v2.json",
            "WpaMcp.Manifests.eng.tool-contracts.v2.json");
        var benchmarkJson = CatalogManifestLoader.Read(
            toolAssembly,
            "benchmarks/capability-matrix.v1.json",
            "WpaMcp.Manifests.benchmarks.capability-matrix.v1.json");
        return LoadAndValidateJson(capabilityJson, toolJson, benchmarkJson, services, toolAssembly);
    }

    internal static ActiveToolCatalog LoadAndValidateJson(
        string capabilityJson,
        string toolJson,
        string benchmarkJson,
        IServiceProvider? services = null,
        Assembly? toolAssembly = null)
    {
        toolAssembly ??= typeof(Program).Assembly;
        var capabilities = Deserialize<CapabilityManifest>(capabilityJson, "capabilities");
        var contracts = Deserialize<ToolContractManifest>(toolJson, "tool contracts");
        var benchmarks = Deserialize<BenchmarkManifest>(benchmarkJson, "benchmark matrix");

        ValidateManifestHeaders(capabilities, contracts, benchmarks);
        ValidateInputSchemaOverlays(contracts);

        ServiceProvider? ownedProvider = null;
        if (services is null)
        {
            var collection = new ServiceCollection();
            collection.AddSingleton(_ => new TraceCache());
            collection.AddSingleton<SymbolService>();
            ownedProvider = collection.BuildServiceProvider();
            services = ownedProvider;
        }

        try
        {
            return Build(capabilities, contracts, benchmarks, services, toolAssembly);
        }
        finally
        {
            ownedProvider?.Dispose();
        }
    }

    internal static string ComputeReviewedContentHash(
        string capabilityJson,
        string toolJson,
        string benchmarkJson)
    {
        var capabilities = Deserialize<CapabilityManifest>(capabilityJson, "capabilities");
        var contracts = Deserialize<ToolContractManifest>(toolJson, "tool contracts");
        var benchmarks = Deserialize<BenchmarkManifest>(benchmarkJson, "benchmark matrix");
        return ComputeCatalogVersion(capabilities, contracts, benchmarks, []);
    }

    public IReadOnlyList<McpServerTool> CreateServerTools(
        IServiceProvider services,
        Func<MethodInfo, McpServerToolCreateOptions>? optionsFactory = null,
        ToolResponseBudgetOptions? responseBudget = null,
        IToolPrivacyRedactor? privacy = null,
        IToolArgumentRewriter? argumentRewriter = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var adapters = new ReviewedToolOutcomeAdapterRegistry(_allTools);
        var capabilityEvaluators = _evaluatorRegistry;
        var fitter = new ToolResponseFrameFitter(
            responseBudget ?? ToolResponseBudgetOptions.Default,
            privacy ?? new ToolPrivacyRedactor(ToolPrivacyMode.Off),
            services);
        return _tools.Select(tool =>
        {
            var method = tool.Method;
            var options = optionsFactory?.Invoke(method)
                ?? new McpServerToolCreateOptions { Services = services };
            options.Services ??= services;
            var inner = CreateServerTool(method, options);
            return (McpServerTool)new ContractMcpServerTool(
                inner,
                tool,
                adapters,
                capabilityEvaluators,
                fitter,
                argumentRewriter ?? RejectingAliasArgumentRewriter.Instance);
        }).ToArray();
    }

    public IReadOnlyList<Tool> CreateProtocolTools(
        IServiceProvider services,
        Func<MethodInfo, McpServerToolCreateOptions>? optionsFactory = null) =>
        CreateServerTools(services, optionsFactory).Select(tool => tool.ProtocolTool).ToArray();

    /// <summary>
    /// Projects the explicitly enabled raw-path migration profile before any
    /// tools/list preflight, cursor binding, or structured-output snapshot is
    /// created. The reviewed secure-default catalog remains immutable.
    /// </summary>
    internal ActiveToolCatalog ProjectTraceReferenceProfile(
        TraceAccessMode mode,
        IReadOnlyList<McpServerTool> serverTools)
    {
        ArgumentNullException.ThrowIfNull(serverTools);
        if (mode == TraceAccessMode.IdOnly)
            return this;

        var analysisNames = _tools
            .Where(tool => tool.ToolName is not ("load_trace" or "unload_trace") &&
                tool.Method.GetParameters().Any(parameter =>
                    string.Equals(parameter.Name, "path", StringComparison.Ordinal)))
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        var protocolByName = serverTools.ToDictionary(
            tool => tool.ProtocolTool.Name,
            StringComparer.Ordinal);
        foreach (var name in analysisNames)
        {
            if (!protocolByName.TryGetValue(name, out var serverTool))
            {
                throw new CatalogValidationException(
                    $"TRACE-PROFILE: projected tool {name} has no protocol binding");
            }

            var protocolTool = serverTool.ProtocolTool;
            protocolTool.Annotations = new ModelContextProtocol.Protocol.ToolAnnotations
            {
                ReadOnlyHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
                DestructiveHint = false,
            };
            var schema = JsonNode.Parse(protocolTool.InputSchema.GetRawText()) as JsonObject;
            if (schema?["properties"]?["path"] is not JsonObject pathProperty)
            {
                throw new CatalogValidationException(
                    $"TRACE-PROFILE: projected tool {name} has no path schema");
            }
            pathProperty["description"] =
                "Canonical TraceId returned by load_trace (preferred), or an allowed absolute local .etl/.etlx source path only while the explicitly enabled compatibility profile remains available.";
            pathProperty["pattern"] = ToolOpaqueLocatorInputOverlay.TraceOrCompatibilityPathPattern;
            pathProperty["x-opaqueLocator"] = "trace_id_or_approved_absolute_etl_path";
            protocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(
                schema.ToJsonString(),
                McpJsonUtilities.DefaultOptions);
        }

        var projectedCapabilityIds = _tools
            .Where(tool => analysisNames.Contains(tool.ToolName))
            .SelectMany(tool => tool.Capabilities)
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var capabilities = _capabilities.Select(capability =>
                projectedCapabilityIds.Contains(capability.CapabilityId)
                    ? capability with
                    {
                        SideEffectClass = ProjectTraceSideEffect(capability.SideEffectClass),
                    }
                    : capability)
            .ToImmutableArray();
        var capabilityById = capabilities.ToDictionary(
            capability => capability.CapabilityId,
            StringComparer.Ordinal);
        var tools = _tools.Select(tool =>
        {
            var projectedCapabilities = tool.Capabilities
                .Select(capability => capabilityById[capability.CapabilityId])
                .ToImmutableArray();
            if (!analysisNames.Contains(tool.ToolName))
                return tool with { Capabilities = projectedCapabilities };
            return tool with
            {
                Capabilities = projectedCapabilities,
                Annotations = new ToolAnnotations(
                    ReadOnlyHint: false,
                    IdempotentHint: true,
                    OpenWorldHint: false,
                    DestructiveHint: false),
                SideEffects = tool.SideEffects
                    .Select(ProjectTraceSideEffect)
                    .ToImmutableArray(),
            };
        }).ToImmutableArray();

        var profilePayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            BaseCatalogVersion = CatalogVersion,
            Profile = "raw_path_compatibility_until_1.0.0",
            Capabilities = capabilities
                .Select(capability => new
                {
                    capability.CapabilityId,
                    capability.SideEffectClass,
                })
                .OrderBy(item => item.CapabilityId, StringComparer.Ordinal),
            Tools = tools
                .Select(tool => new
                {
                    tool.ToolName,
                    tool.Annotations,
                    tool.SideEffects,
                })
                .OrderBy(item => item.ToolName, StringComparer.Ordinal),
            SdkTools = serverTools
                .Select(tool => tool.ProtocolTool)
                .Select(tool => JsonSerializer.SerializeToElement(
                    tool,
                    McpJsonUtilities.DefaultOptions)),
        }, ManifestJsonOptions);
        var profileVersion = Convert.ToHexString(SHA256.HashData(profilePayload))
            .ToLowerInvariant();
        return new ActiveToolCatalog(
            tools,
            tools,
            capabilities,
            _goals,
            _workflows,
            _evaluators,
            profileVersion,
            CatalogScope,
            ExhaustiveForWpa,
            UnlistedCapabilityMeaning,
            CapabilityPolicyProfile.Full);
    }

    /// <summary>
    /// Applies the startup administrative capability policy after all reviewed
    /// contract projections have completed. Disabled mappings remain in
    /// <see cref="AllTools"/>, while <see cref="Tools"/> and the SDK surface
    /// contain callable tools only.
    /// </summary>
    internal (ActiveToolCatalog Catalog, IReadOnlyList<McpServerTool> ServerTools)
        ProjectCapabilityPolicy(
            CapabilityPolicyProfile policy,
            IReadOnlyList<McpServerTool> serverTools)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(serverTools);

        var capabilityIds = _capabilities
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var unknown = policy.DisabledCapabilityIds
            .Where(capabilityId => !capabilityIds.Contains(capabilityId))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-UNKNOWN: undeclared capability IDs: " +
                string.Join(", ", unknown));
        }
        if (policy.IsDisabled("catalog.capability.list"))
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-DISCOVERY: catalog.capability.list cannot be disabled");
        }

        var activeTools = _tools.Where(tool => !tool.Capabilities.Any(capability =>
                policy.IsDisabled(capability.CapabilityId)))
            .ToImmutableArray();
        var activeNames = activeTools
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        if (!activeNames.Contains("list_capabilities"))
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-DISCOVERY: list_capabilities must remain callable");
        }
        if (!activeNames.Contains("get_tool_contract"))
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-DISCOVERY: get_tool_contract must remain callable");
        }

        var inspectRetained = activeNames.Contains("inspect_trace");
        var traceAnalysisRetained = activeTools.Any(tool =>
            tool.ToolName is not ("load_trace" or "unload_trace" or "inspect_trace") &&
            tool.Method.GetParameters().Any(parameter =>
                string.Equals(parameter.Name, "path", StringComparison.Ordinal)));
        if (traceAnalysisRetained && !inspectRetained)
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-INSPECT: inspect_trace must remain callable while trace analysis tools remain");
        }

        var serverToolNames = serverTools
            .Select(tool => tool.ProtocolTool.Name)
            .ToArray();
        if (serverToolNames.Length != serverToolNames.Distinct(StringComparer.Ordinal).Count())
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-SDK: duplicate SDK tool names prevent a closed projection");
        }
        var projectedServerTools = serverTools
            .Where(tool => activeNames.Contains(tool.ProtocolTool.Name))
            .ToArray();
        var projectedNames = projectedServerTools
            .Select(tool => tool.ProtocolTool.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!projectedNames.SetEquals(activeNames))
        {
            throw new CatalogValidationException(
                "CAPABILITY-POLICY-SDK: callable catalog and SDK tool projections do not close");
        }

        ValidatePolicyClosure(policy, activeTools);
        if (policy.IsFull)
            return (this, projectedServerTools);

        return (
            new ActiveToolCatalog(
                activeTools,
                _allTools,
                _capabilities,
                _goals,
                _workflows,
                _evaluators,
                CatalogVersion,
                CatalogScope,
                ExhaustiveForWpa,
                UnlistedCapabilityMeaning,
                policy),
            projectedServerTools);
    }

    private void ValidatePolicyClosure(
        CapabilityPolicyProfile policy,
        IReadOnlyCollection<ActiveToolDefinition> activeTools)
    {
        var activeNames = activeTools.Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var tool in _allTools)
        {
            var disabled = tool.Capabilities.Any(capability =>
                policy.IsDisabled(capability.CapabilityId));
            if (activeNames.Contains(tool.ToolName) == disabled)
            {
                throw new CatalogValidationException(
                    $"CAPABILITY-POLICY-CLOSURE: tool '{tool.ToolName}' is not in exactly one callable/policy-disabled bucket");
            }
        }
        foreach (var workflow in _workflows)
        {
            var mapped = _allTools.Where(tool => tool.Capabilities.Any(capability =>
                    workflow.CapabilityIds.Contains(
                        capability.CapabilityId,
                        StringComparer.Ordinal)))
                .Select(tool => tool.ToolName)
                .ToHashSet(StringComparer.Ordinal);
            if (!mapped.SetEquals(workflow.ToolNames))
            {
                throw new CatalogValidationException(
                    $"CAPABILITY-POLICY-CLOSURE: workflow '{workflow.WorkflowId}' tool mapping is not bidirectionally closed");
            }
        }
    }

    private static string ProjectTraceSideEffect(string sideEffect) =>
        sideEffect switch
        {
            "loaded_trace_query" => "raw_trace_query",
            "loaded_trace_stack_query" => "raw_trace_stack_query",
            _ => sideEffect,
        };

    internal static string TypeIdentity(Type type)
    {
        if (type.IsByRef)
            return TypeIdentity(type.GetElementType()!) + "&";
        if (type.IsArray)
            return TypeIdentity(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var genericName = type.GetGenericTypeDefinition().FullName
            ?? type.GetGenericTypeDefinition().Name;
        var arityMarker = genericName.IndexOf('`');
        if (arityMarker >= 0)
            genericName = genericName[..arityMarker];
        return genericName + "<" +
               string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + ">";
    }

    internal static string InputTypeIdentity(MethodInfo method) =>
        "parameters(" + string.Join(",", method.GetParameters()
            .Where(static parameter => !IsSdkInjectedParameter(parameter.ParameterType))
            .Select(parameter =>
            $"{parameter.Name}:{TypeIdentity(parameter.ParameterType)}")) + ")";

    private static bool IsSdkInjectedParameter(Type type) =>
        type == typeof(CancellationToken) ||
        (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IProgress<>));

    internal static Type EffectiveOutputType(MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(method);
        var returnType = method.ReturnType;
        if (returnType.IsGenericType &&
            (returnType.GetGenericTypeDefinition() == typeof(Task<>) ||
             returnType.GetGenericTypeDefinition() == typeof(ValueTask<>)))
        {
            return returnType.GetGenericArguments()[0];
        }
        return returnType;
    }

    private static ActiveToolCatalog Build(
        CapabilityManifest capabilityManifest,
        ToolContractManifest toolManifest,
        BenchmarkManifest benchmarkManifest,
        IServiceProvider services,
        Assembly toolAssembly)
    {
        EnsureUnique(capabilityManifest.Capabilities, item => item.CapabilityId, "CAPABILITY-DUPLICATE");
        EnsureUnique(capabilityManifest.Goals, item => item.GoalId, "GOAL-DUPLICATE");
        EnsureUnique(capabilityManifest.Workflows, item => item.WorkflowId, "WORKFLOW-DUPLICATE");
        EnsureUnique(capabilityManifest.Evaluators, item => item.EvaluatorId, "EVALUATOR-DUPLICATE");
        EnsureUnique(toolManifest.Tools, item => item.ToolName, "TOOL-DUPLICATE");
        EnsureUnique(benchmarkManifest.Capabilities, item => item.CapabilityId, "BENCHMARK-DUPLICATE");
        EnsureUnique(
            benchmarkManifest.PlannerAdmissions,
            item => item.ToolName,
            "PLANNER-ADMISSION-DUPLICATE");

        var capabilityEntries = capabilityManifest.Capabilities.ToDictionary(
            item => item.CapabilityId,
            StringComparer.Ordinal);
        var benchmarkEntries = benchmarkManifest.Capabilities.ToDictionary(
            item => item.CapabilityId,
            StringComparer.Ordinal);
        ValidateCapabilities(capabilityEntries, benchmarkEntries);
        var discovery = ValidateAndBuildDiscoveryDefinitions(capabilityManifest);

        var plannerAdmissions = ValidateAndBuildPlannerAdmissions(
            benchmarkManifest.PlannerAdmissions,
            toolManifest.Tools,
            capabilityEntries);
        var evidence = benchmarkManifest.Capabilities
            .SelectMany(item => item.EvidenceReferences)
            .Concat(benchmarkManifest.PlannerAdmissions.SelectMany(
                item => item.EvidenceReferences))
            .ToList();
        EnsureUnique(evidence, item => item.EvidenceId, "EVIDENCE-DUPLICATE");
        var evidenceById = evidence.ToDictionary(item => item.EvidenceId, StringComparer.Ordinal);

        var discovered = DiscoverTools(toolAssembly, services);
        ValidateAppliedInputSchemaOverlays(toolManifest, discovered);
        var discoveredByName = discovered.ToDictionary(item => item.Tool.ProtocolTool.Name, StringComparer.Ordinal);
        ValidateToolSet(toolManifest.Tools, discoveredByName);

        var capabilityDefinitions = capabilityEntries.Values
            .Select(entry => BuildCapability(
                entry,
                benchmarkEntries[entry.CapabilityId],
                discovery.GoalIdsByCapability[entry.CapabilityId],
                discovery.WorkflowIdsByCapability[entry.CapabilityId],
                discovery.EvaluatorIdByCapability[entry.CapabilityId]))
            .OrderBy(item => item.CapabilityId, StringComparer.Ordinal)
            .ToImmutableArray();
        var capabilityById = capabilityDefinitions.ToDictionary(item => item.CapabilityId, StringComparer.Ordinal);
        var boundaryCodes = capabilityDefinitions
            .SelectMany(item => item.ConclusionBoundaryCodes)
            .ToHashSet(StringComparer.Ordinal);

        var tools = toolManifest.Tools.Select(entry =>
                BuildTool(
                    entry,
                    discoveredByName,
                    capabilityById,
                    evidenceById,
                    boundaryCodes,
                    plannerAdmissions.GetValueOrDefault(entry.ToolName)))
            .OrderBy(item => item.DiscoveryPriority)
            .ThenBy(item => item.Domain, StringComparer.Ordinal)
            .ThenBy(item => item.Ordinal)
            .ThenBy(item => item.ToolName, StringComparer.Ordinal)
            .ToImmutableArray();

        ValidateCapabilityCoverage(capabilityDefinitions, tools);
        ValidateDiscoveryKeys(tools);

        var workflows = discovery.Workflows.Select(workflow =>
                new CapabilityWorkflowDefinition(
                    workflow.WorkflowId,
                    workflow.Title,
                    workflow.Summary,
                    workflow.GoalIds.ToImmutableArray(),
                    workflow.CapabilityIds.ToImmutableArray(),
                    tools.Where(tool => tool.Capabilities.Any(capability =>
                            workflow.CapabilityIds.Contains(
                                capability.CapabilityId,
                                StringComparer.Ordinal)))
                        .Select(tool => tool.ToolName)
                        .ToImmutableArray()))
            .OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .ToImmutableArray();
        var goals = discovery.Goals.Select(goal =>
                new CapabilityGoalDefinition(
                    goal.GoalId,
                    goal.Title,
                    goal.Summary,
                    workflows.Where(workflow => workflow.GoalIds.Contains(
                            goal.GoalId,
                            StringComparer.Ordinal))
                        .Select(workflow => workflow.WorkflowId)
                        .ToImmutableArray()))
            .OrderBy(goal => goal.GoalId, StringComparer.Ordinal)
            .ToImmutableArray();
        var evaluators = discovery.Evaluators.Select(evaluator =>
                new CapabilityEvaluatorDefinition(
                    evaluator.EvaluatorId,
                    evaluator.Kind,
                    evaluator.CapabilityIds.ToImmutableArray(),
                    evaluator.EventFlags.ToImmutableArray(),
                    evaluator.EventCountProperty,
                    evaluator.CompletedCountProperty,
                    evaluator.UnmatchedCountProperty,
                    evaluator.BoundaryCountProperty,
                    evaluator.StackDomain,
                    evaluator.MeasurementBasis,
                    evaluator.Relationship,
                    evaluator.ObservedConclusion,
                    evaluator.CountRepresentation,
                    evaluator.Provenance))
            .OrderBy(evaluator => evaluator.EvaluatorId, StringComparer.Ordinal)
            .ToImmutableArray();

        var catalogVersion = ComputeCatalogVersion(
            capabilityManifest,
            toolManifest,
            benchmarkManifest,
            tools.Select(tool => discoveredByName[tool.ToolName].Tool.ProtocolTool));

        return new ActiveToolCatalog(
            tools,
            tools,
            capabilityDefinitions,
            goals,
            workflows,
            evaluators,
            catalogVersion,
            capabilityManifest.CatalogScope,
            capabilityManifest.ExhaustiveForWpa,
            capabilityManifest.UnlistedCapabilityMeaning,
            CapabilityPolicyProfile.Full);
    }

    private static void ValidateManifestHeaders(
        CapabilityManifest capabilities,
        ToolContractManifest tools,
        BenchmarkManifest benchmarks)
    {
        Require(capabilities.SchemaVersion == "capabilities.v1", "HEADER", "capability schemaVersion must be capabilities.v1");
        Require(tools.SchemaVersion == "tool-contracts.v2", "HEADER", "tool schemaVersion must be tool-contracts.v2");
        Require(benchmarks.SchemaVersion == "capability-matrix.v1", "HEADER", "benchmark schemaVersion must be capability-matrix.v1");
        Require(capabilities.ContractVersion == ContractVersion, "CONTRACT-VERSION", "capability contractVersion must be literal 2.0");
        Require(tools.ContractVersion == ContractVersion, "CONTRACT-VERSION", "tool contractVersion must be literal 2.0");
        Require(capabilities.CatalogScope == "wpa_mcp_declared_capabilities", "CATALOG-SCOPE", "catalogScope is not the approved universe");
        Require(!capabilities.ExhaustiveForWpa, "CATALOG-SCOPE", "exhaustiveForWpa must remain false");
        Require(capabilities.UnlistedCapabilityMeaning == "unknown_not_catalogued", "CATALOG-SCOPE", "unlistedCapabilityMeaning is not approved");
        Require(capabilities.CatalogVersionPolicy == "sha256_validated_active_model", "CATALOG-VERSION", "catalogVersionPolicy is not approved");
    }

    private static void ValidateCapabilities(
        IReadOnlyDictionary<string, CapabilityManifestEntry> capabilities,
        IReadOnlyDictionary<string, BenchmarkCapabilityEntry> benchmarks)
    {
        var missingBenchmarks = capabilities.Keys.Except(benchmarks.Keys, StringComparer.Ordinal).Order().ToArray();
        var danglingBenchmarks = benchmarks.Keys.Except(capabilities.Keys, StringComparer.Ordinal).Order().ToArray();
        Require(missingBenchmarks.Length == 0 && danglingBenchmarks.Length == 0,
            "BENCHMARK-CLOSURE",
            $"missing=[{string.Join(',', missingBenchmarks)}], dangling=[{string.Join(',', danglingBenchmarks)}]");

        foreach (var capability in capabilities.Values)
        {
            Require(CapabilityIdPattern.IsMatch(capability.CapabilityId), "CAPABILITY-ID", $"invalid CapabilityId '{capability.CapabilityId}'");
            Require(capability.DefinitionVersion > 0, "CAPABILITY-VERSION", $"{capability.CapabilityId} definitionVersion must be positive");
            Require(capability.ContractVersion == ContractVersion, "CONTRACT-VERSION", $"{capability.CapabilityId} must use contract 2.0");
            Require(capability.LifecycleStatus is "active" or "deprecated", "CAPABILITY-LIFECYCLE", $"{capability.CapabilityId} has invalid lifecycleStatus");
            Require(NotBlank(capability.Domain, capability.Title, capability.Summary), "CAPABILITY-SEMANTICS", $"{capability.CapabilityId} has blank semantic fields");
            Require(capability.QuestionsAnswered.Count > 0, "CAPABILITY-SEMANTICS", $"{capability.CapabilityId} has no QuestionsAnswered");
            Require(capability.QuestionsNotAnswered.Count > 0, "CAPABILITY-BOUNDARY", $"{capability.CapabilityId} has no QuestionsNotAnswered");
            Require(capability.ConclusionBoundaryCodes.Count > 0 && capability.ConclusionBoundaryCodes.All(IsBoundaryCode),
                "CAPABILITY-BOUNDARY", $"{capability.CapabilityId} has invalid ConclusionBoundaryCodes");
            Require(capability.SourcePaths.Count > 0 && capability.SourcePaths.All(NotBlank), "CAPABILITY-SOURCE", $"{capability.CapabilityId} has no source paths");
            Require(SymbolRequirements.Contains(capability.SymbolRequirement), "SYMBOL-REQUIREMENT", $"{capability.CapabilityId} has invalid symbolRequirement");
            Require(Relationships.Contains(capability.MaximumRelationship), "RELATIONSHIP", $"{capability.CapabilityId} has invalid maximumRelationship");
            Require(capability.SupportedScopes.Count > 0 && capability.SupportedScopes.All(Scopes.Contains), "SCOPE", $"{capability.CapabilityId} has invalid supported scope");
            Require(CostClasses.Contains(capability.CostClass), "COST", $"{capability.CapabilityId} has invalid costClass");
            Require(SideEffectClasses.Contains(capability.SideEffectClass), "SIDE-EFFECT", $"{capability.CapabilityId} has invalid sideEffectClass");

            if (capability.LifecycleStatus == "deprecated")
            {
                Require(CapabilityIdPattern.IsMatch(capability.ReplacedBy ?? ""), "CAPABILITY-DEPRECATION", $"{capability.CapabilityId} requires replacedBy");
                Require(capabilities.ContainsKey(capability.ReplacedBy!), "CAPABILITY-DEPRECATION", $"{capability.CapabilityId} replacedBy is dangling");
                Require(NotBlank(capability.RemovalContractVersion), "CAPABILITY-DEPRECATION", $"{capability.CapabilityId} requires removalContractVersion");
            }
            else
            {
                Require(capability.ReplacedBy is null && capability.RemovalContractVersion is null,
                    "CAPABILITY-DEPRECATION", $"active capability {capability.CapabilityId} cannot declare replacement/removal");
            }

            var benchmark = benchmarks[capability.CapabilityId];
            Require(Maturities.Contains(benchmark.ProductMaturity), "MATURITY", $"{capability.CapabilityId} has invalid maturity");
            Require(benchmark.EvidenceReferences.Count > 0, "EVIDENCE-MISSING", $"{capability.CapabilityId} has no evidence references");
            foreach (var reference in benchmark.EvidenceReferences)
            {
                Require(NotBlank(reference.EvidenceId, reference.Kind, reference.Path), "EVIDENCE-MISSING", $"{capability.CapabilityId} has incomplete evidence");
            }
        }
    }

    private static IReadOnlyDictionary<string, PlannerAdmissionDefinition>
        ValidateAndBuildPlannerAdmissions(
            IReadOnlyList<PlannerAdmissionManifestEntry> admissions,
            IReadOnlyList<ToolContractManifestEntry> tools,
            IReadOnlyDictionary<string, CapabilityManifestEntry> capabilities)
    {
        var requiredTools = tools
            .Where(tool => tool.Enabled &&
                (tool.CostClass == "composite" || tool.ToolName == "inspect_trace"))
            .Select(tool => tool.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var declaredTools = admissions
            .Select(admission => admission.ToolName)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(requiredTools.SequenceEqual(declaredTools, StringComparer.Ordinal),
            "PLANNER-ADMISSION-CLOSURE",
            $"required=[{string.Join(',', requiredTools)}], declared=[{string.Join(',', declaredTools)}]");

        var toolByName = tools.ToDictionary(tool => tool.ToolName, StringComparer.Ordinal);
        var result = new Dictionary<string, PlannerAdmissionDefinition>(StringComparer.Ordinal);
        foreach (var admission in admissions)
        {
            Require(toolByName.TryGetValue(admission.ToolName, out var tool),
                "PLANNER-ADMISSION-TOOL",
                $"unknown tool '{admission.ToolName}'");
            Require(capabilities.ContainsKey(admission.CapabilityId) &&
                    tool!.CapabilityIds.Contains(admission.CapabilityId, StringComparer.Ordinal),
                "PLANNER-ADMISSION-CAPABILITY",
                $"{admission.ToolName} does not map capability '{admission.CapabilityId}'");
            Require(NotBlank(admission.OperationVersion) &&
                    admission.OperationVersion.Length <= 64,
                "PLANNER-ADMISSION-VERSION",
                $"{admission.ToolName} has an invalid operationVersion");
            Require(admission.AdmissionStatus is
                    "approved" or "not_admitted_evidence_missing",
                "PLANNER-ADMISSION-STATUS",
                $"{admission.ToolName} has an invalid admissionStatus");
            Require(admission.MissingEvidence.All(NotBlank) &&
                    admission.MissingEvidence.Distinct(StringComparer.Ordinal).Count() ==
                    admission.MissingEvidence.Count,
                "PLANNER-ADMISSION-MISSING-EVIDENCE",
                $"{admission.ToolName} has blank or duplicate missingEvidence");
            foreach (var reference in admission.EvidenceReferences)
            {
                Require(NotBlank(reference.EvidenceId, reference.Kind, reference.Path),
                    "PLANNER-ADMISSION-EVIDENCE",
                    $"{admission.ToolName} has incomplete planner evidence");
            }

            if (admission.AdmissionStatus == "approved")
            {
                Require(admission.PhysicalPassLimit is > 0 &&
                        admission.EvidenceReferences.Count > 0 &&
                        admission.MissingEvidence.Count == 0,
                    "PLANNER-ADMISSION-EVIDENCE",
                    $"approved operation {admission.ToolName} lacks pass limit or evidence");
            }
            else
            {
                Require(admission.PhysicalPassLimit is null &&
                        admission.EvidenceReferences.Count == 0 &&
                        admission.MissingEvidence.Count > 0,
                    "PLANNER-ADMISSION-EVIDENCE",
                    $"non-admitted operation {admission.ToolName} must expose its missing evidence");
            }

            result.Add(admission.ToolName, new PlannerAdmissionDefinition(
                admission.ToolName,
                admission.CapabilityId,
                admission.OperationVersion,
                admission.AdmissionStatus,
                admission.PhysicalPassLimit,
                admission.EvidenceReferences.Select(reference => new EvidenceReference(
                        reference.EvidenceId,
                        reference.Kind,
                        reference.Path,
                        reference.Member))
                    .ToImmutableArray(),
                admission.MissingEvidence.ToImmutableArray()));
        }
        return result;
    }

    private static DiscoveryDefinitions ValidateAndBuildDiscoveryDefinitions(
        CapabilityManifest manifest)
    {
        Require(manifest.Goals.Count > 0, "GOAL-MISSING", "the active catalog has no stable goals");
        Require(manifest.Workflows.Count > 0, "WORKFLOW-MISSING", "the active catalog has no stable workflows");
        Require(manifest.Evaluators.Count > 0, "EVALUATOR-MISSING", "the active catalog has no evaluators");

        var capabilityIds = manifest.Capabilities
            .Select(capability => capability.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);
        var goalIds = manifest.Goals
            .Select(goal => goal.GoalId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var goal in manifest.Goals)
        {
            Require(StableSemanticIdPattern.IsMatch(goal.GoalId), "GOAL-ID", $"invalid goalId '{goal.GoalId}'");
            Require(NotBlank(goal.Title, goal.Summary), "GOAL-SEMANTICS", $"{goal.GoalId} has blank semantic fields");
        }

        var workflowIdsByCapability = capabilityIds.ToDictionary(
            id => id,
            static _ => new List<string>(),
            StringComparer.Ordinal);
        var goalIdsByCapability = capabilityIds.ToDictionary(
            id => id,
            static _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (var workflow in manifest.Workflows)
        {
            Require(StableSemanticIdPattern.IsMatch(workflow.WorkflowId), "WORKFLOW-ID", $"invalid workflowId '{workflow.WorkflowId}'");
            Require(NotBlank(workflow.Title, workflow.Summary), "WORKFLOW-SEMANTICS", $"{workflow.WorkflowId} has blank semantic fields");
            Require(workflow.GoalIds.Count > 0 &&
                    workflow.GoalIds.Distinct(StringComparer.Ordinal).Count() == workflow.GoalIds.Count &&
                    workflow.GoalIds.All(goalIds.Contains),
                "WORKFLOW-GOAL-CLOSURE",
                $"{workflow.WorkflowId} has missing, duplicate, or dangling goalIds");
            Require(workflow.CapabilityIds.Count > 0 &&
                    workflow.CapabilityIds.Distinct(StringComparer.Ordinal).Count() == workflow.CapabilityIds.Count &&
                    workflow.CapabilityIds.All(capabilityIds.Contains),
                "WORKFLOW-CAPABILITY-CLOSURE",
                $"{workflow.WorkflowId} has missing, duplicate, or dangling capabilityIds");
            foreach (var capabilityId in workflow.CapabilityIds)
            {
                workflowIdsByCapability[capabilityId].Add(workflow.WorkflowId);
                goalIdsByCapability[capabilityId].UnionWith(workflow.GoalIds);
            }
        }

        var unusedGoals = goalIds.Except(
                manifest.Workflows.SelectMany(workflow => workflow.GoalIds),
                StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(unusedGoals.Length == 0, "GOAL-WORKFLOW-CLOSURE", $"unused=[{string.Join(',', unusedGoals)}]");
        var capabilitiesWithoutWorkflow = workflowIdsByCapability
            .Where(pair => pair.Value.Count == 0)
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(capabilitiesWithoutWorkflow.Length == 0,
            "CAPABILITY-WORKFLOW-CLOSURE",
            $"missing=[{string.Join(',', capabilitiesWithoutWorkflow)}]");

        var evaluatorIdByCapability = new Dictionary<string, string>(StringComparer.Ordinal);
        var traceFlagProperties = typeof(WpaMcp.Output.TraceCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(bool))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        var traceCountProperties = typeof(WpaMcp.Output.TraceCapabilities)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.PropertyType == typeof(long))
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var evaluator in manifest.Evaluators)
        {
            Require(StableSemanticIdPattern.IsMatch(evaluator.EvaluatorId), "EVALUATOR-ID", $"invalid evaluatorId '{evaluator.EvaluatorId}'");
            Require(EvaluatorKinds.Contains(evaluator.Kind), "EVALUATOR-KIND", $"{evaluator.EvaluatorId} has invalid kind");
            Require(evaluator.CapabilityIds.Count > 0 &&
                    evaluator.CapabilityIds.Distinct(StringComparer.Ordinal).Count() == evaluator.CapabilityIds.Count &&
                    evaluator.CapabilityIds.All(capabilityIds.Contains),
                "EVALUATOR-CAPABILITY-CLOSURE",
                $"{evaluator.EvaluatorId} has missing, duplicate, or dangling capabilityIds");
            Require(evaluator.EventFlags.Distinct(StringComparer.Ordinal).Count() == evaluator.EventFlags.Count &&
                    evaluator.EventFlags.All(traceFlagProperties.Contains),
                "EVALUATOR-EVENT-FLAG",
                $"{evaluator.EvaluatorId} has an invalid TraceCapabilities event flag");
            Require(evaluator.Kind != "event" || evaluator.EventFlags.Count > 0,
                "EVALUATOR-EVENT-FLAG",
                $"{evaluator.EvaluatorId} event evaluator has no event flag");
            Require(evaluator.Kind != "event_requirements" || evaluator.EventFlags.Count > 1,
                "EVALUATOR-EVENT-REQUIREMENTS",
                $"{evaluator.EvaluatorId} event_requirements evaluator must declare at least two independent event flags");
            Require(evaluator.Kind is "event" or "event_requirements" ||
                    evaluator.EventFlags.Count == 0,
                "EVALUATOR-EVENT-FLAG",
                $"{evaluator.EvaluatorId} declares eventFlags outside an event-backed evaluator");
            Require(evaluator.Kind != "event_count" || evaluator.EventFlags.Count == 0,
                "EVALUATOR-EVENT-COUNT",
                $"{evaluator.EvaluatorId} event_count evaluator must not use eventFlags");
            Require(evaluator.Kind != "evidence_completion" || evaluator.EventFlags.Count == 0,
                "EVALUATOR-EVIDENCE-COMPLETION",
                $"{evaluator.EvaluatorId} evidence_completion evaluator must not use eventFlags");
            Require(evaluator.Kind is not ("event_count" or "evidence_completion") ||
                    evaluator.EventCountProperty is not null &&
                    traceCountProperties.Contains(evaluator.EventCountProperty),
                "EVALUATOR-EVENT-COUNT",
                $"{evaluator.EvaluatorId} count-backed evaluator has an invalid TraceCapabilities source count property");
            Require(evaluator.Kind is "event_count" or "evidence_completion" ||
                    evaluator.EventCountProperty is null,
                "EVALUATOR-EVENT-COUNT",
                $"{evaluator.EvaluatorId} declares eventCountProperty outside a count-backed evaluator");
            Require(evaluator.Kind != "evidence_completion" ||
                    evaluator.CompletedCountProperty is not null &&
                    traceCountProperties.Contains(evaluator.CompletedCountProperty) &&
                    evaluator.UnmatchedCountProperty is not null &&
                    traceCountProperties.Contains(evaluator.UnmatchedCountProperty) &&
                    evaluator.BoundaryCountProperty is not null &&
                    traceCountProperties.Contains(evaluator.BoundaryCountProperty),
                "EVALUATOR-EVIDENCE-COMPLETION",
                $"{evaluator.EvaluatorId} evidence_completion evaluator has invalid completion, unmatched, or boundary count properties");
            Require(evaluator.Kind != "evidence_completion" ||
                    new[]
                    {
                        evaluator.EventCountProperty,
                        evaluator.CompletedCountProperty,
                        evaluator.UnmatchedCountProperty,
                        evaluator.BoundaryCountProperty,
                    }.Distinct(StringComparer.Ordinal).Count() == 4,
                "EVALUATOR-EVIDENCE-COMPLETION",
                $"{evaluator.EvaluatorId} evidence_completion evaluator must use four distinct count properties");
            Require(evaluator.Kind == "evidence_completion" ||
                    evaluator.CompletedCountProperty is null &&
                    evaluator.UnmatchedCountProperty is null &&
                    evaluator.BoundaryCountProperty is null,
                "EVALUATOR-EVIDENCE-COMPLETION",
                $"{evaluator.EvaluatorId} declares completion evidence properties outside an evidence_completion evaluator");
            Require(evaluator.StackDomain is null || StableSemanticIdPattern.IsMatch(evaluator.StackDomain),
                "EVALUATOR-STACK-DOMAIN",
                $"{evaluator.EvaluatorId} has invalid stackDomain");
            Require(MeasurementBases.Contains(evaluator.MeasurementBasis), "EVALUATOR-BASIS", $"{evaluator.EvaluatorId} has invalid measurementBasis");
            Require(Relationships.Contains(evaluator.Relationship), "EVALUATOR-RELATIONSHIP", $"{evaluator.EvaluatorId} has invalid relationship");
            Require(ConclusionStatuses.Contains(evaluator.ObservedConclusion), "EVALUATOR-CONCLUSION", $"{evaluator.EvaluatorId} has invalid observedConclusion");
            Require(NotBlank(evaluator.CountRepresentation, evaluator.Provenance), "EVALUATOR-PROVENANCE", $"{evaluator.EvaluatorId} has blank count/provenance");
            foreach (var capabilityId in evaluator.CapabilityIds)
            {
                Require(evaluatorIdByCapability.TryAdd(capabilityId, evaluator.EvaluatorId),
                    "CAPABILITY-EVALUATOR-CLOSURE",
                    $"{capabilityId} is assigned to more than one evaluator");
            }
        }

        var capabilitiesWithoutEvaluator = capabilityIds.Except(
                evaluatorIdByCapability.Keys,
                StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(capabilitiesWithoutEvaluator.Length == 0,
            "CAPABILITY-EVALUATOR-CLOSURE",
            $"missing=[{string.Join(',', capabilitiesWithoutEvaluator)}]");

        return new DiscoveryDefinitions(
            manifest.Goals,
            manifest.Workflows,
            manifest.Evaluators,
            goalIdsByCapability.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            workflowIdsByCapability.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.Ordinal).ToArray(),
                StringComparer.Ordinal),
            evaluatorIdByCapability);
    }

    private static ActiveToolDefinition BuildTool(
        ToolContractManifestEntry entry,
        IReadOnlyDictionary<string, DiscoveredTool> discovered,
        IReadOnlyDictionary<string, CapabilityDefinition> capabilities,
        IReadOnlyDictionary<string, EvidenceReferenceManifest> evidence,
        IReadOnlySet<string> boundaryCodes,
        PlannerAdmissionDefinition? plannerAdmission)
    {
        Require(entry.Enabled, "TOOL-INACTIVE", $"{entry.ToolName} is present but disabled; policy profiles are not implemented in Phase 2");
        Require(entry.ContractVersion == ContractVersion, "CONTRACT-VERSION", $"{entry.ToolName} must use contract 2.0");
        Require(entry.CapabilityIds.Count > 0, "TOOL-CAPABILITY", $"{entry.ToolName} has no capability");
        Require(entry.CapabilityIds.Count == 1,
            "TOOL-CAPABILITY-KEYED-OUTCOME",
            $"{entry.ToolName} maps to {entry.CapabilityIds.Count} capabilities; " +
            "multi-capability tools are forbidden until runtime outcomes are keyed by capabilityId");
        Require(entry.CapabilityIds.Distinct(StringComparer.Ordinal).Count() == entry.CapabilityIds.Count,
            "TOOL-CAPABILITY", $"{entry.ToolName} repeats a capability");
        var danglingCapabilities = entry.CapabilityIds.Where(id => !capabilities.ContainsKey(id)).ToArray();
        Require(danglingCapabilities.Length == 0, "CAPABILITY-DANGLING", $"{entry.ToolName}: {string.Join(',', danglingCapabilities)}");

        var binding = discovered[entry.ToolName];
        var method = binding.Method;
        Require(TypeIdentity(method.DeclaringType!) == entry.DeclaringType && method.Name == entry.Method,
            "TOOL-BINDING", $"{entry.ToolName} does not match {entry.DeclaringType}.{entry.Method}");
        Require(InputTypeIdentity(method) == entry.InputType, "INPUT-TYPE", $"{entry.ToolName}: expected {InputTypeIdentity(method)}, manifest {entry.InputType}");
        var effectiveOutputType = EffectiveOutputType(method);
        Require(TypeIdentity(effectiveOutputType) == entry.OutputType, "OUTPUT-TYPE", $"{entry.ToolName}: expected {TypeIdentity(effectiveOutputType)}, manifest {entry.OutputType}");

        var protocolAnnotations = binding.Tool.ProtocolTool.Annotations
            ?? throw new CatalogValidationException($"ANNOTATION-MISMATCH: {entry.ToolName} has no SDK annotations");
        Require(protocolAnnotations.ReadOnlyHint == entry.Annotations.ReadOnlyHint &&
                protocolAnnotations.IdempotentHint == entry.Annotations.IdempotentHint &&
                protocolAnnotations.OpenWorldHint == entry.Annotations.OpenWorldHint &&
                protocolAnnotations.DestructiveHint == entry.Annotations.DestructiveHint,
            "ANNOTATION-MISMATCH", $"{entry.ToolName} manifest annotations differ from SDK output");

        Require(entry.SelectableScopes.Count > 0 && entry.SelectableScopes.All(Scopes.Contains),
            "SCOPE", $"{entry.ToolName} has an invalid selectable scope");
        Require(entry.SelectableScopes.Distinct(StringComparer.Ordinal).Count() == entry.SelectableScopes.Count,
            "SCOPE", $"{entry.ToolName} repeats a selectable scope");
        ValidateSelectableScopes(entry, method);
        Require(entry.RequiredCapabilities.All(StableSemanticIdPattern.IsMatch) &&
                entry.RequiredCapabilities.Distinct(StringComparer.Ordinal).Count() == entry.RequiredCapabilities.Count,
            "REQUIRED-CAPABILITY", $"{entry.ToolName} has blank, malformed, or duplicate requiredCapabilities");
        Require(entry.SideEffects.Count > 0 && entry.SideEffects.All(SideEffectClasses.Contains), "SIDE-EFFECT", $"{entry.ToolName} has invalid side effect");
        Require(CostClasses.Contains(entry.CostClass), "COST", $"{entry.ToolName} has invalid costClass");
        Require(NotBlank(entry.Domain, entry.DefaultOrdering), "TOOL-SEMANTICS", $"{entry.ToolName} has blank domain/order");
        Require(entry.TieBreakers.All(StableSemanticIdPattern.IsMatch) &&
                entry.TieBreakers.Distinct(StringComparer.Ordinal).Count() == entry.TieBreakers.Count,
            "TIE-BREAKER", $"{entry.ToolName} has blank, malformed, or duplicate tieBreakers");
        Require(entry.Ordinal >= 0, "DISCOVERY-ORDER", $"{entry.ToolName} has a negative ordinal");
        Require(PaginationModes.Contains(entry.PaginationMode), "PAGINATION", $"{entry.ToolName} has invalid paginationMode");
        Require(entry.PageableSections.Distinct(StringComparer.Ordinal).Count() == entry.PageableSections.Count,
            "PAGEABLE-POINTER", $"{entry.ToolName} repeats a pageable pointer");
        Require(entry.PaginationMode != "none" || entry.PageableSections.Count == 0,
            "PAGINATION", $"{entry.ToolName} declares pageable sections with mode none");
        Require(entry.PaginationMode == "none" || entry.PageableSections.Count > 0,
            "PAGINATION", $"{entry.ToolName} declares pagination without a section");
        foreach (var pointer in entry.PageableSections)
            ValidateArrayPointer(entry.ToolName, effectiveOutputType, pointer);

        Require(entry.AllowedMeasurementBases.Count > 0 && entry.AllowedMeasurementBases.All(MeasurementBases.Contains),
            "MEASUREMENT-BASIS", $"{entry.ToolName} has invalid allowedMeasurementBases");
        Require(Relationships.Contains(entry.MaximumRelationship), "RELATIONSHIP", $"{entry.ToolName} has invalid maximumRelationship");
        var capabilityRelationshipMaximum = entry.CapabilityIds
            .Select(id => RelationshipRank(capabilities[id].MaximumRelationship))
            .Max();
        Require(RelationshipRank(entry.MaximumRelationship) == capabilityRelationshipMaximum,
            "RELATIONSHIP-CLOSURE",
            $"{entry.ToolName} maximumRelationship does not expose the mapped capability maximum");
        Require(entry.ConclusionRules.All(StableSemanticIdPattern.IsMatch) &&
                entry.ConclusionRules.Distinct(StringComparer.Ordinal).Count() == entry.ConclusionRules.Count,
            "CONCLUSION-RULE", $"{entry.ToolName} has blank, malformed, or duplicate conclusionRules");
        Require(entry.MaximumRelationship != "causal" || entry.ConclusionRules.Count > 0,
            "CAUSAL-RULE", $"{entry.ToolName} claims causal without a conclusion rule");
        var mappedBoundaryCodes = entry.CapabilityIds
            .SelectMany(id => capabilities[id].ConclusionBoundaryCodes)
            .ToHashSet(StringComparer.Ordinal);
        Require(entry.DoesNotProve.Count > 0 && entry.DoesNotProve.All(code =>
                    boundaryCodes.Contains(code) && mappedBoundaryCodes.Contains(code)),
            "BOUNDARY-CLOSURE", $"{entry.ToolName} has dangling or absent doesNotProve codes");
        Require(entry.EvidenceReferences.Count > 0 && entry.EvidenceReferences.All(evidence.ContainsKey),
            "EVIDENCE-DANGLING", $"{entry.ToolName} has dangling or absent evidence references");
        Require(entry.InternalAnalyzerOperations.Count > 0 && entry.InternalAnalyzerOperations.All(NotBlank),
            "ANALYZER-OPERATION", $"{entry.ToolName} has no internal analyzer operation");
        ValidateDeprecation(entry);

        return new ActiveToolDefinition(
            entry.ToolName,
            method,
            entry.InputType,
            entry.OutputType,
            effectiveOutputType,
            entry.CapabilityIds.Select(id => capabilities[id]).ToImmutableArray(),
            entry.RequiredCapabilities.ToImmutableArray(),
            entry.SelectableScopes.ToImmutableArray(),
            new ToolAnnotations(
                entry.Annotations.ReadOnlyHint,
                entry.Annotations.IdempotentHint,
                entry.Annotations.OpenWorldHint,
                entry.Annotations.DestructiveHint),
            entry.SideEffects.ToImmutableArray(),
            entry.CostClass,
            entry.DiscoveryPriority,
            entry.Domain,
            entry.Ordinal,
            entry.DefaultOrdering,
            entry.TieBreakers.ToImmutableArray(),
            entry.PageableSections.ToImmutableArray(),
            entry.PaginationMode,
            new ToolDeprecation(entry.Deprecation.State, entry.Deprecation.ReplacedBy, entry.Deprecation.RemovalContractVersion),
            entry.InternalAnalyzerOperations.ToImmutableArray(),
            entry.AllowedMeasurementBases.ToImmutableArray(),
            entry.MaximumRelationship,
            entry.ConclusionRules.ToImmutableArray(),
            entry.DoesNotProve.ToImmutableArray(),
            entry.EvidenceReferences.ToImmutableArray(),
            plannerAdmission,
            binding.OutputContract);
    }

    private static CapabilityDefinition BuildCapability(
        CapabilityManifestEntry entry,
        BenchmarkCapabilityEntry benchmark,
        IReadOnlyList<string> goalIds,
        IReadOnlyList<string> workflowIds,
        string evaluatorId) =>
        new(
            entry.CapabilityId,
            entry.DefinitionVersion,
            entry.LifecycleStatus,
            entry.Domain,
            entry.Title,
            entry.Summary,
            entry.QuestionsAnswered.ToImmutableArray(),
            entry.QuestionsNotAnswered.ToImmutableArray(),
            entry.ConclusionBoundaryCodes.ToImmutableArray(),
            entry.RequiredEvents.ToImmutableArray(),
            entry.RequiredEventStacks.ToImmutableArray(),
            entry.OptionalEvidence.ToImmutableArray(),
            entry.SymbolRequirement,
            entry.MaximumRelationship,
            entry.SupportedScopes.ToImmutableArray(),
            entry.CostClass,
            entry.SideEffectClass,
            entry.ContractVersion,
            entry.SourcePaths.ToImmutableArray(),
            entry.ReplacedBy,
            entry.RemovalContractVersion,
            benchmark.ProductMaturity,
            benchmark.EvidenceReferences.Select(reference =>
                new EvidenceReference(reference.EvidenceId, reference.Kind, reference.Path, reference.Member)).ToImmutableArray(),
            goalIds.ToImmutableArray(),
            workflowIds.ToImmutableArray(),
            evaluatorId);

    private static void ValidateToolSet(
        IReadOnlyList<ToolContractManifestEntry> manifestTools,
        IReadOnlyDictionary<string, DiscoveredTool> discovered)
    {
        var manifestNames = manifestTools.Select(item => item.ToolName).ToHashSet(StringComparer.Ordinal);
        var missing = discovered.Keys.Except(manifestNames, StringComparer.Ordinal).Order().ToArray();
        var inactive = manifestNames.Except(discovered.Keys, StringComparer.Ordinal).Order().ToArray();
        Require(missing.Length == 0 && inactive.Length == 0,
            "TOOL-SET",
            $"missing=[{string.Join(',', missing)}], inactive=[{string.Join(',', inactive)}]");
    }

    private static void ValidateCapabilityCoverage(
        IReadOnlyList<CapabilityDefinition> capabilities,
        IReadOnlyList<ActiveToolDefinition> tools)
    {
        foreach (var capability in capabilities)
        {
            var mappedTools = tools.Where(tool => tool.Capabilities.Any(candidate =>
                    candidate.CapabilityId == capability.CapabilityId))
                .ToArray();
            var count = mappedTools.Length;
            if (capability.ProductMaturity == "gap")
                Require(count == 0, "GAP-CALLABLE", $"gap capability {capability.CapabilityId} has {count} tools");
            else
            {
                Require(count > 0, "CAPABILITY-UNIMPLEMENTED", $"{capability.ProductMaturity} capability {capability.CapabilityId} has no tool");
                var scopesWithoutSelector = capability.SupportedScopes
                    .Where(scope => !mappedTools.Any(tool => tool.SelectableScopes.Contains(scope, StringComparer.Ordinal)))
                    .ToArray();
                Require(scopesWithoutSelector.Length == 0,
                    "CAPABILITY-SCOPE-CLOSURE",
                    $"{capability.CapabilityId} scopes have no mapped selectable tool: {string.Join(',', scopesWithoutSelector)}");
                var unlistedMappedScopes = mappedTools
                    .SelectMany(tool => tool.SelectableScopes)
                    .Distinct(StringComparer.Ordinal)
                    .Where(scope => !capability.SupportedScopes.Contains(scope, StringComparer.Ordinal))
                    .ToArray();
                Require(unlistedMappedScopes.Length == 0,
                    "CAPABILITY-SCOPE-CLOSURE",
                    $"{capability.CapabilityId} omits mapped selectable scopes: {string.Join(',', unlistedMappedScopes)}");
            }
        }
    }

    private static void ValidateSelectableScopes(ToolContractManifestEntry entry, MethodInfo method)
    {
        var parameters = method.GetParameters()
            .Where(parameter => !IsSdkInjectedParameter(parameter.ParameterType))
            .ToArray();

        foreach (var scope in entry.SelectableScopes)
        {
            var selectorPresent = scope switch
            {
                "thread" => HasParameter(parameters, ["tid"], IsInt32Selector),
                "time_window" =>
                    (HasParameter(parameters, ["startUs"], IsInt64Selector) &&
                     HasParameter(parameters, ["endUs"], IsInt64Selector)) ||
                    HasParameter(parameters, ["windows"], IsTimeWindowCollectionSelector),
                "focus_frame" => HasParameter(parameters, ["focusFunction", "function"], IsStringSelector),
                "provider" => HasParameter(parameters, ["providerName", "providerSubstring"], IsStringSelector),
                "process" =>
                    HasParameter(parameters, ["pid", "parentPid", "awakenedPid"], IsInt32Selector) ||
                    HasParameter(parameters, ["pids"], IsInt32CollectionSelector) ||
                    HasParameter(parameters, ["processSubstring"], IsStringSelector) ||
                    (entry.ToolName == "diagnose_slow_startup" &&
                     HasParameter(parameters, ["nameSubstring"], IsStringSelector)),
                _ => true,
            };

            Require(selectorPresent,
                "SELECTABLE-SCOPE-SCHEMA",
                $"{entry.ToolName} declares '{scope}' but its public input schema has no corresponding selector");
        }
    }

    private static bool HasParameter(
        IReadOnlyList<ParameterInfo> parameters,
        IReadOnlyList<string> names,
        Func<Type, bool> typePredicate) =>
        parameters.Any(parameter =>
            names.Contains(parameter.Name ?? "") &&
            typePredicate(parameter.ParameterType));

    private static bool IsInt32Selector(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(int);

    private static bool IsInt64Selector(Type type) =>
        (Nullable.GetUnderlyingType(type) ?? type) == typeof(long);

    private static bool IsInt32CollectionSelector(Type type) =>
        type == typeof(int[]) || typeof(IEnumerable<int>).IsAssignableFrom(type);

    private static bool IsTimeWindowCollectionSelector(Type type)
    {
        var element = type.IsArray ? type.GetElementType() : null;
        return element?.GetProperty("StartUs")?.PropertyType == typeof(long) &&
               element.GetProperty("EndUs")?.PropertyType == typeof(long);
    }

    private static bool IsStringSelector(Type type) => type == typeof(string);

    private static void ValidateDiscoveryKeys(IReadOnlyList<ActiveToolDefinition> tools)
    {
        var duplicateKeys = tools.GroupBy(tool => $"{tool.DiscoveryPriority}/{tool.Domain}/{tool.Ordinal}", StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Require(duplicateKeys.Length == 0, "DISCOVERY-ORDER", $"duplicate keys=[{string.Join(',', duplicateKeys)}]");

        var bootstrap = tools.Where(tool => tool.DiscoveryPriority == 0).Select(tool => tool.ToolName).ToArray();
        Require(bootstrap.SequenceEqual(
                ["list_capabilities", "get_tool_contract", "inspect_trace", "list_processes", "load_trace"]),
            "DISCOVERY-BOOTSTRAP",
            $"bootstrap order must be list_capabilities,get_tool_contract,inspect_trace,list_processes,load_trace; actual={string.Join(',', bootstrap)}");
        Require(tools.Skip(bootstrap.Length).All(tool => tool.DiscoveryPriority > 0),
            "DISCOVERY-BOOTSTRAP", "domain tools must follow bootstrap tools");
    }

    private static void ValidateArrayPointer(string toolName, Type outputType, string pointer)
    {
        Require(pointer.StartsWith('/') && pointer.Length > 1, "PAGEABLE-POINTER", $"{toolName} has invalid JSON pointer '{pointer}'");
        var current = outputType;
        foreach (var rawSegment in pointer.Split('/').Skip(1))
        {
            var segment = rawSegment.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            var property = current.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(candidate => string.Equals(
                    JsonNamingPolicy.CamelCase.ConvertName(candidate.Name),
                    segment,
                    StringComparison.Ordinal));
            Require(property is not null, "PAGEABLE-POINTER", $"{toolName} pointer '{pointer}' does not resolve at '{segment}'");
            current = Nullable.GetUnderlyingType(property!.PropertyType) ?? property.PropertyType;
        }

        Require(IsJsonArrayType(current), "PAGEABLE-POINTER", $"{toolName} pointer '{pointer}' does not reference an array");
    }

    private static bool IsJsonArrayType(Type type)
    {
        if (type == typeof(string) || typeof(IDictionary).IsAssignableFrom(type))
            return false;
        if (type.GetInterfaces().Any(item => item.IsGenericType &&
                                            item.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            return false;
        return typeof(IEnumerable).IsAssignableFrom(type);
    }

    private static void ValidateDeprecation(ToolContractManifestEntry entry)
    {
        Require(entry.Deprecation.State is "none" or "deprecated", "TOOL-DEPRECATION", $"{entry.ToolName} has invalid deprecation state");
        if (entry.Deprecation.State == "deprecated")
        {
            Require(NotBlank(entry.Deprecation.ReplacedBy, entry.Deprecation.RemovalContractVersion),
                "TOOL-DEPRECATION", $"{entry.ToolName} requires replacement/removal");
        }
        else
        {
            Require(entry.Deprecation.ReplacedBy is null && entry.Deprecation.RemovalContractVersion is null,
                "TOOL-DEPRECATION", $"{entry.ToolName} is not deprecated but declares replacement/removal");
        }
    }

    private static IReadOnlyList<DiscoveredTool> DiscoverTools(Assembly assembly, IServiceProvider services)
    {
        var options = new McpServerToolCreateOptions { Services = services };
        var discovered = new List<DiscoveredTool>();
        foreach (var type in assembly.GetTypes()
                     .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
                     .OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                         .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
                         .OrderBy(method => method.Name, StringComparer.Ordinal))
            {
                var tool = CreateServerTool(method, options);
                discovered.Add(new DiscoveredTool(
                    method,
                    tool,
                    ToolOutputSchemaFactory.CreateContract(
                        tool.ProtocolTool.Name,
                        EffectiveOutputType(method))));
            }
        }

        var duplicates = discovered.GroupBy(item => item.Tool.ProtocolTool.Name, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        Require(duplicates.Length == 0, "SDK-TOOL-DUPLICATE", string.Join(',', duplicates));
        return discovered;
    }

    internal static McpServerTool CreateServerTool(MethodInfo method, McpServerToolCreateOptions options)
    {
        // The SDK tool must first serialize and validate the method's raw T result so
        // ContractMcpServerTool can review it. Supplying the public Envelope<T> schema
        // here makes the SDK reject every successful raw T before the wrapper runs.
        // Keep that execution schema inferred from T, then replace only the advertised
        // protocol schema with the contract-2.0 envelope after construction.
#pragma warning disable MCPEXP001 // Preserve caller-supplied SDK execution options while cloning.
        var innerOptions = new McpServerToolCreateOptions
        {
            Services = options.Services,
            Name = options.Name,
            Description = options.Description,
            Title = options.Title,
            Destructive = options.Destructive,
            Idempotent = options.Idempotent,
            OpenWorld = options.OpenWorld,
            ReadOnly = options.ReadOnly,
            UseStructuredContent = true,
            OutputSchema = null,
            SerializerOptions = options.SerializerOptions,
            SchemaCreateOptions = options.SchemaCreateOptions,
            Metadata = options.Metadata,
            Icons = options.Icons,
            Meta = options.Meta,
            Execution = options.Execution,
        };
#pragma warning restore MCPEXP001

        McpServerTool tool;
        if (method.IsStatic)
        {
            tool = McpServerTool.Create(method, target: null, innerOptions);
        }
        else
        {
            tool = McpServerTool.Create(
                method,
                request => ActivatorUtilities.CreateInstance(
                    request.Server.Services
                        ?? innerOptions.Services
                        ?? throw new InvalidOperationException("MCP request has no service provider."),
                    method.DeclaringType!),
                innerOptions);
        }

        SymbolToolSchemaOverlay.Apply(tool, method);
        ToolExactIntegerInputOverlay.Apply(tool, method);
        ToolOpaqueLocatorInputOverlay.Apply(tool, method);
        tool.ProtocolTool.OutputSchema = ToolOutputSchemaFactory.CreateContract(
            tool.ProtocolTool.Name,
            EffectiveOutputType(method)).ToJsonElement();
        return tool;
    }

    private static void ValidateInputSchemaOverlays(ToolContractManifest manifest)
    {
        Require(manifest.InputSchemaOverlays.Count == 1,
            "INPUT-SCHEMA-OVERLAY",
            "the reviewed manifest must declare exactly one symbol-context overlay");
        var overlay = manifest.InputSchemaOverlays[0];
        Require(
            overlay.OverlayId == SymbolToolSchemaOverlay.OverlayId &&
            overlay.SelectorParameter == SymbolToolSchemaOverlay.SelectorParameter &&
            overlay.InjectedInputProperty == SymbolToolSchemaOverlay.PropertyName &&
            overlay.ExpectedToolCount == SymbolToolSchemaOverlay.ExpectedToolCount,
            "INPUT-SCHEMA-OVERLAY",
            "the reviewed symbol-context overlay differs from the executable overlay contract");
    }

    private static void ValidateAppliedInputSchemaOverlays(
        ToolContractManifest manifest,
        IReadOnlyList<DiscoveredTool> discovered)
    {
        var overlay = manifest.InputSchemaOverlays[0];
        var selected = discovered
            .Where(item => SymbolToolSchemaOverlay.AppliesTo(item.Method))
            .ToArray();
        Require(selected.Length == overlay.ExpectedToolCount,
            "INPUT-SCHEMA-OVERLAY",
            $"expected {overlay.ExpectedToolCount} tools but selected {selected.Length}");
        Require(selected.All(item =>
                SymbolToolSchemaOverlay.AdvertisesExpectedProperty(item.Tool.ProtocolTool)),
            "INPUT-SCHEMA-OVERLAY",
            "one or more selected SDK schemas do not advertise the reviewed symbolContextId contract");
        Require(discovered
                .Except(selected)
                .All(item => !SymbolToolSchemaOverlay.AdvertisesExpectedProperty(item.Tool.ProtocolTool)),
            "INPUT-SCHEMA-OVERLAY",
            "symbolContextId was advertised outside the reviewed selector");

        var exactIntegerSelected = discovered
            .Where(item => ToolExactIntegerInputOverlay.AppliesTo(item.Method))
            .ToArray();
        Require(exactIntegerSelected.All(item =>
                ToolExactIntegerInputOverlay.AdvertisesExactIntegers(item.Tool.ProtocolTool, item.Method)),
            "INPUT-SCHEMA-OVERLAY",
            "one or more Int64/UInt64 inputs do not advertise canonical decimal strings");
        Require(discovered
                .Except(exactIntegerSelected)
                .All(item => !item.Tool.ProtocolTool.InputSchema.TryGetProperty(
                    "x-exactIntegerInputOverlay",
                    out _)),
            "INPUT-SCHEMA-OVERLAY",
            "the exact-integer overlay was advertised outside its executable selector");

        Require(discovered.All(item =>
                ToolOpaqueLocatorInputOverlay.AdvertisesExpectedLocators(
                    item.Tool.ProtocolTool,
                    item.Method)),
            "INPUT-SCHEMA-OVERLAY",
            "one or more locator inputs do not advertise their canonical prefix and character grammar");

        foreach (var item in discovered)
        {
            var expected = item.OutputContract.ParseSchema();
            var actual = JsonNode.Parse(JsonSerializer.Serialize(
                item.Tool.ProtocolTool.OutputSchema,
                McpJsonUtilities.DefaultOptions));
            Require(JsonNode.DeepEquals(expected, actual),
                "OUTPUT-SCHEMA",
                $"{item.Tool.ProtocolTool.Name} does not retain its exact server-side closed envelope schema");
            var regenerated = ToolOutputSchemaFactory.CreateContract(
                item.Tool.ProtocolTool.Name,
                EffectiveOutputType(item.Method));
            Require(
                item.OutputContract == regenerated &&
                item.OutputContract.SchemaUri.EndsWith(item.OutputContract.Sha256, StringComparison.Ordinal),
                "OUTPUT-CONTRACT-REGISTRY",
                $"{item.Tool.ProtocolTool.Name} does not have a stable content-addressed output contract");
        }
    }

    private static string ComputeCatalogVersion(
        CapabilityManifest capabilityManifest,
        ToolContractManifest toolManifest,
        BenchmarkManifest benchmarkManifest,
        IEnumerable<Tool> sdkTools)
    {
        // Hash the complete reviewed inputs plus generated SDK wire metadata. Top-level
        // entity arrays are identity-sorted; order inside semantic arrays is preserved.
        var hashPayload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            CapabilityManifest = new
            {
                capabilityManifest.SchemaVersion,
                capabilityManifest.ContractVersion,
                capabilityManifest.CatalogScope,
                capabilityManifest.ExhaustiveForWpa,
                capabilityManifest.UnlistedCapabilityMeaning,
                capabilityManifest.CatalogVersionPolicy,
                Goals = capabilityManifest.Goals.OrderBy(item => item.GoalId, StringComparer.Ordinal),
                Workflows = capabilityManifest.Workflows.OrderBy(item => item.WorkflowId, StringComparer.Ordinal),
                Evaluators = capabilityManifest.Evaluators.OrderBy(item => item.EvaluatorId, StringComparer.Ordinal),
                Capabilities = capabilityManifest.Capabilities.OrderBy(item => item.CapabilityId, StringComparer.Ordinal),
            },
            ToolManifest = new
            {
                toolManifest.SchemaVersion,
                toolManifest.ContractVersion,
                InputSchemaOverlays = toolManifest.InputSchemaOverlays
                    .OrderBy(item => item.OverlayId, StringComparer.Ordinal),
                Tools = toolManifest.Tools.OrderBy(item => item.ToolName, StringComparer.Ordinal),
            },
            BenchmarkManifest = new
            {
                benchmarkManifest.SchemaVersion,
                Capabilities = benchmarkManifest.Capabilities.OrderBy(item => item.CapabilityId, StringComparer.Ordinal),
            },
            SdkTools = sdkTools.Select(tool => JsonSerializer.SerializeToElement(
                tool,
                McpJsonUtilities.DefaultOptions)),
        }, ManifestJsonOptions);
        return Convert.ToHexString(SHA256.HashData(hashPayload)).ToLowerInvariant();
    }

    private static T Deserialize<T>(string json, string label)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, ManifestJsonOptions)
                   ?? throw new CatalogValidationException($"MANIFEST-JSON: {label} deserialized to null");
        }
        catch (JsonException ex)
        {
            throw new CatalogValidationException($"MANIFEST-JSON: invalid {label}: {ex.Message}");
        }
    }

    private static void EnsureUnique<T>(IEnumerable<T> items, Func<T, string> key, string code)
    {
        var duplicates = items.GroupBy(key, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(duplicates.Length == 0, code, string.Join(',', duplicates));
    }

    private static bool IsBoundaryCode(string value) =>
        value.Length > 0 && value.All(character => character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_');

    private static bool NotBlank(string? value) => !string.IsNullOrWhiteSpace(value);
    private static bool NotBlank(params string?[] values) => values.All(NotBlank);

    private static int RelationshipRank(string relationship) => relationship switch
    {
        "descriptive" => 0,
        "temporal" => 1,
        "association" => 2,
        "attribution" => 3,
        "causal" => 4,
        _ => -1,
    };

    private static void Require(bool condition, string code, string detail)
    {
        if (!condition)
            throw new CatalogValidationException($"{code}: {detail}");
    }

    private sealed record DiscoveryDefinitions(
        IReadOnlyList<CapabilityGoalManifestEntry> Goals,
        IReadOnlyList<CapabilityWorkflowManifestEntry> Workflows,
        IReadOnlyList<CapabilityEvaluatorManifestEntry> Evaluators,
        IReadOnlyDictionary<string, IReadOnlyList<string>> GoalIdsByCapability,
        IReadOnlyDictionary<string, IReadOnlyList<string>> WorkflowIdsByCapability,
        IReadOnlyDictionary<string, string> EvaluatorIdByCapability);

    private sealed record DiscoveredTool(
        MethodInfo Method,
        McpServerTool Tool,
        ToolOutputContract OutputContract);
}

internal static class CatalogManifestLoader
{
    public static string Read(Assembly assembly, string relativePath, string resourceName)
    {
        var diskPath = Path.Combine(
            AppContext.BaseDirectory,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(diskPath))
            return File.ReadAllText(diskPath, Encoding.UTF8);

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new CatalogValidationException(
                $"MANIFEST-NOT-FOUND: '{relativePath}' was absent beside the application and embedded resource '{resourceName}' was absent");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
