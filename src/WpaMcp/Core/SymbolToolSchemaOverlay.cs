using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

internal static class SymbolToolSchemaOverlay
{
    internal const string OverlayId = "symbol_context_binding_v1";
    internal const string SelectorParameter = "resolveSymbols";
    internal const string PropertyName = "symbolContextId";
    internal const int ExpectedToolCount = 36;
    internal const string PropertyDescription =
        "Immutable SymbolContextId returned by prepare_symbols for the same loaded trace generation. " +
        "Required when resolveSymbols=true. Unknown, expired, retired, cross-principal, or generation-mismatched IDs fail closed; queries never fall back to _NT_SYMBOL_PATH, the trace directory, disk search, or network. " +
        "This build validates context binding but does not yet expose a context-bound TraceEvent frame resolver, so resolveSymbols=true returns the stable symbol_resolution_unavailable error instead of silently running unsymbolized.";

    internal static bool AppliesTo(MethodInfo method) =>
        method.GetParameters().Any(static parameter =>
            string.Equals(parameter.Name, SelectorParameter, StringComparison.Ordinal) &&
            parameter.ParameterType == typeof(bool));

    internal static void Apply(McpServerTool tool, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(method);
        if (!AppliesTo(method))
            return;

        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("The SDK tool input schema is not an object.");
        var properties = schema["properties"] as JsonObject;
        if (properties is null)
        {
            properties = new JsonObject();
            schema["properties"] = properties;
        }
        if (properties.ContainsKey(PropertyName))
            throw new InvalidOperationException("The SDK schema already defines symbolContextId.");

        properties[PropertyName] = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^sym_[0-9a-f]{32}$",
                },
                new JsonObject { ["type"] = "null" }),
            ["default"] = null,
            ["description"] = PropertyDescription,
        };
        tool.ProtocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(
            schema.ToJsonString(),
            McpJsonUtilities.DefaultOptions);
    }

    internal static bool AdvertisesExpectedProperty(Tool tool)
    {
        var schema = tool.InputSchema;
        return schema.ValueKind == JsonValueKind.Object &&
               schema.TryGetProperty("properties", out var properties) &&
               properties.ValueKind == JsonValueKind.Object &&
               properties.TryGetProperty(PropertyName, out var property) &&
               property.ValueKind == JsonValueKind.Object &&
               property.TryGetProperty("description", out var description) &&
               string.Equals(description.GetString(), PropertyDescription, StringComparison.Ordinal);
    }
}
