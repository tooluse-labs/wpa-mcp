using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class TimeWindowSemanticsTests
{
    [Fact]
    public void StackAnalysisRequest_UsesHalfOpenWindow()
    {
        var trace = new TraceCache(capacity: 1).Get("fixtures/small_cpu.etl");
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs: 10, endUs: 20, trace, bucketCount: 0);
        var req = new StackAnalysisRequest(
            Pid: 123,
            StartUs: 10,
            EndUs: 20,
            SymbolLog: TextWriter.Null,
            When: when);

        Assert.False(req.PassesFilter(processId: 123, nowUs: 9));
        Assert.True(req.PassesFilter(processId: 123, nowUs: 10));
        Assert.True(req.PassesFilter(processId: 123, nowUs: 19));
        Assert.False(req.PassesFilter(processId: 123, nowUs: 20));
        Assert.False(req.PassesFilter(processId: 456, nowUs: 10));
    }

    [Fact]
    public void TimeWindowedMcpToolsDescribeEndUsAsExclusive()
    {
        var methods = McpToolMethods()
            .Where(method => method.GetParameters().Any(parameter => parameter.Name == "endUs"))
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, method =>
        {
            var endUs = Assert.Single(method.GetParameters(), parameter => parameter.Name == "endUs");
            var description = DescriptionOf(endUs);

            Assert.Contains("exclusive", description, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void NonWindowedMcpToolsExplainTheirScope()
    {
        var expectedNoWindowTools = new HashSet<string>(StringComparer.Ordinal)
        {
            $"{nameof(DiagnoseTools)}.{nameof(DiagnoseTools.DiagnoseSlowStartup)}",
            $"{nameof(ImageLoadTools)}.{nameof(ImageLoadTools.ImageLoadTiming)}",
            $"{nameof(ImageLoadTools)}.{nameof(ImageLoadTools.ImageLoadTopGaps)}",
            $"{nameof(MarkerTools)}.{nameof(MarkerTools.FindMarker)}",
            $"{nameof(MetaTools)}.{nameof(MetaTools.LoadTrace)}",
            $"{nameof(MetaTools)}.{nameof(MetaTools.InspectTrace)}",
            $"{nameof(MetaTools)}.{nameof(MetaTools.ListProcesses)}",
            $"{nameof(MetaTools)}.{nameof(MetaTools.ProcessCreateTiming)}",
            $"{nameof(MetaTools)}.{nameof(MetaTools.ThreadLifetime)}",
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.SetSymbolPath)}",
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.AddSymbolServer)}",
            $"{nameof(SymbolTools)}.{nameof(SymbolTools.DiagnoseSymbols)}",
        };

        var nonWindowed = McpToolMethods()
            .Where(method =>
            {
                var parameters = method.GetParameters();
                return !parameters.Any(parameter => parameter.Name == "startUs") &&
                       !parameters.Any(parameter => parameter.Name == "endUs");
            })
            .Select(method => (Name: $"{method.DeclaringType!.Name}.{method.Name}", Description: DescriptionOf(method)))
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedNoWindowTools.OrderBy(name => name, StringComparer.Ordinal), nonWindowed.Select(item => item.Name));
        Assert.All(nonWindowed, item => Assert.Contains("No startUs/endUs", item.Description));
    }

    private static IReadOnlyList<MethodInfo> McpToolMethods()
        => typeof(MetaTools).Assembly
            .GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .ToList();

    private static string DescriptionOf(MemberInfo member)
    {
        var attribute = Assert.IsType<DescriptionAttribute>(
            Attribute.GetCustomAttribute(member, typeof(DescriptionAttribute)));
        return attribute.Description;
    }

    private static string DescriptionOf(ParameterInfo parameter)
    {
        var attribute = Assert.IsType<DescriptionAttribute>(
            Attribute.GetCustomAttribute(parameter, typeof(DescriptionAttribute)));
        return attribute.Description;
    }
}
