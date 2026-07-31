using System.Reflection;
using System.Text.Json;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class StackResponseOptionsTests
{
    private const string CpuFixture = "fixtures/small_cpu.etl";
    private const string FileIoFixture = "fixtures/small_fileio.etl";

    [Fact]
    public void EffectiveTop_PreservesDefaultAndCapsCompactModes()
    {
        Assert.Equal(1000, StackResponseOptions.EffectiveTop(1000, compactStacks: false, summaryOnly: false));
        Assert.Equal(StackResponseOptions.CompactTopLimit, StackResponseOptions.EffectiveTop(1000, compactStacks: true, summaryOnly: false));
        Assert.Equal(StackResponseOptions.CompactTopLimit, StackResponseOptions.EffectiveTop(1000, compactStacks: false, summaryOnly: true));
        Assert.Equal(10, StackResponseOptions.EffectiveTop(10, compactStacks: true, summaryOnly: false));
    }

    [Fact]
    public void TopStackMethodsExposeCompactAndSummaryOptions()
    {
        var methods = typeof(WaitTools).Assembly.GetTypes()
            .Where(type => type.Namespace == "WpaMcp.Tools")
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method => method.Name.EndsWith("TopStacks", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var parameterNames = method.GetParameters().Select(parameter => parameter.Name).ToHashSet();
            Assert.Contains("compactStacks", parameterNames);
            Assert.Contains("summaryOnly", parameterNames);
        }
    }

    [Fact]
    public void StackRowsDoNotExposeFullStackPayloads()
    {
        var stackRowTypes = typeof(CpuTopFunctionsResponse).Assembly.GetTypes()
            .Where(type => type.Namespace == "WpaMcp.Output"
                           && type.Name.EndsWith("StackRow", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(stackRowTypes);
        foreach (var type in stackRowTypes)
        {
            var propertyNames = type.GetProperties().Select(property => property.Name).ToHashSet();
            Assert.DoesNotContain("Stack", propertyNames);
            Assert.DoesNotContain("Frames", propertyNames);
            Assert.DoesNotContain("CallStack", propertyNames);
        }
    }

    [Fact]
    public void CompactModeCapsTopStackRows()
    {
        var tools = new WaitTools(new TraceCache(capacity: 2));

        var response = tools.WaitTopStacks(CpuFixture, top: 1000, compactStacks: true);

        Assert.True(response.Rows.Count <= StackResponseOptions.CompactTopLimit);
    }

    [Fact]
    public void RepresentativeDefaultStackResponsesStayBelowWarningThreshold()
    {
        var wait = new WaitTools(new TraceCache(capacity: 2))
            .WaitTopStacks(CpuFixture);
        var fileIo = new IoTools(new TraceCache(capacity: 2))
            .FileIoTopStacks(FileIoFixture);

        Assert.True(JsonBytes(wait) < StackResponseOptions.WarningResponseBytes);
        Assert.True(JsonBytes(fileIo) < StackResponseOptions.WarningResponseBytes);
    }

    private static int JsonBytes<T>(T value)
        => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)).Length;
}
