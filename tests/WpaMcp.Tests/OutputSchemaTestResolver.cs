using System.Text.Json.Nodes;
using WpaMcp.Core;

namespace WpaMcp.Tests;

internal static class OutputSchemaTestResolver
{
    internal static JsonObject Resolve(JsonNode node)
    {
        var schema = node.AsObject();
        var root = Root(schema);
        return ToolOutputSchemaReferences.TryResolve(
            root,
            schema,
            out var resolved,
            out var error)
            ? resolved
            : throw new InvalidOperationException(error);
    }

    internal static JsonObject NonNull(JsonNode node)
    {
        var schema = Resolve(node);
        if (schema["anyOf"] is JsonArray alternatives)
        {
            schema = alternatives
                .Select(item => item!.AsObject())
                .Single(item => Resolve(item)["type"]?.GetValue<string>() != "null");
        }
        return Resolve(schema);
    }

    internal static JsonObject Properties(JsonNode node) =>
        NonNull(node)["properties"]?.AsObject()
        ?? throw new InvalidOperationException("Resolved schema has no properties object.");

    internal static JsonObject Items(JsonNode node) =>
        NonNull(NonNull(node)["items"]
            ?? throw new InvalidOperationException("Resolved schema has no items schema."));

    private static JsonObject Root(JsonNode node)
    {
        while (node.Parent is not null)
            node = node.Parent;
        return node.AsObject();
    }
}
