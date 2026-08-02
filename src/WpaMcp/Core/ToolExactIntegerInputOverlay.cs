using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace WpaMcp.Core;

/// <summary>
/// Public MCP inputs use canonical decimal strings for Int64/UInt64 values. The
/// wrapper converts those strings back to exact CLR integers immediately before
/// SDK binding; PID/TID and other Int32 selectors remain bounded JSON numbers.
/// </summary>
internal static class ToolExactIntegerInputOverlay
{
    internal const string OverlayId = "exact_integer_input_v1";
    internal const string SignedPattern = "^(0|-?[1-9][0-9]*)$";
    internal const string UnsignedPattern = "^(0|[1-9][0-9]*)$";

    internal static bool AppliesTo(MethodInfo method) => method.GetParameters().Any(parameter =>
        Contains64BitInteger(parameter.ParameterType));

    internal static void Apply(McpServerTool tool, MethodInfo method)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(method);
        if (!AppliesTo(method))
            return;

        var schema = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText()) as JsonObject
            ?? throw new InvalidOperationException("The SDK tool input schema is not an object.");
        var properties = schema["properties"] as JsonObject
            ?? throw new InvalidOperationException("The SDK tool input schema has no properties object.");
        foreach (var parameter in method.GetParameters().Where(parameter => Contains64BitInteger(parameter.ParameterType)))
        {
            var wireName = parameter.Name
                ?? throw new InvalidOperationException("A public tool parameter has no name.");
            if (properties[wireName] is not JsonObject existing)
                throw new InvalidOperationException($"The SDK input schema omitted Int64 parameter '{wireName}'.");
            properties[wireName] = RewriteSchema(existing, parameter.ParameterType);
        }

        schema["x-exactIntegerInputOverlay"] = OverlayId;
        tool.ProtocolTool.InputSchema = JsonSerializer.Deserialize<JsonElement>(
            schema.ToJsonString(),
            McpJsonUtilities.DefaultOptions);
    }

    internal static IReadOnlyDictionary<string, JsonElement> RewriteArguments(
        MethodInfo method,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        ArgumentNullException.ThrowIfNull(method);
        var rewritten = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : arguments.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        foreach (var parameter in method.GetParameters().Where(parameter => Contains64BitInteger(parameter.ParameterType)))
        {
            var name = parameter.Name!;
            if (rewritten.TryGetValue(name, out var value))
                rewritten[name] = RewriteValue(value, parameter.ParameterType, name);
        }
        return rewritten;
    }

    internal static bool AdvertisesExactIntegers(Tool tool, MethodInfo method)
    {
        if (!AppliesTo(method))
            return true;
        if (tool.InputSchema.ValueKind != JsonValueKind.Object ||
            !tool.InputSchema.TryGetProperty("x-exactIntegerInputOverlay", out var overlay) ||
            overlay.GetString() != OverlayId ||
            !tool.InputSchema.TryGetProperty("properties", out var properties))
            return false;
        return method.GetParameters().Where(parameter => Contains64BitInteger(parameter.ParameterType)).All(parameter =>
            properties.TryGetProperty(parameter.Name!, out var property) && SchemaMatches(property, parameter.ParameterType));
    }

    private static JsonObject RewriteSchema(JsonObject existing, Type declaredType)
    {
        var nullableType = Nullable.GetUnderlyingType(declaredType);
        var type = nullableType ?? declaredType;
        JsonObject replacement;
        if (TryGetCollectionElement(type, out var elementType))
        {
            var existingItems = NonNull(existing)["items"] as JsonObject ?? new JsonObject();
            replacement = new JsonObject
            {
                ["type"] = "array",
                ["items"] = RewriteSchema(existingItems, elementType),
            };
        }
        else
        {
            var unsigned = type == typeof(ulong) || type == typeof(nuint);
            replacement = new JsonObject
            {
                ["type"] = "string",
                ["pattern"] = unsigned ? UnsignedPattern : SignedPattern,
                ["x-exactInteger"] = unsigned ? "unsigned-64-decimal" : "signed-64-decimal",
            };
        }

        JsonObject final = nullableType is not null
            ? new JsonObject
            {
                ["anyOf"] = new JsonArray(replacement, new JsonObject { ["type"] = "null" }),
            }
            : replacement;
        CopyAnnotation(existing, final, "description");
        if (existing["default"] is { } defaultValue)
        {
            final["default"] = defaultValue is JsonValue scalar &&
                scalar.TryGetValue<long>(out var signedDefault)
                    ? JsonValue.Create(signedDefault.ToString(CultureInfo.InvariantCulture))
                    : defaultValue.DeepClone();
        }
        return final;
    }

    private static JsonElement RewriteValue(JsonElement value, Type declaredType, string parameterName)
    {
        var nullableType = Nullable.GetUnderlyingType(declaredType);
        if (value.ValueKind == JsonValueKind.Null && nullableType is not null)
            return value.Clone();
        var type = nullableType ?? declaredType;
        if (TryGetCollectionElement(type, out var elementType))
        {
            if (value.ValueKind != JsonValueKind.Array)
                throw Invalid(parameterName, "must be an array of canonical decimal strings");
            var array = new JsonArray();
            foreach (var item in value.EnumerateArray())
                array.Add(JsonNode.Parse(RewriteValue(item, elementType, parameterName).GetRawText()));
            return JsonSerializer.SerializeToElement(array, McpJsonUtilities.DefaultOptions);
        }

        if (value.ValueKind != JsonValueKind.String)
            throw Invalid(parameterName, "must be a canonical decimal string, not a JSON number");
        var text = value.GetString()!;
        if (type == typeof(long) || type == typeof(nint))
        {
            if (!IsCanonicalSigned(text) || !long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed))
                throw Invalid(parameterName, "is outside Int64 or is not canonical decimal");
            return JsonSerializer.SerializeToElement(signed, McpJsonUtilities.DefaultOptions);
        }
        if (type == typeof(ulong) || type == typeof(nuint))
        {
            if (!IsCanonicalUnsigned(text) || !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
                throw Invalid(parameterName, "is outside UInt64 or is not canonical decimal");
            return JsonSerializer.SerializeToElement(unsigned, McpJsonUtilities.DefaultOptions);
        }
        throw new InvalidOperationException($"Unsupported exact integer input type '{type.FullName}'.");
    }

    private static bool SchemaMatches(JsonElement schema, Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (TryGetCollectionElement(type, out var elementType))
        {
            var nonNull = NonNull(schema);
            return nonNull.TryGetProperty("type", out var arrayType) && arrayType.GetString() == "array" &&
                nonNull.TryGetProperty("items", out var items) && SchemaMatches(items, elementType);
        }

        var valueSchema = NonNull(schema);
        var expectedPattern = type == typeof(ulong) || type == typeof(nuint) ? UnsignedPattern : SignedPattern;
        return valueSchema.TryGetProperty("type", out var kind) && kind.GetString() == "string" &&
            valueSchema.TryGetProperty("pattern", out var pattern) && pattern.GetString() == expectedPattern;
    }

    private static JsonObject NonNull(JsonObject schema) => schema["anyOf"] is JsonArray alternatives
        ? alternatives.Select(item => item!.AsObject()).Single(item => item["type"]?.GetValue<string>() != "null")
        : schema;

    private static JsonElement NonNull(JsonElement schema)
    {
        if (!schema.TryGetProperty("anyOf", out var alternatives))
            return schema;
        return alternatives.EnumerateArray().Single(item =>
            !item.TryGetProperty("type", out var type) || type.GetString() != "null");
    }

    private static void CopyAnnotation(JsonObject source, JsonObject destination, string name)
    {
        if (source[name] is { } value)
            destination[name] = value.DeepClone();
    }

    private static bool Contains64BitInteger(Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(long) || type == typeof(ulong) || type == typeof(nint) || type == typeof(nuint))
            return true;
        return TryGetCollectionElement(type, out var element) && Contains64BitInteger(element);
    }

    private static bool TryGetCollectionElement(Type type, out Type elementType)
    {
        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
            return true;
        }
        var enumerable = type.GetInterfaces().FirstOrDefault(candidate =>
            candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerable is not null && type != typeof(string))
        {
            elementType = enumerable.GetGenericArguments()[0];
            return true;
        }
        elementType = null!;
        return false;
    }

    private static bool IsCanonicalSigned(string value) =>
        value == "0" ||
        (value.Length > 0 && value[0] != '0' && value != "-0" &&
         (value[0] != '-' || value.Length > 1 && value[1] != '0') &&
         value[(value[0] == '-' ? 1 : 0)..].All(char.IsAsciiDigit));

    private static bool IsCanonicalUnsigned(string value) =>
        value == "0" || value.Length > 0 && value[0] != '0' && value.All(char.IsAsciiDigit);

    private static ArgumentException Invalid(string parameterName, string detail) =>
        new($"invalid_argument: '{parameterName}' {detail}.", parameterName);
}
