using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests;

public sealed class ToolExactIntegerInputOverlayTests
{
    private static readonly MethodInfo ProbeMethod = typeof(InputProbe).GetMethod(nameof(InputProbe.Invoke))!;

    [Fact]
    public void Schema_AdvertisesCanonicalStringsForScalarNullableAndArrayInt64()
    {
        var tool = ActiveToolCatalog.CreateServerTool(
            ProbeMethod,
            new McpServerToolCreateOptions());

        ToolExactIntegerInputOverlay.Apply(tool, ProbeMethod);

        Assert.True(ToolExactIntegerInputOverlay.AdvertisesExactIntegers(tool.ProtocolTool, ProbeMethod));
        var properties = tool.ProtocolTool.InputSchema.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("startUs").GetProperty("type").GetString());
        Assert.Equal(ToolExactIntegerInputOverlay.SignedPattern,
            properties.GetProperty("startUs").GetProperty("pattern").GetString());
        var nullableValue = properties.GetProperty("endUs").GetProperty("anyOf")
            .EnumerateArray().Single(item => item.GetProperty("type").GetString() != "null");
        Assert.Equal("string", nullableValue.GetProperty("type").GetString());
        var arrayItems = properties.GetProperty("processStartUs").GetProperty("items");
        Assert.Equal(2, arrayItems.GetProperty("anyOf").GetArrayLength());
        var arrayValue = arrayItems.GetProperty("anyOf")
            .EnumerateArray().Single(item => item.GetProperty("type").GetString() != "null");
        Assert.Equal("string", arrayValue.GetProperty("type").GetString());
        Assert.Equal(ToolExactIntegerInputOverlay.SignedPattern,
            arrayValue.GetProperty("pattern").GetString());
        var pidType = properties.GetProperty("pid").GetProperty("type");
        Assert.True(pidType.ValueKind == JsonValueKind.String
            ? pidType.GetString() == "integer"
            : pidType.EnumerateArray().Any(item => item.GetString() == "integer"));
    }

    [Fact]
    public void RewriteArguments_ParsesExactBoundariesWithoutJavaScriptRoundTrip()
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["startUs"] = JsonSerializer.SerializeToElement("9007199254740992"),
            ["endUs"] = JsonSerializer.SerializeToElement(long.MaxValue.ToString()),
            ["processStartUs"] = JsonSerializer.SerializeToElement(new string?[]
            {
                "9007199254740991",
                null,
                long.MinValue.ToString(),
            }),
            ["pid"] = JsonSerializer.SerializeToElement(42),
        };

        var rewritten = ToolExactIntegerInputOverlay.RewriteArguments(ProbeMethod, arguments);

        Assert.Equal(9_007_199_254_740_992, rewritten["startUs"].GetInt64());
        Assert.Equal(long.MaxValue, rewritten["endUs"].GetInt64());
        var array = rewritten["processStartUs"].EnumerateArray().ToArray();
        Assert.Equal(9_007_199_254_740_991, array[0].GetInt64());
        Assert.Equal(JsonValueKind.Null, array[1].ValueKind);
        Assert.Equal(long.MinValue, array[2].GetInt64());
        Assert.Equal(42, rewritten["pid"].GetInt32());
    }

    [Theory]
    [InlineData("01")]
    [InlineData("-0")]
    [InlineData("+1")]
    [InlineData("9223372036854775808")]
    public void RewriteArguments_RejectsNonCanonicalOrOutOfRangeStrings(string value)
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["startUs"] = JsonSerializer.SerializeToElement(value),
        };

        var error = Assert.Throws<ArgumentException>(() =>
            ToolExactIntegerInputOverlay.RewriteArguments(ProbeMethod, arguments));
        Assert.Contains("invalid_argument", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RewriteArguments_RejectsJsonNumberEvenWhenItWouldFit()
    {
        var arguments = new Dictionary<string, JsonElement>
        {
            ["startUs"] = JsonSerializer.SerializeToElement(1L),
        };

        Assert.Throws<ArgumentException>(() =>
            ToolExactIntegerInputOverlay.RewriteArguments(ProbeMethod, arguments));
    }

    public static class InputProbe
    {
        public static string Invoke(
            long startUs,
            long? endUs = null,
            long?[]? processStartUs = null,
            int? pid = null) => "ok";
    }
}
