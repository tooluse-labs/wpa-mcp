using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal readonly record struct QueryResultCursorBinding(
    string Principal,
    string TraceId,
    string TraceGenerationId,
    string CatalogOrToolVersion,
    string ContractVersion,
    string? SymbolContextId,
    string PrivacyProfile,
    string QueryHash,
    string Ordering,
    string CapabilityPolicyIdentity = "full");

internal readonly record struct QueryResultCursorPosition(
    string Phase,
    int Index,
    string? LastKey);

internal enum QueryResultCursorFailureKind
{
    Invalid,
    RegistryCapacity,
    EntropyFailure,
}

internal sealed class QueryResultCursorException : InvalidOperationException
{
    internal QueryResultCursorException(
        QueryResultCursorFailureKind kind,
        string message) : base(message)
    {
        Kind = kind;
        ToolFailureCaptureContext.Record(this);
    }

    internal QueryResultCursorFailureKind Kind { get; }
}

/// <summary>
/// Principal-scoped opaque continuation registry for generation-bound query
/// results. Positions support both phase/index paging and a future keyset
/// LastKey without changing the public qrc_ locator domain.
/// </summary>
internal sealed class QueryResultCursorRegistry
{
    internal const string Prefix = "qrc_";
    internal const string PendingDeliveryToken = "qrc_00000000000000000000000000000000";
    private const int TokenLength = 36;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<QueryResultCursorBinding, string> _rootContinuations = [];
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _idleTtl;
    private readonly TimeSpan _absoluteTtl;
    private readonly int _maxActive;
    private readonly Func<byte[]> _entropy;

    internal QueryResultCursorRegistry(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? idleTtl = null,
        TimeSpan? absoluteTtl = null,
        int maxActive = 1_024,
        Func<byte[]>? entropy = null)
    {
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(2);
        _absoluteTtl = absoluteTtl ?? TimeSpan.FromMinutes(15);
        if (_idleTtl <= TimeSpan.Zero || _absoluteTtl < _idleTtl)
            throw new ArgumentOutOfRangeException(nameof(idleTtl));
        if (maxActive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActive));
        _maxActive = maxActive;
        _entropy = entropy ?? (static () => RandomNumberGenerator.GetBytes(16));
    }

    internal QueryResultCursorPosition Redeem(
        string token,
        QueryResultCursorBinding expected)
    {
        if (!HasCanonicalShape(token))
            throw Invalid();
        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);
            if (!_active.TryGetValue(token, out var entry) || entry.Binding != expected)
                throw Invalid();
            if (Expired(entry, now))
            {
                _active.Remove(token);
                throw Invalid();
            }
            _active[token] = entry with { LastAccessUtc = now };
            return entry.Position;
        }
    }

    internal string GetOrIssueContinuation(
        QueryResultCursorBinding binding,
        string? parentToken,
        QueryResultCursorPosition position)
    {
        if (position.Index < 0 || string.IsNullOrWhiteSpace(position.Phase))
            throw new ArgumentOutOfRangeException(nameof(position));
        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);
            string? existing = null;
            if (parentToken is null)
            {
                _rootContinuations.TryGetValue(binding, out existing);
            }
            else if (_active.TryGetValue(parentToken, out var parent) &&
                     parent.Binding == binding)
            {
                existing = parent.ContinuationToken;
            }
            else
            {
                throw Invalid();
            }

            if (existing is not null &&
                _active.TryGetValue(existing, out var continuation) &&
                continuation.Binding == binding &&
                continuation.Position == position)
            {
                _active[existing] = continuation with { LastAccessUtc = now };
                return existing;
            }

            if (_active.Count >= _maxActive)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.RegistryCapacity,
                    "The query-result cursor registry is at capacity.");
            }

            var token = Mint();
            _active.Add(token, new Entry(binding, position, now, now, null));
            if (parentToken is null)
                _rootContinuations[binding] = token;
            else
                _active[parentToken] = _active[parentToken] with { ContinuationToken = token };
            return token;
        }
    }

    internal static bool HasCanonicalShape(string? token)
    {
        if (token is null || token.Length != TokenLength ||
            !token.StartsWith(Prefix, StringComparison.Ordinal))
            return false;
        foreach (var value in token.AsSpan(Prefix.Length))
        {
            if (value is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }
        return true;
    }

    private string Mint()
    {
        for (var attempt = 0; attempt < 32; attempt++)
        {
            byte[] bytes;
            try
            {
                bytes = _entropy();
            }
            catch (Exception exception) when (exception is not QueryResultCursorException)
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.EntropyFailure,
                    "The query-result cursor entropy source failed.");
            }
            if (bytes is not { Length: 16 })
            {
                throw new QueryResultCursorException(
                    QueryResultCursorFailureKind.EntropyFailure,
                    "The query-result cursor entropy source returned an invalid locator payload.");
            }
            var token = Prefix + Convert.ToHexString(bytes).ToLowerInvariant();
            if (!_active.ContainsKey(token))
                return token;
        }
        throw new QueryResultCursorException(
            QueryResultCursorFailureKind.EntropyFailure,
            "The query-result cursor registry could not mint a unique locator.");
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _active.ToArray())
        {
            if (Expired(pair.Value, now))
                _active.Remove(pair.Key);
        }
        foreach (var pair in _rootContinuations.ToArray())
        {
            if (!_active.ContainsKey(pair.Value))
                _rootContinuations.Remove(pair.Key);
        }
    }

    private bool Expired(Entry entry, DateTimeOffset now) =>
        now - entry.LastAccessUtc > _idleTtl ||
        now - entry.IssuedUtc > _absoluteTtl;

    private static QueryResultCursorException Invalid() =>
        new(
            QueryResultCursorFailureKind.Invalid,
            "The query-result cursor is invalid or no longer bound to this principal, trace generation, query, and ordering.");

    private sealed record Entry(
        QueryResultCursorBinding Binding,
        QueryResultCursorPosition Position,
        DateTimeOffset IssuedUtc,
        DateTimeOffset LastAccessUtc,
        string? ContinuationToken);
}

internal sealed class QueryResultCursorCoordinator
{
    internal const string InspectOrdering =
        "capabilities_domain_asc_id_asc_then_workflows_id_asc";
    private static readonly Regex FilterPattern = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly QueryResultCursorRegistry _registry;
    private readonly string _principal;
    private readonly string _privacyProfile;
    private readonly string _capabilityPolicyIdentity;

    internal QueryResultCursorCoordinator(
        string principal,
        string privacyProfile,
        QueryResultCursorRegistry? registry = null,
        string capabilityPolicyIdentity = "full")
    {
        _principal = Require(principal, nameof(principal));
        _privacyProfile = Require(privacyProfile, nameof(privacyProfile));
        _capabilityPolicyIdentity = Require(
            capabilityPolicyIdentity,
            nameof(capabilityPolicyIdentity));
        _registry = registry ?? new QueryResultCursorRegistry();
    }

    internal static string? NormalizeFilter(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        if (!FilterPattern.IsMatch(normalized))
        {
            throw new ArgumentException(
                $"{parameterName} must be a lowercase catalog identifier.",
                parameterName);
        }
        return normalized;
    }

    internal QueryResultCursorPosition ResolveInspectTrace(
        string traceId,
        string traceGenerationId,
        string catalogVersion,
        string? domain,
        string? goal,
        string? cursor)
    {
        var binding = InspectBinding(
            traceId,
            traceGenerationId,
            catalogVersion,
            domain,
            goal);
        return cursor is null
            ? new QueryResultCursorPosition("capabilities", 0, null)
            : _registry.Redeem(cursor, binding);
    }

    internal string? FinalizeInspectTrace(
        string traceId,
        string traceGenerationId,
        string catalogVersion,
        string? domain,
        string? goal,
        string? sourceCursor,
        string pagePhase,
        int retainedCapabilities,
        int retainedWorkflows,
        int matchedCapabilities,
        int matchedWorkflows,
        string? lastKey)
    {
        if (retainedCapabilities < 0 || retainedWorkflows < 0 ||
            matchedCapabilities < 0 || matchedWorkflows < 0)
            throw new ArgumentOutOfRangeException(nameof(retainedCapabilities));
        if (retainedCapabilities + retainedWorkflows <= 0)
            throw new ArgumentOutOfRangeException(nameof(retainedCapabilities));
        var binding = InspectBinding(
            traceId,
            traceGenerationId,
            catalogVersion,
            domain,
            goal);
        var start = sourceCursor is null
            ? new QueryResultCursorPosition("capabilities", 0, null)
            : _registry.Redeem(sourceCursor, binding);
        if (!string.Equals(start.Phase, pagePhase, StringComparison.Ordinal))
            throw InvalidPosition();

        QueryResultCursorPosition? next;
        if (pagePhase == "capabilities")
        {
            if (retainedWorkflows != 0)
                throw InvalidPosition();
            var index = checked(start.Index + retainedCapabilities);
            if (index > matchedCapabilities)
                throw InvalidPosition();
            next = index < matchedCapabilities
                ? new QueryResultCursorPosition("capabilities", index, lastKey)
                : matchedWorkflows > 0
                    ? new QueryResultCursorPosition("workflows", 0, null)
                    : null;
        }
        else if (pagePhase == "workflows")
        {
            if (retainedCapabilities != 0)
                throw InvalidPosition();
            var index = checked(start.Index + retainedWorkflows);
            if (index > matchedWorkflows)
                throw InvalidPosition();
            next = index < matchedWorkflows
                ? new QueryResultCursorPosition("workflows", index, lastKey)
                : null;
        }
        else
        {
            throw InvalidPosition();
        }

        return next is { } position
            ? _registry.GetOrIssueContinuation(binding, sourceCursor, position)
            : null;
    }

    internal QueryResultCursorPosition ResolveTimeline(
        TimelineQueryContext context,
        string? cursor)
    {
        var binding = TimelineBinding(context);
        return cursor is null
            ? new QueryResultCursorPosition(TimelinePagination.Phase, 0, null)
            : _registry.Redeem(cursor, binding);
    }

    internal string? FinalizeTimeline(
        TimelineQueryContext context,
        string? sourceCursor,
        int startIndex,
        int retainedRows,
        int totalRows,
        string lastKey)
    {
        if (startIndex < 0 || retainedRows <= 0 || totalRows < 0 ||
            string.IsNullOrWhiteSpace(lastKey))
        {
            throw new ArgumentOutOfRangeException(nameof(retainedRows));
        }

        var binding = TimelineBinding(context);
        var start = sourceCursor is null
            ? new QueryResultCursorPosition(TimelinePagination.Phase, 0, null)
            : _registry.Redeem(sourceCursor, binding);
        if (!string.Equals(start.Phase, TimelinePagination.Phase, StringComparison.Ordinal) ||
            start.Index != startIndex)
        {
            throw InvalidTimelinePosition();
        }

        var nextIndex = checked(start.Index + retainedRows);
        if (nextIndex > totalRows)
            throw InvalidTimelinePosition();
        return nextIndex < totalRows
            ? _registry.GetOrIssueContinuation(
                binding,
                sourceCursor,
                new QueryResultCursorPosition(
                    TimelinePagination.Phase,
                    nextIndex,
                    lastKey))
            : null;
    }

    private QueryResultCursorBinding InspectBinding(
        string traceId,
        string traceGenerationId,
        string catalogVersion,
        string? domain,
        string? goal)
    {
        var normalizedDomain = NormalizeFilter(domain, nameof(domain));
        var normalizedGoal = NormalizeFilter(goal, nameof(goal));
        var query = $"inspect_trace\ndomain={normalizedDomain ?? "*"}\ngoal={normalizedGoal ?? "*"}\nsymbolContext=null";
        var queryHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(query))).ToLowerInvariant();
        return new QueryResultCursorBinding(
            _principal,
            Require(traceId, nameof(traceId)),
            Require(traceGenerationId, nameof(traceGenerationId)),
            Require(catalogVersion, nameof(catalogVersion)),
            ToolContractVersions.V2,
            SymbolContextId: null,
            _privacyProfile,
            queryHash,
            InspectOrdering,
            _capabilityPolicyIdentity);
    }

    private QueryResultCursorBinding TimelineBinding(TimelineQueryContext context)
    {
        if (!TimelinePagination.IsTimelineTool(context.ToolName) ||
            string.IsNullOrWhiteSpace(context.QueryHash) || context.QueryHash.Length != 64)
        {
            throw new ArgumentException(
                "A validated timeline cursor binding is required.",
                nameof(context));
        }
        return new QueryResultCursorBinding(
            _principal,
            Require(context.TraceId, nameof(context.TraceId)),
            Require(context.TraceGenerationId, nameof(context.TraceGenerationId)),
            Require(context.ToolName, nameof(context.ToolName)),
            Require(context.ContractVersion, nameof(context.ContractVersion)),
            context.SymbolContextId,
            _privacyProfile,
            context.QueryHash,
            Require(context.Ordering, nameof(context.Ordering)),
            _capabilityPolicyIdentity);
    }

    private static QueryResultCursorException InvalidPosition() =>
        new(
            QueryResultCursorFailureKind.Invalid,
            "The query-result cursor position does not match the delivered inspect_trace page.");

    private static QueryResultCursorException InvalidTimelinePosition() =>
        new(
            QueryResultCursorFailureKind.Invalid,
            "The query-result cursor position does not match the delivered timeline page.");

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A non-empty cursor binding value is required.", name)
            : value;
}
