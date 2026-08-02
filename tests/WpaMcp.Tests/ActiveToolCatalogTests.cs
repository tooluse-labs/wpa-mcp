using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class ActiveToolCatalogTests
{
    private const string CapabilityPath = "eng/capabilities.v1.json";
    private const string ToolPath = "eng/tool-contracts.v2.json";
    private const string BenchmarkPath = "benchmarks/capability-matrix.v1.json";
    private static readonly Lazy<IReadOnlyDictionary<string, SelectableScopeSchemaCase>> SelectableScopeSchemas =
        new(BuildSelectableScopeSchemas);

    public static IEnumerable<object[]> SelectableScopeToolCases()
    {
        var tools = ReadNode(ToolPath)["tools"]!.AsArray();
        foreach (var tool in tools)
            yield return [tool!["toolName"]!.GetValue<string>()];
    }

    [Fact]
    public void ReviewedManifests_JoinEverySdkToolExactlyOnce()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var catalogNames = catalog.Tools.Select(tool => tool.ToolName).ToArray();
        var attributedMethods = typeof(Program).Assembly.GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(McpServerToolTypeAttribute), inherit: false).Length != 0)
            .SelectMany(type => type.GetMethods().Where(method =>
                method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: false).Length != 0))
            .ToHashSet();

        Assert.Equal(62, catalog.Tools.Count);
        Assert.Equal(62, catalogNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(62, attributedMethods.Count);
        Assert.True(attributedMethods.SetEquals(catalog.Tools.Select(tool => tool.Method)));
        Assert.All(catalog.Tools, tool => Assert.Single(tool.Capabilities));
        Assert.Equal("wpa_mcp_declared_capabilities", catalog.CatalogScope);
        Assert.False(catalog.ExhaustiveForWpa);
        Assert.Equal("unknown_not_catalogued", catalog.UnlistedCapabilityMeaning);
        Assert.Matches("^[0-9a-f]{64}$", catalog.CatalogVersion);
    }

    [Theory]
    [MemberData(nameof(SelectableScopeToolCases))]
    public void SelectableScopes_AreBackedByThePublicInputSchema(string toolName)
    {
        var tool = SelectableScopeSchemas.Value[toolName];
        var properties = tool.InputSchema.GetProperty("properties");

        Assert.NotEmpty(tool.SelectableScopes);
        Assert.All(tool.SelectableScopes, scope => Assert.True(
            HasPublicSelector(toolName, properties, scope),
            $"{toolName} declares selectable scope '{scope}' without its required public selector."));
    }

    [Fact]
    public void ToolManifest_UsesSingleSelectableScopeTruth()
    {
        var tools = ReadNode(ToolPath)["tools"]!.AsArray();

        Assert.Equal(62, tools.Count);
        Assert.All(tools, tool =>
        {
            Assert.NotNull(tool!["selectableScopes"]);
            Assert.Null(tool["supportedScopes"]);
        });
    }

    [Fact]
    public void MaturityAndEvidence_AreBidirectionallyClosed()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var toolsByCapability = catalog.Tools
            .SelectMany(tool => tool.Capabilities.Select(capability => (capability.CapabilityId, tool.ToolName)))
            .ToLookup(item => item.CapabilityId, item => item.ToolName, StringComparer.Ordinal);
        var evidenceIds = catalog.Capabilities
            .SelectMany(capability => capability.EvidenceReferences)
            .Select(reference => reference.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.All(catalog.Capabilities, capability =>
        {
            Assert.NotEmpty(capability.QuestionsNotAnswered);
            Assert.NotEmpty(capability.ConclusionBoundaryCodes);
            Assert.NotEmpty(capability.EvidenceReferences);
            if (capability.ProductMaturity == "gap")
                Assert.Empty(toolsByCapability[capability.CapabilityId]);
            else
                Assert.NotEmpty(toolsByCapability[capability.CapabilityId]);
        });
        Assert.All(catalog.Tools, tool =>
            Assert.All(tool.EvidenceReferenceIds, evidenceId => Assert.Contains(evidenceId, evidenceIds)));
    }

    [Fact]
    public void SplitPredicates_UseOneExactEvaluatorTruthPerCapability()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var expected = new Dictionary<string, (
            string EvaluatorId,
            string Kind,
            string CountProperty,
            string? CompletedProperty,
            string? UnmatchedProperty,
            string? BoundaryProperty)>(
            StringComparer.Ordinal)
        {
            ["trace.process.creation"] = (
                "evaluator.process_creation",
                "event_count",
                "ObservedProcessStartEventCount",
                null, null, null),
            ["trace.thread.lifetime"] = (
                "evaluator.thread_events",
                "evidence_completion",
                "ThreadLifecycleSourceEventCount",
                "ThreadCompletedObservedLifetimeCount",
                "ThreadUnmatchedLifecycleEndpointCount",
                "ThreadInferredBoundaryCount"),
            ["clr.gc.intervals"] = (
                "evaluator.clr_gc_intervals",
                "evidence_completion",
                "ClrGcIntervalEndpointEventCount",
                "ClrGcCompletedIntervalCount",
                "ClrGcUnmatchedEndpointCount",
                "ClrGcBoundaryEvidenceCount"),
            ["clr.gc.heap_stats"] = (
                "evaluator.clr_gc_heap_stats",
                "event_count",
                "ClrGcHeapStatsEventCount",
                null, null, null),
            ["clr.finalizer.activity"] = (
                "evaluator.clr_finalizer_activity",
                "event_count",
                "ClrFinalizerSourceEventCount",
                null, null, null),
            ["network.connections"] = (
                "evaluator.network_connections",
                "evidence_completion",
                "NetworkConnectionLifecycleEndpointEventCount",
                "NetworkConnectionCompletedLifecycleCount",
                "NetworkConnectionUnmatchedEndpointCount",
                "NetworkConnectionBoundaryEvidenceCount"),
            ["clr.jit.intervals"] = (
                "evaluator.clr_jit",
                "evidence_completion",
                "ClrJitIntervalEndpointEventCount",
                "ClrJitCompletedIntervalCount",
                "ClrJitUnmatchedEndpointCount",
                "ClrJitBoundaryEvidenceCount"),
        };

        foreach (var (capabilityId, predicate) in expected)
        {
            var capability = Assert.Single(catalog.Capabilities, candidate =>
                candidate.CapabilityId == capabilityId);
            Assert.Equal(predicate.EvaluatorId, capability.EvaluatorId);
            var evaluator = Assert.Single(catalog.Evaluators, candidate =>
                candidate.EvaluatorId == predicate.EvaluatorId);
            Assert.Equal(predicate.Kind, evaluator.Kind);
            Assert.Equal([capabilityId], evaluator.CapabilityIds);
            Assert.Empty(evaluator.EventFlags);
            Assert.Equal(predicate.CountProperty, evaluator.EventCountProperty);
            Assert.Equal(predicate.CompletedProperty, evaluator.CompletedCountProperty);
            Assert.Equal(predicate.UnmatchedProperty, evaluator.UnmatchedCountProperty);
            Assert.Equal(predicate.BoundaryProperty, evaluator.BoundaryCountProperty);
        }

        var memory = Assert.Single(catalog.Evaluators, evaluator =>
            evaluator.EvaluatorId == "evaluator.memory_resources");
        Assert.Equal("event_requirements", memory.Kind);
        Assert.Equal(
            ["HasMemoryProcessInfo", "HasMemorySystemInfo", "HasHandleEvents", "HasPoolEvents"],
            memory.EventFlags);

        var inventory = Assert.Single(catalog.Evaluators, evaluator =>
            evaluator.EvaluatorId == "evaluator.process_inventory");
        Assert.Equal(["trace.process.inventory"], inventory.CapabilityIds);
        var marker = Assert.Single(catalog.Evaluators, evaluator =>
            evaluator.EvaluatorId == "evaluator.marker_query");
        Assert.DoesNotContain("clr.finalizer.activity", marker.CapabilityIds);
    }

    [Fact]
    public void SplitCapabilityManifest_StatesExactSourceEventBoundaries()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        CapabilityDefinition Capability(string id) => Assert.Single(
            catalog.Capabilities,
            candidate => candidate.CapabilityId == id);

        var inventory = Capability("trace.process.inventory");
        Assert.Empty(inventory.RequiredEvents);
        Assert.Contains("INVENTORY_ROW_NOT_OBSERVED_PROCESS_START",
            inventory.ConclusionBoundaryCodes);

        var creation = Capability("trace.process.creation");
        Assert.Equal(["Process/Start"], creation.RequiredEvents);
        Assert.Contains(creation.OptionalEvidence, item =>
            item.Contains("Image/Load", StringComparison.Ordinal));

        var threads = Capability("trace.thread.lifetime");
        Assert.Equal(
            ["Thread/Start", "Thread/Stop"],
            threads.RequiredEvents);
        Assert.Contains(threads.OptionalEvidence, item =>
            item.Contains("Thread/DCStart", StringComparison.Ordinal));
        Assert.Contains("RUNDOWN_ENDPOINT_NOT_OBSERVED_LIFECYCLE",
            threads.ConclusionBoundaryCodes);

        var connections = Capability("network.connections");
        Assert.Equal(
            [
                "TcpIp/Connect or TcpIp/Accept (IPv4/IPv6)",
                "TcpIp/Disconnect or TcpIp/Reconnect (IPv4/IPv6)",
            ],
            connections.RequiredEvents);
        Assert.DoesNotContain(connections.RequiredEvents, item =>
            item.Contains("Send", StringComparison.Ordinal) ||
            item.Contains("Recv", StringComparison.Ordinal));
        Assert.Contains("CONNECTION_LIFECYCLE_NOT_BYTE_TRANSFER",
            connections.ConclusionBoundaryCodes);

        var cpuAccounting = Capability("scheduler.cpu_accounting");
        Assert.Equal(["Thread/CSwitch"], cpuAccounting.RequiredEvents);
        Assert.Contains(cpuAccounting.OptionalEvidence, item =>
            item.Contains("ReadyThread", StringComparison.Ordinal));
        Assert.Contains("READY_LATENCY_REQUIRES_READY_THREAD",
            cpuAccounting.ConclusionBoundaryCodes);

        var memoryResources = Capability("memory.resource.activity");
        Assert.Equal(
            [
                "Memory/ProcessMemInfo",
                "Memory/SystemMemInfo or Memory/MemInfo",
                "Object/CreateHandle, CloseHandle, or DuplicateHandle",
                "Pool allocation or free events",
            ],
            memoryResources.RequiredEvents);
        Assert.DoesNotContain(memoryResources.RequiredEvents, item =>
            item.Contains("VirtualAlloc", StringComparison.Ordinal));
        Assert.Contains("PARTIAL_MEMORY_FACETS_NOT_COMPLETE_RESOURCE_VIEW",
            memoryResources.ConclusionBoundaryCodes);

        Assert.Equal(
            ["GCStart + GCStop or GCSuspendEEStart + GCRestartEEStop"],
            Capability("clr.gc.intervals").RequiredEvents);
        Assert.Equal(["GCHeapStats"], Capability("clr.gc.heap_stats").RequiredEvents);
        var finalizer = Capability("clr.finalizer.activity");
        Assert.Equal(
            ["GCFinalizeObject", "GCFinalizersStart", "GCFinalizersStop"],
            finalizer.RequiredEvents);
        Assert.Contains("BATCH_ENDPOINT_COUNT_NOT_COMPLETED_PAIR_COUNT",
            finalizer.ConclusionBoundaryCodes);
    }

    [Fact]
    public void CompletionAndRequirementEvaluators_FailClosedOnCollapsedPredicates()
    {
        var source = ReadManifestText();
        var duplicateCompletionProperty = Mutate(
            source,
            static (capabilities, _, _) =>
            {
                var evaluator = FindBy(
                    capabilities,
                    "evaluators",
                    "evaluatorId",
                    "evaluator.clr_jit");
                evaluator["boundaryCountProperty"] =
                    evaluator["unmatchedCountProperty"]!.GetValue<string>();
            });
        var duplicate = Assert.Throws<CatalogValidationException>(() =>
            ActiveToolCatalog.LoadAndValidateJson(
                duplicateCompletionProperty.Capabilities,
                duplicateCompletionProperty.Tools,
                duplicateCompletionProperty.Benchmarks));
        Assert.StartsWith(
            "EVALUATOR-EVIDENCE-COMPLETION:",
            duplicate.Message,
            StringComparison.Ordinal);

        var oneMemoryFacet = Mutate(
            source,
            static (capabilities, _, _) =>
            {
                var evaluator = FindBy(
                    capabilities,
                    "evaluators",
                    "evaluatorId",
                    "evaluator.memory_resources");
                var flags = evaluator["eventFlags"]!.AsArray();
                while (flags.Count > 1)
                    flags.RemoveAt(flags.Count - 1);
            });
        var collapsed = Assert.Throws<CatalogValidationException>(() =>
            ActiveToolCatalog.LoadAndValidateJson(
                oneMemoryFacet.Capabilities,
                oneMemoryFacet.Tools,
                oneMemoryFacet.Benchmarks));
        Assert.StartsWith(
            "EVALUATOR-EVENT-REQUIREMENTS:",
            collapsed.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoveryOrder_IsBootstrapThenDomainThenOrdinal()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        Assert.Equal(
            ["list_capabilities", "get_tool_contract", "inspect_trace", "list_processes", "load_trace"],
            catalog.Tools.Take(5).Select(tool => tool.ToolName));

        var expected = catalog.Tools
            .OrderBy(tool => tool.DiscoveryPriority)
            .ThenBy(tool => tool.Domain, StringComparer.Ordinal)
            .ThenBy(tool => tool.Ordinal)
            .ThenBy(tool => tool.ToolName, StringComparer.Ordinal)
            .Select(tool => tool.ToolName);
        Assert.Equal(expected, catalog.Tools.Select(tool => tool.ToolName));
    }

    [Fact]
    public void ToolEvidenceStrength_ClosesOverMappedCapabilityAndCompositeBases()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        Assert.All(catalog.Tools, tool => Assert.Contains(
            tool.Capabilities,
            capability => capability.MaximumRelationship == tool.MaximumRelationship));

        Assert.Equal("association", catalog.Tools.Single(tool => tool.ToolName == "wait_top_stacks").MaximumRelationship);
        Assert.Equal("association", catalog.Tools.Single(tool => tool.ToolName == "clr_contention_top_stacks").MaximumRelationship);
        Assert.Equal("association", catalog.Tools.Single(tool => tool.ToolName == "diagnose_high_wait").MaximumRelationship);
        Assert.Contains("heuristic", catalog.Tools.Single(tool => tool.ToolName == "diagnose_window").AllowedMeasurementBases);
        Assert.Contains("heuristic", catalog.Tools.Single(tool => tool.ToolName == "diagnose_slow_startup").AllowedMeasurementBases);
        Assert.DoesNotContain("causal", catalog.Tools.Select(tool => tool.MaximumRelationship));
    }

    [Fact]
    public void ProtocolFactory_PreservesValidatedOrderAndCompleteSet()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();

        var protocolTools = catalog.CreateProtocolTools(provider);

        Assert.Equal(
            catalog.Tools.Select(tool => tool.ToolName),
            protocolTools.Select(tool => tool.Name));
        Assert.Equal(62, protocolTools.Select(tool => tool.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void SdkWithTools_RegistersContractWrapperInstances()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var dependencies = new ServiceCollection();
        dependencies.AddSingleton(_ => new TraceCache());
        dependencies.AddSingleton<SymbolService>();
        using var dependencyProvider = dependencies.BuildServiceProvider();
        var tools = catalog.CreateServerTools(dependencyProvider);

        var services = new ServiceCollection();
        services.AddMcpServer().WithTools((IEnumerable<McpServerTool>)tools);
        using var provider = services.BuildServiceProvider();
        var registered = provider.GetServices<McpServerTool>().ToArray();

        Assert.Equal(tools.Count, registered.Length);
        Assert.All(registered, tool => Assert.IsType<ContractMcpServerTool>(tool));
    }

    [Fact]
    public async Task ContractWrapper_ConvertsSdkBinderFailureToStructuredInvalidArgument()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();
        var tool = catalog.CreateServerTools(provider).Single(candidate =>
            candidate.ProtocolTool.Name == "inspect_trace");
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(provider);
        var parameters = new CallToolRequestParams
        {
            Name = tool.ProtocolTool.Name,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(123),
            },
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId("binder-failure"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };

        var result = await tool.InvokeAsync(
            new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
            CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal(
            "invalid_argument",
            result.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText());
        var outputContract = Assert.Single(catalog.Tools, candidate =>
            candidate.ToolName == "inspect_trace").OutputContract;
        Assert.Empty(ToolWireSchemaValidator.Validate(
            structured,
            outputContract.ParseSchema()));
    }

    [Fact]
    public void CompatibilityProjection_PrecedesFramingAndBindsCatalogTruth()
    {
        var secureCatalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();
        var serverTools = secureCatalog.CreateServerTools(provider);
        var secureSchemas = serverTools.ToDictionary(
            tool => tool.ProtocolTool.Name,
            tool => tool.ProtocolTool.InputSchema.Clone(),
            StringComparer.Ordinal);

        var loadPath = NonNullProperty(secureSchemas["load_trace"], "path");
        Assert.Equal(ToolOpaqueLocatorInputOverlay.AbsoluteEtlPathPattern,
            loadPath.GetProperty("pattern").GetString());
        Assert.Matches(loadPath.GetProperty("pattern").GetString()!, @"C:\traces\sample.etl");
        Assert.Matches(loadPath.GetProperty("pattern").GetString()!, @"\\server\share\sample.etlx");
        Assert.DoesNotMatch(loadPath.GetProperty("pattern").GetString()!,
            "trc_0123456789abcdef0123456789abcdef");
        Assert.DoesNotMatch(loadPath.GetProperty("pattern").GetString()!, @"C:\traces\sample.txt");

        var secureQueryPath = NonNullProperty(secureSchemas["inspect_trace"], "path");
        Assert.Equal(ToolOpaqueLocatorInputOverlay.TraceIdPattern,
            secureQueryPath.GetProperty("pattern").GetString());
        Assert.DoesNotMatch(secureQueryPath.GetProperty("pattern").GetString()!, @"C:\traces\sample.etl");
        Assert.Matches(secureQueryPath.GetProperty("pattern").GetString()!,
            "trc_0123456789abcdef0123456789abcdef");
        Assert.Equal(ToolOpaqueLocatorInputOverlay.CapabilityCursorPattern,
            NonNullProperty(secureSchemas["list_capabilities"], "cursor")
                .GetProperty("pattern").GetString());
        Assert.Equal(ToolOpaqueLocatorInputOverlay.QueryCursorPattern,
            NonNullProperty(secureSchemas["inspect_trace"], "cursor")
                .GetProperty("pattern").GetString());

        var projected = secureCatalog.ProjectTraceReferenceProfile(
            TraceAccessMode.Compatibility,
            serverTools);
        var protocolByName = serverTools.ToDictionary(
            tool => tool.ProtocolTool.Name,
            StringComparer.Ordinal);

        Assert.NotEqual(secureCatalog.CatalogVersion, projected.CatalogVersion);
        Assert.All(projected.Tools.Where(tool =>
                tool.ToolName is not ("load_trace" or "unload_trace") &&
                tool.Method.GetParameters().Any(parameter => parameter.Name == "path")),
            tool =>
            {
                Assert.False(tool.Annotations.ReadOnlyHint);
                Assert.True(tool.Annotations.IdempotentHint);
                Assert.False(tool.Annotations.OpenWorldHint);
                Assert.False(tool.Annotations.DestructiveHint);
                Assert.Contains(tool.SideEffects, sideEffect =>
                    sideEffect is "raw_trace_query" or "raw_trace_stack_query");
                var protocol = protocolByName[tool.ToolName].ProtocolTool;
                Assert.Equal(
                    tool.Annotations.ReadOnlyHint,
                    protocol.Annotations!.ReadOnlyHint);
                Assert.Contains(
                    "compatibility profile",
                    protocol.InputSchema.GetRawText(),
                    StringComparison.Ordinal);
            });
        Assert.All(secureCatalog.Tools.Where(tool =>
                tool.ToolName is not ("load_trace" or "unload_trace") &&
                tool.Method.GetParameters().Any(parameter => parameter.Name == "path")),
            tool => Assert.True(tool.Annotations.ReadOnlyHint));

        var compatibilityPath = NonNullProperty(
            protocolByName["inspect_trace"].ProtocolTool.InputSchema,
            "path");
        Assert.Equal(ToolOpaqueLocatorInputOverlay.TraceOrCompatibilityPathPattern,
            compatibilityPath.GetProperty("pattern").GetString());
        Assert.Matches(compatibilityPath.GetProperty("pattern").GetString()!, @"C:\traces\sample.etl");
        Assert.Matches(compatibilityPath.GetProperty("pattern").GetString()!,
            "trc_0123456789abcdef0123456789abcdef");
    }

    [Fact]
    public async Task InstanceTargetFactory_CreatesAndDisposesOneTargetPerInvocation()
    {
        PerInvocationProbe.Reset();
        using var provider = new ServiceCollection().BuildServiceProvider();
        var method = typeof(PerInvocationProbe).GetMethod(nameof(PerInvocationProbe.Invoke))!;
        var tool = ActiveToolCatalog.CreateServerTool(
            method,
            new McpServerToolCreateOptions { Services = provider });
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(provider);
        var parameters = new CallToolRequestParams
        {
            Name = tool.ProtocolTool.Name,
            Arguments = new Dictionary<string, JsonElement>(),
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId(42),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        var context = new RequestContext<CallToolRequestParams>(server.Object, request, parameters);

        await tool.InvokeAsync(context, CancellationToken.None);
        await tool.InvokeAsync(context, CancellationToken.None);

        Assert.Equal(2, PerInvocationProbe.Created);
        Assert.Equal(2, PerInvocationProbe.Disposed);
        Assert.Equal(2, PerInvocationProbe.InstanceIds.Distinct().Count());
    }

    [Fact]
    public void NormalBuildOutputAndEmbeddedFallback_BothContainReviewedManifests()
    {
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "eng", "capabilities.v1.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "eng", "tool-contracts.v2.json")));
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "benchmarks", "capability-matrix.v1.json")));

        var assembly = typeof(Program).Assembly;
        Assert.Contains("WpaMcp.Manifests.eng.capabilities.v1.json", assembly.GetManifestResourceNames());
        Assert.Contains("WpaMcp.Manifests.eng.tool-contracts.v2.json", assembly.GetManifestResourceNames());
        Assert.Contains("WpaMcp.Manifests.benchmarks.capability-matrix.v1.json", assembly.GetManifestResourceNames());

        var fallback = CatalogManifestLoader.Read(
            assembly,
            "deliberately/absent/capabilities.v1.json",
            "WpaMcp.Manifests.eng.capabilities.v1.json");
        Assert.Contains("\"schemaVersion\": \"capabilities.v1\"", fallback, StringComparison.Ordinal);
    }

    [Fact]
    public void AllReviewedSourceAndEvidencePathsExist()
    {
        var root = FindRepositoryRoot();
        var capabilities = ReadNode(CapabilityPath);
        var benchmarks = ReadNode(BenchmarkPath);
        var paths = capabilities["capabilities"]!.AsArray()
            .SelectMany(capability => capability!["sourcePaths"]!.AsArray());

        Assert.All(paths, pathNode =>
        {
            var relativePath = pathNode!.GetValue<string>();
            Assert.True(
                File.Exists(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar))),
                $"Reviewed source/evidence path does not exist: {relativePath}");
        });

        var evidenceReferences = benchmarks["capabilities"]!.AsArray()
            .SelectMany(capability => capability!["evidenceReferences"]!.AsArray());
        Assert.All(evidenceReferences, referenceNode =>
        {
            var reference = referenceNode!.AsObject();
            var relativePath = reference["path"]!.GetValue<string>();
            var member = reference["member"]!.GetValue<string>();
            var fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fullPath), $"Evidence path does not exist: {relativePath}");
            Assert.Contains(member, File.ReadAllText(fullPath), StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CatalogVersion_IsDeterministicAndCoversAllReviewedSemantics()
    {
        var first = ActiveToolCatalog.LoadAndValidate();
        var second = ActiveToolCatalog.LoadAndValidate();
        Assert.Equal(first.CatalogVersion, second.CatalogVersion);

        var original = ReadManifestText();
        var baseline = ActiveToolCatalog.ComputeReviewedContentHash(
            original.Capabilities,
            original.Tools,
            original.Benchmarks);

        var mutations = new[]
        {
            Mutate(original, (capabilities, _, _) =>
                capabilities["capabilities"]![0]!["summary"] = "Semantically changed summary."),
            Mutate(original, (capabilities, tools, _) =>
            {
                const string replacement = "LOAD_RESULT_NOT_HANDLE_STATUS_INSPECTION_V2";
                var capability = FindBy(capabilities, "capabilities", "capabilityId", "lifecycle.trace.load");
                capability["conclusionBoundaryCodes"]!.AsArray()[0] = replacement;
                var tool = FindBy(tools, "tools", "toolName", "load_trace");
                tool["doesNotProve"]!.AsArray()[0] = replacement;
            }),
            Mutate(original, (_, tools, _) =>
            {
                var tool = FindBy(tools, "tools", "toolName", "load_trace");
                tool["annotations"]!["readOnlyHint"] = true;
            }),
            Mutate(original, (_, tools, _) =>
            {
                var sections = FindBy(tools, "tools", "toolName", "alpc_caller_callee")["pageableSections"]!.AsArray();
                var first = sections[0]!.DeepClone();
                var second = sections[1]!.DeepClone();
                sections[0] = second;
                sections[1] = first;
            }),
            Mutate(original, (_, _, benchmarks) =>
            {
                var evidence = benchmarks["capabilities"]![0]!["evidenceReferences"]![0]!;
                evidence["member"] = "Changed executable evidence member";
            }),
        };

        Assert.All(mutations, mutation => Assert.NotEqual(
            baseline,
            ActiveToolCatalog.ComputeReviewedContentHash(
                mutation.Capabilities,
                mutation.Tools,
                mutation.Benchmarks)));

        var reordered = Mutate(original, (capabilities, tools, benchmarks) =>
        {
            Reverse(capabilities["capabilities"]!.AsArray());
            Reverse(tools["tools"]!.AsArray());
            Reverse(benchmarks["capabilities"]!.AsArray());
        });
        Assert.Equal(
            baseline,
            ActiveToolCatalog.ComputeReviewedContentHash(
                reordered.Capabilities,
                reordered.Tools,
                reordered.Benchmarks));
    }

    [Fact]
    public void TraceLifecycleMap_SeparatesImplementedOperationsFromUnprovenInspectionAndPeakBounds()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var load = catalog.Capabilities.Single(capability =>
            capability.CapabilityId == "lifecycle.trace.load");
        var unload = catalog.Capabilities.Single(capability =>
            capability.CapabilityId == "lifecycle.trace.unload");
        var handle = catalog.Capabilities.Single(capability =>
            capability.CapabilityId == "lifecycle.trace.handle");
        var peak = catalog.Capabilities.Single(capability =>
            capability.CapabilityId == "lifecycle.trace.artifact_peak_bound");

        Assert.Equal("supported", load.ProductMaturity);
        Assert.Equal("owned_trace_artifact_write", load.SideEffectClass);
        Assert.Contains("LOAD_RESULT_NOT_HANDLE_STATUS_INSPECTION", load.ConclusionBoundaryCodes);
        Assert.Equal("supported", unload.ProductMaturity);
        Assert.Equal("trace_handle_retirement", unload.SideEffectClass);
        Assert.Contains("UNLOAD_RESULT_NOT_HANDLE_STATUS_INSPECTION", unload.ConclusionBoundaryCodes);

        Assert.Equal("gap", handle.ProductMaturity);
        Assert.Contains("HANDLE_OPERATIONS_NOT_STATUS_INSPECTION", handle.ConclusionBoundaryCodes);
        Assert.Empty(catalog.Tools.Where(tool => tool.Capabilities.Contains(handle)));

        Assert.Equal("gap", peak.ProductMaturity);
        Assert.Contains("RETAINED_QUOTA_NOT_WHOLE_ROOT_PEAK", peak.ConclusionBoundaryCodes);
        Assert.Contains("CONVERTER_TRANSIENT_PEAK_NOT_HARD_BOUNDED", peak.ConclusionBoundaryCodes);
        Assert.Contains(peak.EvidenceReferences, evidence =>
            evidence.Kind == "reviewed_gap" &&
            evidence.Member == "#### Physical artifact peak bound (accepted residual risk)");
        Assert.Empty(catalog.Tools.Where(tool => tool.Capabilities.Contains(peak)));

        var lifecycle = catalog.Workflows.Single(workflow =>
            workflow.WorkflowId == "workflow.trace_lifecycle");
        Assert.Contains(handle.CapabilityId, lifecycle.CapabilityIds);
        Assert.Contains(peak.CapabilityId, lifecycle.CapabilityIds);
    }

    [Theory]
    [InlineData("duplicate_tool", "TOOL-DUPLICATE")]
    [InlineData("missing_tool", "TOOL-SET")]
    [InlineData("dangling_capability", "CAPABILITY-DANGLING")]
    [InlineData("multiple_capabilities", "TOOL-CAPABILITY-KEYED-OUTCOME")]
    [InlineData("bad_capability_id", "CAPABILITY-ID")]
    [InlineData("bad_pageable_pointer", "PAGEABLE-POINTER")]
    [InlineData("annotation_mismatch", "ANNOTATION-MISMATCH")]
    [InlineData("missing_evidence", "EVIDENCE-DANGLING")]
    [InlineData("input_type_mismatch", "INPUT-TYPE")]
    [InlineData("output_type_mismatch", "OUTPUT-TYPE")]
    [InlineData("dangling_benchmark", "BENCHMARK-CLOSURE")]
    [InlineData("selectable_thread_without_tid", "SELECTABLE-SCOPE-SCHEMA")]
    [InlineData("selectable_time_without_bounds", "SELECTABLE-SCOPE-SCHEMA")]
    [InlineData("selectable_focus_without_function", "SELECTABLE-SCOPE-SCHEMA")]
    [InlineData("selectable_provider_without_provider", "SELECTABLE-SCOPE-SCHEMA")]
    [InlineData("selectable_process_without_process", "SELECTABLE-SCOPE-SCHEMA")]
    [InlineData("capability_scope_without_tool", "CAPABILITY-SCOPE-CLOSURE")]
    [InlineData("capability_scope_omits_tool", "CAPABILITY-SCOPE-CLOSURE")]
    public void InvalidFixture_FailsClosed(string fixture, string expectedCode)
    {
        var manifests = ReadManifestText();
        var mutated = Mutate(manifests, (capabilities, tools, benchmarks) =>
        {
            var toolArray = tools["tools"]!.AsArray();
            switch (fixture)
            {
                case "duplicate_tool":
                    toolArray.Add(toolArray[0]!.DeepClone());
                    break;
                case "missing_tool":
                    toolArray.RemoveAt(0);
                    break;
                case "dangling_capability":
                    toolArray[0]!["capabilityIds"]![0] = "missing.capability";
                    break;
                case "multiple_capabilities":
                    toolArray[0]!["capabilityIds"]!.AsArray().Add(
                        capabilities["capabilities"]![1]!["capabilityId"]!.GetValue<string>());
                    break;
                case "bad_capability_id":
                {
                    var capability = capabilities["capabilities"]![0]!;
                    var oldId = capability["capabilityId"]!.GetValue<string>();
                    const string badId = "Bad Capability";
                    capability["capabilityId"] = badId;
                    FindBy(benchmarks, "capabilities", "capabilityId", oldId)["capabilityId"] = badId;
                    FindByCapability(tools, oldId)["capabilityIds"]![0] = badId;
                    break;
                }
                case "bad_pageable_pointer":
                    FindBy(tools, "tools", "toolName", "alpc_top_stacks")["pageableSections"]![0] = "/notAnArray";
                    break;
                case "annotation_mismatch":
                    var current = toolArray[0]!["annotations"]!["readOnlyHint"]!.GetValue<bool>();
                    toolArray[0]!["annotations"]!["readOnlyHint"] = !current;
                    break;
                case "missing_evidence":
                    toolArray[0]!["evidenceReferences"]![0] = "evidence.does_not_exist";
                    break;
                case "input_type_mismatch":
                    toolArray[0]!["inputType"] = "parameters(wrong:System.String)";
                    break;
                case "output_type_mismatch":
                    toolArray[0]!["outputType"] = "System.Object";
                    break;
                case "dangling_benchmark":
                    benchmarks["capabilities"]!.AsArray()[0]!["capabilityId"] = "missing.benchmark_capability";
                    break;
                case "selectable_thread_without_tid":
                    FindBy(tools, "tools", "toolName", "thread_lifetime")["selectableScopes"]!.AsArray().Add("thread");
                    break;
                case "selectable_time_without_bounds":
                    FindBy(tools, "tools", "toolName", "image_load_timing")["selectableScopes"]!.AsArray().Add("time_window");
                    break;
                case "selectable_focus_without_function":
                    FindBy(tools, "tools", "toolName", "alpc_top_stacks")["selectableScopes"]!.AsArray().Add("focus_frame");
                    break;
                case "selectable_provider_without_provider":
                    FindBy(tools, "tools", "toolName", "find_marker")["selectableScopes"]!.AsArray().Add("provider");
                    break;
                case "selectable_process_without_process":
                    FindBy(tools, "tools", "toolName", "find_marker")["selectableScopes"]!.AsArray().Add("process");
                    break;
                case "capability_scope_without_tool":
                    FindBy(capabilities, "capabilities", "capabilityId", "image.load.timing")["supportedScopes"]!.AsArray().Add("time_window");
                    break;
                case "capability_scope_omits_tool":
                    FindBy(capabilities, "capabilities", "capabilityId", "ipc.alpc.stacks")["supportedScopes"]!.AsArray().RemoveAt(1);
                    break;
                default:
                    throw new InvalidOperationException(fixture);
            }
        });

        var exception = Assert.Throws<CatalogValidationException>(() =>
            ActiveToolCatalog.LoadAndValidateJson(
                mutated.Capabilities,
                mutated.Tools,
                mutated.Benchmarks));
        Assert.StartsWith(expectedCode + ":", exception.Message, StringComparison.Ordinal);
    }

    private static ManifestText ReadManifestText() => new(
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), CapabilityPath.Replace('/', Path.DirectorySeparatorChar))),
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), ToolPath.Replace('/', Path.DirectorySeparatorChar))),
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), BenchmarkPath.Replace('/', Path.DirectorySeparatorChar))));

    private static JsonObject ReadNode(string relativePath) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar))))!.AsObject();

    private static ManifestText Mutate(
        ManifestText source,
        Action<JsonObject, JsonObject, JsonObject> mutation)
    {
        var capabilities = JsonNode.Parse(source.Capabilities)!.AsObject();
        var tools = JsonNode.Parse(source.Tools)!.AsObject();
        var benchmarks = JsonNode.Parse(source.Benchmarks)!.AsObject();
        mutation(capabilities, tools, benchmarks);
        return new ManifestText(
            capabilities.ToJsonString(),
            tools.ToJsonString(),
            benchmarks.ToJsonString());
    }

    private static JsonNode FindBy(
        JsonObject root,
        string arrayName,
        string propertyName,
        string value) => root[arrayName]!.AsArray().Single(node =>
            string.Equals(node![propertyName]!.GetValue<string>(), value, StringComparison.Ordinal))!;

    private static JsonNode FindByCapability(JsonObject tools, string capabilityId) =>
        tools["tools"]!.AsArray().First(node =>
            node!["capabilityIds"]!.AsArray().Any(id =>
                string.Equals(id!.GetValue<string>(), capabilityId, StringComparison.Ordinal)))!;

    private static IReadOnlyDictionary<string, SelectableScopeSchemaCase> BuildSelectableScopeSchemas()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();
        var schemas = catalog.CreateProtocolTools(provider).ToDictionary(
            tool => tool.Name,
            tool => tool.InputSchema.Clone(),
            StringComparer.Ordinal);

        return catalog.Tools.ToDictionary(
            tool => tool.ToolName,
            tool => new SelectableScopeSchemaCase(tool.SelectableScopes, schemas[tool.ToolName]),
            StringComparer.Ordinal);
    }

    private static bool HasPublicSelector(string toolName, JsonElement properties, string scope)
    {
        bool Has(string name) => properties.TryGetProperty(name, out _);

        return scope switch
        {
            "thread" => Has("tid"),
            "time_window" => Has("startUs") && Has("endUs") || Has("windows"),
            "focus_frame" => Has("focusFunction") || Has("function"),
            "provider" => Has("providerName") || Has("providerSubstring"),
            "process" =>
                Has("pid") || Has("pids") || Has("parentPid") || Has("awakenedPid") ||
                Has("processSubstring") ||
                (toolName == "diagnose_slow_startup" && Has("nameSubstring")),
            _ => true,
        };
    }

    private static JsonElement NonNullProperty(JsonElement schema, string propertyName)
    {
        var property = schema.GetProperty("properties").GetProperty(propertyName);
        if (!property.TryGetProperty("anyOf", out var alternatives))
            return property;
        return alternatives.EnumerateArray().Single(item =>
            !item.TryGetProperty("type", out var type) || type.GetString() != "null");
    }

    private sealed record SelectableScopeSchemaCase(
        IReadOnlyList<string> SelectableScopes,
        JsonElement InputSchema);

    private static void Reverse(JsonArray array)
    {
        var reversed = array.Select(node => node!.DeepClone()).Reverse().ToArray();
        array.Clear();
        foreach (var node in reversed)
            array.Add(node);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate WpaMcp.sln from the test output directory.");
    }

    private sealed record ManifestText(string Capabilities, string Tools, string Benchmarks);

    private sealed class PerInvocationProbe : IDisposable
    {
        private static int _nextId;
        private static int _created;
        private static int _disposed;
        private static readonly List<int> SeenIds = [];
        private readonly int _id;

        public PerInvocationProbe()
        {
            _id = Interlocked.Increment(ref _nextId);
            Interlocked.Increment(ref _created);
        }

        public static int Created => Volatile.Read(ref _created);
        public static int Disposed => Volatile.Read(ref _disposed);
        public static IReadOnlyList<int> InstanceIds => SeenIds;

        [McpServerTool]
        public string Invoke()
        {
            lock (SeenIds)
                SeenIds.Add(_id);
            return _id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public void Dispose() => Interlocked.Increment(ref _disposed);

        public static void Reset()
        {
            Volatile.Write(ref _nextId, 0);
            Volatile.Write(ref _created, 0);
            Volatile.Write(ref _disposed, 0);
            lock (SeenIds)
                SeenIds.Clear();
        }
    }
}
