using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class TraceReferenceSurfaceTests
{
    [Fact]
    public void AnalysisPathParameters_DescribeCanonicalTraceId_NotRawPath()
    {
        var violations = typeof(MetaTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Where(method => method.Name is not nameof(MetaTools.LoadTrace))
            .Select(method => new
            {
                Method = method,
                Parameter = method.GetParameters().SingleOrDefault(parameter =>
                    string.Equals(parameter.Name, "path", StringComparison.Ordinal)),
            })
            .Where(item => item.Parameter is not null)
            .Where(item => !string.Equals(
                item.Parameter!.GetCustomAttribute<DescriptionAttribute>()?.Description,
                "Canonical TraceId returned by load_trace",
                StringComparison.Ordinal))
            .Select(item => $"{item.Method.DeclaringType!.Name}.{item.Method.Name}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }

    [Fact]
    public void InspectTrace_DescribesIdOnlyBehavior_WithoutClaimingImplicitSidecar()
    {
        var method = typeof(MetaTools).GetMethod(nameof(MetaTools.InspectTrace))!;
        var description = method.GetCustomAttribute<DescriptionAttribute>()!.Description;
        var annotation = method.GetCustomAttribute<McpServerToolAttribute>()!;

        Assert.Contains("ID-only", description, StringComparison.Ordinal);
        Assert.DoesNotContain("materialize or refresh an ETLX sidecar", description, StringComparison.Ordinal);
        Assert.False(annotation.OpenWorld);
        Assert.False(annotation.Destructive);
        Assert.True(annotation.ReadOnly);
        Assert.True(annotation.Idempotent);
    }

    [Fact]
    public void IdOnlyAnalysisAnnotations_AreReadOnlyClosedWorldAndNonDestructive()
    {
        var violations = typeof(MetaTools).Assembly.GetTypes()
            .Where(type => type.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Public))
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Where(method => method.Name is not (
                nameof(MetaTools.LoadTrace) or
                nameof(MetaTools.UnloadTrace) or
                "PrepareSymbols"))
            .Select(method => new
            {
                Method = method,
                Annotation = method.GetCustomAttribute<McpServerToolAttribute>()!,
            })
            .Where(item =>
                !item.Annotation.ReadOnly ||
                !item.Annotation.Idempotent ||
                item.Annotation.OpenWorld ||
                item.Annotation.Destructive)
            .Select(item => $"{item.Method.DeclaringType!.Name}.{item.Method.Name}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(violations);
    }
}
