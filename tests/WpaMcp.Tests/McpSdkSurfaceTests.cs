using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class McpSdkSurfaceTests
{
    [Fact]
    public void ToolAnnotations_ExposeMcpHintProtocolShape()
    {
        var annotations = new ModelContextProtocol.Protocol.ToolAnnotations
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
        var catalog = ActiveToolCatalog.LoadAndValidate();
        Assert.NotEmpty(catalog.Tools);
        foreach (var tool in catalog.Tools)
        {
            var attribute = Assert.IsType<McpServerToolAttribute>(
                tool.Method.GetCustomAttribute<McpServerToolAttribute>());
            Assert.Equal(tool.Annotations.ReadOnlyHint, attribute.ReadOnly);
            Assert.Equal(tool.Annotations.IdempotentHint, attribute.Idempotent);
            Assert.Equal(tool.Annotations.OpenWorldHint, attribute.OpenWorld);
            Assert.Equal(tool.Annotations.DestructiveHint, attribute.Destructive);
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
    public void LegacySymbolPathMutators_AreNotExposedAsMcpTools()
    {
        foreach (var methodName in new[] { nameof(SymbolTools.SetSymbolPath), nameof(SymbolTools.AddSymbolServer) })
        {
            var method = typeof(SymbolTools).GetMethod(methodName);
            Assert.NotNull(method);
            Assert.Null(method!.GetCustomAttribute<McpServerToolAttribute>());
        }
        var activeNames = ActiveToolCatalog.LoadAndValidate().Tools
            .Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("set_symbol_path", activeNames);
        Assert.DoesNotContain("add_symbol_server", activeNames);
        Assert.Contains("prepare_symbols", activeNames);
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
    [InlineData("prepare_symbols")]
    public void LargeLeafContracts_ExposeCompleteOutputSchemaWithoutHidingTools(string toolName)
    {
        var tool = Assert.Single(
            ToolListPayload.MeasureCurrentTools(),
            candidate => candidate.Name == toolName);

        Assert.NotNull(tool.OutputSchema);
        var schema = JsonSerializer.Serialize(tool.OutputSchema, McpJsonUtilities.DefaultOptions);
        Assert.Contains("\"scope\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"completeness\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"evidenceBoundary\"", schema, StringComparison.Ordinal);
    }

    private static HashSet<string> PublicPropertyNames(Type type) =>
        type.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private sealed record SdkSurfaceProbeResponse(string Value);
}
