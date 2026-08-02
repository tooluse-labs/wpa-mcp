using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal readonly record struct CapabilityCursorBinding(
    string Principal,
    string CatalogVersion,
    string? Domain,
    string? Goal,
    string Ordering,
    string CapabilityPolicyIdentity = "full");

internal enum CapabilityCursorFailureKind
{
    Invalid,
    RegistryCapacity,
    EntropyFailure,
}

internal sealed class CapabilityCursorException : InvalidOperationException
{
    internal CapabilityCursorException(
        CapabilityCursorFailureKind kind,
        string message) : base(message)
    {
        Kind = kind;
        ToolFailureCaptureContext.Record(this);
    }

    internal CapabilityCursorFailureKind Kind { get; }
}

internal sealed class CapabilityCursorRegistry
{
    internal const string Prefix = "cpc_";
    internal const string PendingDeliveryToken = "cpc_00000000000000000000000000000000";
    private const int LocatorHexLength = 32;
    private const int TokenLength = 36;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<CapabilityCursorBinding, string> _rootContinuations = [];
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _idleTtl;
    private readonly TimeSpan _absoluteTtl;
    private readonly int _maxActive;

    internal CapabilityCursorRegistry(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? idleTtl = null,
        TimeSpan? absoluteTtl = null,
        int maxActive = 1_024)
    {
        _utcNow = utcNow ?? (static () => DateTimeOffset.UtcNow);
        _idleTtl = idleTtl ?? TimeSpan.FromMinutes(2);
        _absoluteTtl = absoluteTtl ?? TimeSpan.FromMinutes(15);
        if (_idleTtl <= TimeSpan.Zero || _absoluteTtl < _idleTtl)
            throw new ArgumentOutOfRangeException(nameof(idleTtl));
        if (maxActive <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxActive));
        _maxActive = maxActive;
    }

    internal int Redeem(
        string token,
        CapabilityCursorBinding expected)
    {
        if (!HasCanonicalShape(token))
            throw Invalid();
        lock (_gate)
        {
            var now = _utcNow();
            Prune(now);
            if (!_active.TryGetValue(token, out var entry) || entry.Binding != expected)
                throw Invalid();
            if (now - entry.LastAccessUtc > _idleTtl ||
                now - entry.IssuedUtc > _absoluteTtl)
            {
                _active.Remove(token);
                throw Invalid();
            }
            _active[token] = entry with { LastAccessUtc = now };
            return entry.NextIndex;
        }
    }

    internal string GetOrIssueContinuation(
        CapabilityCursorBinding binding,
        string? parentToken,
        int nextIndex)
    {
        if (nextIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(nextIndex));
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
                continuation.NextIndex == nextIndex)
            {
                _active[existing] = continuation with { LastAccessUtc = now };
                return existing;
            }

            if (_active.Count >= _maxActive)
            {
                throw new CapabilityCursorException(
                    CapabilityCursorFailureKind.RegistryCapacity,
                    "The capability cursor registry is at capacity.");
            }
            var token = Mint();
            _active.Add(token, new Entry(binding, nextIndex, now, now, null));
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
            var token = Prefix + Convert.ToHexString(
                RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            if (!_active.ContainsKey(token))
                return token;
        }
        throw new CapabilityCursorException(
            CapabilityCursorFailureKind.EntropyFailure,
            "The capability cursor registry could not mint a unique locator.");
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var pair in _active.ToArray())
        {
            if (now - pair.Value.LastAccessUtc > _idleTtl ||
                now - pair.Value.IssuedUtc > _absoluteTtl)
                _active.Remove(pair.Key);
        }
        foreach (var pair in _rootContinuations.ToArray())
        {
            if (!_active.ContainsKey(pair.Value))
                _rootContinuations.Remove(pair.Key);
        }
    }

    private static CapabilityCursorException Invalid() =>
        new(
            CapabilityCursorFailureKind.Invalid,
            "The capability cursor is invalid or no longer bound to this catalog and filter.");

    private sealed record Entry(
        CapabilityCursorBinding Binding,
        int NextIndex,
        DateTimeOffset IssuedUtc,
        DateTimeOffset LastAccessUtc,
        string? ContinuationToken);
}

/// <summary>Public DI seam; all catalog state and cursor bindings remain internal.</summary>
public sealed class CapabilityDiscoveryRuntime
{
    private const string Ordering = "domain_asc_capability_id_asc";
    private const int HardMaxPageDataBytes = 32_000;
    // Contract page identities must not depend on an instance's configured frame
    // budget. Resources and the Tools-only fallback share this immutable layout;
    // the production startup preflight rejects any cap that cannot deliver it.
    internal const int ToolContractPageUtf8Bytes = 8_192;
    internal const string ToolContractPageOrdering = "page_asc_start_utf8_byte_asc";
    internal const string ToolContractAssemblyRule =
        "Concatenate schemaFragment UTF-8 bytes in ascending page order without separators or normalization.";
    internal const string ToolContractHashRule =
        "Lowercase hexadecimal SHA-256 of the reassembled canonical UTF-8 bytes.";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly Regex FilterPattern = new(
        "^[a-z][a-z0-9_]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex Sha256Pattern = new(
        "^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex WorkflowKeyPattern = new(
        "^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly ActiveToolCatalog _catalog;
    private readonly CapabilityCursorRegistry _cursors;
    private readonly string _principal;
    private readonly IReadOnlyList<ServerCapabilityRecord> _capabilities;
    private readonly IReadOnlyList<CapabilityGoalRecord> _goals;
    private readonly IReadOnlyList<CapabilityWorkflowRecord> _workflows;
    private readonly IReadOnlyList<ServerToolCatalogRecord> _tools;
    private readonly IReadOnlyList<ServerToolResourceRecord> _toolResources;
    private readonly IReadOnlyDictionary<string, ToolOutputContract> _toolOutputContracts;
    private readonly string _canonicalContentHash;
    private readonly CapabilityPolicyRecord _capabilityPolicy;
    private readonly CapabilityPolicyResourceReference _capabilityPolicyResourceReference;
    private readonly RuntimeCompatibilityProfile _runtimeProfile;
    private readonly QueryResultCursorCoordinator _queryResults;
    private readonly int _maxPageDataBytes;
    private readonly int _maxResponseFrameBytes;
    private int _maximumPreflightResourceFrameBytes;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ListedCapabilityRecord>>> _capabilityResourcePages;
    private readonly IReadOnlyList<ResourcePage<string>> _capabilityPolicyResourcePages;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ListedToolResourceRecord>>> _toolResourcePages;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ServerToolSectionContractRecord>>> _toolSectionContractResourcePages;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ToolOutputContractPage>> _toolOutputContractResourcePages;

    internal ActiveToolCatalog Catalog => _catalog;
    internal QueryResultCursorCoordinator QueryResults => _queryResults;
    internal int MaximumPreflightResourceFrameBytes => _maximumPreflightResourceFrameBytes;

    internal CapabilityDiscoveryRuntime(
        ActiveToolCatalog catalog,
        StdioSessionPrincipal principal,
        CapabilityCursorRegistry? cursors = null,
        int maxResponseFrameBytes = ToolResponseBudgetOptions.DefaultMaxResponseFrameBytes,
        RuntimeCompatibilityProfile? runtimeProfile = null,
        QueryResultCursorRegistry? queryCursors = null,
        string privacyProfile = "off",
        string? capabilityPolicyIdentity = null)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _runtimeProfile = runtimeProfile ?? RuntimeCompatibilityPolicy.EvaluateCurrent();
        ArgumentNullException.ThrowIfNull(principal);
        _principal = principal.RegistryKey;
        _cursors = cursors ?? new CapabilityCursorRegistry();
        _capabilityPolicy = catalog.CapabilityPolicy.ToRecord();
        _capabilityPolicyResourceReference = new CapabilityPolicyResourceReference(
            _capabilityPolicy.ProfileName,
            _capabilityPolicy.ProfileIdentity,
            _capabilityPolicy.ProfileHash,
            _capabilityPolicy.Source,
            _capabilityPolicy.SelectionScope,
            _capabilityPolicy.DisabledCapabilityIds.Count,
            "wpa://capabilities/policy");
        if (capabilityPolicyIdentity is not null && !string.Equals(
                capabilityPolicyIdentity,
                catalog.CapabilityPolicy.ProfileHash,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Capability policy identity does not match the projected catalog.",
                nameof(capabilityPolicyIdentity));
        }
        _queryResults = new QueryResultCursorCoordinator(
            _principal,
            privacyProfile,
            queryCursors,
            catalog.CapabilityPolicy.ProfileHash);
        if (maxResponseFrameBytes is < ToolResponseBudgetOptions.MinimumResponseFrameBytes or
            > ToolResponseBudgetOptions.HardMaxResponseFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maxResponseFrameBytes));
        _maxResponseFrameBytes = maxResponseFrameBytes;
        // Keep the provisional domain page conservative before minting its cursor.
        // The final fitter mirrors the same finalized envelope into text and
        // structuredContent and must never trim rows after the continuation has
        // been bound to the delivered count.
        _maxPageDataBytes = Math.Min(
            HardMaxPageDataBytes,
            Math.Max(1_024, (maxResponseFrameBytes - 12_000) / 3));
        _capabilities = ProjectCapabilities(catalog);
        _goals = catalog.Goals.Select(goal => new CapabilityGoalRecord(
                goal.GoalId,
                goal.Title,
                goal.Summary,
                goal.WorkflowIds))
            .ToArray();
        var callableToolNames = catalog.Tools.Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        _workflows = catalog.Workflows.Select(workflow =>
            {
                var callable = workflow.ToolNames
                    .Where(callableToolNames.Contains)
                    .ToArray();
                var disabled = workflow.ToolNames
                    .Where(toolName => !callableToolNames.Contains(toolName))
                    .ToArray();
                if (callable.Length + disabled.Length != workflow.ToolNames.Length)
                {
                    throw new CatalogValidationException(
                        $"CAPABILITY-POLICY-CLOSURE: workflow '{workflow.WorkflowId}' tool buckets do not close");
                }
                return new CapabilityWorkflowRecord(
                    workflow.WorkflowId,
                    workflow.Title,
                    workflow.Summary,
                    workflow.GoalIds,
                    workflow.CapabilityIds,
                    workflow.ToolNames,
                    callable,
                    disabled,
                    workflow.CapabilityIds.Where(
                            catalog.CapabilityPolicy.IsDisabled)
                        .ToArray());
            })
            .ToArray();
        var outcomeContracts = new ReviewedToolOutcomeAdapterRegistry(catalog.AllTools);
        _tools = ProjectTools(catalog, outcomeContracts);
        _toolOutputContracts = catalog.Tools
            .Select(tool =>
            {
                if (!string.Equals(
                        tool.ToolName,
                        tool.OutputContract.ToolName,
                        StringComparison.Ordinal))
                {
                    throw new CatalogValidationException(
                        $"OUTPUT-CONTRACT-TOOL: '{tool.ToolName}' is bound to " +
                        $"'{tool.OutputContract.ToolName}'.");
                }
                ValidateToolOutputContract(tool.OutputContract);
                return tool.OutputContract;
            })
            .ToDictionary(contract => contract.ToolName, StringComparer.Ordinal);
        _toolResources = ProjectToolResources(_tools);
        _canonicalContentHash = ComputeCanonicalContentHash(
            catalog,
            _capabilities,
            _goals,
            _workflows,
            _tools);
        _capabilityPolicyResourcePages = BuildCapabilityPolicyResourcePages();
        _capabilityResourcePages = BuildCapabilityResourcePages();
        _toolResourcePages = BuildToolResourcePages();
        _toolSectionContractResourcePages = BuildToolSectionContractResourcePages();
        _toolOutputContractResourcePages = BuildToolOutputContractResourcePages();
        ValidateResourceSetFitsWireBudget();
    }

    internal ListCapabilitiesResponse List(
        string? domain,
        string? goal,
        string? cursor) => ListCore(domain, goal, cursor, deferCursorPublication: false);

    internal ListCapabilitiesResponse ListForDelivery(
        string? domain,
        string? goal,
        string? cursor) => ListCore(domain, goal, cursor, deferCursorPublication: true);

    private ListCapabilitiesResponse ListCore(
        string? domain,
        string? goal,
        string? cursor,
        bool deferCursorPublication)
    {
        var normalizedDomain = NormalizeFilter(domain, nameof(domain));
        var normalizedGoal = NormalizeFilter(goal, nameof(goal));
        var filtered = _capabilities.Where(capability =>
                (normalizedDomain is null ||
                 string.Equals(capability.Domain, normalizedDomain, StringComparison.Ordinal)) &&
                (normalizedGoal is null ||
                 capability.GoalIds.Contains(normalizedGoal, StringComparer.Ordinal)))
            .ToArray();
        var binding = new CapabilityCursorBinding(
            _principal,
            _catalog.CatalogVersion,
            normalizedDomain,
            normalizedGoal,
            Ordering,
            _catalog.CapabilityPolicy.ProfileHash);
        var startIndex = cursor is null ? 0 : _cursors.Redeem(cursor, binding);
        if (startIndex < 0 || startIndex > filtered.Length ||
            (startIndex == filtered.Length && filtered.Length != 0))
            throw new CapabilityCursorException(
                CapabilityCursorFailureKind.Invalid,
                "The capability cursor position is invalid.");

        if (filtered.Length == 0)
        {
            return Response(
                normalizedDomain,
                normalizedGoal,
                filtered.Length,
                [],
                [],
                [],
                hasMore: false,
                nextCursor: null,
                noDataReason: "no_capabilities_match_filter");
        }

        var page = new List<ServerCapabilityRecord>();
        for (var index = startIndex; index < filtered.Length; index++)
        {
            page.Add(filtered[index]);
            var candidate = Response(
                normalizedDomain,
                normalizedGoal,
                filtered.Length,
                page,
                [],
                [],
                hasMore: index + 1 < filtered.Length,
                nextCursor: index + 1 < filtered.Length
                    ? CapabilityCursorRegistry.Prefix + new string('0', 32)
                    : null,
                noDataReason: null);
            if (page.Count > 1 &&
                JsonSerializer.SerializeToUtf8Bytes(
                    candidate,
                    McpJsonUtilities.DefaultOptions).Length > _maxPageDataBytes)
            {
                page.RemoveAt(page.Count - 1);
                break;
            }
        }

        var nextIndex = checked(startIndex + page.Count);
        var hasMore = nextIndex < filtered.Length;
        var nextCursor = hasMore
            ? deferCursorPublication
                ? CapabilityCursorRegistry.PendingDeliveryToken
                : _cursors.GetOrIssueContinuation(binding, cursor, nextIndex)
            : null;
        return Response(
            normalizedDomain,
            normalizedGoal,
            filtered.Length,
            page,
            [],
            [],
            hasMore,
            nextCursor,
            noDataReason: null);
    }

    internal ListCapabilitiesResponse FullSnapshot() =>
        Response(
            domain: null,
            goal: null,
            _capabilities.Count,
            _capabilities,
            _goals,
            _workflows,
            hasMore: false,
            nextCursor: null,
            noDataReason: null);

    /// <summary>
    /// Mints a continuation for the capability rows actually retained by final
    /// JSON-RPC frame fitting. The source cursor is not mutated, so retries with
    /// different request-id sizes cannot change an already-issued cursor.
    /// </summary>
    internal string? FinalizePageContinuation(
        string? domain,
        string? goal,
        string? sourceCursor,
        int retainedCapabilityCount)
    {
        if (retainedCapabilityCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(retainedCapabilityCount));
        var normalizedDomain = NormalizeFilter(domain, nameof(domain));
        var normalizedGoal = NormalizeFilter(goal, nameof(goal));
        var filteredCount = _capabilities.Count(capability =>
            (normalizedDomain is null ||
             string.Equals(capability.Domain, normalizedDomain, StringComparison.Ordinal)) &&
            (normalizedGoal is null ||
             capability.GoalIds.Contains(normalizedGoal, StringComparer.Ordinal)));
        var binding = new CapabilityCursorBinding(
            _principal,
            _catalog.CatalogVersion,
            normalizedDomain,
            normalizedGoal,
            Ordering,
            _catalog.CapabilityPolicy.ProfileHash);
        var startIndex = sourceCursor is null
            ? 0
            : _cursors.Redeem(sourceCursor, binding);
        var nextIndex = checked(startIndex + retainedCapabilityCount);
        if (nextIndex > filteredCount)
        {
            throw new CapabilityCursorException(
                CapabilityCursorFailureKind.Invalid,
                "The retained capability count exceeds the cursor-bound result set.");
        }
        return nextIndex < filteredCount
            ? _cursors.GetOrIssueContinuation(binding, sourceCursor, nextIndex)
            : null;
    }

    internal ServerToolCatalogResource ToolCatalogSnapshot() => new(
        _catalog.CatalogScope,
        _catalog.ExhaustiveForWpa,
        _catalog.UnlistedCapabilityMeaning,
        _catalog.CatalogVersion,
        _canonicalContentHash,
        _capabilityPolicyResourceReference,
        _tools);

    internal CatalogResourceIndexRecord CapabilityResourceIndex() => new(
        _catalog.CatalogScope,
        _catalog.ExhaustiveForWpa,
        _catalog.UnlistedCapabilityMeaning,
        _catalog.CatalogVersion,
        _canonicalContentHash,
        _capabilityPolicyResourceReference,
        "capabilities",
        _capabilities.Count,
        "domain",
        Ordering,
        _capabilities.GroupBy(capability => capability.Domain, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CatalogResourceShardRecord(
                group.Key,
                $"wpa://capabilities/domain/{group.Key}",
                group.Count()))
            .ToArray());

    internal CatalogResourceIndexRecord ToolResourceIndex() => new(
        _catalog.CatalogScope,
        _catalog.ExhaustiveForWpa,
        _catalog.UnlistedCapabilityMeaning,
        _catalog.CatalogVersion,
        _canonicalContentHash,
        _capabilityPolicyResourceReference,
        "tools",
        _tools.Count,
        "domain",
        "discovery_priority_asc_domain_asc_ordinal_asc_tool_name_asc",
        _tools.GroupBy(tool => tool.Domain, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CatalogResourceShardRecord(
                group.Key,
                $"wpa://tools/domain/{group.Key}",
                group.Count()))
            .ToArray());

    internal ListCapabilitiesResponse CapabilityDomainSnapshot(string domain)
    {
        var normalized = NormalizeFilter(domain, nameof(domain))
            ?? throw new ArgumentException("A capability resource domain is required.", nameof(domain));
        var capabilities = _capabilities.Where(capability => string.Equals(
                capability.Domain,
                normalized,
                StringComparison.Ordinal))
            .ToArray();
        var goalIds = capabilities.SelectMany(capability => capability.GoalIds)
            .ToHashSet(StringComparer.Ordinal);
        var workflowIds = capabilities.SelectMany(capability => capability.WorkflowIds)
            .ToHashSet(StringComparer.Ordinal);
        return Response(
            normalized,
            goal: null,
            capabilities.Length,
            capabilities,
            _goals.Where(goal => goalIds.Contains(goal.GoalId)).ToArray(),
            _workflows.Where(workflow => workflowIds.Contains(workflow.WorkflowId)).ToArray(),
            hasMore: false,
            nextCursor: null,
            noDataReason: capabilities.Length == 0
                ? "no_capabilities_match_filter"
                : null);
    }

    internal ServerToolCatalogShardResource ToolDomainSnapshot(string domain)
    {
        var normalized = NormalizeFilter(domain, nameof(domain))
            ?? throw new ArgumentException("A tool resource domain is required.", nameof(domain));
        var tools = _tools.Where(tool => string.Equals(
                tool.Domain,
                normalized,
                StringComparison.Ordinal))
            .ToArray();
        return new ServerToolCatalogShardResource(
            _catalog.CatalogScope,
            _catalog.ExhaustiveForWpa,
            _catalog.UnlistedCapabilityMeaning,
            _catalog.CatalogVersion,
            _canonicalContentHash,
            _capabilityPolicyResourceReference,
            normalized,
            _tools.Count,
            tools.Length,
            tools,
            tools.Length == 0 ? "no_tools_match_filter" : null);
    }

    internal CapabilityWorkflowCatalogResource WorkflowCatalogSnapshot() => new(
        _catalog.CatalogScope,
        _catalog.ExhaustiveForWpa,
        _catalog.UnlistedCapabilityMeaning,
        _catalog.CatalogVersion,
        _canonicalContentHash,
        _capabilityPolicyResourceReference,
        _goals,
        _workflows);

    internal CatalogResourceIndexRecord WorkflowResourceIndex() => new(
        _catalog.CatalogScope,
        _catalog.ExhaustiveForWpa,
        _catalog.UnlistedCapabilityMeaning,
        _catalog.CatalogVersion,
        _canonicalContentHash,
        _capabilityPolicyResourceReference,
        "workflows",
        _workflows.Count,
        "workflow_id",
        "workflow_id_asc",
        _workflows.OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .Select(workflow => new CatalogResourceShardRecord(
                workflow.WorkflowId,
                $"wpa://workflows/{workflow.WorkflowId}",
                1))
            .ToArray());

    internal TextResourceContents CapabilityIndexResource() => CreateResource(
        "wpa://capabilities/server",
        CapabilityResourceIndex());

    internal TextResourceContents RuntimeProfileResource() => CreateResource(
        "wpa://runtime/profile",
        _runtimeProfile.ToResourceRecord());

    internal CapabilityPolicyResourceIndex CapabilityPolicyResourceIndex() => new(
        _capabilityPolicy.ProfileName,
        _capabilityPolicy.ProfileIdentity,
        _capabilityPolicy.ProfileHash,
        _capabilityPolicy.Source,
        _capabilityPolicy.SelectionScope,
        _capabilityPolicy.DisabledCapabilityIds.Count,
        "capability_id_asc",
        _capabilityPolicy.DisabledCapabilityIds.Count == 0
            ? "complete_empty"
            : "complete_in_listed_pages",
        _capabilityPolicyResourcePages.Select(page => new CatalogResourcePageRecord(
                page.Number,
                page.Uri,
                page.Items.Count))
            .ToArray());

    internal TextResourceContents CapabilityPolicyIndexResource() => CreateResource(
        "wpa://capabilities/policy",
        CapabilityPolicyResourceIndex());

    internal TextResourceContents CapabilityPolicyPageResource(int page)
    {
        var selected = GetPage(_capabilityPolicyResourcePages, page, "capability policy page");
        return CreateResource(
            selected.Uri,
            new CapabilityPolicyResourcePage(
                _capabilityPolicy.ProfileIdentity,
                _capabilityPolicy.ProfileHash,
                selected.Number,
                _capabilityPolicy.DisabledCapabilityIds.Count,
                selected.Items.Count,
                "capability_id_asc",
                "complete_page",
                selected.Items));
    }

    internal TextResourceContents CapabilityDomainResource(string domain)
    {
        var normalized = RequireResourceKey(domain, nameof(domain));
        var pages = GetPages(_capabilityResourcePages, normalized, "capability domain");
        return CreateResource(
            $"wpa://capabilities/domain/{normalized}",
            PageIndex("capabilities", normalized, pages));
    }

    internal TextResourceContents CapabilityDomainPageResource(string domain, int page)
    {
        var normalized = RequireResourceKey(domain, nameof(domain));
        var pages = GetPages(_capabilityResourcePages, normalized, "capability domain");
        var selected = GetPage(pages, page, "capability domain page");
        return CreateResource(
            selected.Uri,
            new ServerCapabilityCatalogShardResource(
                _catalog.CatalogScope,
                _catalog.ExhaustiveForWpa,
                _catalog.UnlistedCapabilityMeaning,
                _catalog.CatalogVersion,
                _canonicalContentHash,
                _capabilityPolicyResourceReference,
                normalized,
                selected.Number,
                pages.Sum(candidate => candidate.Items.Count),
                selected.Items.Count,
                selected.Items));
    }

    internal TextResourceContents CapabilityDetailResource(string capabilityId)
    {
        var normalized = RequireCapabilityKey(capabilityId, nameof(capabilityId));
        var capability = _capabilities.SingleOrDefault(candidate => string.Equals(
                candidate.CapabilityId,
                normalized,
                StringComparison.Ordinal))
            ?? throw new ArgumentException(
                "The capability resource key is not declared.",
                nameof(capabilityId));
        return CreateResource($"wpa://capabilities/detail/{normalized}", capability);
    }

    internal TextResourceContents ToolIndexResource() => CreateResource(
        "wpa://tools/server",
        ToolResourceIndex());

    internal TextResourceContents ToolDomainResource(string domain)
    {
        var normalized = RequireResourceKey(domain, nameof(domain));
        var pages = GetPages(_toolResourcePages, normalized, "tool domain");
        return CreateResource(
            $"wpa://tools/domain/{normalized}",
            PageIndex("tools", normalized, pages));
    }

    internal TextResourceContents ToolDomainPageResource(string domain, int page)
    {
        var normalized = RequireResourceKey(domain, nameof(domain));
        var pages = GetPages(_toolResourcePages, normalized, "tool domain");
        var selected = GetPage(pages, page, "tool domain page");
        return CreateResource(
            selected.Uri,
            new ServerToolResourceShardResource(
                _catalog.CatalogScope,
                _catalog.ExhaustiveForWpa,
                _catalog.UnlistedCapabilityMeaning,
                _catalog.CatalogVersion,
                _canonicalContentHash,
                _capabilityPolicyResourceReference,
                normalized,
                selected.Number,
                pages.Sum(candidate => candidate.Items.Count),
                selected.Items.Count,
                selected.Items));
    }

    internal TextResourceContents ToolDetailResource(string toolName)
    {
        var normalized = RequireResourceKey(toolName, nameof(toolName));
        var tool = _toolResources.SingleOrDefault(candidate => string.Equals(
                candidate.ToolName,
                normalized,
                StringComparison.Ordinal))
            ?? throw new ArgumentException("The tool resource key is not declared.", nameof(toolName));
        return CreateResource($"wpa://tools/detail/{normalized}", tool);
    }

    internal TextResourceContents ToolOutputContractIndexResource(
        string toolName,
        string sha256)
    {
        var contract = GetToolOutputContract(toolName, sha256);
        var pages = _toolOutputContractResourcePages[contract.ToolName];
        return CreateResource(
            contract.SchemaUri,
            new ToolOutputContractResourceIndex(
                contract.ToolName,
                contract.ContractVersion,
                contract.SchemaUri,
                contract.Sha256,
                contract.MediaType,
                contract.Utf8Bytes,
                pages.Count,
                $"{contract.SchemaUri}/pages/{{page}}",
                ToolContractPageOrdering,
                ToolContractAssemblyRule,
                ToolContractHashRule));
    }

    internal TextResourceContents ToolOutputContractPageResource(
        string toolName,
        string sha256,
        int page)
    {
        var contract = GetToolOutputContract(toolName, sha256);
        var pages = _toolOutputContractResourcePages[contract.ToolName];
        var selected = GetToolOutputContractPage(pages, page);
        return CreateResource(
            selected.Uri,
            new ToolOutputContractResourcePage(
                contract.ToolName,
                contract.Sha256,
                selected.Number,
                pages.Count,
                selected.StartUtf8Byte,
                selected.ReturnedUtf8Bytes,
                selected.SchemaFragment));
    }

    internal ToolContractPageResponse ToolContractPage(string toolName, int page)
    {
        var contract = GetToolOutputContract(toolName);
        var pages = _toolOutputContractResourcePages[contract.ToolName];
        var selected = GetToolOutputContractPage(pages, page);
        return new ToolContractPageResponse(
            contract.ToolName,
            contract.ContractVersion,
            contract.SchemaUri,
            contract.Sha256,
            contract.MediaType,
            contract.Utf8Bytes,
            page,
            pages.Count,
            selected.StartUtf8Byte,
            selected.ReturnedUtf8Bytes,
            selected.SchemaFragment,
            page < pages.Count ? page + 1 : null);
    }

    internal CatalogResourcePageIndexRecord ToolSectionContractPageIndex(string toolName)
    {
        var normalized = RequireResourceKey(toolName, nameof(toolName));
        if (!_tools.Any(tool => string.Equals(tool.ToolName, normalized, StringComparison.Ordinal)))
            throw new ArgumentException($"The tool '{normalized}' is not declared.", nameof(toolName));
        var pages = GetPages(
            _toolSectionContractResourcePages,
            normalized,
            "tool section contract");
        return new CatalogResourcePageIndexRecord(
            _catalog.CatalogScope,
            _catalog.ExhaustiveForWpa,
            _catalog.UnlistedCapabilityMeaning,
            _catalog.CatalogVersion,
            _canonicalContentHash,
            _capabilityPolicyResourceReference,
            "tool_section_contracts",
            normalized,
            pages.Sum(page => page.Items.Count),
            "section_pointer_asc",
            pages.Select(page => new CatalogResourcePageRecord(
                    page.Number,
                    page.Uri,
                    page.Items.Count))
                .ToArray());
    }

    internal TextResourceContents ToolSectionContractIndexResource(string toolName)
    {
        var normalized = RequireResourceKey(toolName, nameof(toolName));
        return CreateResource(
            $"wpa://tools/{normalized}/sections",
            ToolSectionContractPageIndex(normalized));
    }

    internal TextResourceContents ToolSectionContractPageResource(string toolName, int page)
    {
        var normalized = RequireResourceKey(toolName, nameof(toolName));
        var pages = GetPages(
            _toolSectionContractResourcePages,
            normalized,
            "tool section contract");
        var selected = GetPage(pages, page, "tool section contract page");
        return CreateResource(
            selected.Uri,
            new ServerToolSectionContractPageResource(
                _catalog.CatalogScope,
                _catalog.ExhaustiveForWpa,
                _catalog.UnlistedCapabilityMeaning,
                _catalog.CatalogVersion,
                _canonicalContentHash,
                _capabilityPolicyResourceReference,
                normalized,
                selected.Number,
                pages.Sum(candidate => candidate.Items.Count),
                selected.Items.Count,
                "section_pointer_asc",
                selected.Items));
    }

    internal TextResourceContents WorkflowResource() => CreateResource(
        "wpa://workflows/server",
        WorkflowResourceIndex());

    internal TextResourceContents WorkflowShardResource(string workflowId)
    {
        var normalized = RequireWorkflowKey(workflowId, nameof(workflowId));
        var workflow = _workflows.SingleOrDefault(candidate => string.Equals(
                candidate.WorkflowId,
                normalized,
                StringComparison.Ordinal))
            ?? throw new ArgumentException("The workflow resource key is not declared.", nameof(workflowId));
        var goalIds = workflow.GoalIds.ToHashSet(StringComparer.Ordinal);
        return CreateResource(
            $"wpa://workflows/{normalized}",
            new CapabilityWorkflowCatalogShardResource(
                _catalog.CatalogScope,
                _catalog.ExhaustiveForWpa,
                _catalog.UnlistedCapabilityMeaning,
                _catalog.CatalogVersion,
                _canonicalContentHash,
                _capabilityPolicyResourceReference,
                normalized,
                _goals.Where(goal => goalIds.Contains(goal.GoalId)).ToArray(),
                workflow));
    }

    private TextResourceContents CreateResource<T>(string uri, T value)
    {
        var content = CreateResourceContent(uri, value);
        var frameBytes = MeasureReadResourceFrame(content);
        _maximumPreflightResourceFrameBytes = Math.Max(
            _maximumPreflightResourceFrameBytes,
            frameBytes);
        if (frameBytes > _maxResponseFrameBytes)
        {
            throw new CatalogValidationException(
                $"RESOURCE-WIRE-BUDGET: '{uri}' requires {frameBytes} bytes but the configured " +
                $"resources/read frame budget is {_maxResponseFrameBytes} bytes.");
        }
        return content;
    }

    internal static TextResourceContents CreateResourceContent<T>(string uri, T value) => new()
    {
        Uri = uri,
        MimeType = "application/json",
        Text = JsonSerializer.Serialize(value, McpJsonUtilities.DefaultOptions),
    };

    internal static int MeasureReadResourceFrame(TextResourceContents content)
    {
        // Request ingress permits at most 128 serialized UTF-8 bytes for the id.
        // A 126-byte ASCII string plus its JSON quotes exercises that exact bound.
        var worstCaseRequestId = new RequestId(new string('r', 126));
        if (ToolRequestIdPolicy.SerializedBytes(worstCaseRequestId) !=
            ToolRequestIdPolicy.MaxSerializedBytes)
        {
            throw new InvalidOperationException(
                "The resources/read request-id measurement no longer matches ingress policy.");
        }
        return JsonSerializer.SerializeToUtf8Bytes(
            new JsonRpcResponse
            {
                Id = worstCaseRequestId,
                Result = JsonSerializer.SerializeToNode(
                    new ReadResourceResult { Contents = [content] },
                    McpJsonUtilities.DefaultOptions),
            },
            McpJsonUtilities.DefaultOptions).Length + 1;
    }

    private void ValidateResourceSetFitsWireBudget()
    {
        _ = RuntimeProfileResource();
        _ = CapabilityPolicyIndexResource();
        foreach (var page in _capabilityPolicyResourcePages)
            _ = CapabilityPolicyPageResource(page.Number);
        _ = CapabilityIndexResource();
        foreach (var domain in CapabilityResourceIndex().Shards.Select(shard => shard.Key))
        {
            _ = CapabilityDomainResource(domain);
            foreach (var page in _capabilityResourcePages[domain])
                _ = CapabilityDomainPageResource(domain, page.Number);
        }
        foreach (var capability in _capabilities)
            _ = CapabilityDetailResource(capability.CapabilityId);
        _ = ToolIndexResource();
        foreach (var domain in ToolResourceIndex().Shards.Select(shard => shard.Key))
        {
            _ = ToolDomainResource(domain);
            foreach (var page in _toolResourcePages[domain])
                _ = ToolDomainPageResource(domain, page.Number);
        }
        foreach (var tool in _toolResources)
            _ = ToolDetailResource(tool.ToolName);
        // Output-contract Resources use the same immutable pages as the
        // get_tool_contract fallback. Production validates both projections
        // together in ToolContractDiscoveryPreflight; embedded runtimes may use
        // a smaller budget when they do not expose contract discovery. An
        // attempted Resource read still enforces this instance's wire budget in
        // CreateResource.
        foreach (var tool in _tools)
        {
            _ = ToolSectionContractIndexResource(tool.ToolName);
            foreach (var page in _toolSectionContractResourcePages[tool.ToolName])
                _ = ToolSectionContractPageResource(tool.ToolName, page.Number);
        }
        _ = WorkflowResource();
        foreach (var workflow in _workflows)
            _ = WorkflowShardResource(workflow.WorkflowId);
    }

    private IReadOnlyList<ResourcePage<string>> BuildCapabilityPolicyResourcePages() =>
        PackResourcePages(
            _capabilityPolicy.DisabledCapabilityIds,
            page => $"wpa://capabilities/policy/{page}",
            (page, items) => new CapabilityPolicyResourcePage(
                _capabilityPolicy.ProfileIdentity,
                _capabilityPolicy.ProfileHash,
                page,
                _capabilityPolicy.DisabledCapabilityIds.Count,
                items.Count,
                "capability_id_asc",
                "complete_page",
                items),
            "disabled capability ID");

    private IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ListedCapabilityRecord>>> BuildCapabilityResourcePages() =>
        _capabilities.Select(ProjectListedCapability)
            .GroupBy(capability => capability.Domain, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => PackResourcePages(
                    group.OrderBy(capability => capability.CapabilityId, StringComparer.Ordinal).ToArray(),
                    page => $"wpa://capabilities/domain/{group.Key}/{page}",
                    (page, items) => new ServerCapabilityCatalogShardResource(
                        _catalog.CatalogScope,
                        _catalog.ExhaustiveForWpa,
                        _catalog.UnlistedCapabilityMeaning,
                        _catalog.CatalogVersion,
                        _canonicalContentHash,
                        _capabilityPolicyResourceReference,
                        group.Key,
                        page,
                        group.Count(),
                        items.Count,
                        items),
                    "capability"),
                StringComparer.Ordinal);

    private IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ListedToolResourceRecord>>> BuildToolResourcePages()
    {
        var resourceTools = _toolResources.Select(tool => new ListedToolResourceRecord(
                tool.ToolName,
                tool.Domain,
                tool.AvailabilityState,
                tool.Callable,
                tool.CapabilityIds,
                tool.RequiredCapabilities,
                tool.SelectableScopes,
                tool.SelectableScopesSemantics,
                tool.CostClass,
                tool.DiscoveryPriority,
                tool.Ordinal,
                tool.SectionContractsResourceUri,
                $"wpa://tools/detail/{tool.ToolName}"))
            .ToArray();
        return resourceTools.GroupBy(tool => tool.Domain, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => PackResourcePages(
                    group.OrderBy(tool => tool.DiscoveryPriority)
                        .ThenBy(tool => tool.Ordinal)
                        .ThenBy(tool => tool.ToolName, StringComparer.Ordinal)
                        .ToArray(),
                    page => $"wpa://tools/domain/{group.Key}/{page}",
                    (page, items) => new ServerToolResourceShardResource(
                        _catalog.CatalogScope,
                        _catalog.ExhaustiveForWpa,
                        _catalog.UnlistedCapabilityMeaning,
                        _catalog.CatalogVersion,
                        _canonicalContentHash,
                        _capabilityPolicyResourceReference,
                        group.Key,
                        page,
                        group.Count(),
                        items.Count,
                        items),
                    "tool"),
                StringComparer.Ordinal);
    }

    private IReadOnlyList<ServerToolResourceRecord> ProjectToolResources(
        IReadOnlyList<ServerToolCatalogRecord> tools) =>
        tools.Select(tool =>
        {
            _toolOutputContracts.TryGetValue(tool.ToolName, out var outputContract);
            return new ServerToolResourceRecord(
                tool.ToolName,
                tool.AvailabilityState,
                tool.Callable,
                tool.CapabilityIds,
                tool.RequiredCapabilities,
                tool.SelectableScopes,
                CatalogScopeSemantics.ToolSelectableScopes,
                tool.Annotations,
                tool.SideEffects,
                tool.CostClass,
                tool.DiscoveryPriority,
                tool.Domain,
                tool.Ordinal,
                tool.DefaultOrdering,
                tool.TieBreakers,
                tool.PageableSections,
                tool.PaginationMode,
                tool.Deprecation,
                tool.AllowedMeasurementBases,
                tool.MaximumRelationship,
                tool.DoesNotProve,
                $"wpa://tools/{tool.ToolName}/sections",
                tool.PlannerAdmission,
                outputContract is null
                    ? "unavailable_by_policy"
                    : "linked_content_addressed_resource",
                outputContract?.SchemaUri,
                outputContract?.Sha256,
                outputContract?.Utf8Bytes,
                outputContract?.MediaType);
        })
        .ToArray();

    private IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<ServerToolSectionContractRecord>>>
        BuildToolSectionContractResourcePages() =>
        _tools.ToDictionary(
            tool => tool.ToolName,
            tool => PackResourcePages(
                tool.SectionContracts
                    .OrderBy(section => section.SectionPointer, StringComparer.Ordinal)
                    .ToArray(),
                page => $"wpa://tools/{tool.ToolName}/sections/{page}",
                (page, items) => new ServerToolSectionContractPageResource(
                    _catalog.CatalogScope,
                    _catalog.ExhaustiveForWpa,
                    _catalog.UnlistedCapabilityMeaning,
                    _catalog.CatalogVersion,
                    _canonicalContentHash,
                    _capabilityPolicyResourceReference,
                    tool.ToolName,
                    page,
                    tool.SectionContracts.Count,
                    items.Count,
                    "section_pointer_asc",
                    items),
                "tool section contract"),
            StringComparer.Ordinal);

    private IReadOnlyDictionary<string, IReadOnlyList<ToolOutputContractPage>>
        BuildToolOutputContractResourcePages() =>
        _toolOutputContracts.Values.ToDictionary(
            contract => contract.ToolName,
            contract => BuildToolOutputContractPages(
                contract,
                ToolContractPageUtf8Bytes),
            StringComparer.Ordinal);

    internal static IReadOnlyList<ToolOutputContractPage> BuildToolOutputContractPages(
        ToolOutputContract contract,
        int maximumPageUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (maximumPageUtf8Bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumPageUtf8Bytes));
        var bytes = StrictUtf8.GetBytes(contract.CanonicalJson);
        var pages = new List<ToolOutputContractPage>();
        var start = 0;
        while (start < bytes.Length)
        {
            var fragment = SliceCanonicalUtf8(bytes, start, maximumPageUtf8Bytes);
            var page = pages.Count + 1;
            pages.Add(new ToolOutputContractPage(
                page,
                $"{contract.SchemaUri}/pages/{page}",
                start,
                fragment.Utf8Bytes,
                fragment.Text));
            start = checked(start + fragment.Utf8Bytes);
        }

        if (pages.Count == 0)
        {
            throw new CatalogValidationException(
                $"OUTPUT-CONTRACT-EMPTY: '{contract.ToolName}' has no canonical schema bytes.");
        }
        return pages;
    }

    private ToolOutputContract GetToolOutputContract(string toolName)
    {
        var canonical = RequireExactToolContractKey(toolName, nameof(toolName));
        return _toolOutputContracts.TryGetValue(canonical, out var contract)
            ? contract
            : throw new ArgumentException(
                "The tool output contract is not active in this runtime profile.",
                nameof(toolName));
    }

    private ToolOutputContract GetToolOutputContract(string toolName, string sha256)
    {
        var contract = GetToolOutputContract(toolName);
        var normalizedHash = RequireSha256(sha256, nameof(sha256));
        return string.Equals(contract.Sha256, normalizedHash, StringComparison.Ordinal)
            ? contract
            : throw new ArgumentException(
                "The content-addressed tool output contract hash is not active.",
                nameof(sha256));
    }

    private static ToolOutputContractPage GetToolOutputContractPage(
        IReadOnlyList<ToolOutputContractPage> pages,
        int page) =>
        pages.SingleOrDefault(candidate => candidate.Number == page)
        ?? throw new ArgumentOutOfRangeException(
            nameof(page),
            "The tool output contract resource page is not declared.");

    private static CanonicalUtf8Fragment SliceCanonicalUtf8(
        byte[] canonicalUtf8,
        int start,
        int maximumBytes)
    {
        if (start < 0 || start >= canonicalUtf8.Length)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (maximumBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        var end = Math.Min(canonicalUtf8.Length, checked(start + maximumBytes));
        while (end < canonicalUtf8.Length &&
               (canonicalUtf8[end] & 0b1100_0000) == 0b1000_0000)
        {
            end--;
        }
        if (end <= start)
        {
            throw new CatalogValidationException(
                "OUTPUT-CONTRACT-PAGE: the configured page budget cannot contain one UTF-8 scalar.");
        }

        var length = end - start;
        return new CanonicalUtf8Fragment(
            StrictUtf8.GetString(canonicalUtf8, start, length),
            length);
    }

    private static void ValidateToolOutputContract(ToolOutputContract contract)
    {
        if (!Sha256Pattern.IsMatch(contract.Sha256))
        {
            throw new CatalogValidationException(
                $"OUTPUT-CONTRACT-HASH: '{contract.ToolName}' has a malformed SHA-256.");
        }
        var utf8 = StrictUtf8.GetBytes(contract.CanonicalJson);
        if (utf8.Length != contract.Utf8Bytes)
        {
            throw new CatalogValidationException(
                $"OUTPUT-CONTRACT-LENGTH: '{contract.ToolName}' declares {contract.Utf8Bytes} " +
                $"bytes but materializes {utf8.Length} bytes.");
        }
        var actualHash = Convert.ToHexString(SHA256.HashData(utf8)).ToLowerInvariant();
        if (!string.Equals(actualHash, contract.Sha256, StringComparison.Ordinal))
        {
            throw new CatalogValidationException(
                $"OUTPUT-CONTRACT-HASH: '{contract.ToolName}' canonical bytes do not match " +
                "the declared SHA-256.");
        }
        var expectedUri = $"wpa://contracts/tools/{contract.ToolName}/{contract.Sha256}";
        if (!string.Equals(expectedUri, contract.SchemaUri, StringComparison.Ordinal))
        {
            throw new CatalogValidationException(
                $"OUTPUT-CONTRACT-URI: '{contract.ToolName}' must use its content-addressed URI.");
        }
    }

    private IReadOnlyList<ResourcePage<T>> PackResourcePages<T, TResource>(
        IReadOnlyList<T> items,
        Func<int, string> uri,
        Func<int, IReadOnlyList<T>, TResource> resource,
        string resourceKind)
    {
        var pages = new List<ResourcePage<T>>();
        var current = new List<T>();
        var pageNumber = 1;
        foreach (var item in items)
        {
            current.Add(item);
            var candidateUri = uri(pageNumber);
            var candidateContent = CreateResourceContent(
                candidateUri,
                resource(pageNumber, current));
            var candidateFrameBytes = MeasureReadResourceFrame(candidateContent);
            if (candidateFrameBytes <= _maxResponseFrameBytes)
                continue;

            current.RemoveAt(current.Count - 1);
            if (current.Count == 0)
            {
                throw new CatalogValidationException(
                    $"RESOURCE-WIRE-BUDGET: one {resourceKind} record cannot fit the configured " +
                    $"{_maxResponseFrameBytes}-byte resources/read frame budget at '{candidateUri}' " +
                    $"(measured {candidateFrameBytes} bytes).");
            }
            pages.Add(new ResourcePage<T>(pageNumber, candidateUri, current.ToArray()));
            pageNumber++;
            current = [item];
            candidateUri = uri(pageNumber);
            candidateContent = CreateResourceContent(
                candidateUri,
                resource(pageNumber, current));
            candidateFrameBytes = MeasureReadResourceFrame(candidateContent);
            if (candidateFrameBytes > _maxResponseFrameBytes)
            {
                throw new CatalogValidationException(
                    $"RESOURCE-WIRE-BUDGET: one {resourceKind} record cannot fit the configured " +
                    $"{_maxResponseFrameBytes}-byte resources/read frame budget at '{candidateUri}' " +
                    $"(measured {candidateFrameBytes} bytes).");
            }
        }
        if (current.Count > 0)
            pages.Add(new ResourcePage<T>(pageNumber, uri(pageNumber), current.ToArray()));
        return pages;
    }

    private CatalogResourcePageIndexRecord PageIndex<T>(
        string resourceKind,
        string key,
        IReadOnlyList<ResourcePage<T>> pages) => new(
            _catalog.CatalogScope,
            _catalog.ExhaustiveForWpa,
            _catalog.UnlistedCapabilityMeaning,
            _catalog.CatalogVersion,
            _canonicalContentHash,
            _capabilityPolicyResourceReference,
            resourceKind,
            key,
            pages.Sum(page => page.Items.Count),
            resourceKind == "capabilities"
                ? Ordering
                : "discovery_priority_asc_ordinal_asc_tool_name_asc",
            pages.Select(page => new CatalogResourcePageRecord(
                    page.Number,
                    page.Uri,
                    page.Items.Count))
                .ToArray());

    private static IReadOnlyList<ResourcePage<T>> GetPages<T>(
        IReadOnlyDictionary<string, IReadOnlyList<ResourcePage<T>>> source,
        string key,
        string resourceKind) =>
        source.TryGetValue(key, out var pages)
            ? pages
            : throw new ArgumentException($"The {resourceKind} '{key}' is not declared.");

    private static ResourcePage<T> GetPage<T>(
        IReadOnlyList<ResourcePage<T>> pages,
        int page,
        string resourceKind) =>
        pages.SingleOrDefault(candidate => candidate.Number == page)
            ?? throw new ArgumentOutOfRangeException(nameof(page), $"The {resourceKind} is not declared.");

    private static string RequireResourceKey(string value, string parameterName) =>
        NormalizeFilter(value, parameterName)
        ?? throw new ArgumentException("A resource key is required.", parameterName);

    private static string RequireSha256(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A SHA-256 resource key is required.", parameterName);
        return Sha256Pattern.IsMatch(value)
            ? value
            : throw new ArgumentException("The SHA-256 resource key is malformed.", parameterName);
    }

    private static string RequireExactToolContractKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A tool output contract key is required.", parameterName);
        }
        // The active ordinal dictionary is the authority for exact MCP tool-name
        // identity. Do not impose the narrower capability-filter grammar here.
        return value;
    }

    private static string RequireCapabilityKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A capability resource key is required.", parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        return WorkflowKeyPattern.IsMatch(normalized)
            ? normalized
            : throw new ArgumentException("The capability resource key is malformed.", parameterName);
    }

    private static string RequireWorkflowKey(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A workflow resource key is required.", parameterName);
        var normalized = value.Trim().ToLowerInvariant();
        return WorkflowKeyPattern.IsMatch(normalized)
            ? normalized
            : throw new ArgumentException("The workflow resource key is malformed.", parameterName);
    }

    private sealed record ResourcePage<T>(
        int Number,
        string Uri,
        IReadOnlyList<T> Items);

    internal sealed record ToolOutputContractPage(
        int Number,
        string Uri,
        int StartUtf8Byte,
        int ReturnedUtf8Bytes,
        string SchemaFragment);

    private readonly record struct CanonicalUtf8Fragment(
        string Text,
        int Utf8Bytes);

    private ListCapabilitiesResponse Response(
        string? domain,
        string? goal,
        int filteredCount,
        IReadOnlyList<ServerCapabilityRecord> capabilities,
        IReadOnlyList<CapabilityGoalRecord> goals,
        IReadOnlyList<CapabilityWorkflowRecord> workflows,
        bool hasMore,
        string? nextCursor,
        string? noDataReason) =>
        new(
            _catalog.CatalogScope,
            _catalog.ExhaustiveForWpa,
            _catalog.UnlistedCapabilityMeaning,
            _catalog.CatalogVersion,
            _canonicalContentHash,
            _capabilityPolicy,
            new CapabilityMapFilter(domain, goal),
            new CapabilityMapTotals(
                _capabilities.Count,
                filteredCount,
                capabilities.Count),
            Ordering,
            capabilities.Select(ProjectListedCapability).ToArray(),
            goals,
            workflows,
            hasMore,
            nextCursor,
            noDataReason);

    private static ListedCapabilityRecord ProjectListedCapability(
        ServerCapabilityRecord capability) => new(
            capability.CapabilityId,
            capability.Domain,
            capability.Title,
            capability.AvailabilityState,
            capability.ProductMaturity,
            capability.RequiredEvents,
            capability.RequiredEventStacks,
            capability.SymbolRequirement,
            capability.MaximumRelationship,
            capability.SupportedScopes,
            CatalogScopeSemantics.CapabilitySupportedScopes,
            capability.ToolNames,
            capability.CallableToolNames,
            capability.DisabledByPolicyToolNames,
            capability.WorkflowIds,
            capability.GoalIds,
            capability.EvaluatorId,
            capability.CostClass,
            capability.ConclusionBoundaryCodes,
            $"wpa://capabilities/detail/{capability.CapabilityId}");

    private static IReadOnlyList<ServerCapabilityRecord> ProjectCapabilities(
        ActiveToolCatalog catalog)
    {
        var callableToolNames = catalog.Tools.Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        return catalog.Capabilities
        .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
        .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
        .Select(capability =>
        {
            var allToolNames = catalog.AllTools.Where(tool => tool.Capabilities.Any(mapped =>
                    mapped.CapabilityId == capability.CapabilityId))
                .Select(tool => tool.ToolName)
                .ToArray();
            var callable = allToolNames.Where(callableToolNames.Contains).ToArray();
            var disabled = allToolNames.Where(
                    toolName => !callableToolNames.Contains(toolName))
                .ToArray();
            if (callable.Length + disabled.Length != allToolNames.Length)
            {
                throw new CatalogValidationException(
                    $"CAPABILITY-POLICY-CLOSURE: capability '{capability.CapabilityId}' tool buckets do not close");
            }
            return new ServerCapabilityRecord(
                capability.CapabilityId,
                capability.Domain,
                capability.Title,
                capability.Summary,
                capability.LifecycleStatus,
                capability.ProductMaturity,
                catalog.CapabilityPolicy.IsDisabled(capability.CapabilityId)
                    ? CapabilityAvailabilityStatus.DisabledByPolicy
                    : capability.ProductMaturity == "gap"
                        ? CapabilityAvailabilityStatus.UnavailableByImplementation
                        : capability.LifecycleStatus == "deprecated"
                            ? CapabilityAvailabilityStatus.Deprecated
                            : CapabilityAvailabilityStatus.Callable,
                capability.QuestionsAnswered,
                capability.QuestionsNotAnswered,
                capability.ConclusionBoundaryCodes,
                capability.RequiredEvents,
                capability.RequiredEventStacks,
                capability.OptionalEvidence,
                capability.SymbolRequirement,
                capability.MaximumRelationship,
                capability.SupportedScopes,
                CatalogScopeSemantics.CapabilitySupportedScopes,
                allToolNames,
                callable,
                disabled,
                capability.WorkflowIds,
                capability.GoalIds,
                capability.EvaluatorId,
                capability.CostClass,
                capability.SideEffectClass,
                capability.ContractVersion,
                capability.EvidenceReferences.Select(reference =>
                    new CapabilityMapEvidenceReference(
                        reference.EvidenceId,
                        reference.Kind,
                        reference.Path,
                        reference.Member)).ToArray(),
                capability.ReplacedBy,
                capability.RemovalContractVersion);
        })
        .ToArray();
    }

    private static IReadOnlyList<ServerToolCatalogRecord> ProjectTools(
        ActiveToolCatalog catalog,
        ReviewedToolOutcomeAdapterRegistry outcomeContracts)
    {
        var callableToolNames = catalog.Tools.Select(tool => tool.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        return catalog.AllTools
        .OrderBy(tool => tool.DiscoveryPriority)
        .ThenBy(tool => tool.Domain, StringComparer.Ordinal)
        .ThenBy(tool => tool.Ordinal)
        .ThenBy(tool => tool.ToolName, StringComparer.Ordinal)
        .Select(tool => new ServerToolCatalogRecord(
            tool.ToolName,
            callableToolNames.Contains(tool.ToolName)
                ? CapabilityAvailabilityStatus.Callable
                : CapabilityAvailabilityStatus.DisabledByPolicy,
            callableToolNames.Contains(tool.ToolName),
            tool.Method.GetCustomAttributes(typeof(DescriptionAttribute), inherit: true)
                .OfType<DescriptionAttribute>()
                .FirstOrDefault()?.Description ?? "",
            tool.InputType,
            tool.OutputType,
            tool.Capabilities.Select(capability => capability.CapabilityId).ToArray(),
            tool.RequiredCapabilities,
            tool.SelectableScopes,
            CatalogScopeSemantics.ToolSelectableScopes,
            new ServerToolAnnotationRecord(
                tool.Annotations.ReadOnlyHint,
                tool.Annotations.IdempotentHint,
                tool.Annotations.OpenWorldHint,
                tool.Annotations.DestructiveHint),
            tool.SideEffects,
            tool.CostClass,
            tool.DiscoveryPriority,
            tool.Domain,
            tool.Ordinal,
            tool.DefaultOrdering,
            tool.TieBreakers,
            tool.PageableSections,
            tool.PaginationMode,
            new ServerToolDeprecationRecord(
                tool.Deprecation.State,
                tool.Deprecation.ReplacedBy,
                tool.Deprecation.RemovalContractVersion),
            tool.InternalAnalyzerOperations,
            tool.AllowedMeasurementBases,
            tool.MaximumRelationship,
            tool.ConclusionRules,
            tool.DoesNotProve,
            tool.EvidenceReferenceIds,
            outcomeContracts.SectionRulesFor(tool.ToolName)
                .Select(ProjectSectionContract)
                .ToArray(),
            tool.PlannerAdmission is null
                ? null
                : new PlannerAdmissionRecord(
                    tool.PlannerAdmission.OperationVersion,
                    tool.PlannerAdmission.AdmissionStatus,
                    tool.PlannerAdmission.PhysicalPassLimit,
                    tool.PlannerAdmission.EvidenceReferences.Select(reference =>
                        new CapabilityMapEvidenceReference(
                            reference.EvidenceId,
                            reference.Kind,
                            reference.Path,
                            reference.Member)).ToArray(),
                    tool.PlannerAdmission.MissingEvidence)))
        .ToArray();
    }

    private static ServerToolSectionContractRecord ProjectSectionContract(
        ReviewedSectionRule rule) => new(
        rule.Pointer,
        rule.Role,
        rule.Mode,
        rule.ProofMode switch
        {
            ReviewedSectionProofMode.Exhaustive => "exhaustive",
            ReviewedSectionProofMode.TopPlusOne => "top_plus_one",
            ReviewedSectionProofMode.ConservativeLimit => "conservative_limit",
            ReviewedSectionProofMode.FixedLimitConservative => "fixed_limit_conservative",
            ReviewedSectionProofMode.FixedLimitExactTotal => "fixed_limit_exact_total",
            ReviewedSectionProofMode.ExactRequestedCount => "exact_requested_count",
            ReviewedSectionProofMode.TypedEmbeddedBoundary => "typed_embedded_boundary",
            ReviewedSectionProofMode.DomainCursor => "domain_cursor",
            _ => throw new InvalidOperationException("Unknown reviewed section proof mode."),
        },
        rule.ProofMode switch
        {
            ReviewedSectionProofMode.Exhaustive => "none",
            ReviewedSectionProofMode.DomainCursor =>
                $"cursor:{rule.LimitParameter ?? "response_budget"}",
            _ when rule.LimitParameter is not null => $"argument:{rule.LimitParameter}",
            _ when rule.FixedLimit is not null => $"fixed:{rule.FixedLimit.Value}",
            _ => "none",
        },
        rule.SortKey,
        rule.SortDirection,
        rule.TieBreakers,
        rule.MeasurementBasis,
        rule.Relationship,
        rule.ConclusionStatus,
        rule.EvidenceIds);

    private static string ComputeCanonicalContentHash(
        ActiveToolCatalog catalog,
        IReadOnlyList<ServerCapabilityRecord> capabilities,
        IReadOnlyList<CapabilityGoalRecord> goals,
        IReadOnlyList<CapabilityWorkflowRecord> workflows,
        IReadOnlyList<ServerToolCatalogRecord> tools)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            catalog.CatalogScope,
            catalog.ExhaustiveForWpa,
            catalog.UnlistedCapabilityMeaning,
            catalog.CatalogVersion,
            CapabilityPolicy = catalog.CapabilityPolicy.ToRecord(),
            Ordering,
            Capabilities = capabilities,
            Goals = goals,
            Workflows = workflows,
            Tools = tools,
        }, McpJsonUtilities.DefaultOptions);
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private static string? NormalizeFilter(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var normalized = value.Trim().ToLowerInvariant();
        return FilterPattern.IsMatch(normalized)
            ? normalized
            : throw new ArgumentException(
                "Capability filters must be 1-64 lowercase letters, digits, or underscores after normalization.",
                parameterName);
    }
}
