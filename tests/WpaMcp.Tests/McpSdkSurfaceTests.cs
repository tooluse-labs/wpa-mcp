using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class McpSdkSurfaceTests
{
    [Fact]
    public void ToolAnnotations_ExposeMcpHintProtocolShape()
    {
        var annotations = new ToolAnnotations
        {
            ReadOnlyHint = true,
            IdempotentHint = true,
            OpenWorldHint = false,
            DestructiveHint = false,
        };

        Assert.True(annotations.ReadOnlyHint);
        Assert.True(annotations.IdempotentHint);
        Assert.False(annotations.OpenWorldHint);
        Assert.False(annotations.DestructiveHint);
        Assert.NotNull(typeof(Tool).GetProperty(nameof(Tool.Annotations)));
    }

    [Fact]
    public void McpServerToolAttribute_ExposesAnnotationsAndStructuredOutput()
    {
        var attribute = new McpServerToolAttribute
        {
            ReadOnly = true,
            Idempotent = true,
            OpenWorld = false,
            Destructive = false,
            UseStructuredContent = true,
            OutputSchemaType = typeof(SdkSurfaceProbeResponse),
        };

        Assert.True(attribute.ReadOnly);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.UseStructuredContent);
        Assert.Equal(typeof(SdkSurfaceProbeResponse), attribute.OutputSchemaType);
    }

    [Fact]
    public void AllMcpToolsDeclareExactRiskAnnotations()
    {
        var nonIdempotentTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.SetSymbolPath)}",
        };
        var closedWorldTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.SetSymbolPath)}",
        };
        var nonDestructiveTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.AddSymbolServer)}",
        };

        var tools = typeof(MetaTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
                .Select(method => (Type: type, Method: method, Attribute: method.GetCustomAttribute<McpServerToolAttribute>())))
            .Where(item => item.Attribute is not null)
            .ToList();

        Assert.NotEmpty(tools);
        foreach (var (type, method, attribute) in tools)
        {
            var name = $"{type.Name}.{method.Name}";
            Assert.Equal(!closedWorldTools.Contains(name), attribute!.OpenWorld);
            Assert.Equal(!nonDestructiveTools.Contains(name), attribute.Destructive);
            // Every current tool can mutate server-visible state: trace queries may
            // materialize/refresh an ETLX sidecar, stack queries may populate a PDB
            // cache, cache-management tools retire entries, and symbol tools update
            // the process symbol configuration. Advertising ReadOnly=true would let
            // an MCP client or LLM suppress required side-effect confirmation.
            Assert.False(attribute.ReadOnly);
            Assert.Equal(!nonIdempotentTools.Contains(name), attribute.Idempotent);
        }
    }

    [Fact]
    public void McpServerToolCreateOptions_ExposesProgrammaticEquivalents()
    {
        var properties = PublicPropertyNames(typeof(McpServerToolCreateOptions));

        Assert.Contains(nameof(McpServerToolCreateOptions.ReadOnly), properties);
        Assert.Contains(nameof(McpServerToolCreateOptions.Idempotent), properties);
        Assert.Contains(nameof(McpServerToolCreateOptions.OpenWorld), properties);
        Assert.Contains(nameof(McpServerToolCreateOptions.Destructive), properties);
        Assert.Contains(nameof(McpServerToolCreateOptions.UseStructuredContent), properties);
        Assert.Contains(nameof(McpServerToolCreateOptions.OutputSchema), properties);
    }

    [Fact]
    public void SymbolPathConfigurationToolsDeclareTheirActualOpenWorldBoundary()
    {
        foreach (var methodName in new[] { nameof(SymbolTools.SetSymbolPath), nameof(SymbolTools.AddSymbolServer) })
        {
            var method = typeof(SymbolTools).GetMethod(methodName);
            var tool = method?.GetCustomAttribute<McpServerToolAttribute>();
            var description = method?.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.NotNull(tool);
            var isAddSymbolServer = methodName == nameof(SymbolTools.AddSymbolServer);
            Assert.Equal(isAddSymbolServer, tool!.OpenWorld);
            Assert.Equal(isAddSymbolServer, tool.Idempotent);
            Assert.Equal(!isAddSymbolServer, tool.Destructive);
            Assert.False(tool.ReadOnly);
            Assert.Contains("trusted as-is", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("subsequent stack-resolving tools may fetch PDBs", description);
        }

        var addServerDescription = typeof(SymbolTools)
            .GetMethod(nameof(SymbolTools.AddSymbolServer))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;
        Assert.Contains("external storage", addServerDescription, StringComparison.Ordinal);
    }

    [Fact]
    public void ProtocolTypes_ExposeStructuredContentAndResourceLinks()
    {
        var callToolContent = typeof(CallToolResult).GetProperty(nameof(CallToolResult.Content));

        Assert.NotNull(callToolContent);
        Assert.Contains(typeof(ContentBlock), callToolContent.PropertyType.GenericTypeArguments);
        Assert.NotNull(typeof(CallToolResult).GetProperty(nameof(CallToolResult.StructuredContent)));
        Assert.NotNull(typeof(Tool).GetProperty(nameof(Tool.OutputSchema)));
        Assert.True(typeof(ContentBlock).IsAssignableFrom(typeof(ResourceLinkBlock)));
        Assert.NotNull(typeof(ResourceLinkBlock).GetProperty(nameof(ResourceLinkBlock.Uri)));
        Assert.NotNull(typeof(ResourceLinkBlock).GetProperty(nameof(ResourceLinkBlock.Name)));
    }

    [Fact]
    public void InspectTrace_OutputSchemaExposesAnalysisContractToMcpClients()
    {
        var inspect = Assert.Single(
            ToolListPayload.MeasureCurrentTools(),
            tool => tool.Name == "inspect_trace");

        Assert.NotNull(inspect.OutputSchema);
        var schema = JsonSerializer.Serialize(
            inspect.OutputSchema,
            McpJsonUtilities.DefaultOptions);
        Assert.Contains("analysisContract", schema, StringComparison.Ordinal);
        Assert.Contains("Trace* fields are whole-trace diagnostics", schema, StringComparison.Ordinal);
        Assert.Contains("source_events_unattributed", schema, StringComparison.Ordinal);
        Assert.Contains("synthetic unknown bucket", schema, StringComparison.Ordinal);
        Assert.Contains("not frame resolution", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void WaitAnalysis_ExposesTraceScopedContractInOutputSchema()
    {
        var tool = Assert.Single(
            ToolListPayload.MeasureCurrentTools(),
            candidate => candidate.Name == "wait_analysis");

        Assert.NotNull(tool.OutputSchema);
        var schema = JsonSerializer.Serialize(
            tool.OutputSchema,
            McpJsonUtilities.DefaultOptions);
        Assert.Contains("matchedEventCount", schema, StringComparison.Ordinal);
        Assert.Contains("matchedIntervalCount", schema, StringComparison.Ordinal);
        Assert.Contains("traceIdentityUnresolvedCSwitchSideCount", schema, StringComparison.Ordinal);
        Assert.Contains("Whole-trace", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("clr_gc_analysis")]
    [InlineData("clr_jit_analysis")]
    [InlineData("net_connections")]
    [InlineData("diagnose_symbols")]
    public void LargeLeafContracts_OmitOutputSchemaToBoundToolsListPayload(string toolName)
    {
        var tool = Assert.Single(
            ToolListPayload.MeasureCurrentTools(),
            candidate => candidate.Name == toolName);

        Assert.Null(tool.OutputSchema);
        Assert.True(tool.Annotations?.OpenWorldHint is true);
    }

    private static HashSet<string> PublicPropertyNames(Type type) =>
        type.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private sealed record SdkSurfaceProbeResponse(string Value);
}
