using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class CapabilityDiscoveryTests
{
    private static readonly Lazy<int> DiscoveryMinimumFrameBytes = new(() =>
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tools = catalog.CreateServerTools(new DeferredCatalogServiceProvider());
        return ToolContractDiscoveryPreflight.Measure(catalog, tools).MinimumViableFrameBytes;
    });

    [Fact]
    public void CapabilityPages_AtMinimumBudget_AreOrderedCompleteAndDuplicateFree()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: DiscoveryMinimumFrameBytes.Value);
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var page = runtime.List(domain: null, goal: null, cursor);
            Assert.NotEmpty(page.Capabilities);
            Assert.Equal(catalog.CatalogVersion, page.CatalogVersion);
            Assert.False(page.ExhaustiveForWpa);
            Assert.Equal("unknown_not_catalogued", page.UnlistedCapabilityMeaning);
            seen.AddRange(page.Capabilities.Select(capability => capability.CapabilityId));
            cursor = page.NextCursor;
        } while (cursor is not null);

        var expected = catalog.Capabilities
            .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => capability.CapabilityId)
            .ToArray();
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Filters_AreNormalizedAndCursorIsBoundToPrincipalCatalogAndFilter()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: DiscoveryMinimumFrameBytes.Value);
        var first = runtime.List("  CLR  ", goal: null, cursor: null);

        Assert.Equal("clr", first.NormalizedFilter.Domain);
        Assert.All(first.Capabilities, capability => Assert.Equal("clr", capability.Domain));
        if (first.NextCursor is { } cursor)
        {
            var wrongFilter = Assert.Throws<CapabilityCursorException>(() =>
                runtime.List("cpu", goal: null, cursor));
            Assert.Equal(CapabilityCursorFailureKind.Invalid, wrongFilter.Kind);

            var otherPrincipal = new CapabilityDiscoveryRuntime(
                catalog,
                new StdioSessionPrincipal(),
                maxResponseFrameBytes: DiscoveryMinimumFrameBytes.Value);
            var wrongPrincipal = Assert.Throws<CapabilityCursorException>(() =>
                otherPrincipal.List("clr", goal: null, cursor));
            Assert.Equal(CapabilityCursorFailureKind.Invalid, wrongPrincipal.Kind);
        }
        Assert.Throws<ArgumentException>(() => runtime.List("évidence", null, null));
    }

    [Fact]
    public void CursorRegistryCapacity_HasTypedBudgetFailure()
    {
        var registry = new CapabilityCursorRegistry(maxActive: 1);
        var first = new CapabilityCursorBinding(
            "principal",
            "catalog",
            null,
            null,
            "ordering");
        var second = first with { Goal = "cpu_hotspots" };
        Assert.NotNull(registry.GetOrIssueContinuation(first, parentToken: null, nextIndex: 1));
        var exception = Assert.Throws<CapabilityCursorException>(() =>
            registry.GetOrIssueContinuation(second, parentToken: null, nextIndex: 1));
        Assert.Equal(CapabilityCursorFailureKind.RegistryCapacity, exception.Kind);
    }

    [Fact]
    public void SameSourceResourceShards_HaveCanonicalHashAndExactUnion()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal());
        var full = runtime.FullSnapshot();
        var capabilityIndex = runtime.CapabilityResourceIndex();
        var capabilityUnion = capabilityIndex.Shards
            .SelectMany(shard => runtime.CapabilityDomainSnapshot(shard.Key).Capabilities)
            .Select(capability => capability.CapabilityId)
            .ToArray();
        var expectedCapabilities = full.Capabilities
            .Select(capability => capability.CapabilityId)
            .ToArray();

        Assert.Equal(full.CanonicalContentHash, capabilityIndex.CanonicalContentHash);
        Assert.Equal(expectedCapabilities, capabilityUnion);
        Assert.Equal(capabilityIndex.TotalItems, capabilityUnion.Length);

        var toolIndex = runtime.ToolResourceIndex();
        var toolUnion = toolIndex.Shards
            .SelectMany(shard => runtime.ToolDomainSnapshot(shard.Key).Tools)
            .Select(tool => tool.ToolName)
            .ToArray();
        Assert.Equal(catalog.Tools.Select(tool => tool.ToolName), toolUnion);
        Assert.Equal(full.CanonicalContentHash, toolIndex.CanonicalContentHash);
        Assert.Equal(toolIndex.TotalItems, toolUnion.Length);
    }

    [Fact]
    public void ScopeMetadata_ExposesStableToolAndCapabilitySemantics()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(catalog, new StdioSessionPrincipal());
        var snapshot = runtime.FullSnapshot();

        Assert.All(snapshot.Capabilities, capability => Assert.Equal(
            CatalogScopeSemantics.CapabilitySupportedScopes,
            capability.SupportedScopesSemantics));
        Assert.All(runtime.ToolCatalogSnapshot().Tools, tool => Assert.Equal(
            CatalogScopeSemantics.ToolSelectableScopes,
            tool.SelectableScopesSemantics));

        var firstShard = runtime.ToolResourceIndex().Shards[0];
        var pageIndex = JsonSerializer.Deserialize<CatalogResourcePageIndexRecord>(
            runtime.ToolDomainResource(firstShard.Key).Text,
            McpJsonUtilities.DefaultOptions)!;
        var resource = runtime.ToolDomainPageResource(firstShard.Key, pageIndex.Pages[0].Page);
        Assert.Contains("\"selectableScopes\":", resource.Text, StringComparison.Ordinal);
        Assert.Contains("\"selectableScopesSemantics\":", resource.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("\"supportedScopes\":", resource.Text, StringComparison.Ordinal);

        var capabilitySchema = ToolOutputSchemaFactory
            .CreateEnvelopeSchema<ListCapabilitiesResponse>()
            .ToJsonString();
        Assert.Contains("supportedScopesSemantics", capabilitySchema, StringComparison.Ordinal);
        Assert.Contains("at least one mapped tool", capabilitySchema, StringComparison.Ordinal);

        var toolResourceSchema = ToolOutputSchemaFactory
            .CreateEnvelopeSchema<ServerToolCatalogResource>()
            .ToJsonString();
        Assert.Contains("selectableScopesSemantics", toolResourceSchema, StringComparison.Ordinal);
        Assert.Contains("public input schema", toolResourceSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("\"supportedScopes\"", toolResourceSchema, StringComparison.Ordinal);
    }

    [Fact]
    public void ToolCapabilityMap_ExposesCompletePerSectionEvidenceAndOrderingContracts()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(catalog, new StdioSessionPrincipal());
        var tools = runtime.ToolCatalogSnapshot().Tools;

        foreach (var definition in catalog.Tools)
        {
            var projected = Assert.Single(tools, tool => tool.ToolName == definition.ToolName);
            Assert.Equal(
                definition.PageableSections.Order(StringComparer.Ordinal),
                projected.SectionContracts.Select(section => section.SectionPointer)
                    .Order(StringComparer.Ordinal));
            Assert.Equal(
                projected.SectionContracts.Count,
                projected.SectionContracts.Select(section => section.SectionPointer)
                    .Distinct(StringComparer.Ordinal).Count());
            Assert.All(projected.SectionContracts, section =>
            {
                Assert.DoesNotContain("section_defined", section.TieBreakers);
                Assert.DoesNotContain("stable_identity", section.TieBreakers);
                Assert.False(string.IsNullOrWhiteSpace(section.ProofMode));
                Assert.False(string.IsNullOrWhiteSpace(section.LimitSource));
                if (section.SortKey is null)
                {
                    Assert.Equal(ToolSortDirection.NotApplicable, section.SortDirection);
                    Assert.Empty(section.TieBreakers);
                }
                else
                {
                    Assert.NotEqual(ToolSortDirection.NotApplicable, section.SortDirection);
                }
            });
        }

        var memory = Assert.Single(tools, tool => tool.ToolName == "memory_resource_analysis");
        Assert.True(memory.SectionContracts.Select(section => section.SortKey)
            .Distinct(StringComparer.Ordinal).Count() > 1);
        Assert.All(memory.SectionContracts, section => Assert.NotEmpty(section.EvidenceReferenceIds));

        var inspect = Assert.Single(tools, tool => tool.ToolName == "inspect_trace");
        Assert.All(
            inspect.SectionContracts.Where(section => section.Role is
                ToolSectionRole.Boundary or ToolSectionRole.Provenance or ToolSectionRole.Recommendation),
            section =>
            {
                Assert.Equal(MeasurementBasis.Unmeasured, section.MeasurementBasis);
                Assert.Equal(Relationship.Descriptive, section.Relationship);
                Assert.Equal(ConclusionStatus.NotApplicable, section.DeclaredConclusionStatus);
                Assert.Empty(section.EvidenceReferenceIds);
            });

        var inspectDefinition = Assert.Single(catalog.Tools, tool => tool.ToolName == "inspect_trace");
        var pageIndex = JsonSerializer.Deserialize<CatalogResourcePageIndexRecord>(
            runtime.ToolDomainResource(inspectDefinition.Domain).Text,
            McpJsonUtilities.DefaultOptions)!;
        var resourceTools = pageIndex.Pages
            .SelectMany(page => JsonSerializer.Deserialize<ServerToolResourceShardResource>(
                    runtime.ToolDomainPageResource(inspectDefinition.Domain, page.Page).Text,
                    McpJsonUtilities.DefaultOptions)!
                .Tools)
            .ToArray();
        var resourceInspect = Assert.Single(
            resourceTools,
            tool => tool.ToolName == "inspect_trace");
        Assert.Equal(
            "wpa://tools/inspect_trace/sections",
            resourceInspect.SectionContractsResourceUri);
        Assert.Equal("complete_in_linked_resource", resourceInspect.SectionContractCompleteness);

        var sectionIndex = runtime.ToolSectionContractPageIndex("inspect_trace");
        var linkedSections = sectionIndex.Pages
            .SelectMany(page => JsonSerializer.Deserialize<ServerToolSectionContractPageResource>(
                    runtime.ToolSectionContractPageResource("inspect_trace", page.Page).Text,
                    McpJsonUtilities.DefaultOptions)!
                .SectionContracts)
            .ToArray();
        var expectedLinkedSections = inspect.SectionContracts
            .OrderBy(section => section.SectionPointer)
            .ToArray();
        Assert.Equal(
            expectedLinkedSections.Select(section => section.SectionPointer),
            linkedSections.Select(section => section.SectionPointer));
        for (var index = 0; index < expectedLinkedSections.Length; index++)
        {
            var expected = expectedLinkedSections[index];
            var actual = linkedSections[index];
            Assert.Equal(expected.Role, actual.Role);
            Assert.Equal(expected.Mode, actual.Mode);
            Assert.Equal(expected.ProofMode, actual.ProofMode);
            Assert.Equal(expected.LimitSource, actual.LimitSource);
            Assert.Equal(expected.SortKey, actual.SortKey);
            Assert.Equal(expected.SortDirection, actual.SortDirection);
            Assert.Equal(expected.TieBreakers, actual.TieBreakers);
            Assert.Equal(expected.MeasurementBasis, actual.MeasurementBasis);
            Assert.Equal(expected.Relationship, actual.Relationship);
            Assert.Equal(expected.DeclaredConclusionStatus, actual.DeclaredConclusionStatus);
            Assert.Equal(expected.EvidenceReferenceIds, actual.EvidenceReferenceIds);
        }
        var linkedResource = runtime.ToolSectionContractPageResource(
            "inspect_trace",
            sectionIndex.Pages[0].Page);
        Assert.Contains("\"sectionContracts\":", linkedResource.Text, StringComparison.Ordinal);
        Assert.Contains("\"proofMode\":", linkedResource.Text, StringComparison.Ordinal);
        Assert.Contains("\"declaredConclusionStatus\":", linkedResource.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCatalogAndSectionResourceShard_FitsConfiguredMinimumReadResourceFrameBudget()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var runtime = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: DiscoveryMinimumFrameBytes.Value);
        var resources = new List<TextResourceContents>
        {
            runtime.RuntimeProfileResource(),
            runtime.CapabilityPolicyIndexResource(),
            runtime.CapabilityIndexResource(),
            runtime.ToolIndexResource(),
            runtime.WorkflowResource(),
        };
        var policyIndex = runtime.CapabilityPolicyResourceIndex();
        Assert.Equal("complete_empty", policyIndex.Completeness);
        Assert.Empty(policyIndex.Pages);
        var capabilityIds = new List<string>();
        var capabilityDetails = new Dictionary<string, ServerCapabilityRecord>(StringComparer.Ordinal);
        var capabilityDetailJson = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        var toolNames = new List<string>();
        var workflowIds = new List<string>();
        var sectionPointersByTool = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        resources.AddRange(runtime.CapabilityResourceIndex().Shards.Select(shard =>
            runtime.CapabilityDomainResource(shard.Key)));
        resources.AddRange(runtime.ToolResourceIndex().Shards.Select(shard =>
            runtime.ToolDomainResource(shard.Key)));
        foreach (var shard in runtime.CapabilityResourceIndex().Shards)
        {
            var index = JsonSerializer.Deserialize<CatalogResourcePageIndexRecord>(
                runtime.CapabilityDomainResource(shard.Key).Text,
                McpJsonUtilities.DefaultOptions)!;
            foreach (var page in index.Pages)
            {
                var resource = runtime.CapabilityDomainPageResource(shard.Key, page.Page);
                resources.Add(resource);
                var data = JsonSerializer.Deserialize<ServerCapabilityCatalogShardResource>(
                    resource.Text,
                    McpJsonUtilities.DefaultOptions)!;
                Assert.Equal(
                    "summary_complete_details_in_linked_resource",
                    data.CapabilityRecordCompleteness);
                foreach (var capability in data.Capabilities)
                {
                    capabilityIds.Add(capability.CapabilityId);
                    Assert.Equal("complete_in_linked_resource", capability.DetailCompleteness);
                    Assert.Equal(
                        $"wpa://capabilities/detail/{capability.CapabilityId}",
                        capability.DetailsResourceUri);
                    var detail = runtime.CapabilityDetailResource(capability.CapabilityId);
                    resources.Add(detail);
                    capabilityDetailJson.Add(capability.CapabilityId, JsonNode.Parse(detail.Text)!);
                    capabilityDetails.Add(
                        capability.CapabilityId,
                        JsonSerializer.Deserialize<ServerCapabilityRecord>(
                            detail.Text,
                            McpJsonUtilities.DefaultOptions)!);
                }
            }
        }
        foreach (var shard in runtime.ToolResourceIndex().Shards)
        {
            var index = JsonSerializer.Deserialize<CatalogResourcePageIndexRecord>(
                runtime.ToolDomainResource(shard.Key).Text,
                McpJsonUtilities.DefaultOptions)!;
            foreach (var page in index.Pages)
            {
                var resource = runtime.ToolDomainPageResource(shard.Key, page.Page);
                resources.Add(resource);
                var data = JsonSerializer.Deserialize<ServerToolResourceShardResource>(
                    resource.Text,
                    McpJsonUtilities.DefaultOptions)!;
                Assert.Equal(
                    "summary_complete_details_in_linked_resource",
                    data.ToolRecordCompleteness);
                foreach (var tool in data.Tools)
                {
                    toolNames.Add(tool.ToolName);
                    Assert.Equal("complete_in_linked_resource", tool.DetailCompleteness);
                    Assert.Equal($"wpa://tools/detail/{tool.ToolName}", tool.DetailsResourceUri);
                    resources.Add(runtime.ToolDetailResource(tool.ToolName));
                }
            }
        }
        foreach (var shard in runtime.WorkflowResourceIndex().Shards)
        {
            var resource = runtime.WorkflowShardResource(shard.Key);
            resources.Add(resource);
            workflowIds.Add(JsonSerializer.Deserialize<CapabilityWorkflowCatalogShardResource>(
                resource.Text,
                McpJsonUtilities.DefaultOptions)!.WorkflowId);
        }
        foreach (var tool in runtime.ToolCatalogSnapshot().Tools)
        {
            var indexResource = runtime.ToolSectionContractIndexResource(tool.ToolName);
            resources.Add(indexResource);
            var index = JsonSerializer.Deserialize<CatalogResourcePageIndexRecord>(
                indexResource.Text,
                McpJsonUtilities.DefaultOptions)!;
            var pointers = new List<string>();
            foreach (var page in index.Pages)
            {
                var resource = runtime.ToolSectionContractPageResource(tool.ToolName, page.Page);
                resources.Add(resource);
                pointers.AddRange(JsonSerializer.Deserialize<ServerToolSectionContractPageResource>(
                        resource.Text,
                        McpJsonUtilities.DefaultOptions)!
                    .SectionContracts.Select(section => section.SectionPointer));
            }
            sectionPointersByTool.Add(tool.ToolName, pointers);
        }

        Assert.Equal(
            catalog.Capabilities.OrderBy(capability => capability.Domain, StringComparer.Ordinal)
                .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
                .Select(capability => capability.CapabilityId),
            capabilityIds);
        Assert.Equal(catalog.Tools.Select(tool => tool.ToolName), toolNames);
        Assert.Equal(
            catalog.Workflows.OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .Select(workflow => workflow.WorkflowId),
            workflowIds);
        Assert.Equal(capabilityIds.Count, capabilityIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(toolNames.Count, toolNames.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(catalog.Capabilities.Count, capabilityDetails.Count);
        foreach (var pair in capabilityDetails)
        {
            Assert.Equal(pair.Key, pair.Value.CapabilityId);
            Assert.False(string.IsNullOrWhiteSpace(pair.Value.Summary));
            Assert.True(JsonNode.DeepEquals(
                capabilityDetailJson[pair.Key],
                JsonSerializer.SerializeToNode(pair.Value, McpJsonUtilities.DefaultOptions)));
        }
        Assert.All(runtime.ToolCatalogSnapshot().Tools, tool => Assert.Equal(
            tool.SectionContracts.Select(section => section.SectionPointer)
                .Order(StringComparer.Ordinal),
            sectionPointersByTool[tool.ToolName]));
        Assert.InRange(
            runtime.MaximumPreflightResourceFrameBytes,
            1,
            DiscoveryMinimumFrameBytes.Value);

        foreach (var resource in resources)
        {
            foreach (var requestId in new[]
                     {
                         new RequestId(new string('r', 126)),
                         new RequestId(new string('界', 21)),
                     })
            {
                Assert.Equal(
                    ToolRequestIdPolicy.MaxSerializedBytes,
                    ToolRequestIdPolicy.SerializedBytes(requestId));
                var frameBytes = MeasureResourceFrame(resource, requestId);
                Assert.True(
                    frameBytes <= DiscoveryMinimumFrameBytes.Value,
                    $"{resource.Uri} serialized to {frameBytes} bytes.");
            }
        }
    }

    [Fact]
    public void RestrictedPolicy_AtMinimumBudget_IsCompleteLinkedAndNotRepeatedInResourceHeaders()
    {
        var full = ActiveToolCatalog.LoadAndValidate();
        var disabledCapabilityIds = full.Capabilities
            .Select(capability => capability.CapabilityId)
            .Where(capabilityId => capabilityId is not
                "catalog.capability.list" and not "trace.capability.inspect")
            .Order(StringComparer.Ordinal)
            .ToArray();
        var policy = CapabilityPolicyProfile.Parse(
            string.Join(',', disabledCapabilityIds),
            "test");
        var projected = full.ProjectCapabilityPolicy(
            policy,
            full.CreateServerTools(new DeferredCatalogServiceProvider()));
        var runtime = new CapabilityDiscoveryRuntime(
            projected.Catalog,
            new StdioSessionPrincipal(),
            maxResponseFrameBytes: DiscoveryMinimumFrameBytes.Value);
        Assert.InRange(
            runtime.MaximumPreflightResourceFrameBytes,
            1,
            DiscoveryMinimumFrameBytes.Value);

        var policyResource = runtime.CapabilityPolicyIndexResource();
        var policyIndex = JsonSerializer.Deserialize<CapabilityPolicyResourceIndex>(
            policyResource.Text,
            McpJsonUtilities.DefaultOptions)!;
        Assert.Equal("complete_in_listed_pages", policyIndex.Completeness);
        Assert.Equal(disabledCapabilityIds.Length, policyIndex.TotalDisabledCapabilities);

        var reconstructed = new List<string>();
        var resources = new List<TextResourceContents> { policyResource };
        foreach (var page in policyIndex.Pages)
        {
            var resource = runtime.CapabilityPolicyPageResource(page.Page);
            resources.Add(resource);
            var data = JsonSerializer.Deserialize<CapabilityPolicyResourcePage>(
                resource.Text,
                McpJsonUtilities.DefaultOptions)!;
            Assert.Equal("complete_page", data.Completeness);
            Assert.Equal(data.ReturnedDisabledCapabilities, data.DisabledCapabilityIds.Count);
            reconstructed.AddRange(data.DisabledCapabilityIds);
        }

        Assert.Equal(disabledCapabilityIds, reconstructed);
        Assert.Equal(reconstructed.Count, reconstructed.Distinct(StringComparer.Ordinal).Count());
        var capabilityIndex = runtime.CapabilityIndexResource();
        resources.Add(capabilityIndex);
        var serializedHeader = JsonNode.Parse(capabilityIndex.Text)!["capabilityPolicy"]!;
        Assert.Equal(disabledCapabilityIds.Length,
            serializedHeader["disabledCapabilityCount"]!.GetValue<int>());
        Assert.Equal("wpa://capabilities/policy",
            serializedHeader["disabledCapabilityIdsResourceUri"]!.GetValue<string>());
        Assert.Null(serializedHeader["disabledCapabilityIds"]);

        foreach (var resource in resources)
        {
            var frameBytes = MeasureResourceFrame(
                resource,
                new RequestId(new string('r', 126)));
            Assert.True(
                frameBytes <= DiscoveryMinimumFrameBytes.Value,
                $"{resource.Uri} serialized to {frameBytes} bytes.");
        }
    }

    [Fact]
    public async Task ProductionWrapper_NormalBudgetPagesAreCompleteConsistentAndDuplicateFree()
    {
        const int frameBudget = 85_343;
        var fixture = CreateProductionFixture(
            maxActiveCursors: 1_024,
            maxResponseBytes: frameBudget);
        using var services = fixture.Services;
        var seen = new List<string>();
        string? cursor = null;
        do
        {
            var result = await InvokeList(
                fixture.Tool,
                services,
                new RequestId(new string('界', 21)),
                cursor);
            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            Assert.True(ToolResponseFrameFitter.MeasureFrame(
                new RequestId(new string('界', 21)),
                result) <= frameBudget);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            var envelope = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), envelope));
            var data = envelope["data"]!.AsObject();
            var rows = data["capabilities"]!.AsArray();
            var returned = data["totals"]!["returnedCapabilities"]!.GetValue<int>();
            var section = Assert.IsType<JsonObject>(
                Assert.Single(envelope["sections"]!.AsArray()));
            Assert.Equal(rows.Count, returned);
            Assert.Equal(rows.Count.ToString(), section["returned"]!.GetValue<string>());
            seen.AddRange(rows.Select(row => row!["capabilityId"]!.GetValue<string>()));
            cursor = data["nextCursor"]?.GetValue<string>();
        } while (cursor is not null);

        var expected = fixture.Catalog.Capabilities
            .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => capability.CapabilityId)
            .ToArray();
        Assert.Equal(expected, seen);
        Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ProductionWrapper_MinimumBudgetReturnsAtomicStructuredFailureWhenOneRowCannotFit()
    {
        var fixture = CreateProductionFixture(
            maxActiveCursors: 1_024,
            maxResponseBytes: ToolResponseBudgetOptions.MinimumResponseFrameBytes,
            runtimeFrameBytes: DiscoveryMinimumFrameBytes.Value);
        using var services = fixture.Services;
        var requestId = new RequestId(new string('r', 126));

        var result = await InvokeList(fixture.Tool, services, requestId, cursor: null);

        Assert.True(result.IsError);
        Assert.True(ToolResponseFrameFitter.MeasureFrame(requestId, result) <=
            ToolResponseBudgetOptions.MinimumResponseFrameBytes);
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
        var text = JsonNode.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)!.AsObject();
        var envelope = structured.AsObject();
        Assert.Equal("failed", text["status"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(text["error"], envelope["error"]));
        Assert.Equal("response_too_large", envelope["error"]!["code"]!.GetValue<string>());
        Assert.Null(envelope["data"]);
        Assert.False(envelope["hasMore"]!.GetValue<bool>());
        Assert.Empty(envelope["sections"]!.AsArray());
    }

    [Fact]
    public async Task ProductionWrapper_MinimumBudgetFailureRemainsStructuredAndFits()
    {
        var fixture = CreateProductionFixture(
            maxActiveCursors: 1,
            maxResponseBytes: ToolResponseBudgetOptions.MinimumResponseFrameBytes,
            runtimeFrameBytes: DiscoveryMinimumFrameBytes.Value);
        using var services = fixture.Services;

        var result = await InvokeList(
            fixture.Tool,
            services,
            new RequestId(new string('r', 126)),
            CapabilityCursorRegistry.Prefix + new string('f', 32));

        Assert.True(result.IsError);
        Assert.Equal(
            "invalid_cursor",
            result.StructuredContent?.GetProperty("error").GetProperty("code").GetString());
        Assert.True(ToolResponseFrameFitter.MeasureFrame(
            new RequestId(new string('r', 126)),
            result) <= ToolResponseBudgetOptions.MinimumResponseFrameBytes);
        var text = JsonNode.Parse(
            Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text)!.AsObject();
        var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
        Assert.Equal("failed", text["status"]!.GetValue<string>());
        Assert.True(JsonNode.DeepEquals(text["error"], structured["error"]));
    }

    [Fact]
    public async Task ProductionWrapper_RetryOneThousandTimesDoesNotLeakCursorQuota()
    {
        var fixture = CreateProductionFixture(
            maxActiveCursors: 1,
            maxResponseBytes: 85_343);
        using var services = fixture.Services;
        string? stableCursor = null;
        for (var index = 0; index < 1_000; index++)
        {
            var requestId = index % 2 == 0
                ? new RequestId(new string('r', 126))
                : new RequestId(new string('界', 21));
            var result = await InvokeList(fixture.Tool, services, requestId, cursor: null);
            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            var envelope = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
            var cursor = envelope["data"]!["nextCursor"]!.GetValue<string>();
            stableCursor ??= cursor;
            Assert.Equal(stableCursor, cursor);
        }
    }

    [Fact]
    public void PlannerAdmissions_AreStrictAndDoNotPromoteMissingEvidence()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var inspect = catalog.Tools.Single(tool => tool.ToolName == "inspect_trace")
            .PlannerAdmission!;
        Assert.Equal("approved", inspect.AdmissionStatus);
        Assert.Equal("trace-facts.v1", inspect.OperationVersion);
        Assert.Equal(1, inspect.PhysicalPassLimit);
        Assert.Equal(2, inspect.EvidenceReferences.Length);

        foreach (var name in new[]
                 {
                     "diagnose_window",
                     "diagnose_high_wait",
                     "diagnose_slow_startup",
                 })
        {
            var admission = catalog.Tools.Single(tool => tool.ToolName == name)
                .PlannerAdmission!;
            Assert.Equal("not_admitted_evidence_missing", admission.AdmissionStatus);
            Assert.Null(admission.PhysicalPassLimit);
            Assert.Empty(admission.EvidenceReferences);
            Assert.Equal(
                ["large_trace_before_after_physical_pass_and_elapsed_measurement"],
                admission.MissingEvidence);
        }
    }

    private static int MeasureResourceFrame(TextResourceContents resource, RequestId requestId) =>
        JsonSerializer.SerializeToUtf8Bytes(
            new JsonRpcResponse
            {
                Id = requestId,
                Result = JsonSerializer.SerializeToNode(
                    new ReadResourceResult { Contents = [resource] },
                    McpJsonUtilities.DefaultOptions),
            },
            McpJsonUtilities.DefaultOptions).Length + 1;

    private static (ActiveToolCatalog Catalog, ServiceProvider Services, McpServerTool Tool)
        CreateProductionFixture(
            int maxActiveCursors,
            int maxResponseBytes,
            int? runtimeFrameBytes = null)
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        services.AddSingleton(catalog);
        services.AddSingleton(new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal(),
            new CapabilityCursorRegistry(maxActive: maxActiveCursors),
            runtimeFrameBytes ?? maxResponseBytes));
        var provider = services.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                provider,
                responseBudget: new ToolResponseBudgetOptions(maxResponseBytes))
            .Single(candidate => candidate.ProtocolTool.Name == "list_capabilities");
        return (catalog, provider, tool);
    }

    private static async Task<CallToolResult> InvokeList(
        McpServerTool tool,
        IServiceProvider services,
        RequestId requestId,
        string? cursor)
    {
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(services);
        var arguments = new Dictionary<string, JsonElement>();
        if (cursor is not null)
            arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
        var parameters = new CallToolRequestParams
        {
            Name = "list_capabilities",
            Arguments = arguments,
        };
        var request = new JsonRpcRequest
        {
            Id = requestId,
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };
        return await tool.InvokeAsync(
            new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
            CancellationToken.None);
    }
}
