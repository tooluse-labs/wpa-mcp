using System.ComponentModel.DataAnnotations;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record ToolOutputSchemaViolation(string Code, string Path, string Message);

internal sealed record ToolOutputContract(
    string ToolName,
    string ContractVersion,
    string SchemaDialect,
    string SchemaUri,
    string Sha256,
    string MediaType,
    string CanonicalJson,
    int Utf8Bytes)
{
    internal const string MetadataKey = "wpa-mcp/outputContract";
    internal const string ContractMediaType = "application/schema+json";
    internal const string Draft202012 = "https://json-schema.org/draft/2020-12/schema";

    internal JsonObject ParseSchema() =>
        JsonNode.Parse(CanonicalJson) as JsonObject
        ?? throw new InvalidOperationException($"The output contract for '{ToolName}' is not a JSON object.");

    internal JsonElement ToJsonElement() =>
        JsonSerializer.Deserialize<JsonElement>(CanonicalJson, McpJsonUtilities.DefaultOptions);

    internal JsonObject ToDiscoveryMetadata() => new()
    {
        ["contractVersion"] = ContractVersion,
        ["schemaDialect"] = SchemaDialect,
        ["uri"] = SchemaUri,
        ["sha256"] = Sha256,
        ["mediaType"] = MediaType,
        ["utf8Bytes"] = Utf8Bytes,
        ["representation"] = "utf8_json_pages",
    };
}

/// <summary>
/// Authoritative Contract 2.0 schema source for server validation, discovery metadata,
/// content-addressed resources, and reviewed contract snapshots.
/// </summary>
internal static class ToolOutputSchemaFactory
{
    private static readonly JsonSerializerOptions CanonicalJsonOptions =
        new(McpJsonUtilities.DefaultOptions)
        {
            Encoder = JavaScriptEncoder.Default,
            WriteIndented = false,
        };

    internal static JsonObject CreateEnvelopeSchema<TData>() where TData : class =>
        CreateEnvelopeSchema(typeof(TData));

    internal static ToolOutputContract CreateContract(string toolName, Type dataType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(dataType);
        var schema = CreateEnvelopeSchema(dataType);
        var canonicalJson = schema.ToJsonString(CanonicalJsonOptions);
        if (canonicalJson.Any(character => character > 0x7f))
        {
            throw new InvalidOperationException(
                $"The canonical output contract for '{toolName}' must be ASCII-safe UTF-8 JSON.");
        }

        var bytes = Encoding.UTF8.GetBytes(canonicalJson);
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new ToolOutputContract(
            toolName,
            ToolContractVersions.V2,
            ToolOutputContract.Draft202012,
            $"wpa://contracts/tools/{toolName}/{sha256}",
            sha256,
            ToolOutputContract.ContractMediaType,
            canonicalJson,
            bytes.Length);
    }

    internal static JsonObject CreateEnvelopeSchema(Type dataType)
    {
        ArgumentNullException.ThrowIfNull(dataType);
        var envelopeType = typeof(ToolEnvelope<>).MakeGenericType(dataType);

        var typeViolations = ToolOutputSchemaLinter.LintPublicType(envelopeType);
        if (typeViolations.Count != 0)
            throw InvalidSchema(typeViolations);

        var schema = new SchemaBuilder().BuildRoot(envelopeType);
        var schemaViolations = ToolOutputSchemaLinter.LintSchema(schema);
        if (schemaViolations.Count != 0)
            throw InvalidSchema(schemaViolations);
        return schema;
    }

    private static InvalidOperationException InvalidSchema(IReadOnlyList<ToolOutputSchemaViolation> violations) =>
        new("The output contract is not a closed contract-2.0 schema: " + string.Join(
            "; ",
            violations.Select(violation => $"{violation.Code}@{violation.Path}")));

    internal sealed class SchemaBuilder
    {
        private readonly NullabilityInfoContext _nullability = new();
        private readonly JsonObject _numericSemantics = new();
        private readonly JsonObject _definitions = new();
        private readonly Dictionary<Type, string> _definitionNames = [];
        private readonly Dictionary<string, Type> _definitionTypes = new(StringComparer.Ordinal);

        internal JsonObject BuildRoot(Type type)
        {
            var root = Build(type, null, new HashSet<Type>(), inlineObject: true);
            root.Insert(0, "$schema", "https://json-schema.org/draft/2020-12/schema");
            root.Insert(1, "title", type.FullName ?? type.Name);
            root.Insert(2, "x-wpa-numeric-semantics", _numericSemantics);
            if (_definitions.Count != 0)
                root.Insert(3, "$defs", _definitions);
            return root;
        }

        private JsonObject Build(
            Type declaredType,
            NullabilityInfo? nullability,
            HashSet<Type> ancestors,
            bool inlineObject = false)
        {
            var nullableValueType = Nullable.GetUnderlyingType(declaredType);
            var type = nullableValueType ?? declaredType;
            var permitsNull = nullableValueType is not null ||
                (!type.IsValueType && nullability?.ReadState == NullabilityState.Nullable);

            var nonNull = BuildNonNull(type, nullability, ancestors, inlineObject);
            if (!permitsNull)
                return nonNull;

            return new JsonObject
            {
                ["anyOf"] = new JsonArray(
                    nonNull,
                    new JsonObject { ["type"] = "null" }),
            };
        }

        private JsonObject BuildNonNull(
            Type type,
            NullabilityInfo? nullability,
            HashSet<Type> ancestors,
            bool inlineObject)
        {
            if (type == typeof(string) || type == typeof(char) || type == typeof(Guid) ||
                type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan))
            {
                return new JsonObject { ["type"] = "string" };
            }
            if (type == typeof(bool))
                return new JsonObject { ["type"] = "boolean" };
            if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
                type == typeof(ushort) || type == typeof(int) || type == typeof(uint))
            {
                return new JsonObject { ["type"] = "integer" };
            }
            if (type == typeof(long) || type == typeof(nint))
            {
                return new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^(0|-?[1-9][0-9]*)$",
                    ["x-exactInteger"] = "signed-64-decimal",
                };
            }
            if (type == typeof(ulong) || type == typeof(nuint))
            {
                return new JsonObject
                {
                    ["type"] = "string",
                    ["pattern"] = "^(0|[1-9][0-9]*)$",
                    ["x-exactInteger"] = "unsigned-64-decimal",
                };
            }
            if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
                return new JsonObject { ["type"] = "number" };
            if (type.IsEnum)
            {
                return new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(EnumWireNames(type)
                        .Select(name => (JsonNode?)JsonValue.Create(name))
                        .ToArray()),
                };
            }

            if (TryGetCollectionElement(type, out var elementType))
            {
                var elementNullability = nullability?.ElementType ??
                    nullability?.GenericTypeArguments.FirstOrDefault();
                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = Build(elementType, elementNullability, ancestors),
                };
            }

            return inlineObject
                ? BuildObject(type, ancestors)
                : BuildReference(type, ancestors);
        }

        private JsonObject BuildObject(Type type, HashSet<Type> ancestors)
        {
            if (!ancestors.Add(type))
            {
                throw new InvalidOperationException(
                    $"Recursive output DTO '{type.FullName}' is not supported by this schema scaffold.");
            }
            try
            {
                var properties = new JsonObject();
                var required = new JsonArray();
                foreach (var property in SerializableProperties(type))
                {
                    var wireName = WireName(property);
                    properties[wireName] = BuildProperty(property, ancestors);
                    required.Add(wireName);
                }

                return new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = false,
                    ["properties"] = properties,
                    ["required"] = required,
                };
            }
            finally
            {
                ancestors.Remove(type);
            }
        }

        private JsonObject BuildReference(Type type, HashSet<Type> ancestors)
        {
            if (ancestors.Contains(type))
            {
                throw new InvalidOperationException(
                    $"Recursive output DTO '{type.FullName}' is not supported by this schema scaffold.");
            }

            if (!_definitionNames.TryGetValue(type, out var name))
            {
                name = DefinitionName(type);
                if (_definitionTypes.TryGetValue(name, out var collision) && collision != type)
                {
                    throw new InvalidOperationException(
                        $"Output schema definition id collision between '{collision.FullName}' and '{type.FullName}'.");
                }
                _definitionNames.Add(type, name);
                _definitionTypes[name] = type;
                _definitions.Add(name, BuildObject(type, ancestors));
            }

            return new JsonObject { ["$ref"] = "#/$defs/" + name };
        }

        private static string DefinitionName(Type type)
        {
            var identity = TypeIdentity(type);
            return "d_" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
                .ToLowerInvariant()[..12];
        }

        private static string TypeIdentity(Type type)
        {
            if (type.IsArray)
                return TypeIdentity(type.GetElementType()!) + "[]";
            if (!type.IsGenericType)
                return type.FullName ?? type.Name;
            var definition = type.GetGenericTypeDefinition().FullName
                ?? type.GetGenericTypeDefinition().Name;
            return definition + "<" +
                string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + ">";
        }

        private JsonObject BuildProperty(PropertyInfo property, HashSet<Type> ancestors)
        {
            var nullability = _nullability.Create(property);
            if (property.GetCustomAttribute<ToolDictionaryRowsAttribute>() is { } rows)
            {
                if (!TryGetDictionaryArguments(property.PropertyType, out var keyType, out var valueType))
                    throw new InvalidOperationException($"'{property.Name}' declares dictionary rows but is not a dictionary.");

                var keySchema = Build(keyType, null, ancestors);
                if (IsNumericScalar(Nullable.GetUnderlyingType(keyType) ?? keyType))
                {
                    DecorateNumericSchema(
                        NonNullBranch(keySchema),
                        DeclaredNumericSemantics(
                            "identifier",
                            "process_id",
                            "exact",
                            "not_applicable",
                            denominator: null,
                            unitProperty: null,
                            minimum: 0,
                            maximum: int.MaxValue,
                            rows.KeyPropertyName));
                }

                var nonNull = new JsonObject
                {
                    ["type"] = "array",
                    ["x-ordering"] = keyType == typeof(int)
                        ? $"{rows.KeyPropertyName}_ascending_numeric"
                        : $"{rows.KeyPropertyName}_ascending_ordinal",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            [rows.KeyPropertyName] = keySchema,
                            [rows.ValuePropertyName] = Build(valueType, null, ancestors),
                        },
                        ["required"] = new JsonArray(rows.KeyPropertyName, rows.ValuePropertyName),
                    },
                };
                return DecoratePropertySchema(
                    property,
                    PermitsNull(property.PropertyType, nullability) ? AsNullable(nonNull) : nonNull);
            }

            if (property.GetCustomAttribute<ToolSafeIntegerCompatibilityAttribute>() is not null)
            {
                var nonNull = new JsonObject
                {
                    ["type"] = "integer",
                    ["minimum"] = 0,
                    ["maximum"] = checked((long)PublicIdentifierFormatter.JavaScriptMaxSafeInteger),
                };
                return DecoratePropertySchema(
                    property,
                    PermitsNull(property.PropertyType, nullability) ? AsNullable(nonNull) : nonNull);
            }

            return DecoratePropertySchema(
                property,
                Build(property.PropertyType, nullability, ancestors));
        }

        private JsonObject DecoratePropertySchema(PropertyInfo property, JsonObject schema)
        {
            if (property.GetCustomAttribute<ObsoleteAttribute>() is { } obsolete)
            {
                schema["deprecated"] = true;
                if (!string.IsNullOrWhiteSpace(obsolete.Message))
                    schema["x-deprecationMessage"] = obsolete.Message;
            }
            if (property.GetCustomAttribute<ToolOpaqueLocatorAttribute>() is { } locator)
            {
                var nonNull = NonNullBranch(schema);
                nonNull["pattern"] = locator.Pattern;
                nonNull["x-opaqueLocator"] = locator.Kind;
            }
            if (property.GetCustomAttribute<DescriptionAttribute>() is { } description &&
                !string.IsNullOrWhiteSpace(description.Description))
            {
                schema["description"] = description.Description;
            }
            if (property.GetCustomAttribute<RangeAttribute>() is { } range)
            {
                if (TryNumber(range.Minimum, out var minimum))
                    schema["minimum"] = minimum;
                if (TryNumber(range.Maximum, out var maximum))
                    schema["maximum"] = maximum;
            }

            var scalar = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (IsNumericScalar(scalar))
            {
                DecorateNumericSchema(
                    NonNullBranch(schema),
                    BuildNumericSemantics(property, scalar, WireName(property)));
            }
            else if (TryGetCollectionElement(scalar, out var elementType))
            {
                var elementScalar = Nullable.GetUnderlyingType(elementType) ?? elementType;
                if (IsNumericScalar(elementScalar) &&
                    NonNullBranch(schema)["items"] is JsonObject itemSchema)
                {
                    DecorateNumericSchema(
                        NonNullBranch(itemSchema),
                        BuildNumericSemantics(property, elementScalar, "$item"));
                }
            }
            return schema;
        }

        private static JsonObject BuildNumericSemantics(
            PropertyInfo property,
            Type scalar,
            string valueField)
        {
            if (property.GetCustomAttribute<ToolNumericSemanticsAttribute>() is { } numeric)
            {
                return DeclaredNumericSemantics(
                    numeric.Role,
                    numeric.Unit,
                    numeric.Precision,
                    numeric.Aggregation,
                    numeric.Denominator,
                    numeric.UnitProperty,
                    numeric.Minimum,
                    numeric.Maximum,
                    valueField,
                    "explicit_attribute");
            }

            if (property.GetCustomAttribute<ToolMetricSemanticsAttribute>() is { } declared)
            {
                return DeclaredNumericSemantics(
                    "metric",
                    declared.Unit,
                    MetricPrecision(scalar),
                    declared.Aggregation,
                    declared.Denominator,
                    unitProperty: null,
                    declared.Minimum,
                    declared.Maximum,
                    valueField,
                    "explicit_attribute");
            }

            if (ToolNumericSemanticsRegistry.TryGet(property, out var registered))
            {
                return DeclaredNumericSemantics(
                    registered.Role,
                    registered.Unit,
                    registered.Precision,
                    registered.Aggregation,
                    registered.Denominator,
                    registered.UnitField,
                    registered.Minimum,
                    registered.Maximum,
                    valueField,
                    "explicit_registry",
                    registered.DeprecatedAlias,
                    registered.Replacement);
            }

            return new JsonObject
            {
                ["role"] = "unknown",
                ["valueField"] = valueField,
                ["unit"] = "unknown",
                ["unitField"] = null,
                ["precision"] = "unknown",
                ["wireRepresentation"] = WireRepresentation(scalar),
                ["aggregation"] = "unknown",
                ["denominator"] = null,
                ["minimum"] = null,
                ["maximum"] = null,
                ["source"] = "unreviewed_unknown",
                ["deprecatedAlias"] = false,
                ["replacement"] = null,
            };
        }

        private static JsonObject DeclaredNumericSemantics(
            string role,
            string unit,
            string precision,
            string aggregation,
            string? denominator,
            string? unitProperty,
            double minimum,
            double maximum,
            string valueField,
            string source = "explicit_registry",
            bool deprecatedAlias = false,
            string? replacement = null) => new()
        {
            ["role"] = role,
            ["valueField"] = valueField,
            ["unit"] = unit,
            ["unitField"] = unitProperty is null
                ? null
                : JsonNamingPolicy.CamelCase.ConvertName(unitProperty),
            ["precision"] = precision,
            ["wireRepresentation"] = null,
            ["aggregation"] = aggregation,
            ["denominator"] = denominator,
            ["minimum"] = double.IsNaN(minimum) ? null : minimum,
            ["maximum"] = double.IsNaN(maximum) ? null : maximum,
            ["source"] = source,
            ["deprecatedAlias"] = deprecatedAlias,
            ["replacement"] = replacement,
        };

        private void DecorateNumericSchema(JsonObject schema, JsonObject semantics)
        {
            var extension = semantics["role"]?.GetValue<string>() == "metric"
                ? "x-metric"
                : "x-numeric";
            var deprecatedAlias = semantics["deprecatedAlias"]?.GetValue<bool>() == true;
            var replacement = semantics["replacement"]?.GetValue<string>();
            schema[extension] = InternNumericSemantics(semantics);
            if (deprecatedAlias)
            {
                schema["deprecated"] = true;
                schema["x-replacedBy"] = replacement;
            }
        }

        private string InternNumericSemantics(JsonObject semantics)
        {
            semantics.Remove("valueField");
            var canonical = semantics.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            var id = "sem_" + Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant()[..12];

            if (_numericSemantics[id] is JsonObject existing &&
                !string.Equals(existing.ToJsonString(), canonical, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Numeric semantic id collision for '{id}'.");
            }
            _numericSemantics[id] ??= JsonNode.Parse(canonical);
            return id;
        }

        private static JsonObject NonNullBranch(JsonObject schema)
        {
            if (schema["anyOf"] is not JsonArray alternatives)
                return schema;

            return alternatives
                .OfType<JsonObject>()
                .Single(alternative =>
                    !string.Equals(alternative["type"]?.GetValue<string>(), "null", StringComparison.Ordinal));
        }

        private static string MetricPrecision(Type scalar) => scalar switch
        {
            var type when type == typeof(float) => "rounded_binary32",
            var type when type == typeof(double) => "rounded_binary64",
            var type when type == typeof(decimal) => "exact_decimal",
            _ => "exact",
        };

        private static string WireRepresentation(Type scalar) => scalar switch
        {
            var type when type == typeof(float) => "ieee_754_binary32",
            var type when type == typeof(double) => "ieee_754_binary64",
            var type when type == typeof(decimal) => "base10_decimal128",
            var type when type == typeof(long) || type == typeof(nint) => "signed_64_decimal_string",
            var type when type == typeof(ulong) || type == typeof(nuint) => "unsigned_64_decimal_string",
            _ => "json_integer",
        };

        private static bool IsNumericScalar(Type type) =>
            type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong) || type == typeof(nint) ||
            type == typeof(nuint) || type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal);

        private static bool TryNumber(object value, out JsonNode? node)
        {
            try
            {
                node = JsonValue.Create(Convert.ToDecimal(value, CultureInfo.InvariantCulture));
                return true;
            }
            catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
            {
                node = null;
                return false;
            }
        }

        private static bool PermitsNull(Type declaredType, NullabilityInfo? nullability) =>
            Nullable.GetUnderlyingType(declaredType) is not null ||
            (!declaredType.IsValueType && nullability?.ReadState == NullabilityState.Nullable);

        private static JsonObject AsNullable(JsonObject nonNull) => new()
        {
            ["anyOf"] = new JsonArray(nonNull, new JsonObject { ["type"] = "null" }),
        };

        private static IEnumerable<PropertyInfo> SerializableProperties(Type type) =>
            type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.GetMethod?.IsPublic == true)
                .Where(property => property.GetCustomAttribute<JsonExtensionDataAttribute>() is null)
                .Where(property => property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition != JsonIgnoreCondition.Always)
                .OrderBy(property => property.MetadataToken);

        private static string WireName(PropertyInfo property) =>
            property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
            JsonNamingPolicy.CamelCase.ConvertName(property.Name);

        private static IReadOnlyList<string> EnumWireNames(Type enumType) =>
            enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(field =>
                    field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name ?? field.Name)
                .ToArray();

        internal static bool TryGetCollectionElement(Type type, out Type elementType)
        {
            if (type.IsArray)
            {
                elementType = type.GetElementType()!;
                return true;
            }

            var enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable is not null && type != typeof(string))
            {
                elementType = enumerable.GetGenericArguments()[0];
                return true;
            }

            elementType = null!;
            return false;
        }

        internal static bool TryGetDictionaryArguments(Type type, out Type keyType, out Type valueType)
        {
            var dictionary = type.IsGenericType && IsDictionaryDefinition(type.GetGenericTypeDefinition())
                ? type
                : type.GetInterfaces().FirstOrDefault(candidate =>
                    candidate.IsGenericType && IsDictionaryDefinition(candidate.GetGenericTypeDefinition()));
            if (dictionary is not null)
            {
                var arguments = dictionary.GetGenericArguments();
                keyType = arguments[0];
                valueType = arguments[1];
                return true;
            }

            keyType = null!;
            valueType = null!;
            return false;
        }

        private static bool IsDictionaryDefinition(Type definition) =>
            definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>);
    }
}

internal static class ToolOutputSchemaReferences
{
    private static readonly HashSet<string> AllowedReferenceSiblings = new(StringComparer.Ordinal)
    {
        "$ref",
        "description",
        "deprecated",
        "x-deprecationMessage",
    };

    internal static IReadOnlyList<ToolOutputSchemaViolation> Validate(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var violations = new List<ToolOutputSchemaViolation>();
        var definitions = root["$defs"] switch
        {
            null => new JsonObject(),
            JsonObject value => value,
            _ => InvalidDefinitions(root, violations),
        };
        var graph = definitions.Select(pair => pair.Key)
            .ToDictionary(
                name => name,
                _ => new HashSet<string>(StringComparer.Ordinal),
                StringComparer.Ordinal);
        var rootReferences = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in graph.Keys.Where(name => !IsSafeDefinitionName(name)))
        {
            violations.Add(new(
                "unsafe_definition_name",
                "$.$defs." + name,
                $"Local schema definition name '{name}' is outside the approved portable identifier grammar."));
        }

        Scan(root, "$", root, definitions, ownerDefinition: null, isRoot: true,
            graph, rootReferences, violations);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var active = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in rootReferences.Order(StringComparer.Ordinal))
            Visit(name, graph, visited, active, violations);
        var reachable = visited.ToHashSet(StringComparer.Ordinal);
        foreach (var name in graph.Keys.Order(StringComparer.Ordinal))
            Visit(name, graph, visited, active, violations);
        foreach (var name in graph.Keys.Where(name => !reachable.Contains(name)))
        {
            violations.Add(new(
                "unreachable_definition",
                "$.$defs." + name,
                $"Local schema definition '{name}' is not reachable from the root schema."));
        }
        return violations;
    }

    internal static bool TryResolve(
        JsonObject root,
        JsonObject schema,
        out JsonObject resolved,
        out string error)
    {
        resolved = schema;
        error = string.Empty;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (resolved["$ref"] is not null)
        {
            if (resolved["$ref"] is not JsonValue value ||
                !value.TryGetValue<string>(out var reference))
            {
                error = "Local output schema $ref must be a string.";
                return false;
            }
            if (!TryDefinitionName(reference, out var name))
            {
                error = $"Only one-segment local #/$defs references are supported: '{reference}'.";
                return false;
            }
            if (!visited.Add(name))
            {
                error = $"Local output schema reference cycle reaches '{reference}'.";
                return false;
            }
            if (root["$defs"] is not JsonObject definitions ||
                definitions[name] is not JsonObject target)
            {
                error = $"Local output schema reference '{reference}' is dangling.";
                return false;
            }
            resolved = target;
        }
        return true;
    }

    private static JsonObject InvalidDefinitions(
        JsonObject root,
        List<ToolOutputSchemaViolation> violations)
    {
        violations.Add(new(
            "invalid_definitions",
            "$.$defs",
            "Root $defs must be an object."));
        return new JsonObject();
    }

    private static void Scan(
        JsonNode? node,
        string path,
        JsonObject root,
        JsonObject definitions,
        string? ownerDefinition,
        bool isRoot,
        IReadOnlyDictionary<string, HashSet<string>> graph,
        HashSet<string> rootReferences,
        List<ToolOutputSchemaViolation> violations)
    {
        if (node is JsonObject value)
        {
            foreach (var forbidden in new[] { "$id", "$anchor", "$dynamicRef", "$dynamicAnchor" })
            {
                if (value.ContainsKey(forbidden))
                {
                    violations.Add(new(
                        "unsupported_reference_scope",
                        path + "." + forbidden,
                        "Output schemas permit only root-local static $defs references."));
                }
            }
            if (!isRoot && value.ContainsKey("$defs"))
            {
                violations.Add(new(
                    "nested_definitions",
                    path + ".$defs",
                    "Output schema definitions must be declared only at the root."));
            }

            if (value.ContainsKey("$ref"))
            {
                foreach (var sibling in value.Select(property => property.Key)
                             .Where(name => !AllowedReferenceSiblings.Contains(name)))
                {
                    violations.Add(new(
                        "unsupported_reference_sibling",
                        path + "." + sibling,
                        $"Keyword '{sibling}' is not an approved annotation sibling of $ref."));
                }
                ValidateReferenceAnnotation<string>(
                    value,
                    "description",
                    path,
                    violations);
                ValidateReferenceAnnotation<bool>(
                    value,
                    "deprecated",
                    path,
                    violations);
                ValidateReferenceAnnotation<string>(
                    value,
                    "x-deprecationMessage",
                    path,
                    violations);
                if (value["$ref"] is not JsonValue referenceNode ||
                    !referenceNode.TryGetValue<string>(out var reference) ||
                    !TryDefinitionName(reference, out var targetName))
                {
                    violations.Add(new(
                        "escaping_reference",
                        path + ".$ref",
                        "Only one-segment local #/$defs references are permitted."));
                }
                else if (definitions[targetName] is not JsonObject)
                {
                    violations.Add(new(
                        "dangling_reference",
                        path + ".$ref",
                        $"Local definition '{targetName}' does not exist or is not a schema object."));
                }
                else if (ownerDefinition is null)
                {
                    rootReferences.Add(targetName);
                }
                else if (graph.TryGetValue(ownerDefinition, out var targets))
                {
                    targets.Add(targetName);
                }
            }

            foreach (var property in value)
            {
                if (isRoot && property.Key == "$defs" && property.Value is JsonObject rootDefinitions)
                {
                    foreach (var definition in rootDefinitions)
                    {
                        if (definition.Value is not JsonObject)
                        {
                            violations.Add(new(
                                "invalid_definition",
                                "$.$defs." + definition.Key,
                                "Every local definition must be a schema object."));
                            continue;
                        }
                        Scan(definition.Value, "$.$defs." + definition.Key, root,
                            definitions, definition.Key, isRoot: false, graph,
                            rootReferences, violations);
                    }
                    continue;
                }
                if (property.Key == "$ref")
                    continue;
                Scan(property.Value, path + "." + property.Key, root, definitions,
                    ownerDefinition, isRoot: false, graph, rootReferences, violations);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
            {
                Scan(array[index], $"{path}[{index}]", root, definitions,
                    ownerDefinition, isRoot: false, graph, rootReferences, violations);
            }
        }
    }

    private static void ValidateReferenceAnnotation<T>(
        JsonObject schema,
        string name,
        string path,
        List<ToolOutputSchemaViolation> violations)
    {
        if (schema[name] is null)
            return;
        if (schema[name] is JsonValue value && value.TryGetValue<T>(out _))
            return;
        violations.Add(new(
            "invalid_reference_annotation",
            path + "." + name,
            $"Reference annotation '{name}' must be a {typeof(T).Name}."));
    }

    private static void Visit(
        string name,
        IReadOnlyDictionary<string, HashSet<string>> graph,
        HashSet<string> visited,
        HashSet<string> active,
        List<ToolOutputSchemaViolation> violations)
    {
        if (visited.Contains(name))
            return;
        if (!active.Add(name))
        {
            violations.Add(new(
                "cyclic_reference",
                "$.$defs." + name,
                $"Local schema definition cycle reaches '{name}'."));
            return;
        }
        if (graph.TryGetValue(name, out var targets))
        {
            foreach (var target in targets)
                Visit(target, graph, visited, active, violations);
        }
        active.Remove(name);
        visited.Add(name);
    }

    private static bool TryDefinitionName(string? reference, out string name)
    {
        name = string.Empty;
        const string prefix = "#/$defs/";
        if (string.IsNullOrEmpty(reference) ||
            !reference.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        name = reference[prefix.Length..];
        return IsSafeDefinitionName(name);
    }

    private static bool IsSafeDefinitionName(string name)
    {
        if (name.Length is 0 or > 128 ||
            !IsAsciiLetter(name[0]) && name[0] != '_')
        {
            return false;
        }
        return name.Skip(1).All(character =>
            IsAsciiLetter(character) ||
            character is >= '0' and <= '9' or '_' or '.' or '-');
    }

    private static bool IsAsciiLetter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

internal static class ToolOutputSchemaLinter
{
    internal static IReadOnlyList<ToolOutputSchemaViolation> LintPublicType(Type rootType)
    {
        ArgumentNullException.ThrowIfNull(rootType);
        var violations = new List<ToolOutputSchemaViolation>();
        VisitType(rootType, "$", violations, new HashSet<Type>(), new HashSet<Type>());
        return violations;
    }

    internal static IReadOnlyList<ToolOutputSchemaViolation> LintSchema(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var violations = new List<ToolOutputSchemaViolation>();
        if (schema is JsonObject root)
        {
            violations.AddRange(ToolOutputSchemaReferences.Validate(root));
            if (!string.Equals(
                    StringValue(root, "$schema"),
                    "https://json-schema.org/draft/2020-12/schema",
                    StringComparison.Ordinal))
            {
                violations.Add(new(
                    "invalid_schema_dialect",
                    "$.$schema",
                    "Output schemas require the JSON Schema draft 2020-12 dialect."));
            }
        }
        var registry = (schema as JsonObject)?["x-wpa-numeric-semantics"] as JsonObject;
        if (registry is null)
        {
            violations.Add(new(
                "missing_numeric_semantics_registry",
                "$",
                "The root schema requires x-wpa-numeric-semantics."));
            registry = new JsonObject();
        }
        var referencedSemantics = new HashSet<string>(StringComparer.Ordinal);
        CollectNumericSemanticReferences(schema, referencedSemantics);
        foreach (var semantic in registry)
        {
            if (!referencedSemantics.Contains(semantic.Key))
            {
                violations.Add(new(
                    "unreachable_numeric_semantics",
                    "$.x-wpa-numeric-semantics." + semantic.Key,
                    $"Numeric semantic id '{semantic.Key}' is not referenced by the output schema."));
            }
        }
        VisitSchema(schema, "$", violations, registry);
        if (schema is JsonObject schemaRoot && schemaRoot["$defs"] is JsonObject definitions)
        {
            foreach (var definition in definitions)
            {
                if (definition.Value is JsonObject definitionSchema)
                {
                    VisitSchema(
                        definitionSchema,
                        "$.$defs." + definition.Key,
                        violations,
                        registry);
                }
            }
        }
        return violations;
    }

    private static void CollectNumericSemanticReferences(
        JsonNode? node,
        HashSet<string> referenced)
    {
        if (node is JsonObject value)
        {
            foreach (var extension in new[] { "x-metric", "x-numeric" })
            {
                if (StringValue(value, extension) is { } semanticId)
                    referenced.Add(semanticId);
            }
            foreach (var property in value)
            {
                if (property.Key == "x-wpa-numeric-semantics")
                    continue;
                CollectNumericSemanticReferences(property.Value, referenced);
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                CollectNumericSemanticReferences(item, referenced);
        }
    }

    internal static IReadOnlyList<ToolOutputSchemaViolation> LintReviewedNumericClosure(JsonNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var violations = LintSchema(schema).ToList();
        if (schema is not JsonObject root ||
            root["x-wpa-numeric-semantics"] is not JsonObject registry)
        {
            return violations;
        }

        VisitReviewedNumericClosure(root, "$", registry, violations);
        return violations;
    }

    private static void VisitReviewedNumericClosure(
        JsonNode? node,
        string path,
        JsonObject registry,
        List<ToolOutputSchemaViolation> violations)
    {
        if (node is JsonObject value)
        {
            var semanticId = StringValue(value, "x-metric") ?? StringValue(value, "x-numeric");
            if (semanticId is not null &&
                registry[semanticId] is JsonObject semantics &&
                StringValue(semantics, "source") == "unreviewed_unknown")
            {
                violations.Add(new(
                    "unreviewed_numeric_semantics",
                    path,
                    "Active public output numeric values require an explicit property review."));
            }

            foreach (var property in value)
            {
                if (property.Key == "x-wpa-numeric-semantics")
                    continue;
                VisitReviewedNumericClosure(property.Value, path + "." + property.Key, registry, violations);
            }
        }
        else if (node is JsonArray array)
        {
            for (var index = 0; index < array.Count; index++)
                VisitReviewedNumericClosure(array[index], $"{path}[{index}]", registry, violations);
        }
    }

    private static void VisitType(
        Type declaredType,
        string path,
        List<ToolOutputSchemaViolation> violations,
        HashSet<Type> ancestors,
        HashSet<Type> completed)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (IsScalar(type) || type.IsEnum)
            return;
        if (IsArbitraryObject(type))
        {
            violations.Add(new("arbitrary_object", path, $"'{type.FullName}' is not a closed DTO type."));
            return;
        }
        if (ToolOutputSchemaFactory.SchemaBuilder.TryGetCollectionElement(type, out var elementType))
        {
            VisitType(elementType, path + "[]", violations, ancestors, completed);
            return;
        }
        if (completed.Contains(type))
            return;
        if (!ancestors.Add(type))
        {
            violations.Add(new("recursive_object", path, "Recursive DTO graphs require an approved schema representation."));
            return;
        }
        if (!type.IsValueType && !type.IsSealed)
            violations.Add(new("open_polymorphic_type", path, $"'{type.FullName}' must be sealed."));

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(property => property.GetMethod?.IsPublic == true))
        {
            if (property.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always)
                continue;
            var propertyPath = path + "." +
                (property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ??
                 JsonNamingPolicy.CamelCase.ConvertName(property.Name));
            if (property.GetCustomAttribute<JsonExtensionDataAttribute>() is not null)
                violations.Add(new("extension_data", propertyPath, "JsonExtensionData is forbidden in public output DTOs."));

            if (property.GetCustomAttribute<ToolDictionaryRowsAttribute>() is { } rows)
            {
                ValidateDictionaryRows(property, rows, propertyPath, violations, ancestors, completed);
                continue;
            }
            if (property.GetCustomAttribute<ToolSafeIntegerCompatibilityAttribute>() is { } compatibility)
                ValidateSafeIntegerCompatibility(property, compatibility, propertyPath, violations);

            VisitType(property.PropertyType, propertyPath, violations, ancestors, completed);
        }

        ancestors.Remove(type);
        completed.Add(type);
    }

    private static void VisitSchema(
        JsonNode node,
        string path,
        List<ToolOutputSchemaViolation> violations,
        JsonObject numericSemantics)
    {
        if (node is not JsonObject schema)
        {
            violations.Add(new("invalid_schema_node", path, "A schema node must be an object."));
            return;
        }

        if (schema.ContainsKey("$ref"))
            return;

        if (schema["anyOf"] is JsonArray alternatives)
        {
            var nullAlternatives = alternatives.Count(IsNullSchema);
            if (alternatives.Count != 2 || nullAlternatives != 1)
                violations.Add(new("invalid_nullable_shape", path, "Nullable properties require one value schema and one null schema."));
            for (var index = 0; index < alternatives.Count; index++)
                VisitSchema(alternatives[index]!, $"{path}.anyOf[{index}]", violations, numericSemantics);
            return;
        }

        var type = schema["type"]?.GetValue<string>();
        if (type is null)
        {
            violations.Add(new("arbitrary_schema", path, "A schema without type/anyOf is forbidden."));
            return;
        }

        if (type is "integer" or "number" || schema["x-exactInteger"] is not null)
            ValidateNumericSemantics(schema, path, violations, numericSemantics);

        if (schema["enum"] is JsonArray enumValues && enumValues.Any(value =>
                value is null || !value.AsValue().TryGetValue<string>(out _)))
        {
            violations.Add(new("non_string_enum", path, "Public enum values must be stable strings."));
        }

        if (type == "object")
        {
            if (schema["additionalProperties"]?.GetValue<bool>() != false)
                violations.Add(new("additional_properties", path, "Object schemas require additionalProperties=false."));
            if (schema["properties"] is not JsonObject properties)
            {
                violations.Add(new("missing_properties", path, "Object schemas require properties."));
                return;
            }
            if (schema["required"] is not JsonArray required)
            {
                violations.Add(new("missing_required", path, "Every object schema requires a required array."));
                return;
            }

            var propertyNames = properties.Select(property => property.Key).ToHashSet(StringComparer.Ordinal);
            var requiredNames = required.Select(item => item?.GetValue<string>() ?? string.Empty).ToHashSet(StringComparer.Ordinal);
            if (!propertyNames.SetEquals(requiredNames))
                violations.Add(new("require_all_properties", path, "required must contain every and only declared property."));

            foreach (var property in properties)
            {
                var propertySchema = NonNullSchema(property.Value);
                var replacement = propertySchema is null
                    ? null
                    : StringValue(propertySchema, "x-replacedBy");
                if (replacement is not null && !properties.ContainsKey(replacement))
                {
                    violations.Add(new(
                        "missing_deprecated_alias_replacement",
                        path + ".properties." + property.Key,
                        $"Deprecated alias replacement '{replacement}' is not a sibling property."));
                }
                VisitSchema(property.Value!, path + ".properties." + property.Key, violations, numericSemantics);
            }
        }
        else if (type == "array")
        {
            if (schema["items"] is not JsonObject items)
                violations.Add(new("missing_items", path, "Array schemas require typed items."));
            else
                VisitSchema(items, path + ".items", violations, numericSemantics);
        }
    }

    private static void ValidateNumericSemantics(
        JsonObject schema,
        string path,
        List<ToolOutputSchemaViolation> violations,
        JsonObject numericSemantics)
    {
        var metricId = StringValue(schema, "x-metric");
        var numericId = StringValue(schema, "x-numeric");
        if (metricId is null && numericId is null)
        {
            violations.Add(new(
                "missing_numeric_semantics",
                path,
                "Every public numeric value requires explicit or honestly unknown semantics."));
            return;
        }
        if (metricId is not null && numericId is not null)
        {
            violations.Add(new(
                "ambiguous_numeric_semantics",
                path,
                "A numeric value cannot be both x-metric and x-numeric."));
            return;
        }

        var semanticId = metricId ?? numericId!;
        if (numericSemantics[semanticId] is not JsonObject semantics)
        {
            violations.Add(new(
                "dangling_numeric_semantics",
                path,
                $"Numeric semantic id '{semanticId}' is not present in the root registry."));
            return;
        }

        var required = new[]
        {
            "role", "unit", "unitField", "precision", "wireRepresentation",
            "aggregation", "denominator", "minimum", "maximum", "source",
            "deprecatedAlias", "replacement",
        };
        foreach (var name in required)
        {
            if (!semantics.ContainsKey(name))
            {
                violations.Add(new(
                    "incomplete_numeric_semantics",
                    path + "." + (metricId is null ? "x-numeric" : "x-metric"),
                    $"Numeric semantics require '{name}'."));
            }
        }

        var role = StringValue(semantics, "role");
        var unit = StringValue(semantics, "unit");
        var aggregation = StringValue(semantics, "aggregation");
        var source = StringValue(semantics, "source");
        var denominator = StringValue(semantics, "denominator");
        var unitField = StringValue(semantics, "unitField");
        var deprecatedAlias = semantics["deprecatedAlias"]?.GetValue<bool>() == true;
        var replacement = StringValue(semantics, "replacement");

        if ((role == "metric") != (metricId is not null))
        {
            violations.Add(new(
                "numeric_role_extension_mismatch",
                path,
                "Only role=metric may use x-metric; non-metric numeric values use x-numeric."));
        }
        if (string.Equals(source, "reviewed_naming_convention", StringComparison.Ordinal) ||
            string.Equals(source, "naming_inference", StringComparison.Ordinal))
        {
            violations.Add(new(
                "numeric_naming_inference_claimed_reviewed",
                path,
                "A field-name heuristic cannot be presented as reviewed numeric semantics."));
        }

        var requiresDenominator = role == "metric" &&
            (unit is "ratio" or "percent" || aggregation == "ratio");
        if (requiresDenominator && string.IsNullOrWhiteSpace(denominator))
        {
            violations.Add(new(
                "ratio_denominator_required",
                path,
                "Every ratio or percentage requires an explicit, population-specific denominator."));
        }
        if (denominator == "documented_population_total")
        {
            violations.Add(new(
                "ambiguous_ratio_denominator",
                path,
                "A ratio denominator must identify its exact population, not a generic documented total."));
        }
        if ((unit == "dynamic") != (unitField is not null))
        {
            violations.Add(new(
                "invalid_dynamic_metric_unit",
                path,
                "A dynamic unit requires exactly one machine-readable unitField."));
        }
        if (unit == "percent" &&
            (NumberValue(semantics, "minimum") != 0 || NumberValue(semantics, "maximum") != 100))
        {
            violations.Add(new(
                "invalid_percent_range",
                path,
                "Percentage metrics require the explicit [0,100] range."));
        }

        if (source == "unreviewed_unknown" &&
            (role != "unknown" || unit != "unknown" || aggregation != "unknown" ||
             denominator is not null || deprecatedAlias || replacement is not null))
        {
            violations.Add(new(
                "dishonest_unknown_numeric_semantics",
                path,
                "Unreviewed numeric semantics must remain unknown and cannot invent a denominator."));
        }
        if (deprecatedAlias != (replacement is not null))
        {
            violations.Add(new(
                "invalid_numeric_alias_replacement",
                path,
                "A deprecated numeric alias requires exactly one explicit replacement."));
        }
        var schemaDeprecated = schema["deprecated"]?.GetValue<bool>() == true;
        var schemaReplacement = StringValue(schema, "x-replacedBy");
        if ((deprecatedAlias && !schemaDeprecated) ||
            !string.Equals(replacement, schemaReplacement, StringComparison.Ordinal))
        {
            violations.Add(new(
                "numeric_alias_schema_marker_mismatch",
                path,
                "Numeric alias semantics must be projected as deprecated=true with the same x-replacedBy value."));
        }
        if (deprecatedAlias && string.Equals(replacement, path.Split('.').Last(), StringComparison.Ordinal))
        {
            violations.Add(new(
                "self_replacing_numeric_alias",
                path,
                "A deprecated numeric alias cannot replace itself."));
        }
    }

    private static JsonObject? NonNullSchema(JsonNode? node)
    {
        if (node is not JsonObject schema)
            return null;
        if (schema["anyOf"] is not JsonArray alternatives)
            return schema;
        return alternatives
            .OfType<JsonObject>()
            .SingleOrDefault(alternative => !IsNullSchema(alternative));
    }

    private static string? StringValue(JsonObject value, string name) =>
        value[name] is JsonValue node && node.TryGetValue<string>(out var text)
            ? text
            : null;

    private static double? NumberValue(JsonObject value, string name) =>
        value[name] is JsonValue node && node.TryGetValue<double>(out var number)
            ? number
            : null;

    private static bool IsNullSchema(JsonNode? node) =>
        node is JsonObject schema && string.Equals(schema["type"]?.GetValue<string>(), "null", StringComparison.Ordinal);

    private static bool IsScalar(Type type) =>
        type == typeof(string) || type == typeof(char) || type == typeof(bool) ||
        type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
        type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
        type == typeof(long) || type == typeof(ulong) || type == typeof(nint) || type == typeof(nuint) ||
        type == typeof(float) || type == typeof(double) ||
        type == typeof(decimal) || type == typeof(Guid) || type == typeof(DateTime) ||
        type == typeof(DateTimeOffset) || type == typeof(TimeSpan);

    private static bool IsArbitraryObject(Type type) =>
        type == typeof(object) || type == typeof(JsonNode) || type == typeof(JsonObject) ||
        type == typeof(JsonArray) || type == typeof(JsonValue) || type == typeof(JsonElement) ||
        type == typeof(JsonDocument) || typeof(System.Collections.IDictionary).IsAssignableFrom(type) ||
        IsDictionaryInterface(type) ||
        type.GetInterfaces().Any(candidate =>
            IsDictionaryInterface(candidate));

    private static bool IsDictionaryInterface(Type type) =>
        type.IsGenericType &&
        (type.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
         type.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));

    private static void ValidateDictionaryRows(
        PropertyInfo property,
        ToolDictionaryRowsAttribute rows,
        string path,
        List<ToolOutputSchemaViolation> violations,
        HashSet<Type> ancestors,
        HashSet<Type> completed)
    {
        if (string.Equals(rows.KeyPropertyName, rows.ValuePropertyName, StringComparison.Ordinal))
            violations.Add(new("dictionary_row_name_collision", path, "Dictionary row key/value names must differ."));
        if (!ToolOutputSchemaFactory.SchemaBuilder.TryGetDictionaryArguments(
                property.PropertyType,
                out var keyType,
                out var valueType))
        {
            violations.Add(new("invalid_dictionary_rows", path, "ToolDictionaryRows requires a dictionary property."));
            return;
        }
        if (keyType != typeof(string) && keyType != typeof(int))
        {
            violations.Add(new(
                "unsupported_dictionary_key",
                path,
                "Reviewed dictionary-row keys are limited to string and int."));
            return;
        }

        VisitType(keyType, path + "[]." + rows.KeyPropertyName, violations, ancestors, completed);
        VisitType(valueType, path + "[]." + rows.ValuePropertyName, violations, ancestors, completed);
    }

    private static void ValidateSafeIntegerCompatibility(
        PropertyInfo property,
        ToolSafeIntegerCompatibilityAttribute compatibility,
        string path,
        List<ToolOutputSchemaViolation> violations)
    {
        if (property.PropertyType != typeof(ulong?))
            violations.Add(new("unsafe_integer_compatibility", path, "The compatibility projection must be nullable ulong."));

        var range = property.GetCustomAttribute<RangeAttribute>();
        if (range is null || Convert.ToDouble(range.Minimum, CultureInfo.InvariantCulture) != 0d ||
            Convert.ToDouble(range.Maximum, CultureInfo.InvariantCulture) !=
            (double)PublicIdentifierFormatter.JavaScriptMaxSafeInteger)
        {
            violations.Add(new(
                "unsafe_integer_compatibility",
                path,
                "The compatibility projection requires an explicit [0, Number.MAX_SAFE_INTEGER] range."));
        }

        var declaringType = property.DeclaringType!;
        var authoritative = declaringType.GetProperty(compatibility.AuthoritativeStringProperty);
        var status = declaringType.GetProperty(compatibility.StatusProperty);
        if (authoritative?.PropertyType != typeof(string) || status?.PropertyType != typeof(string))
        {
            violations.Add(new(
                "unsafe_integer_compatibility",
                path,
                "The compatibility projection requires authoritative string and precision/deprecation status siblings."));
        }
    }
}
