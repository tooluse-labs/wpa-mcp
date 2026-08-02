using System.Collections;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WpaMcp.Output;

namespace WpaMcp.Core;

/// <summary>
/// Projects CLR result DTOs into the contract-2.0 JSON representation. In particular,
/// every 64-bit integer is an exact canonical decimal string unless a property carries
/// the narrowly reviewed JavaScript-safe compatibility attribute.
/// </summary>
internal static class ToolWireJson
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    internal static JsonNode? Project(object? value, Type declaredType)
    {
        ArgumentNullException.ThrowIfNull(declaredType);
        return ProjectValue(value, Nullable.GetUnderlyingType(declaredType) ?? declaredType, new HashSet<object>(ReferenceEqualityComparer.Instance));
    }

    internal static JsonObject ProjectEnvelope(object envelope, Type dataType)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(dataType);
        var expected = typeof(ToolEnvelope<>).MakeGenericType(dataType);
        if (!expected.IsInstanceOfType(envelope))
            throw new ArgumentException($"Expected an instance of '{expected.FullName}'.", nameof(envelope));
        return Project(envelope, expected)!.AsObject();
    }

    private static JsonNode? ProjectValue(object? value, Type type, HashSet<object> ancestors)
    {
        if (value is null)
            return null;

        if (type == typeof(string) || type == typeof(char))
            return JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture));
        if (type == typeof(bool))
            return JsonValue.Create((bool)value);
        if (type == typeof(byte)) return JsonValue.Create((byte)value);
        if (type == typeof(sbyte)) return JsonValue.Create((sbyte)value);
        if (type == typeof(short)) return JsonValue.Create((short)value);
        if (type == typeof(ushort)) return JsonValue.Create((ushort)value);
        if (type == typeof(int)) return JsonValue.Create((int)value);
        if (type == typeof(uint)) return JsonValue.Create((uint)value);
        if (type == typeof(long)) return JsonValue.Create(((long)value).ToString(CultureInfo.InvariantCulture));
        if (type == typeof(ulong)) return JsonValue.Create(((ulong)value).ToString(CultureInfo.InvariantCulture));
        if (type == typeof(nint)) return JsonValue.Create(((nint)value).ToString(CultureInfo.InvariantCulture));
        if (type == typeof(nuint)) return JsonValue.Create(((nuint)value).ToString(CultureInfo.InvariantCulture));
        if (type == typeof(float))
        {
            var number = (float)value;
            if (!float.IsFinite(number)) throw new InvalidOperationException("Non-finite floating-point output is not valid JSON.");
            return JsonValue.Create(number);
        }
        if (type == typeof(double))
        {
            var number = (double)value;
            if (!double.IsFinite(number)) throw new InvalidOperationException("Non-finite floating-point output is not valid JSON.");
            return JsonValue.Create(number);
        }
        if (type == typeof(decimal)) return JsonValue.Create((decimal)value);
        if (type == typeof(Guid)) return JsonValue.Create(((Guid)value).ToString("D", CultureInfo.InvariantCulture));
        if (type == typeof(DateTime)) return JsonValue.Create(((DateTime)value).ToString("O", CultureInfo.InvariantCulture));
        if (type == typeof(DateTimeOffset)) return JsonValue.Create(((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture));
        if (type == typeof(TimeSpan)) return JsonValue.Create(((TimeSpan)value).ToString("c", CultureInfo.InvariantCulture));
        if (type.IsEnum) return JsonValue.Create(EnumWireName(type, value));

        if (ToolOutputSchemaFactory.SchemaBuilder.TryGetCollectionElement(type, out var elementType))
        {
            var array = new JsonArray();
            foreach (var item in (IEnumerable)value)
                array.Add(ProjectValue(item, elementType, ancestors));
            return array;
        }

        if (!type.IsValueType && !ancestors.Add(value))
            throw new InvalidOperationException($"Recursive output object '{type.FullName}' cannot be projected.");

        try
        {
            var projected = new JsonObject();
            foreach (var property in SerializableProperties(type))
            {
                var propertyValue = property.GetValue(value);
                projected[WireName(property)] = ProjectProperty(value, property, propertyValue, ancestors);
            }
            return projected;
        }
        finally
        {
            if (!type.IsValueType)
                ancestors.Remove(value);
        }
    }

    private static JsonNode? ProjectProperty(
        object owner,
        PropertyInfo property,
        object? value,
        HashSet<object> ancestors)
    {
        if (property.GetCustomAttribute<ToolDictionaryRowsAttribute>() is { } rows)
            return ProjectDictionaryRows(property, value, rows, ancestors);

        if (property.GetCustomAttribute<ToolSafeIntegerCompatibilityAttribute>() is { } compatibility)
            return ProjectSafeCompatibility(owner, property, value, compatibility);

        return ProjectValue(value, Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType, ancestors);
    }

    private static JsonNode? ProjectDictionaryRows(
        PropertyInfo property,
        object? value,
        ToolDictionaryRowsAttribute rows,
        HashSet<object> ancestors)
    {
        if (value is null)
            return null;
        if (!ToolOutputSchemaFactory.SchemaBuilder.TryGetDictionaryArguments(property.PropertyType, out var keyType, out var valueType))
            throw new InvalidOperationException($"'{property.Name}' is not a dictionary.");

        var entries = new List<(object? Key, object? Value)>();
        foreach (var entry in (IEnumerable)value)
        {
            var entryType = entry!.GetType();
            var key = entryType.GetProperty("Key")!.GetValue(entry);
            var itemValue = entryType.GetProperty("Value")!.GetValue(entry);
            entries.Add((key, itemValue));
        }

        var projected = new JsonArray();
        IEnumerable<(object? Key, object? Value)> ordered = keyType == typeof(int)
            ? entries.OrderBy(item => (int)item.Key!)
            : keyType == typeof(string)
                ? entries.OrderBy(item => (string)item.Key!, StringComparer.Ordinal)
                : throw new InvalidOperationException(
                    $"Dictionary-row key type '{keyType.FullName}' is not approved for public output.");
        foreach (var entry in ordered)
        {
            projected.Add(new JsonObject
            {
                [rows.KeyPropertyName] = ProjectValue(entry.Key, keyType, ancestors),
                [rows.ValuePropertyName] = ProjectValue(entry.Value, valueType, ancestors),
            });
        }
        return projected;
    }

    private static JsonNode? ProjectSafeCompatibility(
        object owner,
        PropertyInfo property,
        object? value,
        ToolSafeIntegerCompatibilityAttribute compatibility)
    {
        if (property.PropertyType != typeof(ulong?))
            throw new InvalidOperationException($"'{property.Name}' is not an approved nullable ulong compatibility projection.");

        var authoritative = property.DeclaringType!.GetProperty(compatibility.AuthoritativeStringProperty)?.GetValue(owner) as string;
        var status = property.DeclaringType.GetProperty(compatibility.StatusProperty)?.GetValue(owner) as string;
        if (!TryParseCanonicalUnsigned(authoritative, out var exact))
            throw new InvalidOperationException($"'{compatibility.AuthoritativeStringProperty}' is not a canonical unsigned decimal string.");

        if (value is ulong numeric)
        {
            if (numeric > (ulong)PublicIdentifierFormatter.JavaScriptMaxSafeInteger || numeric != exact ||
                !string.Equals(status, "exact_safe_integer_deprecated", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"'{property.Name}' is not an exact JavaScript-safe projection of its authoritative sibling.");
            }
            return JsonValue.Create(numeric);
        }

        if (exact <= (ulong)PublicIdentifierFormatter.JavaScriptMaxSafeInteger ||
            !string.Equals(status, "null_unsafe_integer_deprecated", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"A null '{property.Name}' must represent an authoritative value above Number.MAX_SAFE_INTEGER.");
        }
        return null;
    }

    private static bool TryParseCanonicalUnsigned(string? value, out ulong parsed)
    {
        parsed = 0;
        if (string.IsNullOrEmpty(value) || (value.Length > 1 && value[0] == '0'))
            return false;
        return ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed);
    }

    private static PropertyInfo[] SerializableProperties(Type type) => Properties.GetOrAdd(
        type,
        static candidate => candidate.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true)
            .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
            .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
            .OrderBy(property => property.MetadataToken)
            .ToArray());

    private static string WireName(PropertyInfo property) =>
        property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
        JsonNamingPolicy.CamelCase.ConvertName(property.Name);

    private static string EnumWireName(Type enumType, object value)
    {
        var name = Enum.GetName(enumType, value)
            ?? throw new InvalidOperationException($"Undefined enum value '{value}' for '{enumType.FullName}'.");
        var field = enumType.GetField(name)!;
        return field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? name;
    }
}
