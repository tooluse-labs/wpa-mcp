using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WprMcp.Tests;

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

    private static HashSet<string> PublicPropertyNames(Type type) =>
        type.GetProperties().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

    private sealed record SdkSurfaceProbeResponse(string Value);
}
