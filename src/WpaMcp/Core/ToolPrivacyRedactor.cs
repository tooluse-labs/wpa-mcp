using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using WpaMcp.Core.Catalog;

namespace WpaMcp.Core;

internal enum ToolPrivacyMode
{
    Off,
    Paths,
    Strict,
}

internal sealed record ToolPrivacyOptions(ToolPrivacyMode Mode, string Profile)
{
    internal const string EnvironmentVariable = "WPAMCP_PRIVACY_PROFILE";

    internal static ToolPrivacyOptions Parse(string? raw, string source)
    {
        var normalized = string.IsNullOrWhiteSpace(raw) ? "off" : raw.Trim().ToLowerInvariant();
        return normalized switch
        {
            "off" => new(ToolPrivacyMode.Off, "off"),
            "paths" => new(ToolPrivacyMode.Paths, "paths"),
            "strict" => new(ToolPrivacyMode.Strict, "strict"),
            _ => throw new ArgumentException($"{source} must be 'off', 'paths', or 'strict'."),
        };
    }
}

internal enum SensitiveFieldKind
{
    TracePath,
    FilePath,
    RegistryPath,
    SymbolPath,
    UncPath,
    UserName,
    MachineName,
    Host,
    IpAddress,
    RegistryValue,
    MarkerPayload,
}

internal interface IToolPrivacyRedactor
{
    JsonObject Redact(JsonObject envelope, ActiveToolDefinition tool);
}

internal interface ITypedAliasRegistry
{
    string Issue(SensitiveFieldKind kind, string value);
    bool TryResolve(SensitiveFieldKind kind, string alias, out string value);
}

internal sealed record ResolvedAliasArgument(
    string ParameterName,
    SensitiveFieldKind Kind,
    string Alias,
    string Value);

internal sealed record ToolArgumentRewrite(
    JsonObject Arguments,
    IReadOnlyList<ResolvedAliasArgument> ResolvedAliases);

internal interface IToolArgumentRewriter
{
    ToolArgumentRewrite Rewrite(string toolName, JsonObject arguments);
}

public interface IPrivacyLogSink
{
    TextWriter Writer { get; }
    string RedactMessage(string message);
}

internal sealed class PassThroughPrivacyLogSink : IPrivacyLogSink
{
    internal static PassThroughPrivacyLogSink Instance { get; } = new();
    public TextWriter Writer => Console.Error;
    public string RedactMessage(string message) => message;
}

internal sealed class PrivacyAliasCollisionException
    : InvalidOperationException
{
    internal PrivacyAliasCollisionException()
        : base("A privacy alias collision was detected.")
    {
    }
}

internal static class SensitiveFieldKinds
{
    private static readonly IReadOnlyDictionary<SensitiveFieldKind, string> Tokens =
        new Dictionary<SensitiveFieldKind, string>
        {
            [SensitiveFieldKind.TracePath] = "trace_path",
            [SensitiveFieldKind.FilePath] = "file_path",
            [SensitiveFieldKind.RegistryPath] = "registry_path",
            [SensitiveFieldKind.SymbolPath] = "symbol_path",
            [SensitiveFieldKind.UncPath] = "unc_path",
            [SensitiveFieldKind.UserName] = "user_name",
            [SensitiveFieldKind.MachineName] = "machine_name",
            [SensitiveFieldKind.Host] = "host",
            [SensitiveFieldKind.IpAddress] = "ip_address",
            [SensitiveFieldKind.RegistryValue] = "registry_value",
            [SensitiveFieldKind.MarkerPayload] = "marker_payload",
        };

    private static readonly IReadOnlyDictionary<string, SensitiveFieldKind> Kinds =
        Tokens.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);

    internal static string Token(SensitiveFieldKind kind) => Tokens[kind];

    internal static SensitiveFieldKind Parse(string token) =>
        Kinds.TryGetValue(token, out var kind)
            ? kind
            : throw new InvalidOperationException($"Unknown privacy taxonomy kind '{token}'.");
}

/// <summary>
/// Process-scoped, bounded aliases retain equality/replay value without disclosing
/// the sensitive source string. Reads never refresh insertion order.
/// </summary>
internal sealed class TypedAliasRegistry : ITypedAliasRegistry, IDisposable
{
    internal const int MaxAliases = 4_096;
    internal const int MaxInboundAliasChars = 128;
    private readonly object _gate = new();
    private readonly byte[] _key;
    private readonly Dictionary<AliasSource, string> _bySource = new();
    private readonly Dictionary<string, AliasEntry> _byAlias = new(StringComparer.Ordinal);
    private readonly Queue<AliasSource> _insertionOrder = new();
    private bool _disposed;

    internal TypedAliasRegistry(byte[]? key = null)
    {
        _key = key is null ? RandomNumberGenerator.GetBytes(32) : (byte[])key.Clone();
        if (_key.Length != 32)
            throw new ArgumentException("The privacy alias key must contain exactly 32 bytes.", nameof(key));
    }

    public string Issue(SensitiveFieldKind kind, string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var source = new AliasSource(kind, value);
        lock (_gate)
        {
            if (_bySource.TryGetValue(source, out var existing))
                return existing;

            var alias = ComputeAlias(kind, value);
            if (_byAlias.TryGetValue(alias, out var collision) &&
                (collision.Kind != kind || !string.Equals(collision.Value, value, StringComparison.Ordinal)))
            {
                throw new PrivacyAliasCollisionException();
            }

            while (_bySource.Count >= MaxAliases)
            {
                var evicted = _insertionOrder.Dequeue();
                if (_bySource.Remove(evicted, out var evictedAlias))
                    _byAlias.Remove(evictedAlias);
            }

            _bySource.Add(source, alias);
            _byAlias.Add(alias, new AliasEntry(kind, value));
            _insertionOrder.Enqueue(source);
            return alias;
        }
    }

    public bool TryResolve(SensitiveFieldKind kind, string alias, out string value)
    {
        value = string.Empty;
        if (_disposed || string.IsNullOrEmpty(alias) || alias.Length > MaxInboundAliasChars)
            return false;
        var prefix = "alias_" + SensitiveFieldKinds.Token(kind) + "_";
        if (!alias.StartsWith(prefix, StringComparison.Ordinal) ||
            alias.Length != prefix.Length + 22 ||
            alias.AsSpan(prefix.Length).ContainsAnyExcept(
                "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_byAlias.TryGetValue(alias, out var entry) || entry.Kind != kind)
                return false;
            value = entry.Value;
            return true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            CryptographicOperations.ZeroMemory(_key);
            _bySource.Clear();
            _byAlias.Clear();
            _insertionOrder.Clear();
        }
    }

    private string ComputeAlias(SensitiveFieldKind kind, string value)
    {
        using var hmac = new HMACSHA256(_key);
        var payload = Encoding.UTF8.GetBytes(SensitiveFieldKinds.Token(kind) + "\0" + value);
        var digest = hmac.ComputeHash(payload);
        var token = Convert.ToBase64String(digest, 0, 16)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "alias_" + SensitiveFieldKinds.Token(kind) + "_" + token;
    }

    private readonly record struct AliasSource(SensitiveFieldKind Kind, string Value);
    private sealed record AliasEntry(SensitiveFieldKind Kind, string Value);
}

internal sealed class ToolArgumentRewriter(
    ToolPrivacyTaxonomy taxonomy,
    ITypedAliasRegistry aliases) : IToolArgumentRewriter
{
    private readonly ToolPrivacyTaxonomy _taxonomy = taxonomy ?? throw new ArgumentNullException(nameof(taxonomy));
    private readonly ITypedAliasRegistry _aliases = aliases ?? throw new ArgumentNullException(nameof(aliases));

    public ToolArgumentRewrite Rewrite(string toolName, JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        var clone = arguments.DeepClone().AsObject();
        var resolved = new List<ResolvedAliasArgument>();

        foreach (var property in clone.ToArray())
        {
            if (property.Value is not JsonValue scalar ||
                !scalar.TryGetValue<string>(out var text) ||
                !_taxonomy.TryGetAliasInput(toolName, property.Key, out var kind) ||
                !text.StartsWith("alias_", StringComparison.Ordinal))
            {
                continue;
            }

            if (text.Length > TypedAliasRegistry.MaxInboundAliasChars ||
                !_aliases.TryResolve(kind, text, out var value))
            {
                throw new ArgumentException("The supplied privacy alias is invalid for this tool parameter.");
            }

            clone[property.Key] = value;
            resolved.Add(new(property.Key, kind, text, value));
        }

        return new(clone, resolved);
    }
}

internal sealed class RejectingAliasArgumentRewriter : IToolArgumentRewriter
{
    internal static RejectingAliasArgumentRewriter Instance { get; } = new();

    public ToolArgumentRewrite Rewrite(string toolName, JsonObject arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(arguments);
        return new(arguments.DeepClone().AsObject(), Array.Empty<ResolvedAliasArgument>());
    }
}

internal enum PrivacyFieldBehavior
{
    Retain,
    Alias,
    Redact,
}

internal sealed record PrivacyFieldRule(
    string Property,
    SensitiveFieldKind Kind,
    PrivacyFieldBehavior Paths,
    PrivacyFieldBehavior Strict,
    bool ApprovedBasename);

internal sealed record PrivacySemanticRule(
    string ToolName,
    string JsonPointerPattern,
    SensitiveFieldKind Kind,
    PrivacyFieldBehavior Paths,
    PrivacyFieldBehavior Strict);

internal sealed class ToolPrivacyTaxonomy
{
    private readonly IReadOnlyDictionary<string, PrivacyFieldRule> _fields;
    private readonly IReadOnlyList<PrivacySemanticRule> _semanticRules;
    private readonly IReadOnlyDictionary<(string ToolName, string ParameterName), SensitiveFieldKind> _aliasInputs;

    private ToolPrivacyTaxonomy(
        IReadOnlyDictionary<string, PrivacyFieldRule> fields,
        IReadOnlyList<PrivacySemanticRule> semanticRules,
        IReadOnlyDictionary<(string ToolName, string ParameterName), SensitiveFieldKind> aliasInputs)
    {
        _fields = fields;
        _semanticRules = semanticRules;
        _aliasInputs = aliasInputs;
    }

    internal static ToolPrivacyTaxonomy Default { get; } = LoadDefault();

    internal bool TryGetField(string property, out PrivacyFieldRule rule) =>
        _fields.TryGetValue(property, out rule!);

    internal PrivacySemanticRule? MatchSemantic(string toolName, IReadOnlyList<string> path) =>
        _semanticRules.FirstOrDefault(rule =>
            string.Equals(rule.ToolName, toolName, StringComparison.Ordinal) &&
            PointerMatches(rule.JsonPointerPattern, path));

    internal bool TryGetAliasInput(string toolName, string parameterName, out SensitiveFieldKind kind) =>
        _aliasInputs.TryGetValue((toolName, parameterName), out kind);

    private static ToolPrivacyTaxonomy LoadDefault()
    {
        var json = CatalogManifestLoader.Read(
            typeof(ToolPrivacyTaxonomy).Assembly,
            "eng/privacy-taxonomy.v1.json",
            "WpaMcp.Manifests.eng.privacy-taxonomy.v1.json");
        var manifest = JsonSerializer.Deserialize<PrivacyTaxonomyManifest>(
            json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            }) ?? throw new InvalidOperationException("The privacy taxonomy deserialized to null.");
        if (!string.Equals(manifest.SchemaVersion, "privacy-taxonomy.v1", StringComparison.Ordinal))
            throw new InvalidOperationException("The privacy taxonomy schemaVersion is unsupported.");

        var fields = new Dictionary<string, PrivacyFieldRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Fields)
        {
            if (string.IsNullOrWhiteSpace(entry.Property) || !fields.TryAdd(
                    entry.Property,
                    new(
                        entry.Property,
                        SensitiveFieldKinds.Parse(entry.Kind),
                        ParseBehavior(entry.Paths),
                        ParseBehavior(entry.Strict),
                        entry.ApprovedBasename)))
            {
                throw new InvalidOperationException("The privacy taxonomy contains a blank or duplicate field.");
            }
        }

        var semantic = manifest.SemanticPaths.Select(entry => new PrivacySemanticRule(
            Require(entry.ToolName, "semantic toolName"),
            ValidatePointer(entry.JsonPointerPattern),
            SensitiveFieldKinds.Parse(entry.Kind),
            ParseBehavior(entry.Paths),
            ParseBehavior(entry.Strict))).ToArray();
        if (semantic.Select(item => (item.ToolName, item.JsonPointerPattern)).Distinct().Count() != semantic.Length)
            throw new InvalidOperationException("The privacy taxonomy repeats a semantic path.");

        var aliases = new Dictionary<(string, string), SensitiveFieldKind>();
        foreach (var entry in manifest.AliasEnabledInputs)
        {
            var key = (Require(entry.ToolName, "alias toolName"), Require(entry.ParameterName, "alias parameterName"));
            if (!aliases.TryAdd(key, SensitiveFieldKinds.Parse(entry.Kind)))
                throw new InvalidOperationException("The privacy taxonomy repeats an alias-enabled input.");
        }
        return new(fields, semantic, aliases);
    }

    private static PrivacyFieldBehavior ParseBehavior(string value) => value switch
    {
        "retain" => PrivacyFieldBehavior.Retain,
        "alias" => PrivacyFieldBehavior.Alias,
        "redact" => PrivacyFieldBehavior.Redact,
        _ => throw new InvalidOperationException($"Unknown privacy behavior '{value}'."),
    };

    private static string Require(string value, string label) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"The privacy taxonomy has a blank {label}.")
            : value;

    private static string ValidatePointer(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/", StringComparison.Ordinal))
            throw new InvalidOperationException("Privacy semantic paths must be absolute JSON pointer patterns.");
        return value;
    }

    private static bool PointerMatches(string pattern, IReadOnlyList<string> path)
    {
        var segments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != path.Count)
            return false;
        for (var index = 0; index < segments.Length; index++)
        {
            if (segments[index] != "*" && !string.Equals(segments[index], path[index], StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    private sealed class PrivacyTaxonomyManifest
    {
        public string SchemaVersion { get; init; } = "";
        public IReadOnlyList<PrivacyFieldManifestEntry> Fields { get; init; } = [];
        public IReadOnlyList<PrivacySemanticManifestEntry> SemanticPaths { get; init; } = [];
        public IReadOnlyList<PrivacyAliasInputManifestEntry> AliasEnabledInputs { get; init; } = [];
    }

    private sealed class PrivacyFieldManifestEntry
    {
        public string Property { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Paths { get; init; } = "";
        public string Strict { get; init; } = "";
        public bool ApprovedBasename { get; init; }
    }

    private sealed class PrivacySemanticManifestEntry
    {
        public string ToolName { get; init; } = "";
        public string JsonPointerPattern { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Paths { get; init; } = "";
        public string Strict { get; init; } = "";
    }

    private sealed class PrivacyAliasInputManifestEntry
    {
        public string ToolName { get; init; } = "";
        public string ParameterName { get; init; } = "";
        public string Kind { get; init; } = "";
    }
}

internal sealed partial class ToolPrivacyRedactor : IToolPrivacyRedactor
{
    private static readonly HashSet<string> SafeLiterals = new(StringComparer.Ordinal)
    {
        "", "<unset>", "<unknown>", "<none>",
    };

    private readonly ToolPrivacyMode _mode;
    private readonly ToolPrivacyTaxonomy _taxonomy;
    private readonly ITypedAliasRegistry _aliases;

    internal ToolPrivacyRedactor(
        ToolPrivacyMode mode,
        ToolPrivacyTaxonomy? taxonomy = null,
        ITypedAliasRegistry? aliases = null)
    {
        _mode = mode;
        _taxonomy = taxonomy ?? ToolPrivacyTaxonomy.Default;
        _aliases = aliases ?? new TypedAliasRegistry();
    }

    public JsonObject Redact(JsonObject envelope, ActiveToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(tool);
        var clone = envelope.DeepClone().AsObject();
        if (_mode != ToolPrivacyMode.Off)
            RedactObject(clone, tool.ToolName, []);
        return clone;
    }

    internal string RedactDiagnostic(string message) =>
        _mode == ToolPrivacyMode.Off ? message : ScrubFreeText(message);

    private void RedactObject(JsonObject value, string toolName, IReadOnlyList<string> parentPath)
    {
        foreach (var property in value.ToArray())
        {
            var path = Append(parentPath, property.Key);
            if (property.Value is JsonObject child)
                RedactObject(child, toolName, path);
            else if (property.Value is JsonArray array)
                RedactArray(array, toolName, path);
            else if (property.Value is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                value[property.Key] = RedactString(toolName, path, property.Key, text);
        }
    }

    private void RedactArray(JsonArray array, string toolName, IReadOnlyList<string> parentPath)
    {
        for (var index = 0; index < array.Count; index++)
        {
            var path = Append(parentPath, index.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (array[index] is JsonObject child)
                RedactObject(child, toolName, path);
            else if (array[index] is JsonArray nested)
                RedactArray(nested, toolName, path);
            else if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text))
                array[index] = RedactString(toolName, path, parentPath.Count == 0 ? "" : parentPath[^1], text);
        }
    }

    private string RedactString(
        string toolName,
        IReadOnlyList<string> path,
        string field,
        string value)
    {
        // These server-generated values are the integrity-bearing representation of
        // one immutable output contract page. The exception is deliberately bound to
        // one tool and exact pointers; it is not a field-name or general retain rule.
        if (IsGetToolContractMachineString(toolName, path))
            return value;

        var semantic = _taxonomy.MatchSemantic(toolName, path);
        if (semantic is not null)
        {
            var semanticBehavior = Behavior(semantic.Paths, semantic.Strict);
            if (semanticBehavior != PrivacyFieldBehavior.Retain)
                return Apply(semanticBehavior, semantic.Kind, value);
        }

        if (IsOpaqueContractLocator(field, value))
            return value;

        if (_taxonomy.TryGetField(field, out var rule))
        {
            if (IsSafeLiteral(rule.Kind, value) || IsIssuedAlias(rule.Kind, value))
                return value;
            var behavior = Behavior(rule.Paths, rule.Strict);
            if (behavior != PrivacyFieldBehavior.Retain)
                return Apply(behavior, rule.Kind, value);
            // A taxonomy-approved basename is safe only while it is actually a
            // basename. Analyzer drift that puts a full path in the same field
            // must fail closed at this final boundary.
            if (LooksLikeSensitivePath(value, out var detectedKind))
                return _aliases.Issue(detectedKind, value);
        }

        if (LooksLikeSensitivePath(value, out var kind))
            return _aliases.Issue(kind, value);

        return ScrubFreeText(value);
    }

    private static bool IsGetToolContractMachineString(
        string toolName,
        IReadOnlyList<string> path)
    {
        if (!string.Equals(toolName, "get_tool_contract", StringComparison.Ordinal) ||
            path.Count != 2 ||
            !string.Equals(path[0], "data", StringComparison.Ordinal))
        {
            return false;
        }

        return path[1] is "toolName" or
            "contractVersion" or
            "schemaUri" or
            "sha256" or
            "mediaType" or
            "schemaFragment";
    }

    private PrivacyFieldBehavior Behavior(
        PrivacyFieldBehavior paths,
        PrivacyFieldBehavior strict) =>
        _mode == ToolPrivacyMode.Strict ? strict : paths;

    private string Apply(PrivacyFieldBehavior behavior, SensitiveFieldKind kind, string value) =>
        behavior switch
        {
            PrivacyFieldBehavior.Retain => value,
            PrivacyFieldBehavior.Alias => _aliases.Issue(kind, value),
            PrivacyFieldBehavior.Redact => kind is SensitiveFieldKind.TracePath or
                SensitiveFieldKind.FilePath or SensitiveFieldKind.RegistryPath or
                SensitiveFieldKind.SymbolPath or SensitiveFieldKind.UncPath
                    ? "[redacted-path]"
                    : "[redacted]",
            _ => throw new InvalidOperationException("Unknown privacy field behavior."),
        };

    private string ScrubFreeText(string value)
    {
        var scrubbed = RegistryPathRegex().Replace(
            value,
            match => _aliases.Issue(SensitiveFieldKind.RegistryPath, match.Value));
        scrubbed = WindowsPathRegex().Replace(
            scrubbed,
            match => _aliases.Issue(
                match.Value.StartsWith("\\\\", StringComparison.Ordinal)
                    ? SensitiveFieldKind.UncPath
                    : SensitiveFieldKind.FilePath,
                match.Value));
        scrubbed = UnixPathRegex().Replace(
            scrubbed,
            match => _aliases.Issue(SensitiveFieldKind.FilePath, match.Value));
        if (_mode == ToolPrivacyMode.Strict)
        {
            scrubbed = IpCandidateRegex().Replace(scrubbed, match =>
                IsUnambiguousIpLiteral(match.Value)
                    ? _aliases.Issue(SensitiveFieldKind.IpAddress, match.Value)
                    : match.Value);
        }
        return scrubbed;
    }

    private static bool IsUnambiguousIpLiteral(string candidate)
    {
        var value = candidate.Trim('[', ']');
        if (value.Contains(':'))
            return IPAddress.TryParse(value, out var ipv6) &&
                ipv6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

        // IPAddress.TryParse intentionally accepts abbreviated IPv4 forms such as
        // "2.0". Those forms are ambiguous with contract/schema versions and must
        // not be treated as sensitive addresses at the final response boundary.
        var octets = value.Split('.');
        return octets.Length == 4 && octets.All(static octet =>
            octet.Length is >= 1 and <= 3 &&
            octet.All(char.IsAsciiDigit) &&
            byte.TryParse(
                octet,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out _));
    }

    private static bool LooksLikeSensitivePath(string value, out SensitiveFieldKind kind)
    {
        if (RegistryPathRegex().IsMatch(value))
        {
            kind = SensitiveFieldKind.RegistryPath;
            return true;
        }
        if (value.StartsWith("\\\\", StringComparison.Ordinal))
        {
            kind = SensitiveFieldKind.UncPath;
            return true;
        }
        if (value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
            (value[2] == '\\' || value[2] == '/'))
        {
            kind = SensitiveFieldKind.FilePath;
            return true;
        }
        if (value.StartsWith("/", StringComparison.Ordinal) && !value.Contains(' '))
        {
            kind = SensitiveFieldKind.FilePath;
            return true;
        }
        kind = default;
        return false;
    }

    private bool IsIssuedAlias(SensitiveFieldKind kind, string value) =>
        value.StartsWith("alias_", StringComparison.Ordinal) &&
        _aliases.TryResolve(kind, value, out _);

    private static bool IsSafeLiteral(SensitiveFieldKind kind, string value) =>
        (kind is SensitiveFieldKind.TracePath or SensitiveFieldKind.FilePath or
            SensitiveFieldKind.RegistryPath or SensitiveFieldKind.SymbolPath or
            SensitiveFieldKind.UncPath) &&
        (SafeLiterals.Contains(value) ||
         value.StartsWith("<unmapped:", StringComparison.Ordinal) ||
         value.StartsWith("<ambiguous:", StringComparison.Ordinal));

    private static bool IsOpaqueContractLocator(string field, string value)
    {
        if (!OpaqueLocatorRegex().IsMatch(value))
            return false;
        return (field.Equals("traceId", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("path", StringComparison.OrdinalIgnoreCase)) &&
            value.StartsWith("trc_", StringComparison.Ordinal) ||
            field.Equals("symbolContextId", StringComparison.OrdinalIgnoreCase) && value.StartsWith("sym_", StringComparison.Ordinal) ||
            (field.Equals("cursor", StringComparison.OrdinalIgnoreCase) ||
             field.Equals("nextCursor", StringComparison.OrdinalIgnoreCase)) &&
            (value.StartsWith("qrc_", StringComparison.Ordinal) ||
             value.StartsWith("cpc_", StringComparison.Ordinal) ||
             value.StartsWith("tlc_", StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> Append(IReadOnlyList<string> path, string segment)
    {
        var result = new string[path.Count + 1];
        for (var index = 0; index < path.Count; index++)
            result[index] = path[index];
        result[^1] = segment;
        return result;
    }

    [GeneratedRegex(@"^(?:(?:trc|sym|qrc|cpc|tlc)_[0-9a-f]{32})$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex OpaqueLocatorRegex();

    [GeneratedRegex(@"(?i)\b(?:HKLM|HKCU|HKCR|HKU|HKCC|HKEY_LOCAL_MACHINE|HKEY_CURRENT_USER|HKEY_CLASSES_ROOT|HKEY_USERS|HKEY_CURRENT_CONFIG)(?:\\[^,;:\""'\r\n]+)+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex RegistryPathRegex();

    [GeneratedRegex(@"(?i)(?:[a-z]:[\\/]|\\\\)[^,;\""'<>|\r\n]+", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex WindowsPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9:])/(?:[^/\s\""'<>]+/)*[^/\s\""'<>]+", RegexOptions.CultureInvariant)]
    private static partial Regex UnixPathRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:\[[0-9A-Fa-f:]+\]|[0-9A-Fa-f:.]*[.:][0-9A-Fa-f:.]+)(?![A-Za-z0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex IpCandidateRegex();
}

internal sealed class PrivacyLogSink : IPrivacyLogSink, IDisposable
{
    private readonly ToolPrivacyMode _mode;
    private readonly ToolPrivacyRedactor _redactor;
    private readonly TextWriter _destination;
    private readonly RedactingLineWriter? _redactingWriter;

    internal PrivacyLogSink(
        ToolPrivacyMode mode,
        ToolPrivacyRedactor redactor,
        TextWriter? destination = null)
    {
        _mode = mode;
        _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));
        _destination = destination ?? Console.Error;
        _redactingWriter = mode == ToolPrivacyMode.Off
            ? null
            : new RedactingLineWriter(_destination, RedactMessage);
        Writer = _redactingWriter ?? _destination;
    }

    public TextWriter Writer { get; }

    public string RedactMessage(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Strict mode cannot assign a trustworthy taxonomy kind to arbitrary
        // third-party DIA/SymbolReader diagnostics, so it suppresses the whole
        // free-text line. Paths mode retains non-path diagnostics after scrubbing.
        return _mode == ToolPrivacyMode.Strict
            ? "[redacted-diagnostic]"
            : _redactor.RedactDiagnostic(message);
    }

    public void Dispose() => _redactingWriter?.Dispose();

    private sealed class RedactingLineWriter(
        TextWriter destination,
        Func<string, string> redact) : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _pending = new();
        private bool _disposed;

        public override Encoding Encoding => destination.Encoding;

        public override void Write(char value)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value == '\n')
                    FlushLine(appendNewLine: true);
                else if (value != '\r')
                    _pending.Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;
            foreach (var character in value)
                Write(character);
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (value is not null)
                    _pending.Append(value);
                FlushLine(appendNewLine: true);
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                FlushLine(appendNewLine: false);
                destination.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!disposing)
                return;
            lock (_gate)
            {
                if (_disposed)
                    return;
                FlushLine(appendNewLine: false);
                destination.Flush();
                _disposed = true;
            }
            base.Dispose(disposing);
        }

        private void FlushLine(bool appendNewLine)
        {
            if (_pending.Length > 0)
            {
                destination.Write(redact(_pending.ToString()));
                _pending.Clear();
            }
            if (appendNewLine)
                destination.WriteLine();
        }
    }
}

internal sealed class PrivacyLoggerProvider(IPrivacyLogSink sink) : ILoggerProvider
{
    private readonly IPrivacyLogSink _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public ILogger CreateLogger(string categoryName) => new PrivacyLogger(_sink, categoryName);
    public void Dispose() { }

    private sealed class PrivacyLogger(IPrivacyLogSink sink, string categoryName) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            sink.Writer.WriteLine($"[{logLevel}] {categoryName}: {message}");
        }
    }
}
