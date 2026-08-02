using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WpaMcp.Core;

/// <summary>
/// Bounded validator for the JSON Schema subset emitted by the MCP SDK for
/// public tool inputs. Validation happens before trace/symbol resolution so a
/// malformed ordinary argument cannot escape the contract wrapper.
/// </summary>
internal static class ToolInputSchemaValidator
{
    private static readonly TimeSpan PatternTimeout = TimeSpan.FromMilliseconds(100);

    internal static void Validate(
        JsonElement schema,
        IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("A reviewed tool input schema must be an object schema.");
        using var document = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(arguments));
        ValidateValue(schema, document.RootElement, "$arguments");
    }

    private static void ValidateValue(JsonElement schema, JsonElement value, string path)
    {
        if (schema.ValueKind is JsonValueKind.True)
            return;
        if (schema.ValueKind is JsonValueKind.False or JsonValueKind.Undefined or JsonValueKind.Null)
            throw Invalid(path, "is rejected by the advertised schema");

        if (schema.TryGetProperty("anyOf", out var anyOf))
        {
            if (!MatchesAny(anyOf, value, path))
                throw Invalid(path, "does not match any advertised schema alternative");
            return;
        }
        if (schema.TryGetProperty("oneOf", out var oneOf))
        {
            var matches = CountMatches(oneOf, value, path);
            if (matches != 1)
                throw Invalid(path, "does not match exactly one advertised schema alternative");
            return;
        }
        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var alternative in allOf.EnumerateArray())
                ValidateValue(alternative, value, path);
        }
        if (schema.TryGetProperty("not", out var notSchema) && Matches(notSchema, value, path))
            throw Invalid(path, "matches a forbidden advertised schema");

        ValidateType(schema, value, path);
        ValidateEnum(schema, value, path);
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                ValidateObject(schema, value, path);
                break;
            case JsonValueKind.Array:
                ValidateArray(schema, value, path);
                break;
            case JsonValueKind.String:
                ValidateString(schema, value.GetString()!, path);
                break;
            case JsonValueKind.Number:
                ValidateNumber(schema, value, path);
                break;
        }
    }

    private static void ValidateType(JsonElement schema, JsonElement value, string path)
    {
        if (!schema.TryGetProperty("type", out var declared))
            return;
        var matches = declared.ValueKind switch
        {
            JsonValueKind.String => MatchesType(declared.GetString()!, value),
            JsonValueKind.Array => declared.EnumerateArray().Any(item =>
                item.ValueKind == JsonValueKind.String && MatchesType(item.GetString()!, value)),
            _ => false,
        };
        if (!matches)
            throw Invalid(path, "has a type that differs from the advertised input schema");
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && IsInteger(value),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => throw new InvalidOperationException($"Unsupported advertised input schema type '{type}'."),
    };

    private static void ValidateObject(JsonElement schema, JsonElement value, string path)
    {
        var properties = schema.TryGetProperty("properties", out var declaredProperties) &&
            declaredProperties.ValueKind == JsonValueKind.Object
                ? declaredProperties
                : default;
        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var item in required.EnumerateArray())
            {
                var name = item.GetString()!;
                if (!value.TryGetProperty(name, out _))
                    throw Invalid(path + "." + name, "is required");
            }
        }

        var additionalAllowed = true;
        JsonElement additionalSchema = default;
        if (schema.TryGetProperty("additionalProperties", out var additional))
        {
            additionalAllowed = additional.ValueKind != JsonValueKind.False;
            if (additional.ValueKind == JsonValueKind.Object)
                additionalSchema = additional;
        }
        foreach (var property in value.EnumerateObject())
        {
            if (properties.ValueKind == JsonValueKind.Object &&
                properties.TryGetProperty(property.Name, out var propertySchema))
            {
                ValidateValue(propertySchema, property.Value, path + "." + property.Name);
            }
            else if (additionalSchema.ValueKind == JsonValueKind.Object)
            {
                ValidateValue(additionalSchema, property.Value, path + "." + property.Name);
            }
            else if (!additionalAllowed)
            {
                throw Invalid(path + "." + property.Name, "is not an advertised input property");
            }
        }
    }

    private static void ValidateArray(JsonElement schema, JsonElement value, string path)
    {
        var length = value.GetArrayLength();
        if (ReadInt(schema, "minItems") is { } minimum && length < minimum)
            throw Invalid(path, $"contains fewer than {minimum} items");
        if (ReadInt(schema, "maxItems") is { } maximum && length > maximum)
            throw Invalid(path, $"contains more than {maximum} items");
        if (schema.TryGetProperty("items", out var itemSchema))
        {
            var index = 0;
            foreach (var item in value.EnumerateArray())
                ValidateValue(itemSchema, item, $"{path}[{index++}]");
        }
        if (schema.TryGetProperty("uniqueItems", out var unique) && unique.ValueKind == JsonValueKind.True)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateArray())
            {
                if (!seen.Add(item.GetRawText()))
                    throw Invalid(path, "contains duplicate items but advertises uniqueItems");
            }
        }
    }

    private static void ValidateString(JsonElement schema, string value, string path)
    {
        if (ReadInt(schema, "minLength") is { } minimum && value.Length < minimum)
            throw Invalid(path, $"is shorter than {minimum} characters");
        if (ReadInt(schema, "maxLength") is { } maximum && value.Length > maximum)
            throw Invalid(path, $"is longer than {maximum} characters");
        if (schema.TryGetProperty("pattern", out var pattern) &&
            !Regex.IsMatch(value, pattern.GetString()!, RegexOptions.CultureInvariant, PatternTimeout))
        {
            throw Invalid(path, "does not match the advertised pattern");
        }
    }

    private static void ValidateNumber(JsonElement schema, JsonElement value, string path)
    {
        if (!decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw Invalid(path, "is outside the supported numeric range");
        if (ReadDecimal(schema, "minimum") is { } minimum && number < minimum)
            throw Invalid(path, $"is below the advertised minimum {minimum}");
        if (ReadDecimal(schema, "maximum") is { } maximum && number > maximum)
            throw Invalid(path, $"is above the advertised maximum {maximum}");
        if (ReadDecimal(schema, "exclusiveMinimum") is { } exclusiveMinimum && number <= exclusiveMinimum)
            throw Invalid(path, $"must be greater than {exclusiveMinimum}");
        if (ReadDecimal(schema, "exclusiveMaximum") is { } exclusiveMaximum && number >= exclusiveMaximum)
            throw Invalid(path, $"must be less than {exclusiveMaximum}");
    }

    private static void ValidateEnum(JsonElement schema, JsonElement value, string path)
    {
        if (schema.TryGetProperty("const", out var constant) && !JsonEquals(constant, value))
            throw Invalid(path, "differs from the advertised constant");
        if (schema.TryGetProperty("enum", out var choices) &&
            !choices.EnumerateArray().Any(choice => JsonEquals(choice, value)))
        {
            throw Invalid(path, "is not one of the advertised values");
        }
    }

    private static bool MatchesAny(JsonElement alternatives, JsonElement value, string path) =>
        alternatives.EnumerateArray().Any(item => Matches(item, value, path));

    private static int CountMatches(JsonElement alternatives, JsonElement value, string path) =>
        alternatives.EnumerateArray().Count(item => Matches(item, value, path));

    private static bool Matches(JsonElement schema, JsonElement value, string path)
    {
        try
        {
            ValidateValue(schema, value, path);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool JsonEquals(JsonElement left, JsonElement right) =>
        string.Equals(left.GetRawText(), right.GetRawText(), StringComparison.Ordinal);

    private static bool IsInteger(JsonElement value)
    {
        var raw = value.GetRawText();
        return raw.IndexOfAny(['.', 'e', 'E']) < 0;
    }

    private static int? ReadInt(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static decimal? ReadDecimal(JsonElement schema, string name) =>
        schema.TryGetProperty(name, out var value) &&
        decimal.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
            ? result
            : null;

    private static ArgumentException Invalid(string path, string detail) =>
        new($"invalid_argument: '{path}' {detail}.");
}
