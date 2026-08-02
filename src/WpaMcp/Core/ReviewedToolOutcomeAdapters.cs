using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal enum ReviewedSectionProofMode
{
    Exhaustive,
    TopPlusOne,
    ConservativeLimit,
    FixedLimitConservative,
    FixedLimitExactTotal,
    ExactRequestedCount,
    TypedEmbeddedBoundary,
    DomainCursor,
}

internal sealed record ReviewedSectionRule(
    string Pointer,
    ReviewedSectionProofMode ProofMode,
    string? LimitParameter,
    int? FixedLimit,
    ToolSectionMode Mode,
    string? SortKey,
    ToolSortDirection SortDirection,
    ImmutableArray<string> TieBreakers,
    string EmptyNoDataReason,
    ToolSectionRole Role,
    ImmutableArray<string> EvidenceIds,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus ConclusionStatus);

internal sealed record ReviewedSectionProof(
    string Pointer,
    long? Requested,
    long Returned,
    long? TotalAvailable,
    string TotalState,
    bool HasMore,
    string? TruncationReason,
    ToolSectionMode Mode,
    string? SortKey,
    ToolSortDirection SortDirection,
    ImmutableArray<string> TieBreakers,
    string? NoDataReason,
    string? NextCursor,
    ToolSectionRole Role,
    ImmutableArray<string> EvidenceIds,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus ConclusionStatus);

internal enum ReviewedDataPolicyKind
{
    DeclaredSections,
    CollectionPointer,
    CpuBatchScopeResults,
    StackCoverage,
    CallerCalleeStackCoverage,
    AlwaysUsable,
}

internal enum ReviewedScopeSource
{
    NotApplicable,
    ResultFields,
    Trace,
}

internal enum ReviewedCapabilityEvaluator
{
    ResultFields,
    ObservedData,
    LifecycleSuccess,
}

internal enum ReviewedPartialFailureEvaluator
{
    None,
    CpuBatchScopeResults,
    HighWaitBudget,
}

internal sealed class ReviewedToolTerminalException(string code) : InvalidOperationException
{
    internal string Code { get; } = ToolErrorCodeRegistry.Contains(code)
        ? code
        : throw new ArgumentException($"Unregistered terminal tool error '{code}'.", nameof(code));
}

internal sealed record ReviewedToolPolicy(
    string PolicyId,
    ReviewedDataPolicyKind DataPolicy,
    string? CollectionPointer,
    string DefaultNoDataReason,
    ReviewedScopeSource ScopeSource,
    ReviewedCapabilityEvaluator CapabilityEvaluator,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus DataConclusion,
    string? PartialField = null,
    ReviewedPartialFailureEvaluator PartialFailureEvaluator = ReviewedPartialFailureEvaluator.None)
{
    internal ReviewedToolRuntimeOutcome Evaluate(
        string toolName,
        JsonNode domain,
        IReadOnlyList<ReviewedSectionProof> sections)
    {
        if (domain is not JsonObject root)
            throw new InvalidOperationException($"Reviewed tool '{toolName}' returned non-object data.");

        if (DataPolicy == ReviewedDataPolicyKind.CpuBatchScopeResults)
            return EvaluateCpuBatch(toolName, root);
        if (DataPolicy is ReviewedDataPolicyKind.StackCoverage or
            ReviewedDataPolicyKind.CallerCalleeStackCoverage)
        {
            return EvaluateStackResult(
                toolName,
                root,
                sections,
                callerCallee: DataPolicy == ReviewedDataPolicyKind.CallerCalleeStackCoverage);
        }

        var hasUsableData = DataPolicy switch
        {
            ReviewedDataPolicyKind.AlwaysUsable => true,
            ReviewedDataPolicyKind.DeclaredSections =>
                sections.Any(section =>
                    (section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence) &&
                    section.Returned > 0),
            ReviewedDataPolicyKind.CollectionPointer =>
                CollectionCount(ResolvePointer(root, CollectionPointer!)) > 0,
            ReviewedDataPolicyKind.CpuBatchScopeResults => throw new InvalidOperationException(
                $"Reviewed tool '{toolName}' bypassed its batch scope evaluator."),
            ReviewedDataPolicyKind.StackCoverage or ReviewedDataPolicyKind.CallerCalleeStackCoverage =>
                throw new InvalidOperationException($"Reviewed tool '{toolName}' bypassed its stack-coverage evaluator."),
            _ => throw new InvalidOperationException($"Unknown reviewed data policy for '{toolName}'."),
        };
        var rawNoData = ReadString(root, "noDataReason");
        var retainedStartupInputOmitted = toolName == "diagnose_slow_startup" &&
            root["discovery"] is JsonObject startupDiscovery &&
            ReadBoolean(startupDiscovery, "candidateInputHasMore") == true;
        var noDataReason = hasUsableData
            ? null
            : retainedStartupInputOmitted
                ? "no_candidates_in_retained_input"
                : string.IsNullOrWhiteSpace(rawNoData) ? DefaultNoDataReason : rawNoData;
        var matched = ReadLong(root, "matchedEventCount");
        var declared = ReadString(root, "capabilityStatus");
        var (traceStatus, scopedStatus) = ScopeSource == ReviewedScopeSource.NotApplicable
            ? (ToolCapabilityStatus.NotApplicable, ToolCapabilityStatus.NotApplicable)
            : CapabilityEvaluator switch
        {
            ReviewedCapabilityEvaluator.LifecycleSuccess =>
                (ToolCapabilityStatus.Available, ToolCapabilityStatus.Available),
            ReviewedCapabilityEvaluator.ObservedData => hasUsableData
                ? (ToolCapabilityStatus.Available, ToolCapabilityStatus.Available)
                : (ToolCapabilityStatus.Unknown, ToolCapabilityStatus.Unknown),
            ReviewedCapabilityEvaluator.ResultFields => EvaluateResultCapability(
                declared,
                matched,
                noDataReason,
                ToolCaptureIntegrityStatus.Unknown),
            _ => throw new InvalidOperationException($"Unknown reviewed capability evaluator for '{toolName}'."),
        };
        var partial = retainedStartupInputOmitted ||
            (PartialField is not null && ReadBoolean(root, PartialField) == true);
        if (PartialFailureEvaluator == ReviewedPartialFailureEvaluator.HighWaitBudget &&
            !partial &&
            ReadString(root, "partialCode") is not null)
        {
            throw new InvalidOperationException(
                $"Reviewed tool '{toolName}' emitted a partialCode while Partial was false.");
        }
        var partialErrorCode = retainedStartupInputOmitted
            ? "response_too_large"
            : partial
                ? EvaluatePartialErrorCode(toolName, root, PartialFailureEvaluator)
                : null;
        return new ReviewedToolRuntimeOutcome(
            hasUsableData,
            noDataReason,
            partial,
            ScopeSource,
            traceStatus,
            scopedStatus,
            matched,
            ToolCaptureIntegrityStatus.Unknown,
            MeasurementBasis,
            Relationship,
            hasUsableData ? DataConclusion : ConclusionStatus.NotConcluded,
            partialErrorCode);
    }

    private ReviewedToolRuntimeOutcome EvaluateCpuBatch(string toolName, JsonObject root)
    {
        if (root["scopeResults"] is not JsonArray rawRows)
            throw new InvalidOperationException($"Reviewed tool '{toolName}' omitted its per-selector scope results.");

        var rows = rawRows.OfType<JsonObject>().ToArray();
        if (rows.Length != rawRows.Count || rows.Length == 0)
            throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted invalid or empty per-selector scope results.");

        var acceptedStatuses = new HashSet<string>(StringComparer.Ordinal)
        {
            "completed",
            "completed_no_samples",
            "scope_not_found",
            "ambiguous_process_instance",
            "budget_skipped",
            "analysis_failed",
        };
        var coverageByPid = new Dictionary<long, (long Total, long Stacked)>();
        foreach (var row in rows)
        {
            var status = ReadString(row, "resultStatus") ??
                throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted a selector result without resultStatus.");
            if (!acceptedStatuses.Contains(status))
                throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted unknown selector status '{status}'.");
            var matched = ReadLong(row, "matchedSampleCount") ??
                throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted a selector result without matchedSampleCount.");
            if (matched < 0)
                throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted a negative matchedSampleCount.");
            if (string.Equals(status, "completed", StringComparison.Ordinal) != (matched > 0))
            {
                throw new InvalidOperationException(
                    $"Reviewed tool '{toolName}' emitted an inconsistent completed/sample-count selector result.");
            }
            if (status is "completed" or "completed_no_samples")
            {
                var pid = ReadLong(row, "pid") ??
                    throw new InvalidOperationException($"Reviewed tool '{toolName}' emitted a completed selector without pid.");
                if (row["result"] is not JsonObject pidResult ||
                    pidResult["stackCoverage"] is not JsonObject coverage)
                {
                    throw new InvalidOperationException(
                        $"Reviewed tool '{toolName}' omitted stack coverage for completed PID {pid}.");
                }
                var counts = ReadStackCoverage(toolName, coverage);
                if (counts.Total != matched)
                {
                    throw new InvalidOperationException(
                        $"Reviewed tool '{toolName}' emitted mismatched sample and stack-coverage counts for PID {pid}.");
                }
                coverageByPid.Add(pid, counts);
            }

            var completedResult = row["result"] as JsonObject;
            ValidateEmbeddedBatchBoundary(
                row["rowsBoundary"] as JsonObject,
                "/scopeResults/result/rows",
                completedResult?["rows"] is JsonArray resultRows ? resultRows.Count : 0,
                completedResult is not null,
                expectedExactTotal: null);
            var unresolvedTotal = completedResult?["stats"] is JsonObject stats
                ? ReadLong(stats, "unresolvedModuleCount")
                : null;
            ValidateEmbeddedBatchBoundary(
                row["topUnresolvedModulesBoundary"] as JsonObject,
                "/scopeResults/result/stats/topUnresolvedModules",
                completedResult?["stats"]?["topUnresolvedModules"] is JsonArray unresolvedRows
                    ? unresolvedRows.Count
                    : 0,
                completedResult is not null,
                unresolvedTotal);
        }

        var matchedEventCount = rows.Aggregate(
            0L,
            (total, row) => checked(total + ReadLong(row, "matchedSampleCount")!.Value));
        var stackedEventCount = coverageByPid.Values.Aggregate(
            0L,
            (total, coverage) => checked(total + coverage.Stacked));
        var hasUsableData = stackedEventCount > 0;
        if ((ReadLong(root, "completedPidCount") ?? 0) == 0 &&
            rows.All(row => string.Equals(
                ReadString(row, "resultStatus"),
                "budget_skipped",
                StringComparison.Ordinal)))
        {
            throw new ReviewedToolTerminalException("budget_exceeded");
        }
        var hasIncompleteSelector = rows.Any(row => ReadString(row, "resultStatus") is
            "scope_not_found" or "ambiguous_process_instance" or "budget_skipped" or "analysis_failed");
        var partial = ReadBoolean(root, "partial") == true;
        var hasScopedEvidenceLimitation = partial || hasIncompleteSelector ||
            rows.Any(row => string.Equals(ReadString(row, "resultStatus"), "completed_no_samples", StringComparison.Ordinal)) ||
            coverageByPid.Values.Any(coverage => coverage.Total > 0 && coverage.Stacked < coverage.Total);
        var noDataReason = hasUsableData
            ? null
            : matchedEventCount > 0
                ? "stacks_unavailable"
                : rows.Any(row => string.Equals(
                ReadString(row, "resultStatus"),
                "completed_no_samples",
                StringComparison.Ordinal))
                ? "no_events_in_scope"
                : DefaultNoDataReason;
        var partialErrorCode = partial
            ? ReadString(root, "partialErrorCode") ?? CpuBatchPartialErrorCode(rows)
            : null;

        var traceStatus = hasUsableData
            ? coverageByPid.Values.Any(coverage => coverage.Stacked < coverage.Total)
                ? ToolCapabilityStatus.Partial
                : ToolCapabilityStatus.Available
            : matchedEventCount > 0 ? ToolCapabilityStatus.Unavailable : ToolCapabilityStatus.Unknown;
        var scopedStatus = hasUsableData && hasScopedEvidenceLimitation
            ? ToolCapabilityStatus.Partial
            : traceStatus;

        return new ReviewedToolRuntimeOutcome(
            hasUsableData,
            noDataReason,
            partial,
            ScopeSource,
            traceStatus,
            scopedStatus,
            matchedEventCount,
            ToolCaptureIntegrityStatus.Unknown,
            MeasurementBasis,
            Relationship,
            hasUsableData ? DataConclusion : ConclusionStatus.NotConcluded,
            partialErrorCode);
    }

    private static string CpuBatchPartialErrorCode(IReadOnlyList<JsonObject> rows)
    {
        if (rows.Any(row => string.Equals(ReadString(row, "resultStatus"), "budget_skipped", StringComparison.Ordinal)))
            return "budget_exceeded";
        if (rows.Any(row => string.Equals(ReadString(row, "resultStatus"), "analysis_failed", StringComparison.Ordinal)))
            return "analysis_failed";
        if (rows.Any(row => string.Equals(ReadString(row, "resultStatus"), "ambiguous_process_instance", StringComparison.Ordinal)))
            return "ambiguous_process_instance";
        return "process_instance_not_found";
    }

    private static void ValidateEmbeddedBatchBoundary(
        JsonObject? boundary,
        string pointer,
        int returned,
        bool available,
        long? expectedExactTotal)
    {
        if (boundary is null ||
            !string.Equals(ReadString(boundary, "sectionPointer"), pointer, StringComparison.Ordinal) ||
            ReadLong(boundary, "returned") != returned ||
            ReadBoolean(boundary, "continuationAvailable") != false)
        {
            throw new InvalidOperationException(
                $"cpu_top_functions_batch omitted or contradicted embedded boundary '{pointer}'.");
        }
        var totalState = ReadString(boundary, "totalState");
        var moreState = ReadString(boundary, "moreState");
        var total = ReadLong(boundary, "totalAvailable");
        var requested = ReadLong(boundary, "requested");
        var declaredHasMore = ReadBoolean(boundary, "hasMore");
        var truncationReason = ReadString(boundary, "truncationReason");
        if (requested is null || requested < returned ||
            declaredHasMore != (moreState == "present") ||
            ((moreState is "present" or "unknown") !=
                !string.IsNullOrWhiteSpace(truncationReason)))
        {
            throw new InvalidOperationException(
                $"cpu_top_functions_batch emitted invalid common invariants for '{pointer}'.");
        }
        if (!available)
        {
            if (totalState != "unknown" || total is not null || moreState != "unknown")
            {
                throw new InvalidOperationException(
                    $"cpu_top_functions_batch must mark unavailable boundary '{pointer}' unknown.");
            }
        }
        else if (expectedExactTotal is { } exact)
        {
            if (totalState != "exact" || total != exact || exact < returned ||
                (moreState == "present") != (exact > returned) ||
                (moreState == "absent" && exact != returned))
            {
                throw new InvalidOperationException(
                    $"cpu_top_functions_batch emitted an inconsistent exact boundary '{pointer}'.");
            }
        }
        else if (!((totalState == "exact" && total == returned && moreState == "absent") ||
                   (totalState == "unknown" && total is null && moreState == "unknown")))
        {
            throw new InvalidOperationException(
                $"cpu_top_functions_batch emitted an inconsistent conservative boundary '{pointer}'.");
        }
    }

    private ReviewedToolRuntimeOutcome EvaluateStackResult(
        string toolName,
        JsonObject root,
        IReadOnlyList<ReviewedSectionProof> sections,
        bool callerCallee)
    {
        if (root["stackCoverage"] is not JsonObject coverage)
            throw new InvalidOperationException($"Reviewed stack tool '{toolName}' omitted stackCoverage.");

        var (total, stacked) = ReadStackCoverage(toolName, coverage);

        var rawNoData = ReadString(root, "noDataReason");
        ValidateRawStackContract(
            toolName,
            total,
            stacked,
            callerCallee,
            ReadString(root, "capabilityStatus"),
            rawNoData);
        var hasProjectedStackEvidence = callerCallee
            ? rawNoData is null &&
              !string.Equals(ReadString(root, "focusFunction"), "?!?", StringComparison.Ordinal)
            : sections.Any(section =>
                (section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence) &&
                section.Returned > 0);
        var hasUsableData = stacked > 0 && hasProjectedStackEvidence;
        if (!callerCallee && stacked > 0 && !hasProjectedStackEvidence)
        {
            throw new InvalidOperationException(
                $"Reviewed stack tool '{toolName}' measured captured stacks but projected no real stack frame.");
        }

        var noDataReason = hasUsableData
            ? null
            : total > 0 && stacked == 0
                ? "stacks_unavailable"
                : !string.IsNullOrWhiteSpace(rawNoData)
                    ? rawNoData
                    : callerCallee ? "focus_not_found" : "no_events_in_scope";
        var traceStatus = total == 0
            ? ToolCapabilityStatus.Unknown
            : stacked == 0
                ? ToolCapabilityStatus.Unavailable
                : stacked < total ? ToolCapabilityStatus.Partial : ToolCapabilityStatus.Available;
        // A missing focus frame is a query-selection outcome, not proof that
        // the underlying stack capability is unavailable.
        var scopedStatus = traceStatus;

        return new ReviewedToolRuntimeOutcome(
            hasUsableData,
            noDataReason,
            Partial: false,
            ScopeSource,
            traceStatus,
            scopedStatus,
            ReadLong(root, "matchedEventCount"),
            ToolCaptureIntegrityStatus.Unknown,
            MeasurementBasis,
            Relationship,
            hasUsableData ? DataConclusion : ConclusionStatus.NotConcluded,
            PartialErrorCode: null);
    }

    private static void ValidateRawStackContract(
        string toolName,
        long total,
        long stacked,
        bool callerCallee,
        string? rawStatus,
        string? rawNoData)
    {
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            throw new InvalidOperationException(
                $"Reviewed stack tool '{toolName}' omitted capabilityStatus.");
        }

        if (total == 0)
        {
            if (rawStatus is not ("unknown" or "not_observed") ||
                string.IsNullOrWhiteSpace(rawNoData) ||
                rawNoData is "stacks_unavailable" or "focus_not_found")
            {
                throw new InvalidOperationException(
                    $"Reviewed stack tool '{toolName}' contradicts its zero-event stack coverage.");
            }
            return;
        }

        var focusMissing = callerCallee &&
            string.Equals(rawNoData, "focus_not_found", StringComparison.Ordinal);
        var expectedStatus = stacked == 0
            ? "unavailable"
            : stacked < total ? "partial" : "observed";
        var expectedNoData = stacked == 0
            ? "stacks_unavailable"
            : focusMissing ? "focus_not_found" : null;
        if (!string.Equals(rawStatus, expectedStatus, StringComparison.Ordinal) ||
            !string.Equals(rawNoData, expectedNoData, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reviewed stack tool '{toolName}' raw capabilityStatus/noDataReason contradicts " +
                "its scoped stack coverage and focus result.");
        }
    }

    private static (long Total, long Stacked) ReadStackCoverage(
        string toolName,
        JsonObject coverage)
    {
        var total = ReadLong(coverage, "totalEventCount") ??
            throw new InvalidOperationException($"Reviewed stack tool '{toolName}' omitted stackCoverage.totalEventCount.");
        var stacked = ReadLong(coverage, "stackedEventCount") ??
            throw new InvalidOperationException($"Reviewed stack tool '{toolName}' omitted stackCoverage.stackedEventCount.");
        if (total < 0 || stacked < 0 || stacked > total)
            throw new InvalidOperationException($"Reviewed stack tool '{toolName}' emitted invalid stack-coverage counts.");

        var expectedState = total == 0
            ? "no_events"
            : stacked == 0
                ? "no_stacks"
                : stacked == total ? "full" : "partial";
        var declaredState = ReadString(coverage, "coverageState");
        if (!string.Equals(declaredState, expectedState, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Reviewed stack tool '{toolName}' emitted stack coverage state '{declaredState ?? "<null>"}' " +
                $"for counts that require '{expectedState}'.");
        }
        return (total, stacked);
    }

    private static string EvaluatePartialErrorCode(
        string toolName,
        JsonObject root,
        ReviewedPartialFailureEvaluator evaluator) => evaluator switch
    {
        ReviewedPartialFailureEvaluator.CpuBatchScopeResults =>
            ContainsResultStatus(root, "budget_skipped") ? "budget_exceeded" : "analysis_failed",
        ReviewedPartialFailureEvaluator.HighWaitBudget =>
            string.Equals(
                ReadString(root, "partialCode"),
                "time_budget_exhausted",
                StringComparison.Ordinal)
                ? "budget_exceeded"
                : throw new InvalidOperationException(
                    $"Reviewed tool '{toolName}' reported a partial result without the typed time-budget code."),
        ReviewedPartialFailureEvaluator.None => throw new InvalidOperationException(
            $"Reviewed tool '{toolName}' reported a partial result without a typed partial-failure evaluator."),
        _ => throw new InvalidOperationException($"Unknown partial-failure evaluator for '{toolName}'."),
    };

    private static bool ContainsResultStatus(JsonObject root, string status) =>
        root["scopeResults"] is JsonArray rows && rows
            .OfType<JsonObject>()
            .Any(row => string.Equals(ReadString(row, "resultStatus"), status, StringComparison.Ordinal));

    private static (ToolCapabilityStatus Trace, ToolCapabilityStatus Scoped) EvaluateResultCapability(
        string? declared,
        long? matched,
        string? noDataReason,
        ToolCaptureIntegrityStatus captureIntegrity)
    {
        if (noDataReason == "invalid_lifetime_boundaries")
            return (ToolCapabilityStatus.Partial, ToolCapabilityStatus.Partial);
        if (noDataReason == "no_events_in_scope")
            return (ParseDeclared(declared), ToolCapabilityStatus.Unknown);
        if (noDataReason == "focus_not_found")
        {
            var status = ParseDeclared(declared);
            return (status, status);
        }
        if (noDataReason is "no_completed_intervals_in_scope" or
            "no_completed_intervals" or "unpaired_endpoints_in_scope")
        {
            // Source endpoints exist, but the requested interval/lifecycle
            // measurement could not be completed. This is partial evidence,
            // not capability unavailability.
            return (ToolCapabilityStatus.Partial, ToolCapabilityStatus.Partial);
        }
        if (noDataReason == "event_class_not_observed")
        {
            return captureIntegrity == ToolCaptureIntegrityStatus.Complete
                ? (ToolCapabilityStatus.Unavailable, ToolCapabilityStatus.Unavailable)
                : (ToolCapabilityStatus.Unknown, ToolCapabilityStatus.Unknown);
        }
        if (noDataReason == "source_events_unattributed")
            return (ToolCapabilityStatus.Partial, ToolCapabilityStatus.Partial);
        if (noDataReason is "stacks_unavailable" or "symbols_unresolved")
            return (ToolCapabilityStatus.Partial, ToolCapabilityStatus.Unavailable);
        if (matched > 0)
            return (ToolCapabilityStatus.Available, ToolCapabilityStatus.Available);
        return (ParseDeclared(declared), ParseDeclared(declared));
    }

    private static ToolCapabilityStatus ParseDeclared(string? value) => value switch
    {
        "available" or "observed" => ToolCapabilityStatus.Available,
        "partial" => ToolCapabilityStatus.Partial,
        "not_applicable" => ToolCapabilityStatus.NotApplicable,
        // not_observed cannot mean unavailable while capture completeness is unknown.
        "unavailable" or "not_observed" => ToolCapabilityStatus.Unknown,
        _ => ToolCapabilityStatus.Unknown,
    };

    private static int CollectionCount(JsonNode? node) => node switch
    {
        JsonArray array => array.Count,
        JsonObject value => value.Count,
        _ => throw new InvalidOperationException("The reviewed collection data predicate did not resolve to an array or object."),
    };

    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current?[segment];
        }
        return current;
    }

    private static string? ReadString(JsonObject root, string property) =>
        root[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? ReadBoolean(JsonObject root, string property) =>
        root[property] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static long? ReadLong(JsonObject root, string property)
    {
        if (root[property] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var number))
            return number;
        if (value.TryGetValue<int>(out var intNumber))
            return intNumber;
        return value.TryGetValue<string>(out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }
}

internal sealed record ReviewedToolRuntimeOutcome(
    bool HasUsableData,
    string? NoDataReason,
    bool Partial,
    ReviewedScopeSource ScopeSource,
    ToolCapabilityStatus TraceCapabilityStatus,
    ToolCapabilityStatus ScopedCapabilityStatus,
    long? MatchedEventCount,
    ToolCaptureIntegrityStatus CaptureIntegrity,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus ConclusionStatus,
    string? PartialErrorCode);

internal sealed record ReviewedToolInvocationPlan(
    ActiveToolDefinition Tool,
    IReadOnlyDictionary<string, JsonElement> PublicArguments,
    IReadOnlyDictionary<string, JsonElement> InnerArguments,
    ImmutableArray<ReviewedSectionRule> SectionRules,
    ReviewedToolPolicy Policy)
{
    internal ReviewedToolResult Adapt(JsonNode domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        if (domain is not JsonObject root)
            throw new InvalidOperationException($"Tool '{Tool.ToolName}' returned non-object data.");

        var projected = domain.DeepClone();
        var proofs = new List<ReviewedSectionProof>();
        foreach (var rule in SectionRules)
        {
            var node = ResolvePointer(projected, rule.Pointer);
            if (node is null)
                continue; // Nullable mutually-exclusive sections are not part of this response.
            if (node is not JsonArray rows)
                throw new InvalidOperationException($"Reviewed section '{rule.Pointer}' is not an array.");

            if (rule.ProofMode == ReviewedSectionProofMode.DomainCursor)
            {
                var globalHasMore = ReadBoolean(root, "hasMore") == true;
                var globalCursor = ReadString(root, "nextCursor");
                if (globalHasMore != !string.IsNullOrWhiteSpace(globalCursor))
                    throw new InvalidOperationException($"Tool '{Tool.ToolName}' emitted inconsistent cursor state.");
                if (TimelinePagination.IsTimelineTool(Tool.ToolName))
                {
                    var page = root["pageContext"] as JsonObject
                        ?? throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' omitted pageContext.");
                    var timelineTotal = ReadLongValue(page, "totalCount")
                        ?? throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' omitted pageContext.totalCount.");
                    var startIndex = ReadLongValue(page, "startIndex")
                        ?? throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' omitted pageContext.startIndex.");
                    var requestedPageSize = ReadLongValue(page, "requestedPageSize")
                        ?? throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' omitted pageContext.requestedPageSize.");
                    var declaredPageReturned = ReadLongValue(page, "returnedCount");
                    var declaredRootReturned = ReadLongValue(root, "returnedCount");
                    var publicPageSize = EffectiveRequestedLimit(
                        Tool.ToolName == TimelinePagination.ListProcessesTool
                            ? "top"
                            : "pageSize");
                    var declaredTotal = Tool.ToolName switch
                    {
                        TimelinePagination.ThreadLifetimeTool => ReadLongValue(root, "totalThreads"),
                        TimelinePagination.ProcessCreateTimingTool => ReadLongValue(root, "spawnCount"),
                        TimelinePagination.ImageLoadTimingTool => ReadLongValue(root, "totalImageLoads"),
                        TimelinePagination.ListProcessesTool => ReadLongValue(root, "totalCount"),
                        TimelinePagination.CpuTopFunctionsBatchTool =>
                            ReadLongValue(root, "requestedPidCount"),
                        _ => null,
                    };
                    if (timelineTotal < 0 || startIndex < 0 || startIndex > timelineTotal ||
                        requestedPageSize != publicPageSize ||
                        rows.Count > requestedPageSize ||
                        checked(startIndex + rows.Count) > timelineTotal ||
                        declaredPageReturned != rows.Count ||
                        declaredRootReturned != rows.Count ||
                        declaredTotal != timelineTotal)
                    {
                        throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' emitted inconsistent exact page counts.");
                    }
                    var expectedHasMore = checked(startIndex + rows.Count) < timelineTotal;
                    if (globalHasMore != expectedHasMore ||
                        (expectedHasMore && rows.Count == 0))
                    {
                        throw new InvalidOperationException(
                            $"Timeline tool '{Tool.ToolName}' emitted a continuation inconsistent with its exact total.");
                    }
                    proofs.Add(new(
                        rule.Pointer,
                        Requested: requestedPageSize,
                        rows.Count,
                        timelineTotal,
                        TotalState: "exact",
                        expectedHasMore,
                        expectedHasMore ? "cursor_page" : null,
                        rule.Mode,
                        rule.SortKey,
                        rule.SortDirection,
                        rule.TieBreakers,
                        rows.Count == 0
                            ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                            : null,
                        expectedHasMore ? globalCursor : null,
                        rule.Role,
                        rule.EvidenceIds,
                        rule.MeasurementBasis,
                        rule.Relationship,
                        rule.ConclusionStatus));
                    continue;
                }
                var total = rule.Pointer switch
                {
                    "/traceEvidenceMap/capabilities" =>
                        ReadNestedLong(root, "traceEvidenceMap", "totalCapabilities"),
                    "/traceEvidenceMap/workflows" =>
                        ReadNestedLong(root, "traceEvidenceMap", "totalWorkflows"),
                    _ => ReadNestedLong(root, "totals", "totalCapabilitiesAfterFilter"),
                };
                var sectionHasMore = globalHasMore;
                var sectionCursor = globalCursor;
                if (Tool.ToolName == "inspect_trace")
                {
                    var phase = ReadNestedString(root, "pageContext", "phase")
                        ?? throw new InvalidOperationException("inspect_trace omitted its cursor phase.");
                    var startIndex = ReadNestedLong(root, "pageContext", "startIndex")
                        ?? throw new InvalidOperationException("inspect_trace omitted its cursor start index.");
                    var activePointer = phase switch
                    {
                        "capabilities" => "/traceEvidenceMap/capabilities",
                        "workflows" => "/traceEvidenceMap/workflows",
                        _ => throw new InvalidOperationException("inspect_trace emitted an invalid cursor phase."),
                    };
                    sectionHasMore = string.Equals(rule.Pointer, activePointer, StringComparison.Ordinal) &&
                        total is not null && checked(startIndex + rows.Count) < total;
                    if (sectionHasMore && !globalHasMore)
                        throw new InvalidOperationException("inspect_trace section continuation exceeds global continuation state.");
                    sectionCursor = sectionHasMore ? globalCursor : null;
                }
                proofs.Add(new(
                    rule.Pointer,
                    Requested: null,
                    rows.Count,
                    total,
                    total is null ? "unknown" : "exact",
                    sectionHasMore,
                    sectionHasMore ? "cursor_page" : null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    rows.Count == 0
                        ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                        : null,
                    sectionCursor,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }

            if (rule.ProofMode == ReviewedSectionProofMode.Exhaustive)
            {
                proofs.Add(new(
                    rule.Pointer,
                    Requested: null,
                    rows.Count,
                    rows.Count,
                    "exact",
                    HasMore: false,
                    TruncationReason: null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    rows.Count == 0
                        ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                        : null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }

            if (rule.ProofMode == ReviewedSectionProofMode.TypedEmbeddedBoundary)
            {
                var boundary = root["candidateBoundary"] as JsonObject
                    ?? throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' omitted its typed candidateBoundary.");
                var boundaryRequested = ReadLongValue(boundary, "requested")
                    ?? throw new InvalidOperationException("candidateBoundary omitted requested.");
                var returned = ReadLongValue(boundary, "returned")
                    ?? throw new InvalidOperationException("candidateBoundary omitted returned.");
                var total = ReadLongValue(boundary, "totalAvailable");
                var totalState = ReadString(boundary, "totalState")
                    ?? throw new InvalidOperationException("candidateBoundary omitted totalState.");
                var moreState = ReadString(boundary, "moreState")
                    ?? throw new InvalidOperationException("candidateBoundary omitted moreState.");
                var boundaryHasMore = ReadBoolean(boundary, "hasMore") == true;
                var continuation = ReadBoolean(boundary, "continuationAvailable") == true;
                var truncationReason = ReadString(boundary, "truncationReason");
                var boundaryTieBreakers = boundary["tieBreakers"] is JsonArray tieBreakerArray
                    ? tieBreakerArray.Select(item => item?.GetValue<string>() ?? string.Empty).ToArray()
                    : null;
                var expectedRequested = EffectiveRequestedLimit(rule.LimitParameter!);
                if (ReadString(boundary, "sectionPointer") != rule.Pointer ||
                    boundaryRequested != expectedRequested || returned != rows.Count ||
                    continuation || boundaryHasMore != (moreState == "present") ||
                    ReadString(boundary, "sortKey") != rule.SortKey ||
                    ReadString(boundary, "sortDirection") != (rule.SortDirection switch
                    {
                        ToolSortDirection.NotApplicable => "not_applicable",
                        ToolSortDirection.Ascending => "ascending",
                        ToolSortDirection.Descending => "descending",
                        _ => throw new InvalidOperationException("Unknown sort direction."),
                    }) || boundaryTieBreakers is null ||
                    !boundaryTieBreakers.SequenceEqual(rule.TieBreakers, StringComparer.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' emitted a candidateBoundary inconsistent with '{rule.Pointer}'.");
                }
                var exact = totalState == "exact";
                var unknown = totalState == "unknown";
                if ((!exact && !unknown) ||
                    (exact && (total is null || total < rows.Count ||
                        moreState == "unknown" ||
                        (moreState == "absent" && total != rows.Count) ||
                        (moreState == "present" && total <= rows.Count))) ||
                    (unknown && (total is not null || moreState != "unknown" || boundaryHasMore)) ||
                    ((moreState is "present" or "unknown") !=
                        !string.IsNullOrWhiteSpace(truncationReason)))
                {
                    throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' emitted internally inconsistent candidate totals.");
                }
                proofs.Add(new(
                    rule.Pointer,
                    boundaryRequested,
                    rows.Count,
                    total,
                    totalState,
                    boundaryHasMore,
                    truncationReason,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    rows.Count == 0
                        ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                        : null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }

            if (rule.ProofMode == ReviewedSectionProofMode.ExactRequestedCount)
            {
                var exactRequested = EffectiveRequestedLimit(rule.LimitParameter!);
                if (rows.Count != exactRequested)
                {
                    throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' returned {rows.Count} rows for exact-requested section " +
                        $"'{rule.Pointer}', expected {exactRequested}.");
                }
                proofs.Add(new(
                    rule.Pointer,
                    exactRequested,
                    rows.Count,
                    rows.Count,
                    TotalState: "exact",
                    HasMore: false,
                    TruncationReason: null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    NoDataReason: null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }

            var requested = rule.ProofMode is ReviewedSectionProofMode.FixedLimitConservative or
                ReviewedSectionProofMode.FixedLimitExactTotal
                ? rule.FixedLimit!.Value
                : EffectiveRequestedLimit(rule.LimitParameter!);
            if (Tool.ToolName == "diagnose_slow_startup" &&
                rule.Pointer == "/discovery/excludedSamples")
            {
                var exactTotal = ReadNestedLong(
                    root,
                    "discovery",
                    "excludedStartupInstanceCount")
                    ?? throw new InvalidOperationException(
                        "diagnose_slow_startup omitted its exact exclusion total.");
                var declaredHasMore = root["discovery"] is JsonObject discovery &&
                    ReadBoolean(discovery, "excludedSamplesHasMore") == true;
                var expectedHasMore = exactTotal > rows.Count;
                if (rows.Count > requested || exactTotal < rows.Count ||
                    declaredHasMore != expectedHasMore)
                {
                    throw new InvalidOperationException(
                        "diagnose_slow_startup emitted inconsistent exclusion sample totals.");
                }
                proofs.Add(new(
                    rule.Pointer,
                    requested,
                    rows.Count,
                    exactTotal,
                    TotalState: "exact",
                    expectedHasMore,
                    expectedHasMore ? "fixed_sample_limit" : null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    NoDataReason: null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }
            if (rule.ProofMode == ReviewedSectionProofMode.FixedLimitExactTotal)
            {
                var exactTotal = rule.Pointer switch
                {
                    "/metadata/providerEvents/topProviders" =>
                        ReadNestedLong(root, "metadata", "providerEvents", "totalProviderCount"),
                    "/metadata/drivers/topDrivers" =>
                        ReadNestedLong(root, "metadata", "drivers", "totalDriverModuleCount"),
                    "/symbolQuality/topModulesMissingPdbName" =>
                        ReadNestedLong(root, "symbolQuality", "moduleCount") is { } moduleCount &&
                        ReadNestedLong(root, "symbolQuality", "modulesWithPdbName") is { } withPdbName
                            ? checked(moduleCount - withPdbName)
                            : null,
                    "/symbolQuality/frameResolution/topUnresolvedModules" =>
                        ReadNestedLong(root, "symbolQuality", "frameResolution", "unresolvedModuleCount"),
                    "/stats/topUnresolvedModules" =>
                        ReadNestedLong(root, "stats", "unresolvedModuleCount"),
                    _ => null,
                } ?? throw new InvalidOperationException(
                    $"Tool '{Tool.ToolName}' omitted the exact total for '{rule.Pointer}'.");
                var expectedReturned = Math.Min(exactTotal, requested);
                if (exactTotal < 0 || rows.Count != expectedReturned)
                {
                    throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' emitted rows inconsistent with the exact total for '{rule.Pointer}'.");
                }
                var exactHasMore = exactTotal > rows.Count;
                proofs.Add(new(
                    rule.Pointer,
                    requested,
                    rows.Count,
                    exactTotal,
                    TotalState: "exact",
                    exactHasMore,
                    exactHasMore ? "fixed_source_limit" : null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    NoDataReason: null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }
            if (rule.ProofMode is ReviewedSectionProofMode.ConservativeLimit or
                ReviewedSectionProofMode.FixedLimitConservative)
            {
                if (rows.Count > requested)
                {
                    throw new InvalidOperationException(
                        $"Tool '{Tool.ToolName}' exceeded its reviewed section limit for '{rule.Pointer}'.");
                }
                var saturated = rows.Count == requested;
                proofs.Add(new(
                    rule.Pointer,
                    requested,
                    rows.Count,
                    TotalAvailable: saturated ? null : rows.Count,
                    TotalState: saturated ? "unknown" : "exact",
                    // Saturation proves neither that another row exists nor that the
                    // section is terminal. The public section carries moreState=unknown
                    // and no continuation; hasMore remains the compatibility boolean for
                    // known-present omission only.
                    HasMore: false,
                    TruncationReason: saturated ? "source_limit_saturated" : null,
                    rule.Mode,
                    rule.SortKey,
                    rule.SortDirection,
                    rule.TieBreakers,
                    rows.Count == 0
                        ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                        : null,
                    NextCursor: null,
                    rule.Role,
                    rule.EvidenceIds,
                    rule.MeasurementBasis,
                    rule.Relationship,
                    rule.ConclusionStatus));
                continue;
            }
            if (rows.Count > requested + 1)
            {
                throw new InvalidOperationException(
                    $"Tool '{Tool.ToolName}' violated its reviewed top+1 section bound for '{rule.Pointer}'.");
            }
            var hasMore = rows.Count == requested + 1;
            if (hasMore)
                rows.RemoveAt(rows.Count - 1);
            proofs.Add(new(
                rule.Pointer,
                requested,
                rows.Count,
                hasMore ? requested + 1 : rows.Count,
                hasMore ? "lower_bound" : "exact",
                hasMore,
                hasMore ? "requested_top" : null,
                rule.Mode,
                rule.SortKey,
                rule.SortDirection,
                rule.TieBreakers,
                rows.Count == 0
                    ? ReadString(root, "noDataReason") ?? rule.EmptyNoDataReason
                    : null,
                NextCursor: null,
                rule.Role,
                rule.EvidenceIds,
                rule.MeasurementBasis,
                rule.Relationship,
                rule.ConclusionStatus));
        }
        SynchronizeCompatibilityPaginationFlags(Tool.ToolName, projected, proofs);
        return new ReviewedToolResult(
            projected,
            proofs,
            Policy.Evaluate(Tool.ToolName, projected, proofs));
    }

    private static void SynchronizeCompatibilityPaginationFlags(
        string toolName,
        JsonNode projected,
        IReadOnlyList<ReviewedSectionProof> proofs)
    {
        if (projected is not JsonObject root)
        {
            return;
        }

        if (root["hasMore"] is JsonValue hasMoreValue &&
            hasMoreValue.TryGetValue<bool>(out _))
        {
            root["hasMore"] = proofs.Any(section => section.HasMore);
        }

        if (!string.Equals(toolName, "security_scan_analysis", StringComparison.Ordinal))
            return;

        // The typed analyzer sees the internal top+1 limit, so its legacy flags
        // describe that overfetch boundary. Publish flags for the caller-visible
        // rows after the reviewed adapter removes the proof row.
        root["rowsHasMore"] = proofs.Single(section => section.Pointer == "/rows").HasMore;
        root["slowScansHasMore"] = proofs.Single(section => section.Pointer == "/slowScans").HasMore;
        root["providersHasMore"] = proofs.Single(section => section.Pointer == "/providers").HasMore;
    }

    private int EffectiveRequestedLimit(string parameter)
    {
        var requested = ReadInt(PublicArguments, parameter) ??
            Convert.ToInt32(Tool.Method.GetParameters().Single(item => item.Name == parameter).DefaultValue, CultureInfo.InvariantCulture);
        if (string.Equals(parameter, "top", StringComparison.Ordinal) &&
            (ReadBoolean(PublicArguments, "compactStacks") == true ||
             ReadBoolean(PublicArguments, "summaryOnly") == true))
            return Math.Min(requested, StackResponseOptions.CompactTopLimit);
        return requested;
    }

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static bool? ReadBoolean(IReadOnlyDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;

    private static string? ReadString(JsonObject root, string property) =>
        root[property] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool? ReadBoolean(JsonObject root, string property) =>
        root[property] is JsonValue value && value.TryGetValue<bool>(out var result) ? result : null;

    private static long? ReadLongValue(JsonObject root, string property)
    {
        if (root[property] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var number))
            return number;
        if (value.TryGetValue<int>(out var intNumber))
            return intNumber;
        return value.TryGetValue<string>(out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static long? ReadNestedLong(JsonObject root, string parent, string property)
    {
        if (root[parent] is not JsonObject nested || nested[property] is not JsonValue value)
            return null;
        if (value.TryGetValue<long>(out var number))
            return number;
        if (value.TryGetValue<int>(out var intNumber))
            return intNumber;
        return value.TryGetValue<string>(out var text) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
                ? number
                : null;
    }

    private static long? ReadNestedLong(
        JsonObject root,
        string parent,
        string child,
        string property) =>
        root[parent] is JsonObject parentObject && parentObject[child] is JsonObject childObject
            ? ReadLongValue(childObject, property)
            : null;

    private static string? ReadNestedString(JsonObject root, string parent, string property) =>
        root[parent] is JsonObject nested && nested[property] is JsonValue value &&
        value.TryGetValue<string>(out var text)
            ? text
            : null;

    private static JsonNode? ResolvePointer(JsonNode root, string pointer)
    {
        JsonNode? current = root;
        foreach (var raw in pointer.Split('/').Skip(1))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal)
                .Replace("~0", "~", StringComparison.Ordinal);
            current = current?[segment];
        }
        return current;
    }
}

internal sealed record ReviewedToolResult(
    JsonNode Domain,
    IReadOnlyList<ReviewedSectionProof> Sections,
    ReviewedToolRuntimeOutcome Outcome);

internal sealed class ReviewedToolOutcomeAdapterRegistry
{
    // Every entry is intentionally explicit: adding an MCP tool without reviewing its
    // scope/no-data/evidence and section proof strategy fails startup.
    private static readonly HashSet<string> ReviewedToolNames = new(StringComparer.Ordinal)
    {
        "alpc_caller_callee", "alpc_top_stacks", "clr_alloc_caller_callee", "clr_alloc_top_stacks",
        "clr_contention_caller_callee", "clr_contention_top_stacks", "clr_exception_caller_callee",
        "clr_exception_top_stacks", "clr_finalizer_analysis", "clr_gc_analysis", "clr_gc_heap_stats",
        "clr_jit_analysis", "cpu_caller_callee", "cpu_precise_analysis", "cpu_top_functions",
        "cpu_top_functions_batch", "diagnose_high_wait", "diagnose_slow_startup", "diagnose_window",
        "disk_io_caller_callee", "disk_io_top_stacks", "file_io_caller_callee", "file_io_top_files",
        "file_io_top_stacks", "find_marker", "generic_event_caller_callee", "generic_event_top_stacks",
        "hard_fault_by_file", "hard_fault_caller_callee", "hard_fault_top_stacks",
        "heap_alloc_caller_callee", "heap_alloc_top_stacks", "image_load_caller_callee",
        "image_load_timing", "image_load_top_gaps", "image_load_top_stacks", "inspect_trace",
        "get_tool_contract", "interrupt_caller_callee", "interrupt_top_stacks", "list_capabilities", "list_processes", "load_trace",
        "memory_resource_analysis", "net_caller_callee", "net_connections", "net_top_stacks",
        "prepare_symbols", "process_create_timing", "ready_thread_caller_callee",
        "ready_thread_top_stacks", "registry_caller_callee", "registry_top_stacks",
        "security_scan_analysis", "thread_lifetime", "unload_trace", "virtual_alloc_caller_callee",
        "virtual_alloc_top_stacks", "wait_analysis", "wait_caller_callee", "wait_top_stacks",
    };

    private static readonly IReadOnlyDictionary<string, ReviewedToolPolicy> ReviewedPolicies =
        BuildPolicies();

    private readonly IReadOnlyDictionary<string, ImmutableArray<ReviewedSectionRule>> _rules;
    private readonly IReadOnlyDictionary<string, ReviewedToolPolicy> _policies;

    internal ReviewedToolOutcomeAdapterRegistry(IReadOnlyList<ActiveToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var active = tools.Select(tool => tool.ToolName).ToHashSet(StringComparer.Ordinal);
        if (!active.SetEquals(ReviewedToolNames))
        {
            throw new InvalidOperationException(
                "Reviewed outcome adapters do not close over the active tool catalog: " +
                $"missing=[{string.Join(',', active.Except(ReviewedToolNames))}], " +
                $"inactive=[{string.Join(',', ReviewedToolNames.Except(active))}].");
        }

        _policies = ReviewedPolicies;
        _rules = tools.ToDictionary(tool => tool.ToolName, BuildRules, StringComparer.Ordinal);
        if (!active.SetEquals(_policies.Keys))
        {
            throw new InvalidOperationException(
                "Reviewed semantic policies do not close over the active tool catalog: " +
                $"missing=[{string.Join(',', active.Except(_policies.Keys))}], " +
                $"inactive=[{string.Join(',', _policies.Keys.Except(active))}].");
        }
        foreach (var tool in tools)
        {
            ValidateRuntimeFields(tool);
            ValidatePolicy(tool, _policies[tool.ToolName]);
            ValidateSectionRules(tool, _rules[tool.ToolName]);
        }
    }

    internal ReviewedToolInvocationPlan Plan(
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        if (!_rules.TryGetValue(tool.ToolName, out var rules))
            throw new InvalidOperationException($"No reviewed outcome adapter exists for '{tool.ToolName}'.");
        if (!_policies.TryGetValue(tool.ToolName, out var policy))
            throw new InvalidOperationException($"No reviewed semantic policy exists for '{tool.ToolName}'.");
        var publicArguments = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : arguments.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        var innerArguments = publicArguments.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        foreach (var parameter in rules
                     .Where(rule => rule.ProofMode == ReviewedSectionProofMode.TopPlusOne)
                     .Select(rule => rule.LimitParameter!)
                     .Distinct(StringComparer.Ordinal))
        {
            var definition = tool.Method.GetParameters().Single(item => item.Name == parameter);
            var requested = publicArguments.TryGetValue(parameter, out var supplied)
                ? supplied.GetInt32()
                : Convert.ToInt32(definition.DefaultValue, CultureInfo.InvariantCulture);
            if (parameter == "top" &&
                (ReadBoolean(publicArguments, "compactStacks") == true ||
                 ReadBoolean(publicArguments, "summaryOnly") == true))
                requested = Math.Min(requested, StackResponseOptions.CompactTopLimit);
            innerArguments[parameter] = JsonSerializer.SerializeToElement(checked(requested + 1));
        }

        // compactStacks/summaryOnly are documented row caps only. Disable their
        // internal cap for the top+1 probe, then trim back to the public effective top.
        if (rules.Any(rule => rule.LimitParameter == "top"))
        {
            if (innerArguments.ContainsKey("compactStacks"))
                innerArguments["compactStacks"] = JsonSerializer.SerializeToElement(false);
            if (innerArguments.ContainsKey("summaryOnly"))
                innerArguments["summaryOnly"] = JsonSerializer.SerializeToElement(false);
        }

        return new ReviewedToolInvocationPlan(tool, publicArguments, innerArguments, rules, policy);
    }

    internal ImmutableArray<ReviewedSectionRule> SectionRulesFor(string toolName) =>
        _rules.TryGetValue(toolName, out var rules)
            ? rules
            : throw new InvalidOperationException(
                $"No reviewed section contract exists for '{toolName}'.");

    private static ImmutableArray<ReviewedSectionRule> BuildRules(ActiveToolDefinition tool)
    {
        if (tool.PageableSections.Length == 0)
            return [];

        if (tool.ToolName == "list_capabilities")
            return [Section(tool, "/capabilities", ReviewedSectionProofMode.DomainCursor)];
        if (TimelinePagination.IsTimelineTool(tool.ToolName))
            return tool.PageableSections
                .Select(pointer => Section(
                    tool,
                    pointer,
                    ReviewedSectionProofMode.DomainCursor,
                    limitParameter: "pageSize"))
                .ToImmutableArray();
        if (tool.ToolName == "prepare_symbols")
            return [Section(tool, "/modulePdbIdentities", ReviewedSectionProofMode.TopPlusOne, "top")];
        if (ReviewedPolicies[tool.ToolName].DataPolicy ==
            ReviewedDataPolicyKind.CallerCalleeStackCoverage)
        {
            return tool.PageableSections.Select(pointer => Section(
                tool,
                pointer,
                pointer == "/stats/topUnresolvedModules"
                    ? ReviewedSectionProofMode.FixedLimitExactTotal
                    : ReviewedSectionProofMode.TopPlusOne,
                pointer == "/stats/topUnresolvedModules" ? null : "top",
                fixedLimit: pointer == "/stats/topUnresolvedModules" ? 10 : null,
                sortKey: pointer == "/stats/topUnresolvedModules"
                    ? "unresolved_frame_count_desc"
                    : "inclusive_metric_desc",
                direction: ToolSortDirection.Descending,
                tieBreakers: pointer == "/stats/topUnresolvedModules"
                    ? ["module_name_ordinal_asc"]
                    : ["function_ordinal_asc"]))
                .ToImmutableArray();
        }
        if (tool.ToolName == "net_connections")
        {
            return
            [
                Section(
                    tool,
                    "/connections",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "observed_duration_us_desc_nulls_last",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "open_time_us_asc",
                        "pid_asc",
                        "process_start_us_asc",
                        "conn_id_text_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "memory_resource_analysis")
        {
            return
            [
                Section(tool, "/processes", ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "working_set_bytes_then_commit_bytes", direction: ToolSortDirection.Descending,
                    tieBreakers: ["pid_asc", "process_start_us_asc"]),
                Section(tool, "/handles", ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "absolute_net_delta_then_operation_count", direction: ToolSortDirection.Descending,
                    tieBreakers: ["pid_asc", "process_start_us_asc"]),
                Section(tool, "/poolProcesses", ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "outstanding_bytes_then_allocated_bytes", direction: ToolSortDirection.Descending,
                    tieBreakers: ["pid_asc", "process_start_us_asc"]),
                Section(tool, "/poolTags", ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "outstanding_bytes_then_allocated_bytes", direction: ToolSortDirection.Descending,
                    tieBreakers: ["tag_asc", "pool_kind_asc"]),
                Section(tool, "/systemMemory", ReviewedSectionProofMode.FixedLimitConservative,
                    fixedLimit: 100, sortKey: "time_us", direction: ToolSortDirection.Ascending,
                    tieBreakers: ["source_sequence_asc"]),
                Section(tool, "/pressure/topPeakWorkingSetProcesses",
                    ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "peak_working_set_bytes_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: ["peak_commit_bytes_desc", "pid_asc", "process_start_us_asc"]),
                Section(tool, "/pressure/topPeakCommitProcesses",
                    ReviewedSectionProofMode.TopPlusOne, "top",
                    sortKey: "peak_commit_bytes_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: ["peak_working_set_bytes_desc", "pid_asc", "process_start_us_asc"]),
            ];
        }
        if (tool.ToolName == "clr_finalizer_analysis")
        {
            return
            [
                Section(tool, "/batches", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "start_us", direction: ToolSortDirection.Ascending,
                    tieBreakers:
                    [
                        "pid_asc",
                        "process_start_us_asc",
                        "end_us_asc",
                        "finalizers_run_asc",
                        "accounted_duration_us_asc",
                        "full_duration_us_asc",
                    ],
                    measurementBasis: MeasurementBasis.Derived,
                    relationship: Relationship.Temporal),
                Section(tool, "/topTypes", ReviewedSectionProofMode.FixedLimitConservative,
                    fixedLimit: 20, sortKey: "finalized_count", direction: ToolSortDirection.Descending,
                    tieBreakers: ["type_name_ordinal_asc"],
                    measurementBasis: MeasurementBasis.Derived,
                    relationship: Relationship.Descriptive),
            ];
        }
        if (tool.ToolName == "clr_gc_analysis")
        {
            return
            [
                Section(
                    tool,
                    "/events",
                    ReviewedSectionProofMode.Exhaustive,
                    sortKey: "start_us_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers:
                    [
                        "pid_asc",
                        "process_start_us_asc",
                        "end_us_asc",
                        "generation_asc",
                        "reason_ordinal_asc",
                        "interval_kind_ordinal_asc",
                        "clr_instance_id_asc_nulls_first",
                        "gc_count_asc_nulls_first",
                        "is_orphan_pause_asc",
                        "accounted_duration_us_asc",
                        "accounted_pause_us_asc_nulls_first",
                    ],
                    measurementBasis: MeasurementBasis.Derived,
                    relationship: Relationship.Temporal),
            ];
        }
        if (tool.ToolName == "clr_gc_heap_stats")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.Exhaustive,
                    sortKey: "time_us_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: ["pid_asc", "process_start_us_asc"],
                    measurementBasis: MeasurementBasis.Direct,
                    relationship: Relationship.Descriptive),
            ];
        }
        if (tool.ToolName == "inspect_trace")
        {
            var exactTotalLimits = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["/metadata/providerEvents/topProviders"] = 50,
                ["/metadata/drivers/topDrivers"] = 50,
                ["/symbolQuality/topModulesMissingPdbName"] = 20,
            };
            return tool.PageableSections.Select(pointer =>
                pointer is "/traceEvidenceMap/workflows" or "/traceEvidenceMap/capabilities"
                    ? Section(
                        tool,
                        pointer,
                        ReviewedSectionProofMode.DomainCursor,
                        sortKey: pointer == "/traceEvidenceMap/capabilities"
                            ? "domain_then_capability_id_asc"
                            : "workflow_id_asc",
                        direction: ToolSortDirection.Ascending,
                        tieBreakers: pointer == "/traceEvidenceMap/capabilities"
                            ? ["capability_id_asc"]
                            : [])
                    : exactTotalLimits.TryGetValue(pointer, out var exactLimit)
                        ? Section(
                            tool,
                            pointer,
                            ReviewedSectionProofMode.FixedLimitExactTotal,
                            fixedLimit: exactLimit,
                            sortKey: pointer == "/metadata/providerEvents/topProviders"
                                ? "event_count_desc"
                                : "module_name_ordinal_ignore_case_asc",
                            direction: pointer == "/metadata/providerEvents/topProviders"
                                ? ToolSortDirection.Descending
                                : ToolSortDirection.Ascending,
                            tieBreakers: pointer == "/metadata/providerEvents/topProviders"
                                ? ["provider_name_ordinal_ignore_case_asc"]
                                : [])
                    : pointer == "/symbolQuality/frameResolution/topUnresolvedModules"
                        ? Section(
                            tool,
                            pointer,
                            ReviewedSectionProofMode.FixedLimitExactTotal,
                            fixedLimit: 10,
                            sortKey: "unresolved_frame_count_desc",
                            direction: ToolSortDirection.Descending,
                            tieBreakers: ["module_name_ordinal_asc"])
                    : Section(
                        tool,
                        pointer,
                        ReviewedSectionProofMode.Exhaustive)).ToImmutableArray();
        }
        if (tool.ToolName == "clr_jit_analysis")
        {
            return
            [
                Section(
                    tool,
                    "/topMethods",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "accounted_duration_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "start_us_asc",
                        "pid_asc",
                        "process_start_us_asc",
                        "method_ordinal_asc",
                        "end_us_asc",
                        "method_il_size_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "cpu_precise_analysis")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "cpu_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "ready_latency_us_desc",
                        "pid_asc",
                        "process_start_us_asc",
                        "tid_asc",
                        "thread_generation_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "file_io_top_files")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "total_bytes_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: ["file_path_ordinal_asc"]),
            ];
        }
        if (tool.ToolName == "hard_fault_by_file")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "caller_selected_metric_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "page_in_bytes_desc",
                        "page_in_count_desc",
                        "file_path_ordinal_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "image_load_top_gaps")
        {
            return
            [
                Section(
                    tool,
                    "/topGaps",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "gap_from_previous_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: ["time_us_asc", "event_index_asc"]),
            ];
        }
        if (tool.ToolName == "list_processes")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.DomainCursor,
                    sortKey: "caller_selected_metric_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "wall_us_desc_when_wait_ratio",
                        "pid_asc",
                        "process_start_us_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "wait_analysis")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "blocked_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "cpu_us_desc",
                        "pid_asc",
                        "process_start_us_asc",
                        "tid_asc",
                        "thread_generation_asc",
                    ]),
            ];
        }
        if (tool.ToolName == "find_marker")
        {
            return
            [
                Section(
                    tool,
                    "/counts",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "count_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: ["key_ordinal_asc"],
                    measurementBasis: MeasurementBasis.Derived),
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "trace_event_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: [],
                    measurementBasis: MeasurementBasis.Direct),
            ];
        }
        if (tool.ToolName == "security_scan_analysis")
        {
            return
            [
                Section(
                    tool,
                    "/rows",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "total_accounted_duration_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "event_count_desc",
                        "paired_scan_count_desc",
                        "source_ordinal_asc",
                        "provider_name_ordinal_asc",
                        "process_name_ordinal_asc",
                        "pid_asc",
                        "path_ordinal_asc",
                        "process_start_us_asc_nulls_first",
                        "target_identity_source_ordinal_asc",
                        "evidence_kind_ordinal_asc",
                        "provenance_ordinal_asc",
                        "confidence_ordinal_asc",
                    ]),
                Section(
                    tool,
                    "/slowScans",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "accounted_duration_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "start_us_asc",
                        "source_ordinal_asc",
                        "provider_name_ordinal_asc",
                        "id_ordinal_asc",
                        "pid_asc",
                        "process_start_us_asc_nulls_first",
                        "process_name_ordinal_asc",
                        "path_ordinal_asc",
                        "stop_us_asc",
                        "evidence_kind_ordinal_asc",
                        "provenance_ordinal_asc",
                        "confidence_ordinal_asc",
                        "target_identity_source_ordinal_asc",
                        "reason_ordinal_asc",
                    ]),
                Section(
                    tool,
                    "/providers",
                    ReviewedSectionProofMode.TopPlusOne,
                    "top",
                    sortKey: "event_count_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "source_ordinal_asc",
                        "provider_name_ordinal_asc",
                        "evidence_kind_ordinal_asc",
                        "provenance_ordinal_asc",
                        "confidence_ordinal_asc",
                    ]),
            ];
        }

        var fixedTwenty = tool.ToolName switch
        {
            "clr_alloc_top_stacks" => "/topTypes",
            "clr_exception_top_stacks" => "/topTypes",
            "generic_event_top_stacks" => "/topEventNames",
            _ => null,
        };
        if (fixedTwenty is not null)
        {
            return tool.PageableSections.Select(pointer => pointer == fixedTwenty
                ? Section(
                    tool,
                    pointer,
                    ReviewedSectionProofMode.FixedLimitConservative,
                    fixedLimit: 20,
                    sortKey: pointer == "/topTypes" && tool.ToolName == "clr_alloc_top_stacks"
                        ? "bytes_desc"
                        : "count_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers: pointer == "/topEventNames"
                        ? ["event_name_ordinal_asc"]
                        : ["type_name_ordinal_asc"])
                : pointer == "/stats/topUnresolvedModules"
                    ? Section(
                        tool,
                        pointer,
                        ReviewedSectionProofMode.FixedLimitExactTotal,
                        fixedLimit: 10,
                        sortKey: "unresolved_frame_count_desc",
                        direction: ToolSortDirection.Descending,
                        tieBreakers: ["module_name_ordinal_asc"])
                    : pointer == "/when/buckets"
                        ? Section(
                            tool,
                            pointer,
                            ReviewedSectionProofMode.ExactRequestedCount,
                            "whenBuckets",
                            sortKey: "bucket_index_asc",
                            direction: ToolSortDirection.Ascending,
                            tieBreakers: [])
                    : Section(
                        tool,
                        pointer,
                        ReviewedSectionProofMode.TopPlusOne,
                        "top",
                        sortKey: "exclusive_metric_desc",
                        direction: ToolSortDirection.Descending,
                        tieBreakers: ["function_ordinal_asc"]))
                .ToImmutableArray();
        }

        if (ReviewedPolicies[tool.ToolName].DataPolicy == ReviewedDataPolicyKind.StackCoverage)
        {
            return tool.PageableSections.Select(pointer => Section(
                tool,
                pointer,
                pointer switch
                {
                    "/stats/topUnresolvedModules" => ReviewedSectionProofMode.FixedLimitExactTotal,
                    "/when/buckets" => ReviewedSectionProofMode.ExactRequestedCount,
                    _ => ReviewedSectionProofMode.TopPlusOne,
                },
                pointer switch
                {
                    "/stats/topUnresolvedModules" => null,
                    "/when/buckets" => "whenBuckets",
                    _ => "top",
                },
                fixedLimit: pointer == "/stats/topUnresolvedModules" ? 10 : null,
                sortKey: pointer switch
                {
                    "/stats/topUnresolvedModules" => "unresolved_frame_count_desc",
                    "/when/buckets" => "bucket_index_asc",
                    _ => "exclusive_metric_desc",
                },
                direction: pointer == "/when/buckets"
                    ? ToolSortDirection.Ascending
                    : ToolSortDirection.Descending,
                tieBreakers: pointer == "/stats/topUnresolvedModules"
                    ? ["module_name_ordinal_asc"]
                    : pointer == "/when/buckets" ? [] : ["function_ordinal_asc"]))
                .ToImmutableArray();
        }

        if (tool.ToolName == "diagnose_high_wait")
        {
            return
            [
                Section(
                    tool,
                    "/candidates",
                    ReviewedSectionProofMode.TypedEmbeddedBoundary,
                    "maxCandidates",
                    sortKey: "total_blocked_us_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "wait_ratio_desc_nulls_last",
                        "pid_asc",
                        "process_start_us_asc",
                    ]),
                Section(tool, "/evidence", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/notConcluded", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/nextTools", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/executedToolCalls", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "execution_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
            ];
        }
        if (tool.ToolName == "diagnose_slow_startup")
        {
            return
            [
                Section(
                    tool,
                    "/candidates",
                    ReviewedSectionProofMode.TypedEmbeddedBoundary,
                    "maxCandidates",
                    sortKey: "startup_wait_ratio_desc",
                    direction: ToolSortDirection.Descending,
                    tieBreakers:
                    [
                        "observed_startup_wall_us_desc",
                        "process_start_us_asc",
                        "pid_asc",
                    ]),
                Section(tool, "/evidence", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/notConcluded", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/nextTools", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(tool, "/executedToolCalls", ReviewedSectionProofMode.Exhaustive,
                    sortKey: "execution_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(
                    tool,
                    "/firstImageLoadGapEvidence",
                    ReviewedSectionProofMode.Exhaustive,
                    sortKey: "candidate_construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: []),
                Section(
                    tool,
                    "/discovery/excludedSamples",
                    ReviewedSectionProofMode.FixedLimitConservative,
                    fixedLimit: StartupDiscoverySummary.ExcludedSampleLimit,
                    sortKey: "process_start_us_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: ["pid_asc"],
                    role: ToolSectionRole.Boundary),
            ];
        }
        if (tool.ToolName == "diagnose_window")
        {
            var order = new Dictionary<string, (string SortKey, ImmutableArray<string> Ties)>(
                StringComparer.Ordinal)
            {
                ["/hardFaultsByBytes"] = ("page_in_bytes_desc",
                    ["page_in_count_desc", "file_path_ordinal_asc"]),
                ["/hardFaultsByMaxLatency"] = ("max_latency_us_desc",
                    ["page_in_bytes_desc", "page_in_count_desc", "file_path_ordinal_asc"]),
                ["/fileIoTopFiles"] = ("total_bytes_desc", ["file_path_ordinal_asc"]),
                ["/securityScanTargets"] = ("total_accounted_duration_us_desc",
                    [
                        "event_count_desc",
                        "paired_scan_count_desc",
                        "source_ordinal_asc",
                        "provider_name_ordinal_asc",
                        "process_name_ordinal_asc",
                        "pid_asc",
                        "path_ordinal_asc",
                        "process_start_us_asc_nulls_first",
                        "target_identity_source_ordinal_asc",
                        "evidence_kind_ordinal_asc",
                        "provenance_ordinal_asc",
                        "confidence_ordinal_asc",
                    ]),
                ["/slowScans"] = ("accounted_duration_us_desc",
                    [
                        "start_us_asc",
                        "source_ordinal_asc",
                        "provider_name_ordinal_asc",
                        "id_ordinal_asc",
                        "pid_asc",
                        "process_start_us_asc_nulls_first",
                        "process_name_ordinal_asc",
                        "path_ordinal_asc",
                        "stop_us_asc",
                        "evidence_kind_ordinal_asc",
                        "provenance_ordinal_asc",
                        "confidence_ordinal_asc",
                        "target_identity_source_ordinal_asc",
                        "reason_ordinal_asc",
                    ]),
                ["/waits"] = ("blocked_us_desc",
                    ["cpu_us_desc", "pid_asc", "process_start_us_asc", "tid_asc", "thread_generation_asc"]),
                ["/pressure/topPeakWorkingSetProcesses"] = ("peak_working_set_bytes_desc",
                    ["peak_commit_bytes_desc", "pid_asc", "process_start_us_asc"]),
                ["/pressure/topPeakCommitProcesses"] = ("peak_commit_bytes_desc",
                    ["peak_working_set_bytes_desc", "pid_asc", "process_start_us_asc"]),
            };
            return tool.PageableSections.Select(pointer => order.TryGetValue(pointer, out var spec)
                ? Section(
                    tool,
                    pointer,
                    ReviewedSectionProofMode.ConservativeLimit,
                    "top",
                    sortKey: spec.SortKey,
                    direction: ToolSortDirection.Descending,
                    tieBreakers: spec.Ties)
                : Section(
                    tool,
                    pointer,
                    ReviewedSectionProofMode.Exhaustive,
                    sortKey: pointer == "/executedToolCalls"
                        ? "execution_sequence_asc"
                        : "construction_sequence_asc",
                    direction: ToolSortDirection.Ascending,
                    tieBreakers: [])).ToImmutableArray();
        }

        if (tool.Method.GetParameters().Any(parameter => parameter.Name == "top" && parameter.ParameterType == typeof(int)))
            return tool.PageableSections.Select(pointer =>
                pointer == "/stats/topUnresolvedModules"
                    ? Section(tool, pointer, ReviewedSectionProofMode.FixedLimitExactTotal, fixedLimit: 10)
                    : Section(tool, pointer, ReviewedSectionProofMode.TopPlusOne, "top"))
                .ToImmutableArray();

        // A reviewed section without a caller limit is an exhaustive projection.
        return tool.PageableSections.Select(pointer =>
            Section(tool, pointer, ReviewedSectionProofMode.Exhaustive)).ToImmutableArray();
    }

    private static IReadOnlyDictionary<string, ReviewedToolPolicy> BuildPolicies()
    {
        var result = new Dictionary<string, ReviewedToolPolicy>(StringComparer.Ordinal)
        {
            ["alpc_caller_callee"] = CallerCallee(),
            ["alpc_top_stacks"] = Stacks(),
            ["clr_alloc_caller_callee"] = CallerCallee(),
            ["clr_alloc_top_stacks"] = Stacks(),
            ["clr_contention_caller_callee"] = CallerCallee(),
            ["clr_contention_top_stacks"] = Stacks(),
            ["clr_exception_caller_callee"] = CallerCallee(),
            ["clr_exception_top_stacks"] = Stacks(),
            ["clr_finalizer_analysis"] = TemporalSections("finalizer_intervals"),
            ["clr_gc_analysis"] = TemporalSections("gc_intervals"),
            ["clr_gc_heap_stats"] = DescriptiveSections("gc_heap_samples"),
            ["clr_jit_analysis"] = TemporalSections("jit_events"),
            ["cpu_caller_callee"] = CallerCallee(),
            ["cpu_precise_analysis"] = DescriptiveSections("precise_cpu_events"),
            ["cpu_top_functions"] = Stacks(),
            ["cpu_top_functions_batch"] = new(
                "cpu_batch_per_pid",
                ReviewedDataPolicyKind.CpuBatchScopeResults,
                null,
                "no_candidates_in_considered_input",
                ReviewedScopeSource.Trace,
                ReviewedCapabilityEvaluator.ObservedData,
                MeasurementBasis.Derived,
                Relationship.Association,
                ConclusionStatus.NotConcluded,
                PartialField: "partial",
                PartialFailureEvaluator: ReviewedPartialFailureEvaluator.CpuBatchScopeResults),
            ["diagnose_high_wait"] = new(
                "high_wait_composite",
                ReviewedDataPolicyKind.DeclaredSections,
                null,
                "no_events_in_scope",
                ReviewedScopeSource.ResultFields,
                ReviewedCapabilityEvaluator.ResultFields,
                MeasurementBasis.Derived,
                Relationship.Association,
                ConclusionStatus.NotConcluded,
                PartialField: "partial",
                PartialFailureEvaluator: ReviewedPartialFailureEvaluator.HighWaitBudget),
            ["diagnose_slow_startup"] = HeuristicSections(
                "slow_startup_composite",
                ReviewedScopeSource.Trace,
                ReviewedCapabilityEvaluator.ObservedData),
            ["diagnose_window"] = HeuristicSections("window_composite"),
            ["disk_io_caller_callee"] = CallerCallee(),
            ["disk_io_top_stacks"] = Stacks(),
            ["file_io_caller_callee"] = CallerCallee(),
            ["file_io_top_files"] = DescriptiveSections("file_io_events"),
            ["file_io_top_stacks"] = Stacks(),
            ["find_marker"] = DescriptiveSections(
                "marker_events",
                ReviewedScopeSource.Trace,
                ReviewedCapabilityEvaluator.ObservedData),
            ["generic_event_caller_callee"] = CallerCallee(),
            ["generic_event_top_stacks"] = Stacks(),
            ["get_tool_contract"] = new(
                "content_addressed_output_contract_page",
                ReviewedDataPolicyKind.AlwaysUsable,
                null,
                "contract_page_unavailable",
                ReviewedScopeSource.NotApplicable,
                ReviewedCapabilityEvaluator.LifecycleSuccess,
                MeasurementBasis.Metadata,
                Relationship.Descriptive,
                ConclusionStatus.Observed),
            ["hard_fault_by_file"] = AssociationSections("hard_fault_file_association"),
            ["hard_fault_caller_callee"] = CallerCallee(),
            ["hard_fault_top_stacks"] = Stacks(),
            ["heap_alloc_caller_callee"] = CallerCallee(),
            ["heap_alloc_top_stacks"] = Stacks(),
            ["image_load_caller_callee"] = CallerCallee(),
            ["image_load_timing"] = TemporalSections("image_load_timing"),
            ["image_load_top_gaps"] = TemporalSections("image_load_gaps"),
            ["image_load_top_stacks"] = Stacks(),
            ["inspect_trace"] = Lifecycle(
                "inspect_trace_metadata",
                MeasurementBasis.Metadata,
                ReviewedScopeSource.Trace),
            ["interrupt_caller_callee"] = CallerCallee(),
            ["interrupt_top_stacks"] = Stacks(),
            ["list_capabilities"] = new(
                "validated_capability_catalog",
                ReviewedDataPolicyKind.CollectionPointer,
                "/capabilities",
                "no_capabilities_match_filter",
                ReviewedScopeSource.NotApplicable,
                ReviewedCapabilityEvaluator.LifecycleSuccess,
                MeasurementBasis.Metadata,
                Relationship.Descriptive,
                ConclusionStatus.Observed),
            ["list_processes"] = DescriptiveSections(
                "process_lifetimes",
                ReviewedScopeSource.Trace,
                ReviewedCapabilityEvaluator.ObservedData),
            ["load_trace"] = Lifecycle("load_trace_metadata", MeasurementBasis.Metadata),
            ["memory_resource_analysis"] = DescriptiveSections("memory_resource_events"),
            ["net_caller_callee"] = CallerCallee(),
            ["net_connections"] = TemporalSections("network_connections"),
            ["net_top_stacks"] = Stacks(),
            ["prepare_symbols"] = Lifecycle("symbol_context_metadata", MeasurementBasis.Metadata),
            ["process_create_timing"] = TemporalSections("process_create_events"),
            ["ready_thread_caller_callee"] = CallerCallee(),
            ["ready_thread_top_stacks"] = Stacks(),
            ["registry_caller_callee"] = CallerCallee(),
            ["registry_top_stacks"] = Stacks(),
            ["security_scan_analysis"] = HeuristicSections("security_event_heuristic"),
            ["thread_lifetime"] = TemporalSections("thread_lifetimes"),
            ["unload_trace"] = Lifecycle("trace_retirement_metadata", MeasurementBasis.Metadata),
            ["virtual_alloc_caller_callee"] = CallerCallee(),
            ["virtual_alloc_top_stacks"] = Stacks(),
            ["wait_analysis"] = DescriptiveSections("wait_intervals"),
            ["wait_caller_callee"] = CallerCallee(),
            ["wait_top_stacks"] = Stacks(),
        };
        return result;
    }

    private static ReviewedToolPolicy CallerCallee() => new(
        "caller_callee_association",
        ReviewedDataPolicyKind.CallerCalleeStackCoverage,
        null,
        "focus_not_found",
        ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator.ResultFields,
        MeasurementBasis.Derived,
        Relationship.Association,
        ConclusionStatus.NotConcluded);

    private static ReviewedToolPolicy Stacks() => new(
        "event_stack_association",
        ReviewedDataPolicyKind.StackCoverage,
        null,
        "stacks_unavailable",
        ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator.ResultFields,
        MeasurementBasis.Derived,
        Relationship.Association,
        ConclusionStatus.NotConcluded);

    private static ReviewedToolPolicy DescriptiveSections(
        string id,
        ReviewedScopeSource scope = ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator capability = ReviewedCapabilityEvaluator.ResultFields) => new(
            id,
            ReviewedDataPolicyKind.DeclaredSections,
            null,
            "no_events_in_scope",
            scope,
            capability,
            MeasurementBasis.Derived,
            Relationship.Descriptive,
            ConclusionStatus.Observed);

    private static ReviewedToolPolicy TemporalSections(string id) => new(
        id,
        ReviewedDataPolicyKind.DeclaredSections,
        null,
        "no_events_in_scope",
        ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator.ResultFields,
        MeasurementBasis.Derived,
        Relationship.Temporal,
        ConclusionStatus.Observed);

    private static ReviewedToolPolicy AssociationSections(string id) => new(
        id,
        ReviewedDataPolicyKind.DeclaredSections,
        null,
        "no_events_in_scope",
        ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator.ResultFields,
        MeasurementBasis.Derived,
        Relationship.Association,
        ConclusionStatus.NotConcluded);

    private static ReviewedToolPolicy HeuristicSections(
        string id,
        ReviewedScopeSource scope = ReviewedScopeSource.ResultFields,
        ReviewedCapabilityEvaluator capability = ReviewedCapabilityEvaluator.ResultFields) => new(
            id,
            ReviewedDataPolicyKind.DeclaredSections,
            null,
            "no_candidates_in_considered_input",
            scope,
            capability,
            MeasurementBasis.Heuristic,
            Relationship.Association,
            ConclusionStatus.NotConcluded);

    private static ReviewedToolPolicy Lifecycle(
        string id,
        MeasurementBasis basis,
        ReviewedScopeSource scope = ReviewedScopeSource.NotApplicable) => new(
        id,
        ReviewedDataPolicyKind.AlwaysUsable,
        null,
        "no_candidates_in_considered_input",
        scope,
        ReviewedCapabilityEvaluator.LifecycleSuccess,
        basis,
        Relationship.Descriptive,
        ConclusionStatus.Observed);

    private static void ValidatePolicy(ActiveToolDefinition tool, ReviewedToolPolicy policy)
    {
        if (!tool.AllowedMeasurementBases.Contains(Wire(policy.MeasurementBasis), StringComparer.Ordinal))
            throw new InvalidOperationException($"Reviewed policy '{policy.PolicyId}' uses an unadvertised measurement basis for '{tool.ToolName}'.");
        if (RelationshipRank(policy.Relationship) > RelationshipRank(tool.MaximumRelationship))
            throw new InvalidOperationException($"Reviewed policy '{policy.PolicyId}' exceeds the relationship ceiling for '{tool.ToolName}'.");
        if (policy.DataPolicy == ReviewedDataPolicyKind.DeclaredSections && tool.PageableSections.Length == 0)
            throw new InvalidOperationException($"Reviewed policy '{policy.PolicyId}' requires declared sections for '{tool.ToolName}'.");
        if (policy.DataPolicy == ReviewedDataPolicyKind.CollectionPointer)
            RequireOutputProperty(tool, policy.CollectionPointer!.Trim('/'));
        if (policy.DataPolicy == ReviewedDataPolicyKind.CpuBatchScopeResults)
            RequireOutputProperty(tool, "scopeResults");
        if (policy.DataPolicy is ReviewedDataPolicyKind.StackCoverage or
            ReviewedDataPolicyKind.CallerCalleeStackCoverage)
        {
            RequireOutputProperty(tool, "stackCoverage");
        }
        if (policy.DataPolicy == ReviewedDataPolicyKind.CallerCalleeStackCoverage)
        {
            RequireOutputProperty(tool, "focusFunction");
            RequireOutputProperty(tool, "focusInclusiveMetric");
        }
        if (policy.ScopeSource == ReviewedScopeSource.ResultFields)
        {
            RequireOutputProperty(tool, "scopeStatus");
            RequireOutputProperty(tool, "scopeMode");
        }
        if (policy.CapabilityEvaluator == ReviewedCapabilityEvaluator.ResultFields)
        {
            RequireOutputProperty(tool, "capabilityStatus");
            RequireOutputProperty(tool, "matchedEventCount");
            RequireOutputProperty(tool, "noDataReason");
        }
        if (policy.PartialField is not null)
        {
            RequireOutputProperty(tool, policy.PartialField);
            if (policy.PartialFailureEvaluator == ReviewedPartialFailureEvaluator.None)
                throw new InvalidOperationException($"Reviewed partial policy for '{tool.ToolName}' lacks a typed partial-failure evaluator.");
        }
    }

    private static void ValidateSectionRules(
        ActiveToolDefinition tool,
        ImmutableArray<ReviewedSectionRule> rules)
    {
        var declared = tool.PageableSections.ToHashSet(StringComparer.Ordinal);
        var reviewed = rules.Select(rule => rule.Pointer).ToArray();
        if (reviewed.Distinct(StringComparer.Ordinal).Count() != reviewed.Length ||
            !declared.SetEquals(reviewed))
        {
            throw new InvalidOperationException(
                $"Reviewed section rules do not exactly cover pageable sections for '{tool.ToolName}'.");
        }

        foreach (var rule in rules)
        {
            var isDomain = rule.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;
            if (isDomain != (rule.EvidenceIds.Length > 0))
            {
                throw new InvalidOperationException(
                    $"Section '{tool.ToolName}{rule.Pointer}' must bind evidence only when it is domain data/evidence.");
            }
            if (rule.SortKey is null)
            {
                if (rule.SortDirection != ToolSortDirection.NotApplicable ||
                    rule.TieBreakers.Length != 0)
                {
                    throw new InvalidOperationException(
                        $"Unordered section '{tool.ToolName}{rule.Pointer}' has contradictory comparator metadata.");
                }
            }
            else if (rule.SortDirection == ToolSortDirection.NotApplicable)
            {
                throw new InvalidOperationException(
                    $"Ordered section '{tool.ToolName}{rule.Pointer}' lacks a sort direction.");
            }
            if (rule.TieBreakers.Any(item =>
                    item is "section_defined_order" or "stable_identity_asc"))
            {
                throw new InvalidOperationException(
                    $"Section '{tool.ToolName}{rule.Pointer}' contains a placeholder comparator token.");
            }
            if (rule.Mode == ToolSectionMode.Cursor && rule.SortKey is null)
            {
                throw new InvalidOperationException(
                    $"Cursor section '{tool.ToolName}{rule.Pointer}' requires a stable total order.");
            }
            if (!tool.AllowedMeasurementBases.Contains(
                    Wire(rule.MeasurementBasis),
                    StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Section '{tool.ToolName}{rule.Pointer}' uses an unadvertised measurement basis.");
            }
            if (RelationshipRank(rule.Relationship) > RelationshipRank(tool.MaximumRelationship))
            {
                throw new InvalidOperationException(
                    $"Section '{tool.ToolName}{rule.Pointer}' exceeds the tool relationship ceiling.");
            }
        }
    }

    private static void RequireOutputProperty(ActiveToolDefinition tool, string wireName)
    {
        var property = tool.OutputDataType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(candidate => string.Equals(
                JsonNamingPolicy.CamelCase.ConvertName(candidate.Name),
                wireName,
                StringComparison.Ordinal));
        if (property is null)
        {
            throw new InvalidOperationException(
                $"Reviewed policy for '{tool.ToolName}' requires output field '{wireName}'.");
        }
    }

    private static string Wire(MeasurementBasis value) => value switch
    {
        MeasurementBasis.Direct => "direct",
        MeasurementBasis.Derived => "derived",
        MeasurementBasis.Heuristic => "heuristic",
        MeasurementBasis.Metadata => "metadata",
        MeasurementBasis.Unmeasured => "unmeasured",
        _ => throw new InvalidOperationException("Unknown measurement basis."),
    };

    private static int RelationshipRank(Relationship value) => value switch
    {
        Relationship.Descriptive => 0,
        Relationship.Temporal => 1,
        Relationship.Association => 2,
        Relationship.Attribution => 3,
        Relationship.Causal => 4,
        _ => throw new InvalidOperationException("Unknown relationship."),
    };

    private static int RelationshipRank(string value) => value switch
    {
        "descriptive" => 0,
        "temporal" => 1,
        "association" => 2,
        "attribution" => 3,
        "causal" => 4,
        _ => throw new InvalidOperationException("Unknown relationship ceiling."),
    };

    private static ImmutableArray<ReviewedSectionRule> Rules(
        ActiveToolDefinition tool,
        IReadOnlyDictionary<string, string> controlled) =>
        tool.PageableSections.Select(pointer => controlled.TryGetValue(pointer, out var parameter)
            ? Section(tool, pointer, ReviewedSectionProofMode.TopPlusOne, parameter)
            : Section(tool, pointer, ReviewedSectionProofMode.Exhaustive)).ToImmutableArray();

    private static ReviewedSectionRule Section(
        ActiveToolDefinition tool,
        string pointer,
        ReviewedSectionProofMode mode,
        string? limitParameter = null,
        int? fixedLimit = null,
        string? sortKey = null,
        ToolSortDirection? direction = null,
        ImmutableArray<string>? tieBreakers = null,
        string emptyNoDataReason = "no_candidates_in_considered_input",
        ToolSectionRole? role = null,
        ImmutableArray<string>? evidenceIds = null,
        MeasurementBasis? measurementBasis = null,
        Relationship? relationship = null,
        ConclusionStatus? conclusionStatus = null)
    {
        role ??= SectionRole(tool.ToolName, pointer);
        var isDomain = role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;
        var isDiagnostic = role == ToolSectionRole.Diagnostic;
        var policy = ReviewedPolicies[tool.ToolName];
        var sectionMode = mode switch
        {
            ReviewedSectionProofMode.DomainCursor => ToolSectionMode.Cursor,
            ReviewedSectionProofMode.TopPlusOne or
            ReviewedSectionProofMode.ConservativeLimit or
            ReviewedSectionProofMode.FixedLimitConservative or
            ReviewedSectionProofMode.FixedLimitExactTotal or
            ReviewedSectionProofMode.TypedEmbeddedBoundary => ToolSectionMode.TopN,
            ReviewedSectionProofMode.ExactRequestedCount => ToolSectionMode.None,
            _ => ToolSectionMode.None,
        };
        var inheritsToolOrdering = isDomain &&
            tool.DefaultOrdering is not ("not_applicable" or "section_specific");
        sortKey ??= inheritsToolOrdering ? tool.DefaultOrdering : null;
        direction ??= sortKey?.EndsWith("_asc", StringComparison.Ordinal) == true
            ? ToolSortDirection.Ascending
            : sortKey?.EndsWith("_desc", StringComparison.Ordinal) == true
                ? ToolSortDirection.Descending
                : ToolSortDirection.NotApplicable;
        tieBreakers ??= sortKey is null || tool.TieBreakers.Any(item =>
                item is "section_defined_order" or "stable_identity_asc")
            ? ImmutableArray<string>.Empty
            : tool.TieBreakers;
        evidenceIds ??= isDomain
            ? tool.EvidenceReferenceIds
            : ImmutableArray<string>.Empty;
        measurementBasis ??= isDomain
            ? policy.MeasurementBasis
            : isDiagnostic ? MeasurementBasis.Metadata : MeasurementBasis.Unmeasured;
        relationship ??= isDomain ? policy.Relationship : Relationship.Descriptive;
        conclusionStatus ??= isDomain
            ? policy.DataConclusion
            : isDiagnostic ? ConclusionStatus.Observed : ConclusionStatus.NotApplicable;
        return new ReviewedSectionRule(
            pointer,
            mode,
            limitParameter,
            fixedLimit,
            sectionMode,
            sortKey,
            direction.Value,
            tieBreakers.Value,
            emptyNoDataReason,
            role.Value,
            evidenceIds.Value,
            measurementBasis.Value,
            relationship.Value,
            conclusionStatus.Value);
    }

    private static ToolSectionRole SectionRole(string toolName, string pointer)
    {
        if (pointer is "/notConcluded")
            return ToolSectionRole.Boundary;
        if (pointer is "/nextTools" or "/orientationTools" or "/recommendedDiagnosticFlows")
            return ToolSectionRole.Recommendation;
        if (pointer is "/executedToolCalls" or "/traceEvidenceMap/workflows" or "/traceEvidenceMap/capabilities")
            return ToolSectionRole.Provenance;
        if (pointer is "/stats/topUnresolvedModules" or
            "/symbolQuality/topUnresolvedModules" or
            "/symbolQuality/frameResolution/topUnresolvedModules" or
            "/symbolQuality/topModulesMissingPdbName" ||
            (toolName == "inspect_trace" && pointer is (
                "/metadata/providerEvents/topProviders" or "/metadata/drivers/topDrivers")))
            return ToolSectionRole.Diagnostic;
        if (toolName == "inspect_trace" && pointer is (
            "/capabilitySupportedTools" or "/enabledCapabilities"))
            return ToolSectionRole.Provenance;
        if (pointer is "/evidence" or "/firstImageLoadGapEvidence" or "/providers")
        {
            return ToolSectionRole.DomainEvidence;
        }
        return ToolSectionRole.DomainData;
    }

    private static void ValidateRuntimeFields(ActiveToolDefinition tool)
    {
        var parameters = tool.Method.GetParameters();
        var hasScalarScope = parameters.Any(parameter => parameter.Name is "pid" or "tid" or "awakenedPid" or "parentPid");
        var outputType = tool.OutputDataType;
        if (hasScalarScope && outputType.GetProperty("ScopeStatus") is null)
        {
            throw new InvalidOperationException(
                $"Reviewed scoped tool '{tool.ToolName}' does not expose ScopeStatus.");
        }
    }

    private static bool? ReadBoolean(IReadOnlyDictionary<string, JsonElement> arguments, string name) =>
        arguments.TryGetValue(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
}

internal static class ToolOverfetchExecutionContext
{
    private static readonly AsyncLocal<int> Depth = new();

    internal static bool Active => Depth.Value > 0;

    internal static int MaximumAllowed(int publicMaximum) =>
        Active ? checked(publicMaximum + 1) : publicMaximum;

    internal static IDisposable Begin()
    {
        Depth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Depth.Value--;
        }
    }
}
