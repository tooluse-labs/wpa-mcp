using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class CapabilityDiscoveryTools(CapabilityDiscoveryRuntime runtime)
{
    private readonly CapabilityDiscoveryRuntime _runtime = runtime;

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Lists the server's validated declared capability map before selecting an analyzer. " +
        "The map is exhaustive only for this wpa-mcp server surface, never for the complete WPA/ETW universe; unlisted capabilities remain unknown_not_catalogued. " +
        "Each capability links stable goals, workflows, callable tools, evidence boundaries, scopes, costs, symbol requirements, and its runtime evaluator. " +
        "Optional domain and goal filters are normalized and bound into opaque principal-scoped cursors. Follow nextCursor without changing either filter. " +
        "The canonical content hash and catalog version are stable for one validated active catalog. " +
        "Read wpa://runtime/profile for the immutable startup contract/trace-reference modes, deprecation warnings, and release blockers. " +
        "No startUs/endUs: this is a server-catalog query, not trace-event analysis.")]
    public ListCapabilitiesResponse ListCapabilities(
        [Description("Optional declared capability domain, such as cpu, scheduler, io, memory, or lifecycle.")]
        string? domain = null,
        [Description("Optional stable goal ID, such as cpu_hotspots, waits, startup, or capability_discovery.")]
        string? goal = null,
        [Description("Opaque continuation returned by the preceding page. It is bound to this principal, catalog version, filters, and ordering.")]
        string? cursor = null) =>
        _runtime.ListForDelivery(domain, goal, cursor);
}

[McpServerResourceType]
public sealed class CapabilityDiscoveryResources(CapabilityDiscoveryRuntime runtime)
{
    private readonly CapabilityDiscoveryRuntime _runtime = runtime;

    [McpServerResource(
        UriTemplate = "wpa://runtime/profile",
        Name = "wpa_runtime_profile",
        Title = "wpa-mcp immutable runtime compatibility profile",
        MimeType = "application/json"), Description(
        "Exposes the startup-selected contract and trace-reference modes, release line, deprecation warnings, and release blockers. Modes are immutable for the server lifetime and cannot be selected by a tool call.")]
    public TextResourceContents GetRuntimeProfile() =>
        _runtime.RuntimeProfileResource();

    [McpServerResource(
        UriTemplate = "wpa://capabilities/policy",
        Name = "wpa_capability_policy",
        Title = "wpa-mcp capability policy page index",
        MimeType = "application/json"), Description(
        "Complete index for startup-disabled capability IDs. Empty profiles are explicitly complete_empty; restricted profiles list every page needed to recover every disabled ID without repetition or silent omission.")]
    public TextResourceContents GetCapabilityPolicy() =>
        _runtime.CapabilityPolicyIndexResource();

    [McpServerResource(
        UriTemplate = "wpa://capabilities/policy/{page}",
        Name = "wpa_capability_policy_page",
        Title = "wpa-mcp capability policy disabled-ID page",
        MimeType = "application/json"), Description(
        "One byte-budgeted page of startup-disabled capability IDs, ordered by canonical capability ID. Follow every page from wpa://capabilities/policy for the complete set.")]
    public TextResourceContents GetCapabilityPolicyPage(int page) =>
        _runtime.CapabilityPolicyPageResource(page);

    [McpServerResource(
        UriTemplate = "wpa://capabilities/server",
        Name = "wpa_server_capabilities",
        Title = "wpa-mcp declared capability catalog",
        MimeType = "application/json"), Description(
        "Small same-source index for domain-sharded capability resources. Follow every listed shard for a complete catalog; no resource is silently truncated.")]
    public TextResourceContents GetCapabilityCatalogIndex() =>
        _runtime.CapabilityIndexResource();

    [McpServerResource(
        UriTemplate = "wpa://capabilities/domain/{domain}",
        Name = "wpa_server_capabilities_by_domain",
        Title = "wpa-mcp capability page index for one declared domain",
        MimeType = "application/json"), Description(
        "A complete page index for one declared domain. Follow every listed page URI; no row is silently truncated.")]
    public TextResourceContents GetCapabilityDomain(string domain) =>
        _runtime.CapabilityDomainResource(domain);

    [McpServerResource(
        UriTemplate = "wpa://capabilities/domain/{domain}/{page}",
        Name = "wpa_server_capability_domain_page",
        Title = "wpa-mcp capability page",
        MimeType = "application/json"), Description(
        "One byte-budgeted, stable summary page from a declared capability domain. Every summary contains a detailsResourceUri; follow it for the complete ServerCapabilityRecord. No capability or detail field is silently omitted.")]
    public TextResourceContents GetCapabilityDomainPage(string domain, int page) =>
        _runtime.CapabilityDomainPageResource(domain, page);

    [McpServerResource(
        UriTemplate = "wpa://capabilities/detail/{capabilityId}",
        Name = "wpa_server_capability_detail",
        Title = "wpa-mcp complete capability detail",
        MimeType = "application/json"), Description(
        "The complete, untruncated ServerCapabilityRecord for one canonical capability ID. Domain summary pages link here explicitly.")]
    public TextResourceContents GetCapabilityDetail(string capabilityId) =>
        _runtime.CapabilityDetailResource(capabilityId);

    [McpServerResource(
        UriTemplate = "wpa://tools/server",
        Name = "wpa_server_tools",
        Title = "wpa-mcp active tool catalog",
        MimeType = "application/json"), Description(
        "Small same-source index for domain-sharded tool contract resources. Follow every listed shard for a complete catalog; no resource is silently truncated.")]
    public TextResourceContents GetToolCatalogIndex() => _runtime.ToolIndexResource();

    [McpServerResource(
        UriTemplate = "wpa://tools/domain/{domain}",
        Name = "wpa_server_tools_by_domain",
        Title = "wpa-mcp tool page index for one declared domain",
        MimeType = "application/json"), Description(
        "A complete page index for one declared tool domain. Follow every listed page URI; no tool is silently truncated.")]
    public TextResourceContents GetToolDomain(string domain) =>
        _runtime.ToolDomainResource(domain);

    [McpServerResource(
        UriTemplate = "wpa://tools/domain/{domain}/{page}",
        Name = "wpa_server_tool_domain_page",
        Title = "wpa-mcp tool contract page",
        MimeType = "application/json"), Description(
        "One byte-budgeted, stable tool summary page projected from the validated Active Catalog. Every row links its complete tool detail resource; no tool contract field is silently omitted.")]
    public TextResourceContents GetToolDomainPage(string domain, int page) =>
        _runtime.ToolDomainPageResource(domain, page);

    [McpServerResource(
        UriTemplate = "wpa://tools/detail/{toolName}",
        Name = "wpa_server_tool_detail",
        Title = "wpa-mcp complete tool resource detail",
        MimeType = "application/json"), Description(
        "The complete, untruncated ServerToolResourceRecord for one active tool. Its sectionContractsResourceUri links the complete byte-budgeted section contracts.")]
    public TextResourceContents GetToolDetail(string toolName) =>
        _runtime.ToolDetailResource(toolName);

    [McpServerResource(
        UriTemplate = "wpa://tools/{toolName}/sections",
        Name = "wpa_server_tool_section_contracts",
        Title = "wpa-mcp per-tool section contract page index",
        MimeType = "application/json"), Description(
        "Complete page index for one tool's section-level ordering, truncation proof, evidence references, measurement basis, relationship, and declared conclusion boundaries. Follow every page; no section contract is silently omitted.")]
    public TextResourceContents GetToolSectionContracts(string toolName) =>
        _runtime.ToolSectionContractIndexResource(toolName);

    [McpServerResource(
        UriTemplate = "wpa://tools/{toolName}/sections/{page}",
        Name = "wpa_server_tool_section_contract_page",
        Title = "wpa-mcp per-tool section contract page",
        MimeType = "application/json"), Description(
        "One byte-budgeted page of complete section contracts for one active tool, ordered by JSON pointer.")]
    public TextResourceContents GetToolSectionContractPage(string toolName, int page) =>
        _runtime.ToolSectionContractPageResource(toolName, page);

    [McpServerResource(
        UriTemplate = "wpa://workflows/server",
        Name = "wpa_server_workflows",
        Title = "wpa-mcp workflow resource index",
        MimeType = "application/json"), Description(
        "Small index of stable per-workflow resources projected from the same validated Active Catalog as list_capabilities.")]
    public TextResourceContents GetWorkflowCatalog() => _runtime.WorkflowResource();

    [McpServerResource(
        UriTemplate = "wpa://workflows/{workflowId}",
        Name = "wpa_server_workflow",
        Title = "wpa-mcp capability workflow",
        MimeType = "application/json"), Description(
        "One complete workflow and its linked goals from the validated Active Catalog.")]
    public TextResourceContents GetWorkflow(string workflowId) =>
        _runtime.WorkflowShardResource(workflowId);
}
