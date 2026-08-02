using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class NetConnectionIdentifierContractTests
{
    private static readonly ulong[] BoundaryIds =
    [
        NetConnectionAnalysis.JavaScriptMaxSafeInteger,
        NetConnectionAnalysis.JavaScriptMaxSafeInteger + 1,
        NetConnectionAnalysis.JavaScriptMaxSafeInteger + 2,
        ulong.MaxValue,
    ];

    [Fact]
    public void WireContract_PreservesExactConnectionIdsAcrossJavaScriptBoundary()
    {
        foreach (var connId in BoundaryIds)
        {
            var row = Project(connId);
            var expectedText = connId.ToString(CultureInfo.InvariantCulture);
            var isJavaScriptSafe =
                connId <= NetConnectionAnalysis.JavaScriptMaxSafeInteger;

            Assert.Equal(expectedText, row.ConnIdText);
            Assert.Equal(isJavaScriptSafe ? connId : null, row.ConnId);
            Assert.Equal(
                isJavaScriptSafe
                    ? "exact_safe_integer_deprecated"
                    : "null_unsafe_integer_deprecated",
                row.ConnIdLegacyStatus);

            var json = JsonSerializer.SerializeToElement(
                row,
                McpJsonUtilities.DefaultOptions);
            Assert.Equal(
                JsonValueKind.String,
                json.GetProperty("connIdText").ValueKind);
            Assert.Equal(expectedText, json.GetProperty("connIdText").GetString());
            Assert.Equal(
                row.ConnIdLegacyStatus,
                json.GetProperty("connIdLegacyStatus").GetString());

            var legacy = json.GetProperty("connId");
            if (isJavaScriptSafe)
            {
                Assert.Equal(JsonValueKind.Number, legacy.ValueKind);
                Assert.Equal(connId, legacy.GetUInt64());
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, legacy.ValueKind);
            }
        }
    }

    [Fact]
    public void AuthoritativeStrings_RemainDistinctWhenJavaScriptNumbersWouldCollapse()
    {
        const ulong firstUnsafe = 9_007_199_254_740_992UL;
        const ulong nextUnsafe = 9_007_199_254_740_993UL;

        Assert.Equal((double)firstUnsafe, (double)nextUnsafe);

        var first = Project(firstUnsafe);
        var next = Project(nextUnsafe);
        Assert.NotEqual(first.ConnIdText, next.ConnIdText);
        Assert.Null(first.ConnId);
        Assert.Null(next.ConnId);
    }

    [Fact]
    public void GeneratedOutputSchema_IdentifiesExactAndDeprecatedFields()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var contract = catalog.OutputContracts["net_connections"];
        var tool = Assert.Single(
            catalog.CreateProtocolTools(new DeferredCatalogServiceProvider()),
            candidate => candidate.Name == contract.ToolName);
        Assert.Null(tool.OutputSchema);
        var metadata = Assert.IsType<JsonObject>(
            tool.Meta?[ToolOutputContract.MetadataKey]);
        Assert.Equal(contract.SchemaUri, metadata["uri"]!.GetValue<string>());
        Assert.Equal(contract.Sha256, metadata["sha256"]!.GetValue<string>());
        Assert.Equal(contract.Utf8Bytes, metadata["utf8Bytes"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(contract.ToDiscoveryMetadata(), metadata));

        var schema = contract.CanonicalJson;
        using var schemaDocument = JsonDocument.Parse(schema);
        var root = FindObjectSchemaWithProperty(
            schemaDocument.RootElement,
            "connIdText");
        var properties = root.GetProperty("properties");

        Assert.Contains("\"connIdText\"", schema, StringComparison.Ordinal);
        Assert.Contains("canonical unsigned decimal string", schema, StringComparison.Ordinal);
        Assert.Contains("Authoritative exact TCP connection identifier", schema, StringComparison.Ordinal);
        Assert.Contains("\"connId\"", schema, StringComparison.Ordinal);
        Assert.Contains("9007199254740991", schema, StringComparison.Ordinal);
        Assert.Contains("required null for larger identifiers", schema, StringComparison.Ordinal);
        Assert.Contains("\"connIdLegacyStatus\"", schema, StringComparison.Ordinal);
        Assert.Contains("exact_safe_integer_deprecated", schema, StringComparison.Ordinal);
        Assert.Contains("null_unsafe_integer_deprecated", schema, StringComparison.Ordinal);

        Assert.Equal(
            "string",
            properties.GetProperty("connIdText").GetProperty("type").GetString());
        var legacyBranches = properties.GetProperty("connId")
            .GetProperty("anyOf")
            .EnumerateArray()
            .ToArray();
        var legacyTypes = legacyBranches
            .Select(item => item.GetProperty("type").GetString())
            .ToArray();
        Assert.Equal(["integer", "null"], legacyTypes);
        Assert.Equal(
            NetConnectionAnalysis.JavaScriptMaxSafeInteger,
            legacyBranches[0].GetProperty("maximum").GetUInt64());
        Assert.Equal(
            "string",
            properties.GetProperty("connIdLegacyStatus").GetProperty("type").GetString());
        var required = root.GetProperty("required")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("connIdText", required);
        Assert.Contains("connId", required);
        Assert.Contains("connIdLegacyStatus", required);

        Assert.Equal(typeof(string), PropertyType(nameof(NetConnectionRow.ConnIdText)));
        Assert.Equal(typeof(ulong?), PropertyType(nameof(NetConnectionRow.ConnId)));
        Assert.Equal(typeof(string), PropertyType(nameof(NetConnectionRow.ConnIdLegacyStatus)));
    }

    private static NetConnectionRow Project(ulong connId)
    {
        var process = new ProcessInstanceKey(42, 0);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes: [new ProcessLifetime(process, 100, true, false)],
            events:
            [
                new NetConnectionEvent(
                    Pid: process.Pid,
                    ConnId: connId,
                    Kind: NetConnectionEventKind.Connect,
                    TimeUs: 10),
                new NetConnectionEvent(
                    Pid: process.Pid,
                    ConnId: connId,
                    Kind: NetConnectionEventKind.Disconnect,
                    TimeUs: 20),
            ],
            pid: process.Pid,
            top: 1,
            window: new TimeWindow(0, 100),
            processStartUs: process.StartUs);

        return Assert.Single(response.Connections);
    }

    private static Type PropertyType(string propertyName)
    {
        var property = Assert.IsAssignableFrom<PropertyInfo>(
            typeof(NetConnectionRow).GetProperty(propertyName));
        Assert.False(string.IsNullOrWhiteSpace(
            property.GetCustomAttribute<DescriptionAttribute>()?.Description));
        return property.PropertyType;
    }

    private static JsonElement FindObjectSchemaWithProperty(
        JsonElement element,
        string propertyName)
    {
        if (TryFindObjectSchemaWithProperty(element, propertyName, out var result))
            return result;

        throw new KeyNotFoundException(
            $"No object schema contains property '{propertyName}'.");
    }

    private static bool TryFindObjectSchemaWithProperty(
        JsonElement element,
        string propertyName,
        out JsonElement result)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("properties", out var properties) &&
                properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(propertyName, out _))
            {
                result = element;
                return true;
            }

            foreach (var property in element.EnumerateObject())
            {
                if (TryFindObjectSchemaWithProperty(
                        property.Value,
                        propertyName,
                        out result))
                    return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (TryFindObjectSchemaWithProperty(item, propertyName, out result))
                    return true;
            }
        }

        result = default;
        return false;
    }
}
