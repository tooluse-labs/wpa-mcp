using System.Collections;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests.ContractBaselines;

internal static class LegacyDtoInventorySnapshotBuilder
{
    private const string OutputNamespace = "WpaMcp.Output";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static LegacyDtoInventorySnapshot Build()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var activeToolOutputs = catalog.Tools
            .Select(tool =>
            {
                var dataType = ActiveToolCatalog.EffectiveOutputType(tool.Method);
                var dataTypeName = TypeIdentity(dataType);
                return new ActiveToolDtoBinding(
                    tool.ToolName,
                    dataTypeName,
                    $"WpaMcp.Output.ToolEnvelope<{dataTypeName}>");
            })
            .OrderBy(binding => binding.ToolName, StringComparer.Ordinal)
            .ToList();
        var nullability = new NullabilityInfoContext();
        var types = typeof(Program).Assembly.GetTypes()
            .Where(IsPublicOutputContract)
            .OrderBy(type => type.FullName, StringComparer.Ordinal)
            .Select(type => BuildType(type, nullability))
            .ToList();
        var properties = types
            .SelectMany(type => type.Properties.Select(property => (Type: type, Property: property)))
            .ToList();
        var candidates = new LegacyDtoCandidateInventory(
            PublicUlongProperties: SelectProperties(properties, property => property.IsPublicUlong),
            IdLikeIntegerProperties: SelectProperties(
                properties,
                property => property.IsIdLikeInteger),
            CollectionProperties: SelectProperties(
                properties,
                property => property.Classifications.Contains("collection", StringComparer.Ordinal)),
            TopNCandidates: SelectProperties(
                properties,
                property => property.Classifications.Contains("top_n_candidate", StringComparer.Ordinal)),
            TimelineCandidates: SelectProperties(
                properties,
                property => property.Classifications.Contains("timeline_candidate", StringComparer.Ordinal)),
            ResponseTypes: types
                .Where(type => type.Name.EndsWith("Response", StringComparison.Ordinal))
                .Select(type => type.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());

        var typeNames = types.Select(type => type.Name).ToList();
        var propertyNames = properties
            .Select(item => $"{item.Type.Name}.{item.Property.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var contractPayload = JsonSerializer.SerializeToUtf8Bytes(
            new LegacyDtoContractHashInput(types, candidates, activeToolOutputs),
            HashJsonOptions);

        return new LegacyDtoInventorySnapshot(
            FormatVersion: "active-dto-inventory.v1",
            BaselineKind: "reviewed_current_active_dto_contract",
            Namespace: OutputNamespace,
            ActiveToolCount: activeToolOutputs.Count,
            ActiveDataTypeCount: activeToolOutputs
                .Select(binding => binding.DataType)
                .Distinct(StringComparer.Ordinal)
                .Count(),
            TypeCount: types.Count,
            ResponseTypeCount: candidates.ResponseTypes.Count,
            PropertyCount: properties.Count,
            TypeSetSha256: Sha256(Encoding.UTF8.GetBytes(string.Join("\n", typeNames))),
            PropertySetSha256: Sha256(Encoding.UTF8.GetBytes(string.Join("\n", propertyNames))),
            ContractSha256: Sha256(contractPayload),
            Types: types,
            Candidates: candidates,
            ActiveToolOutputs: activeToolOutputs);
    }

    public static string BuildCanonicalJson()
    {
        var snapshot = Build();
        var compact = new
        {
            snapshot.FormatVersion,
            snapshot.BaselineKind,
            snapshot.Namespace,
            snapshot.ActiveToolCount,
            snapshot.ActiveDataTypeCount,
            snapshot.TypeCount,
            snapshot.ResponseTypeCount,
            snapshot.PropertyCount,
            snapshot.TypeSetSha256,
            snapshot.PropertySetSha256,
            snapshot.ContractSha256,
            InventorySemantics = new
            {
                Scope = "all public non-static classes with public instance properties in WpaMcp.Output",
                JsonNames = "JsonPropertyNameAttribute, otherwise the active MCP serializer naming policy",
                Defaults = "constructor parameter defaults when reflectable; not constructor runtime behavior",
                Classifications = "deterministic name/type heuristics for review candidates, not semantic or runtime conclusions",
            },
            TypeColumns = new[] { "name", "kind", "properties" },
            PropertyColumns = new[]
            {
                "name",
                "jsonName",
                "clrType",
                "nullabilityCode",
                "jsonIgnoreCode",
                "defaultValueStateCode",
                "defaultValueJson",
                "description",
                "classificationCodes",
            },
            Codes = new
            {
                Nullability = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["N"] = "not_null",
                    ["Q"] = "nullable",
                    ["?"] = "unknown",
                },
                JsonIgnore = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["-"] = "not_declared",
                    ["A"] = "Always",
                    ["N"] = "Never",
                    ["D"] = "WhenWritingDefault",
                    ["Q"] = "WhenWritingNull",
                },
                DefaultValueState = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["A"] = "available",
                    ["R"] = "not_defined",
                    ["U"] = "unavailable_not_constructor_bound",
                },
                Classification = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["c"] = "collection",
                    ["i"] = "identifier",
                    ["m"] = "metric",
                    ["n"] = "top_n_candidate",
                    ["t"] = "timeline_candidate",
                },
            },
            Types = snapshot.Types.Select(type => new object?[]
            {
                type.Name,
                type.Kind,
                type.Properties.Select(property => new object?[]
                {
                    property.Name,
                    property.JsonName,
                    property.ClrType,
                    NullabilityCode(property.Nullability),
                    JsonIgnoreCode(property.JsonIgnoreCondition),
                    DefaultValueStateCode(property.DefaultValueState),
                    property.DefaultValueJson,
                    property.Description,
                    ClassificationCodes(property.Classifications),
                }).ToList(),
            }).ToList(),
            snapshot.Candidates,
            snapshot.ActiveToolOutputs,
        };
        var json = JsonSerializer.Serialize(compact, SnapshotJsonOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static string NullabilityCode(string value) => value switch
    {
        "not_null" => "N",
        "nullable" => "Q",
        _ => "?",
    };

    private static string JsonIgnoreCode(string value) => value switch
    {
        "not_declared" => "-",
        "Always" => "A",
        "Never" => "N",
        "WhenWritingDefault" => "D",
        "WhenWritingNull" => "Q",
        _ => throw new InvalidOperationException($"Unknown JsonIgnoreCondition '{value}'."),
    };

    private static string DefaultValueStateCode(string value) => value switch
    {
        "available" => "A",
        "not_defined" => "R",
        "unavailable_not_constructor_bound" => "U",
        _ => throw new InvalidOperationException($"Unknown default-value state '{value}'."),
    };

    private static string ClassificationCodes(IReadOnlyList<string> values)
    {
        var codes = new StringBuilder(values.Count);
        foreach (var value in values)
        {
            codes.Append(value switch
            {
                "collection" => 'c',
                "identifier" => 'i',
                "metric" => 'm',
                "top_n_candidate" => 'n',
                "timeline_candidate" => 't',
                _ => throw new InvalidOperationException($"Unknown classification '{value}'."),
            });
        }

        return codes.ToString();
    }

    private static LegacyDtoTypeSnapshot BuildType(Type type, NullabilityInfoContext nullability)
    {
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property => BuildProperty(type, property, nullability))
            .ToList();

        return new LegacyDtoTypeSnapshot(
            Name: TypeIdentity(type),
            Kind: IsRecord(type) ? "record" : "class",
            Properties: properties);
    }

    private static LegacyDtoPropertySnapshot BuildProperty(
        Type declaringType,
        PropertyInfo property,
        NullabilityInfoContext nullability)
    {
        var constructorParameter = FindConstructorParameter(declaringType, property);
        var jsonIgnore = property.GetCustomAttribute<JsonIgnoreAttribute>();
        var classifications = Classify(property);
        var underlyingType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

        return new LegacyDtoPropertySnapshot(
            Name: property.Name,
            JsonName: property.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
                      ?? McpJsonUtilities.DefaultOptions.PropertyNamingPolicy?.ConvertName(property.Name)
                      ?? property.Name,
            ClrType: TypeIdentity(property.PropertyType),
            Nullability: NullabilityName(nullability.Create(property).ReadState),
            JsonIgnoreCondition: jsonIgnore?.Condition.ToString() ?? "not_declared",
            DefaultValueState: constructorParameter is null
                ? "unavailable_not_constructor_bound"
                : constructorParameter.HasDefaultValue
                    ? "available"
                    : "not_defined",
            DefaultValueJson: SerializeDefaultValue(constructorParameter),
            Description: property.GetCustomAttribute<DescriptionAttribute>()?.Description
                         ?? constructorParameter?.GetCustomAttribute<DescriptionAttribute>()?.Description,
            Classifications: classifications,
            IsPublicUlong: underlyingType == typeof(ulong),
            IsIdLikeInteger: IsIntegral(underlyingType) && IsIdentifier(property));
    }

    private static ParameterInfo? FindConstructorParameter(Type type, PropertyInfo property) =>
        type.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .SelectMany(constructor => constructor.GetParameters())
            .FirstOrDefault(parameter =>
                string.Equals(parameter.Name, property.Name, StringComparison.OrdinalIgnoreCase)
                && parameter.ParameterType == property.PropertyType);

    private static IReadOnlyList<string> Classify(PropertyInfo property)
    {
        var classifications = new List<string>();
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        var isCollection = IsCollection(property.PropertyType);
        var isIdentifier = IsIdentifier(property);

        if (isCollection)
            classifications.Add("collection");
        if (isIdentifier)
            classifications.Add("identifier");
        if (IsNumeric(type) && !isIdentifier && IsMetricName(property.Name))
            classifications.Add("metric");
        if (isCollection && IsTopNCandidateName(property.Name))
            classifications.Add("top_n_candidate");
        if (IsTimelineCandidateName(property.Name))
            classifications.Add("timeline_candidate");

        return classifications;
    }

    private static bool IsPublicOutputContract(Type type) =>
        type.IsPublic
        && type.IsClass
        && !(type.IsAbstract && type.IsSealed)
        && string.Equals(type.Namespace, OutputNamespace, StringComparison.Ordinal)
        && type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Any(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0);

    private static bool IsRecord(Type type) =>
        type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic)?.DeclaringType == type;

    private static bool IsCollection(Type type) =>
        type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsIntegral(Type type) =>
        type == typeof(byte)
        || type == typeof(sbyte)
        || type == typeof(short)
        || type == typeof(ushort)
        || type == typeof(int)
        || type == typeof(uint)
        || type == typeof(long)
        || type == typeof(ulong);

    private static bool IsNumeric(Type type) =>
        IsIntegral(type)
        || type == typeof(float)
        || type == typeof(double)
        || type == typeof(decimal);

    private static bool IsIdentifier(PropertyInfo property) =>
        IsIdentifierName(property.Name)
        || property is { Name: "StartUs", DeclaringType.Name: "ProcessRow" }
        || property is { Name: "StartTimeUs", DeclaringType.Name: "ChildSpawnTiming" or "ThreadLifetimeRow" };

    private static bool IsIdentifierName(string name) =>
        name is "Pid" or "Tid" or "ConnId" or "ConnIdText" or "FileKey" or "FileObject"
            or "ProcessStartUs" or "ThreadStartUs" or "ThreadGeneration" or "ParentStartUs"
            or "Core" or "PrimaryCore"
        || name.EndsWith("Pid", StringComparison.Ordinal)
        || name.EndsWith("Tid", StringComparison.Ordinal)
        || name.EndsWith("Id", StringComparison.Ordinal)
        || name.EndsWith("Ids", StringComparison.Ordinal)
        || name.EndsWith("Key", StringComparison.Ordinal)
        || name.EndsWith("Keys", StringComparison.Ordinal)
        || name.EndsWith("Port", StringComparison.Ordinal)
        || name.EndsWith("ProcessStartUs", StringComparison.Ordinal)
        || name.EndsWith("ThreadStartUs", StringComparison.Ordinal)
        || name.Contains("PdbAge", StringComparison.Ordinal);

    private static bool IsMetricName(string name) =>
        name.Contains("Count", StringComparison.Ordinal)
        || name.Contains("Bytes", StringComparison.Ordinal)
        || name.EndsWith("Us", StringComparison.Ordinal)
        || name.EndsWith("Ms", StringComparison.Ordinal)
        || name.Contains("Duration", StringComparison.Ordinal)
        || name.Contains("Rate", StringComparison.Ordinal)
        || name.Contains("Pct", StringComparison.Ordinal)
        || name.Contains("Percent", StringComparison.Ordinal)
        || name.Contains("Size", StringComparison.Ordinal)
        || name.Contains("Length", StringComparison.Ordinal)
        || name.Contains("Weight", StringComparison.Ordinal)
        || name.Contains("Score", StringComparison.Ordinal)
        || name.Contains("Cpu", StringComparison.Ordinal)
        || name.Contains("Memory", StringComparison.Ordinal)
        || name.Contains("WorkingSet", StringComparison.Ordinal)
        || name.Contains("EventsLost", StringComparison.Ordinal)
        || name.StartsWith("Total", StringComparison.Ordinal);

    private static bool IsTopNCandidateName(string name) =>
        name.Contains("Top", StringComparison.Ordinal)
        || name.Contains("Rows", StringComparison.Ordinal)
        || name.Contains("Candidates", StringComparison.Ordinal)
        || name.Contains("Sample", StringComparison.Ordinal)
        || name.Contains("Unresolved", StringComparison.Ordinal)
        || name.Contains("Recommendations", StringComparison.Ordinal)
        || name.EndsWith("Functions", StringComparison.Ordinal)
        || name.EndsWith("Stacks", StringComparison.Ordinal)
        || name.EndsWith("Modules", StringComparison.Ordinal)
        || name.EndsWith("Providers", StringComparison.Ordinal)
        || name.EndsWith("Processes", StringComparison.Ordinal)
        || name.EndsWith("Buckets", StringComparison.Ordinal)
        || name.EndsWith("Requests", StringComparison.Ordinal)
        || name.EndsWith("Targets", StringComparison.Ordinal)
        || name.EndsWith("Types", StringComparison.Ordinal)
        || name.EndsWith("Tags", StringComparison.Ordinal)
        || name.EndsWith("Events", StringComparison.Ordinal)
        || name.EndsWith("Calls", StringComparison.Ordinal)
        || name.EndsWith("Connections", StringComparison.Ordinal);

    private static bool IsTimelineCandidateName(string name) =>
        name.EndsWith("Us", StringComparison.Ordinal)
        || name.EndsWith("Ms", StringComparison.Ordinal)
        || name.Contains("Time", StringComparison.Ordinal)
        || name.Contains("Duration", StringComparison.Ordinal)
        || name.Contains("Window", StringComparison.Ordinal)
        || name.Contains("Interval", StringComparison.Ordinal)
        || name.Contains("Timestamp", StringComparison.Ordinal)
        || name.Contains("Timeline", StringComparison.Ordinal)
        || name.Contains("Histogram", StringComparison.Ordinal)
        || name.Contains("Gap", StringComparison.Ordinal);

    private static string NullabilityName(NullabilityState state) => state switch
    {
        NullabilityState.NotNull => "not_null",
        NullabilityState.Nullable => "nullable",
        _ => "unknown",
    };

    private static string? SerializeDefaultValue(ParameterInfo? parameter)
    {
        if (parameter is null || !parameter.HasDefaultValue)
            return null;

        var value = parameter.DefaultValue;
        if (value is null)
            return "null";
        if (ReferenceEquals(value, Missing.Value))
            return "<missing>";
        if (ReferenceEquals(value, DBNull.Value))
            return "<dbnull>";

        return Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), HashJsonOptions));
    }

    private static IReadOnlyList<string> SelectProperties(
        IEnumerable<(LegacyDtoTypeSnapshot Type, LegacyDtoPropertySnapshot Property)> properties,
        Func<LegacyDtoPropertySnapshot, bool> predicate) =>
        properties
            .Where(item => predicate(item.Property))
            .Select(item => $"{item.Type.Name}.{item.Property.Name}")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    private static string TypeIdentity(Type type)
    {
        if (type.IsArray)
            return TypeIdentity(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var genericName = type.GetGenericTypeDefinition().FullName
            ?? type.GetGenericTypeDefinition().Name;
        var arityMarker = genericName.IndexOf('`');
        if (arityMarker >= 0)
            genericName = genericName[..arityMarker];
        return genericName + "<"
               + string.Join(",", type.GetGenericArguments().Select(TypeIdentity))
               + ">";
    }

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
}

internal sealed record LegacyDtoInventorySnapshot(
    string FormatVersion,
    string BaselineKind,
    string Namespace,
    int ActiveToolCount,
    int ActiveDataTypeCount,
    int TypeCount,
    int ResponseTypeCount,
    int PropertyCount,
    string TypeSetSha256,
    string PropertySetSha256,
    string ContractSha256,
    IReadOnlyList<LegacyDtoTypeSnapshot> Types,
    LegacyDtoCandidateInventory Candidates,
    IReadOnlyList<ActiveToolDtoBinding> ActiveToolOutputs);

internal sealed record ActiveToolDtoBinding(
    string ToolName,
    string DataType,
    string EnvelopeType);

internal sealed record LegacyDtoTypeSnapshot(
    string Name,
    string Kind,
    IReadOnlyList<LegacyDtoPropertySnapshot> Properties);

internal sealed record LegacyDtoPropertySnapshot(
    string Name,
    string JsonName,
    string ClrType,
    string Nullability,
    string JsonIgnoreCondition,
    string DefaultValueState,
    string? DefaultValueJson,
    string? Description,
    IReadOnlyList<string> Classifications,
    bool IsPublicUlong,
    bool IsIdLikeInteger);

internal sealed record LegacyDtoCandidateInventory(
    IReadOnlyList<string> PublicUlongProperties,
    IReadOnlyList<string> IdLikeIntegerProperties,
    IReadOnlyList<string> CollectionProperties,
    IReadOnlyList<string> TopNCandidates,
    IReadOnlyList<string> TimelineCandidates,
    IReadOnlyList<string> ResponseTypes);

internal sealed record LegacyDtoContractHashInput(
    IReadOnlyList<LegacyDtoTypeSnapshot> Types,
    LegacyDtoCandidateInventory Candidates,
    IReadOnlyList<ActiveToolDtoBinding> ActiveToolOutputs);
