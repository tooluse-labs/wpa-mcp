using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class CapabilityPolicyTests
{
    [Fact]
    public void Projection_FiltersCallableSurfaceButRetainsClosedCatalogEvidence()
    {
        var full = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var sdkTools = full.CreateServerTools(services);
        var policy = CapabilityPolicyProfile.Parse(
            "cpu.sampled.stacks",
            CapabilityPolicyProfile.DisableCapabilitiesOption);

        var projection = full.ProjectCapabilityPolicy(policy, sdkTools);
        var catalog = projection.Catalog;

        Assert.Equal(60, full.Tools.Count);
        Assert.Equal(60, catalog.AllTools.Count);
        Assert.Equal(57, catalog.Tools.Count);
        Assert.Equal(57, projection.ServerTools.Count);
        Assert.DoesNotContain(catalog.Tools, tool =>
            tool.Capabilities.Any(capability =>
                capability.CapabilityId == "cpu.sampled.stacks"));
        Assert.DoesNotContain(projection.ServerTools, tool => tool.ProtocolTool.Name is
            "cpu_caller_callee" or "cpu_top_functions" or "cpu_top_functions_batch");
        Assert.Contains(catalog.Tools, tool => tool.ToolName == "list_capabilities");
        Assert.Contains(catalog.Tools, tool => tool.ToolName == "inspect_trace");

        var discovery = new CapabilityDiscoveryRuntime(
            catalog,
            new StdioSessionPrincipal());
        var snapshot = discovery.FullSnapshot();
        Assert.Equal(policy.ProfileHash, snapshot.CapabilityPolicy.ProfileHash);
        Assert.Equal(policy.ProfileIdentity, snapshot.CapabilityPolicy.ProfileIdentity);
        var disabled = Assert.Single(snapshot.Capabilities, capability =>
            capability.CapabilityId == "cpu.sampled.stacks");
        Assert.Equal(
            WpaMcp.Output.CapabilityAvailabilityStatus.DisabledByPolicy,
            disabled.AvailabilityState);
        Assert.Empty(disabled.CallableToolNames);
        Assert.Equal(
            ["cpu_caller_callee", "cpu_top_functions", "cpu_top_functions_batch"],
            disabled.DisabledByPolicyToolNames.Order(StringComparer.Ordinal));
        Assert.Equal(disabled.ToolNames.Order(StringComparer.Ordinal),
            disabled.DisabledByPolicyToolNames.Order(StringComparer.Ordinal));

        Assert.All(snapshot.Workflows, workflow =>
        {
            Assert.Empty(workflow.CallableToolNames.Intersect(
                workflow.DisabledByPolicyToolNames,
                StringComparer.Ordinal));
            Assert.True(workflow.ToolNames.ToHashSet(StringComparer.Ordinal).SetEquals(
                workflow.CallableToolNames.Concat(workflow.DisabledByPolicyToolNames)));
            Assert.Equal(
                workflow.CapabilityIds.Where(policy.IsDisabled),
                workflow.DisabledByPolicyCapabilityIds);
        });
        Assert.Equal(policy.ProfileHash, discovery.CapabilityResourceIndex()
            .CapabilityPolicy.ProfileHash);
        Assert.Equal(policy.ProfileHash, discovery.WorkflowCatalogSnapshot()
            .CapabilityPolicy.ProfileHash);
        var toolCatalog = discovery.ToolCatalogSnapshot();
        Assert.Equal(60, toolCatalog.Tools.Count);
        Assert.Equal(3, toolCatalog.Tools.Count(tool => !tool.Callable));
        Assert.All(
            toolCatalog.Tools.Where(tool => !tool.Callable),
            tool => Assert.Equal(
                WpaMcp.Output.CapabilityAvailabilityStatus.DisabledByPolicy,
                tool.AvailabilityState));
        Assert.Equal(60, discovery.ToolResourceIndex().TotalItems);
    }

    [Theory]
    [InlineData("catalog.capability.list", "CAPABILITY-POLICY-DISCOVERY")]
    [InlineData("trace.capability.inspect", "CAPABILITY-POLICY-INSPECT")]
    [InlineData("not.a.declared.capability", "CAPABILITY-POLICY-UNKNOWN")]
    public void Projection_FailsClosedForInvalidDiscoveryPolicy(
        string capabilityId,
        string expectedCode)
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var sdkTools = catalog.CreateServerTools(services);
        var policy = CapabilityPolicyProfile.Parse(
            capabilityId,
            CapabilityPolicyProfile.DisableCapabilitiesOption);

        var error = Assert.Throws<CatalogValidationException>(() =>
            catalog.ProjectCapabilityPolicy(policy, sdkTools));
        Assert.StartsWith(expectedCode, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpaqueCursors_FailClosedAcrossCapabilityPolicyProfiles()
    {
        var policyA = CapabilityPolicyProfile.Parse(
            "cpu.sampled.stacks",
            "test");
        var policyB = CapabilityPolicyProfile.Parse(
            "io.file.activity",
            "test");

        var toolsRegistry = new ToolsListCursorRegistry(
            ToolsListPaginationOptions.Default);
        var toolsA = new ToolsListCursorBinding(
            "server",
            "catalog",
            "2.0",
            "order",
            policyA.ProfileHash);
        var toolsB = toolsA with { CapabilityPolicyIdentity = policyB.ProfileHash };
        var toolsCursor = toolsRegistry.Issue(toolsA, 1);
        var toolsError = Assert.Throws<ToolsListCursorException>(() =>
            toolsRegistry.Redeem(toolsCursor, toolsB));
        Assert.Equal(ToolsListCursorFailure.BindingMismatch, toolsError.Failure);

        var capabilityRegistry = new CapabilityCursorRegistry();
        var capabilityA = new CapabilityCursorBinding(
            "principal",
            "catalog",
            null,
            null,
            "order",
            policyA.ProfileHash);
        var capabilityB = capabilityA with
        {
            CapabilityPolicyIdentity = policyB.ProfileHash,
        };
        var capabilityCursor = capabilityRegistry.GetOrIssueContinuation(
            capabilityA,
            null,
            1);
        Assert.Throws<CapabilityCursorException>(() =>
            capabilityRegistry.Redeem(capabilityCursor, capabilityB));

        var queryRegistry = new QueryResultCursorRegistry();
        var queryA = new QueryResultCursorBinding(
            "principal",
            "trace",
            "generation",
            "catalog",
            "2.0",
            null,
            "off",
            new string('a', 64),
            "order",
            policyA.ProfileHash);
        var queryB = queryA with { CapabilityPolicyIdentity = policyB.ProfileHash };
        var queryCursor = queryRegistry.GetOrIssueContinuation(
            queryA,
            null,
            new QueryResultCursorPosition("rows", 1, null));
        Assert.Throws<QueryResultCursorException>(() =>
            queryRegistry.Redeem(queryCursor, queryB));
    }
}
