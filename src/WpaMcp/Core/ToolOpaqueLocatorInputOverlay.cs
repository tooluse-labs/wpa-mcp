using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

internal static class ToolOpaqueLocatorInputOverlay
{
    internal const string OverlayId = "opaque_locator_input_v1";
    internal const string TraceIdPattern = "^trc_[0-9a-f]{32}$";
    internal const string AbsoluteEtlPathPattern =
        "^(?:(?:[A-Za-z]:[\\\\/])|(?:\\\\\\\\[^\\\\/]+[\\\\/][^\\\\/]+[\\\\/])).+\\.[eE][tT][lL](?:[xX])?$";
    internal const string TraceOrCompatibilityPathPattern =
        "^(?:trc_[0-9a-f]{32}|(?:(?:[A-Za-z]:[\\\\/])|(?:\\\\\\\\[^\\\\/]+[\\\\/][^\\\\/]+[\\\\/])).+\\.[eE][tT][lL](?:[xX])?)$";
    internal const string QueryCursorPattern = "^qrc_[0-9a-f]{32}$";
    internal const string CapabilityCursorPattern = "^cpc_[0-9a-f]{32}$";

    internal static bool AppliesTo(MethodInfo method) => Parameters(method).Any();

    internal static void Apply(McpServerTool tool, MethodInfo method)
    {
        if (!AppliesTo(method))
            return;
        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!.AsObject();
        var properties = schema["properties"]!.AsObject();
        foreach (var parameter in Parameters(method))
        {
            var name = parameter.Name!;
            var property = properties[name]!.AsObject();
            var nonNull = property["anyOf"] is JsonArray alternatives
                ? alternatives.Select(item => item!.AsObject())
                    .Single(item => item["type"]?.GetValue<string>() != "null")
                : property;
            nonNull["pattern"] = Pattern(method, name);
            nonNull["x-opaqueLocator"] = name == "cursor"
                ? "continuation_cursor"
                : method.Name == "LoadTrace"
                    ? "approved_absolute_etl_path"
                    : "trace_id";
        }
        schema["x-opaqueLocatorInputOverlay"] = OverlayId;
        tool.ProtocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(
            schema.ToJsonString(),
            McpJsonUtilities.DefaultOptions);
    }

    internal static bool AdvertisesExpectedLocators(Tool tool, MethodInfo method)
    {
        if (!AppliesTo(method))
            return !tool.InputSchema.TryGetProperty("x-opaqueLocatorInputOverlay", out _);
        if (!tool.InputSchema.TryGetProperty("x-opaqueLocatorInputOverlay", out var overlay) ||
            overlay.GetString() != OverlayId ||
            !tool.InputSchema.TryGetProperty("properties", out var properties))
            return false;
        return Parameters(method).All(parameter =>
        {
            if (!properties.TryGetProperty(parameter.Name!, out var property))
                return false;
            var nonNull = NonNull(property);
            return nonNull.TryGetProperty("pattern", out var pattern) &&
                pattern.GetString() == Pattern(method, parameter.Name!);
        });
    }

    private static IEnumerable<ParameterInfo> Parameters(MethodInfo method) =>
        method.GetParameters().Where(parameter =>
            parameter.ParameterType == typeof(string) &&
            (parameter.Name == "cursor" ||
             parameter.Name == "path"));

    private static string Pattern(MethodInfo method, string name) => name switch
    {
        "path" when method.Name == "LoadTrace" => AbsoluteEtlPathPattern,
        "path" => TraceIdPattern,
        "cursor" when method.Name == "ListCapabilities" => CapabilityCursorPattern,
        "cursor" => QueryCursorPattern,
        _ => throw new InvalidOperationException($"Unsupported locator input '{name}'."),
    };

    private static JsonElement NonNull(JsonElement schema)
    {
        if (!schema.TryGetProperty("anyOf", out var alternatives))
            return schema;
        return alternatives.EnumerateArray().Single(item =>
            !item.TryGetProperty("type", out var type) || type.GetString() != "null");
    }
}
