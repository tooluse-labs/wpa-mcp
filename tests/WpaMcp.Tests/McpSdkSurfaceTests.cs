using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
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
    public void AllMcpToolsDeclareRiskAnnotations()
    {
        var statefulTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(MetaTools)}.{nameof(MetaTools.LoadTrace)}",
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.SetSymbolPath)}",
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.AddSymbolServer)}",
        };
        var nonIdempotentTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.SetSymbolPath)}",
        };
        var openWorldTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(AlpcTools)}.{nameof(AlpcTools.AlpcTopStacks)}",
            $"{nameof(AlpcTools)}.{nameof(AlpcTools.AlpcCallerCallee)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrAllocTopStacks)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrAllocCallerCallee)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrExceptionTopStacks)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrExceptionCallerCallee)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrContentionTopStacks)}",
            $"{nameof(ClrTools)}.{nameof(ClrTools.ClrContentionCallerCallee)}",
            $"{nameof(CpuTools)}.{nameof(CpuTools.CpuTopFunctions)}",
            $"{nameof(CpuTools)}.{nameof(CpuTools.CpuTopFunctionsBatch)}",
            $"{nameof(CpuTools)}.{nameof(CpuTools.CpuCallerCallee)}",
            $"{nameof(DiagnoseTools)}.{nameof(DiagnoseTools.DiagnoseWindow)}",
            $"{nameof(DiagnoseTools)}.{nameof(DiagnoseTools.DiagnoseSlowStartup)}",
            $"{nameof(DiagnoseTools)}.{nameof(DiagnoseTools.DiagnoseHighWait)}",
            $"{nameof(GenericProviderTools)}.{nameof(GenericProviderTools.GenericEventTopStacks)}",
            $"{nameof(GenericProviderTools)}.{nameof(GenericProviderTools.GenericEventCallerCallee)}",
            $"{nameof(HardFaultTools)}.{nameof(HardFaultTools.HardFaultTopStacks)}",
            $"{nameof(HardFaultTools)}.{nameof(HardFaultTools.HardFaultCallerCallee)}",
            $"{nameof(HeapTools)}.{nameof(HeapTools.HeapAllocTopStacks)}",
            $"{nameof(HeapTools)}.{nameof(HeapTools.HeapAllocCallerCallee)}",
            $"{nameof(ImageLoadTools)}.{nameof(ImageLoadTools.ImageLoadTopStacks)}",
            $"{nameof(ImageLoadTools)}.{nameof(ImageLoadTools.ImageLoadCallerCallee)}",
            $"{nameof(InterruptTools)}.{nameof(InterruptTools.InterruptTopStacks)}",
            $"{nameof(InterruptTools)}.{nameof(InterruptTools.InterruptCallerCallee)}",
            $"{nameof(IoTools)}.{nameof(IoTools.FileIoTopStacks)}",
            $"{nameof(IoTools)}.{nameof(IoTools.FileIoCallerCallee)}",
            $"{nameof(IoTools)}.{nameof(IoTools.DiskIoTopStacks)}",
            $"{nameof(IoTools)}.{nameof(IoTools.DiskIoCallerCallee)}",
            $"{nameof(NetIoTools)}.{nameof(NetIoTools.NetTopStacks)}",
            $"{nameof(NetIoTools)}.{nameof(NetIoTools.NetCallerCallee)}",
            $"{nameof(ReadyThreadTools)}.{nameof(ReadyThreadTools.ReadyThreadTopStacks)}",
            $"{nameof(ReadyThreadTools)}.{nameof(ReadyThreadTools.ReadyThreadCallerCallee)}",
            $"{nameof(RegistryTools)}.{nameof(RegistryTools.RegistryTopStacks)}",
            $"{nameof(RegistryTools)}.{nameof(RegistryTools.RegistryCallerCallee)}",
            $"{nameof(VirtualMemoryTools)}.{nameof(VirtualMemoryTools.VirtualAllocTopStacks)}",
            $"{nameof(VirtualMemoryTools)}.{nameof(VirtualMemoryTools.VirtualAllocCallerCallee)}",
            $"{nameof(WaitTools)}.{nameof(WaitTools.WaitTopStacks)}",
            $"{nameof(WaitTools)}.{nameof(WaitTools.WaitCallerCallee)}",
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
            Assert.Equal(openWorldTools.Contains(name), attribute!.OpenWorld);
            Assert.False(attribute.Destructive);
            Assert.Equal(!statefulTools.Contains(name), attribute.ReadOnly);
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
    public void SymbolPathToolsRemainClosedWorldButWarnAboutTrustedServers()
    {
        foreach (var methodName in new[] { nameof(SymbolTools.SetSymbolPath), nameof(SymbolTools.AddSymbolServer) })
        {
            var method = typeof(SymbolTools).GetMethod(methodName);
            var tool = method?.GetCustomAttribute<McpServerToolAttribute>();
            var description = method?.GetCustomAttribute<DescriptionAttribute>()?.Description;

            Assert.NotNull(tool);
            Assert.False(tool!.OpenWorld);
            Assert.False(tool.ReadOnly);
            Assert.Contains("trusted as-is", description, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("subsequent stack-resolving tools may fetch PDBs", description);
        }
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
