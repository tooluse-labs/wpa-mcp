using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Tests.ContractBaselines;

internal static class LegacyActiveToolSnapshotBuilder
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
    };

    public static LegacyActiveToolSnapshot Build()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tools = ToolListPayload.MeasureCurrentTools(catalog).ToList();
        var bindings = DiscoverBindings(catalog)
            .ToDictionary(binding => binding.ToolName, StringComparer.Ordinal);

        var missingBindings = tools
            .Select(tool => tool.Name)
            .Where(name => !bindings.ContainsKey(name))
            .ToArray();
        var inactiveBindings = bindings.Keys
            .Where(name => tools.All(tool => !string.Equals(tool.Name, name, StringComparison.Ordinal)))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (missingBindings.Length != 0 || inactiveBindings.Length != 0)
        {
            throw new InvalidOperationException(
                $"SDK tool/reflection binding mismatch. Missing=[{string.Join(",", missingBindings)}]; " +
                $"inactive=[{string.Join(",", inactiveBindings)}].");
        }

        var catalogPayload = JsonSerializer.SerializeToUtf8Bytes(
            new ListToolsResult { Tools = tools },
            McpJsonUtilities.DefaultOptions);
        var toolSnapshots = tools
            .Select(tool => BuildToolSnapshot(tool, bindings[tool.Name]))
            .ToList();

        return new LegacyActiveToolSnapshot(
            FormatVersion: "active-tools.v1",
            BaselineKind: "reviewed_current_active_catalog",
            PreRefactorObservation: new LegacyCatalogObservation(
                Commit: "2dfb459",
                ToolCount: 61,
                StructuredToolCount: 5,
                CatalogBytes: 178_923),
            ToolCount: tools.Count,
            CatalogBytes: catalogPayload.Length,
            CatalogSha256: Sha256(catalogPayload),
            StructuredToolNames: toolSnapshots
                .Where(tool => tool.UseStructuredContent)
                .Select(tool => tool.Name)
                .ToList(),
            Tools: toolSnapshots);
    }

    public static string BuildCanonicalJson()
    {
        var json = JsonSerializer.Serialize(Build(), SnapshotJsonOptions);
        return json.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    private static LegacyToolSnapshot BuildToolSnapshot(Tool tool, ToolBinding binding)
    {
        var toolPayload = SerializeProtocol(tool);
        var inputSchema = SerializeProtocol(tool.InputSchema);
        var outputSchema = tool.OutputSchema is null
            ? null
            : SerializeProtocol(tool.OutputSchema);
        var annotations = tool.Annotations is null
            ? (JsonElement?)null
            : JsonSerializer.SerializeToElement(tool.Annotations, McpJsonUtilities.DefaultOptions);

        return new LegacyToolSnapshot(
            Name: tool.Name,
            DeclaringType: TypeIdentity(binding.Method.DeclaringType!),
            Method: binding.Method.Name,
            ReturnType: TypeIdentity(binding.Method.ReturnType),
            Description: tool.Description,
            Annotations: annotations,
            // The active snapshot records the executable protocol Tool, including
            // catalog wrappers that add contract-2.0 output schemas/structured results.
            // The method attribute alone describes only the legacy SDK binding.
            UseStructuredContent: tool.OutputSchema is not null,
            Parameters: binding.Method.GetParameters()
                .Select(BuildParameterSnapshot)
                .ToList(),
            InputSchemaBytes: inputSchema.Length,
            InputSchemaSha256: Sha256(inputSchema),
            OutputSchemaBytes: outputSchema?.Length ?? 0,
            OutputSchemaSha256: outputSchema is null ? null : Sha256(outputSchema),
            ToolBytes: toolPayload.Length);
    }

    private static LegacyParameterSnapshot BuildParameterSnapshot(ParameterInfo parameter)
    {
        return new LegacyParameterSnapshot(
            Name: parameter.Name ?? throw new InvalidOperationException("MCP parameter has no name."),
            Type: TypeIdentity(parameter.ParameterType),
            HasDefaultValue: parameter.HasDefaultValue,
            DefaultValueJson: SerializeDefaultValue(parameter),
            Description: parameter.GetCustomAttribute<DescriptionAttribute>()?.Description);
    }

    private static string? SerializeDefaultValue(ParameterInfo parameter)
    {
        if (!parameter.HasDefaultValue)
            return null;

        var value = parameter.DefaultValue;
        if (value is null)
            return "null";
        if (ReferenceEquals(value, Missing.Value))
            return "<missing>";
        if (ReferenceEquals(value, DBNull.Value))
            return "<dbnull>";

        return Encoding.UTF8.GetString(
            JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), McpJsonUtilities.DefaultOptions));
    }

    private static IReadOnlyList<ToolBinding> DiscoverBindings(ActiveToolCatalog catalog)
        => catalog.Tools
            .Select(tool => new ToolBinding(
                tool.ToolName,
                tool.Method,
                tool.Method.GetCustomAttribute<McpServerToolAttribute>()
                    ?? throw new InvalidOperationException(
                        $"Validated active tool '{tool.ToolName}' lost its SDK attribute.")))
            .ToArray();

    private static byte[] SerializeProtocol<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, McpJsonUtilities.DefaultOptions);

    private static string Sha256(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    private static string TypeIdentity(Type type)
    {
        if (type.IsByRef)
            return TypeIdentity(type.GetElementType()!) + "&";
        if (type.IsArray)
            return TypeIdentity(type.GetElementType()!) + "[]";
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var genericName = type.GetGenericTypeDefinition().FullName
            ?? type.GetGenericTypeDefinition().Name;
        var arityMarker = genericName.IndexOf('`');
        if (arityMarker >= 0)
            genericName = genericName[..arityMarker];
        return genericName + "<" +
               string.Join(",", type.GetGenericArguments().Select(TypeIdentity)) + ">";
    }

    private sealed record ToolBinding(
        string ToolName,
        MethodInfo Method,
        McpServerToolAttribute Attribute);
}

internal sealed record LegacyActiveToolSnapshot(
    string FormatVersion,
    string BaselineKind,
    LegacyCatalogObservation PreRefactorObservation,
    int ToolCount,
    int CatalogBytes,
    string CatalogSha256,
    IReadOnlyList<string> StructuredToolNames,
    IReadOnlyList<LegacyToolSnapshot> Tools);

internal sealed record LegacyCatalogObservation(
    string Commit,
    int ToolCount,
    int StructuredToolCount,
    int CatalogBytes);

internal sealed record LegacyToolSnapshot(
    string Name,
    string DeclaringType,
    string Method,
    string ReturnType,
    string? Description,
    JsonElement? Annotations,
    bool UseStructuredContent,
    IReadOnlyList<LegacyParameterSnapshot> Parameters,
    int InputSchemaBytes,
    string InputSchemaSha256,
    int OutputSchemaBytes,
    string? OutputSchemaSha256,
    int ToolBytes);

internal sealed record LegacyParameterSnapshot(
    string Name,
    string Type,
    bool HasDefaultValue,
    string? DefaultValueJson,
    string? Description);
