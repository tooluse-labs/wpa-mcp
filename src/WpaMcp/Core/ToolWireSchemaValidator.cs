using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WpaMcp.Core;

internal sealed record ToolWireValidationFailure(string Path, string Message);

internal static class ToolWireSchemaValidator
{
    internal static void ValidateOrThrow(JsonNode? value, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var failures = Validate(value, schema);
        if (failures.Count != 0)
        {
            throw new InvalidOperationException(
                "The finalized tool envelope does not match its advertised schema: " +
                string.Join("; ", failures.Take(8).Select(item => $"{item.Path}: {item.Message}")));
        }
    }

    internal static IReadOnlyList<ToolWireValidationFailure> Validate(JsonNode? value, JsonObject schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var failures = new List<ToolWireValidationFailure>();
        failures.AddRange(ToolOutputSchemaReferences.Validate(schema)
            .Select(violation => new ToolWireValidationFailure(
                violation.Path,
                violation.Code + ": " + violation.Message)));
        if (failures.Count == 0)
            Validate(value, schema, schema, "$", failures);
        return failures;
    }

    private static void Validate(
        JsonNode? value,
        JsonObject schema,
        JsonObject rootSchema,
        string path,
        List<ToolWireValidationFailure> failures)
    {
        if (!ToolOutputSchemaReferences.TryResolve(
                rootSchema,
                schema,
                out schema,
                out var referenceError))
        {
            failures.Add(new(path, referenceError));
            return;
        }
        if (schema["anyOf"] is JsonArray alternatives)
        {
            if (value is null && alternatives.Any(candidate =>
                    IsNullSchema(candidate, rootSchema)))
                return;
            var candidates = alternatives
                .OfType<JsonObject>()
                .Where(candidate => !IsNullSchema(candidate, rootSchema))
                .ToArray();
            if (candidates.Length != 1)
            {
                failures.Add(new(path, "nullable schema is not closed"));
                return;
            }
            Validate(value, candidates[0], rootSchema, path, failures);
            return;
        }

        var type = schema["type"]?.GetValue<string>();
        if (type == "null")
        {
            if (value is not null) failures.Add(new(path, "expected null"));
            return;
        }
        if (value is null)
        {
            failures.Add(new(path, $"expected {type}, got null"));
            return;
        }

        switch (type)
        {
            case "object":
                ValidateObject(value, schema, rootSchema, path, failures);
                break;
            case "array":
                ValidateArray(value, schema, rootSchema, path, failures);
                break;
            case "string":
                ValidateString(value, schema, path, failures);
                break;
            case "integer":
                ValidateInteger(value, schema, path, failures);
                break;
            case "number":
                ValidateNumber(value, path, failures);
                break;
            case "boolean":
                if (value is not JsonValue boolean || !boolean.TryGetValue<bool>(out _))
                    failures.Add(new(path, "expected boolean"));
                break;
            default:
                failures.Add(new(path, $"unsupported schema type '{type}'"));
                break;
        }
    }

    private static void ValidateObject(
        JsonNode value,
        JsonObject schema,
        JsonObject rootSchema,
        string path,
        List<ToolWireValidationFailure> failures)
    {
        if (value is not JsonObject objectValue)
        {
            failures.Add(new(path, "expected object"));
            return;
        }
        var properties = schema["properties"] as JsonObject;
        var required = schema["required"] as JsonArray;
        if (properties is null || required is null)
        {
            failures.Add(new(path, "object schema is incomplete"));
            return;
        }
        foreach (var name in required.Select(node => node!.GetValue<string>()))
        {
            if (!objectValue.ContainsKey(name))
                failures.Add(new(path + "." + name, "required property is absent"));
        }
        foreach (var property in objectValue)
        {
            if (properties[property.Key] is not JsonObject propertySchema)
                failures.Add(new(path + "." + property.Key, "additional property is forbidden"));
            else
                Validate(
                    property.Value,
                    propertySchema,
                    rootSchema,
                    path + "." + property.Key,
                    failures);
        }
    }

    private static void ValidateArray(
        JsonNode value,
        JsonObject schema,
        JsonObject rootSchema,
        string path,
        List<ToolWireValidationFailure> failures)
    {
        if (value is not JsonArray array || schema["items"] is not JsonObject itemSchema)
        {
            failures.Add(new(path, "expected typed array"));
            return;
        }
        for (var index = 0; index < array.Count; index++)
            Validate(array[index], itemSchema, rootSchema, $"{path}[{index}]", failures);
    }

    private static void ValidateString(
        JsonNode value,
        JsonObject schema,
        string path,
        List<ToolWireValidationFailure> failures)
    {
        if (value is not JsonValue scalar || !scalar.TryGetValue<string>(out var text))
        {
            failures.Add(new(path, "expected string"));
            return;
        }
        if (schema["pattern"]?.GetValue<string>() is { } pattern &&
            !Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            failures.Add(new(path, "string does not match canonical pattern"));
        if (schema["enum"] is JsonArray values &&
            !values.Any(candidate => string.Equals(candidate!.GetValue<string>(), text, StringComparison.Ordinal)))
            failures.Add(new(path, "string is outside the closed enum"));
    }

    private static void ValidateInteger(
        JsonNode value,
        JsonObject schema,
        string path,
        List<ToolWireValidationFailure> failures)
    {
        if (value is not JsonValue scalar || !TryInteger(scalar, out var integer))
        {
            failures.Add(new(path, "expected exact JSON integer"));
            return;
        }
        if (schema["minimum"] is JsonValue minimum && TryInteger(minimum, out var minimumValue) && integer < minimumValue)
            failures.Add(new(path, "integer is below minimum"));
        if (schema["maximum"] is JsonValue maximum && TryInteger(maximum, out var maximumValue) && integer > maximumValue)
            failures.Add(new(path, "integer is above maximum"));
    }

    private static void ValidateNumber(JsonNode value, string path, List<ToolWireValidationFailure> failures)
    {
        if (value is not JsonValue scalar)
        {
            failures.Add(new(path, "expected number"));
            return;
        }
        if (scalar.TryGetValue<double>(out var doubleValue) && double.IsFinite(doubleValue)) return;
        if (scalar.TryGetValue<decimal>(out _)) return;
        failures.Add(new(path, "expected finite JSON number"));
    }

    private static bool TryInteger(JsonValue value, out decimal integer)
    {
        if (value.TryGetValue<int>(out var signed32)) { integer = signed32; return true; }
        if (value.TryGetValue<uint>(out var unsigned32)) { integer = unsigned32; return true; }
        if (value.TryGetValue<long>(out var signed64)) { integer = signed64; return true; }
        if (value.TryGetValue<ulong>(out var unsigned64)) { integer = unsigned64; return true; }
        if (value.TryGetValue<decimal>(out var decimalValue) && decimal.Truncate(decimalValue) == decimalValue)
        {
            integer = decimalValue;
            return true;
        }
        if (value.TryGetValue<string>(out var text) &&
            decimal.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            integer = parsed;
            return false;
        }
        integer = 0;
        return false;
    }

    private static bool IsNullSchema(JsonNode? node, JsonObject rootSchema) =>
        node is JsonObject schema &&
        ToolOutputSchemaReferences.TryResolve(
            rootSchema,
            schema,
            out var resolved,
            out _) &&
        resolved["type"]?.GetValue<string>() == "null";
}
