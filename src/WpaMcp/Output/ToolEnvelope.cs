using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Serialization;
using WpaMcp.Core;

namespace WpaMcp.Output;

public static class ToolContractVersions
{
    public const string V2 = "2.0";
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolCompletionStatus>))]
public enum ToolCompletionStatus
{
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("failed")]
    Failed,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolSectionMode>))]
public enum ToolSectionMode
{
    [JsonStringEnumMemberName("none")]
    None,
    [JsonStringEnumMemberName("top_n")]
    TopN,
    [JsonStringEnumMemberName("cursor")]
    Cursor,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolSectionRole>))]
public enum ToolSectionRole
{
    [JsonStringEnumMemberName("domain_data")]
    DomainData,
    [JsonStringEnumMemberName("domain_evidence")]
    DomainEvidence,
    [JsonStringEnumMemberName("boundary")]
    Boundary,
    [JsonStringEnumMemberName("provenance")]
    Provenance,
    [JsonStringEnumMemberName("recommendation")]
    Recommendation,
    [JsonStringEnumMemberName("diagnostic")]
    Diagnostic,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolSectionTotalState>))]
public enum ToolSectionTotalState
{
    [JsonStringEnumMemberName("exact")]
    Exact,
    [JsonStringEnumMemberName("lower_bound")]
    LowerBound,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolSectionMoreState>))]
public enum ToolSectionMoreState
{
    [JsonStringEnumMemberName("absent")]
    Absent,
    [JsonStringEnumMemberName("present")]
    Present,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolSortDirection>))]
public enum ToolSortDirection
{
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
    [JsonStringEnumMemberName("ascending")]
    Ascending,
    [JsonStringEnumMemberName("descending")]
    Descending,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolScopeStatus>))]
public enum ToolScopeStatus
{
    [JsonStringEnumMemberName("not_evaluated")]
    NotEvaluated,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
    [JsonStringEnumMemberName("ok")]
    Ok,
    [JsonStringEnumMemberName("process_instance_not_found")]
    ProcessInstanceNotFound,
    [JsonStringEnumMemberName("process_start_required")]
    ProcessStartRequired,
    [JsonStringEnumMemberName("ambiguous_process_instance")]
    AmbiguousProcessInstance,
    [JsonStringEnumMemberName("thread_instance_not_found")]
    ThreadInstanceNotFound,
    [JsonStringEnumMemberName("ambiguous_thread_instance")]
    AmbiguousThreadInstance,
    [JsonStringEnumMemberName("identity_unresolved")]
    IdentityUnresolved,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolScopeMode>))]
public enum ToolScopeMode
{
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
    [JsonStringEnumMemberName("server")]
    Server,
    [JsonStringEnumMemberName("trace")]
    Trace,
    [JsonStringEnumMemberName("all_processes")]
    AllProcesses,
    [JsonStringEnumMemberName("pid_aggregate")]
    PidAggregate,
    [JsonStringEnumMemberName("process_instance")]
    ProcessInstance,
    [JsonStringEnumMemberName("thread_instance")]
    ThreadInstance,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolScopeDetailCompleteness>))]
public enum ToolScopeDetailCompleteness
{
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("omitted_due_to_response_budget")]
    OmittedDueToResponseBudget,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolTraceRefKind>))]
public enum ToolTraceRefKind
{
    [JsonStringEnumMemberName("canonical")]
    Canonical,
    [JsonStringEnumMemberName("ephemeral")]
    Ephemeral,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolCapabilityStatus>))]
public enum ToolCapabilityStatus
{
    [JsonStringEnumMemberName("available")]
    Available,
    [JsonStringEnumMemberName("unavailable")]
    Unavailable,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<CapabilityAvailabilityStatus>))]
public enum CapabilityAvailabilityStatus
{
    [JsonStringEnumMemberName("callable")]
    Callable,
    [JsonStringEnumMemberName("disabled_by_policy")]
    DisabledByPolicy,
    [JsonStringEnumMemberName("unavailable_by_implementation")]
    UnavailableByImplementation,
    [JsonStringEnumMemberName("deprecated")]
    Deprecated,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolEvidenceCompletionState>))]
public enum ToolEvidenceCompletionState
{
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
    [JsonStringEnumMemberName("no_source_evidence")]
    NoSourceEvidence,
    [JsonStringEnumMemberName("source_without_completed_evidence")]
    SourceWithoutCompletedEvidence,
    [JsonStringEnumMemberName("completed_with_incomplete_evidence")]
    CompletedWithIncompleteEvidence,
    [JsonStringEnumMemberName("complete")]
    Complete,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolCaptureIntegrityStatus>))]
public enum ToolCaptureIntegrityStatus
{
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("unknown")]
    Unknown,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolCompletenessStatus>))]
public enum ToolCompletenessStatus
{
    [JsonStringEnumMemberName("complete")]
    Complete,
    [JsonStringEnumMemberName("top_n")]
    TopN,
    [JsonStringEnumMemberName("paged")]
    Paged,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("no_data")]
    NoData,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<MeasurementBasis>))]
public enum MeasurementBasis
{
    [JsonStringEnumMemberName("direct")]
    Direct,
    [JsonStringEnumMemberName("derived")]
    Derived,
    [JsonStringEnumMemberName("heuristic")]
    Heuristic,
    [JsonStringEnumMemberName("metadata")]
    Metadata,
    [JsonStringEnumMemberName("unmeasured")]
    Unmeasured,
}

[JsonConverter(typeof(JsonStringEnumConverter<Relationship>))]
public enum Relationship
{
    [JsonStringEnumMemberName("descriptive")]
    Descriptive,
    [JsonStringEnumMemberName("temporal")]
    Temporal,
    [JsonStringEnumMemberName("association")]
    Association,
    [JsonStringEnumMemberName("attribution")]
    Attribution,
    [JsonStringEnumMemberName("causal")]
    Causal,
}

[JsonConverter(typeof(JsonStringEnumConverter<ConclusionStatus>))]
public enum ConclusionStatus
{
    [JsonStringEnumMemberName("observed")]
    Observed,
    [JsonStringEnumMemberName("supported")]
    Supported,
    [JsonStringEnumMemberName("partial")]
    Partial,
    [JsonStringEnumMemberName("not_concluded")]
    NotConcluded,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolIdentifierPrecision>))]
public enum ToolIdentifierPrecision
{
    [JsonStringEnumMemberName("exact")]
    Exact,
    [JsonStringEnumMemberName("mixed")]
    Mixed,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

[JsonConverter(typeof(JsonStringEnumConverter<ToolMetricPrecision>))]
public enum ToolMetricPrecision
{
    [JsonStringEnumMemberName("exact")]
    Exact,
    [JsonStringEnumMemberName("estimated")]
    Estimated,
    [JsonStringEnumMemberName("rounded")]
    Rounded,
    [JsonStringEnumMemberName("mixed")]
    Mixed,
    [JsonStringEnumMemberName("not_applicable")]
    NotApplicable,
}

public static class ToolErrorCodeRegistry
{
    private static readonly ReadOnlyCollection<string> RegisteredCodes = Array.AsReadOnly(new[]
    {
        "invalid_argument",
        "process_instance_not_found",
        "process_start_required",
        "ambiguous_process_instance",
        "thread_instance_not_found",
        "ambiguous_thread_instance",
        "trace_not_loaded",
        "trace_access_denied",
        "trace_conversion_failed",
        "symbol_context_expired",
        "symbol_policy_denied",
        "symbol_resolution_unavailable",
        "invalid_cursor",
        "analysis_failed",
        "cancelled",
        "budget_exceeded",
        "response_too_large",
    });

    public static IReadOnlyList<string> Codes => RegisteredCodes;

    public static bool Contains(string code) =>
        RegisteredCodes.Contains(code, StringComparer.Ordinal);
}

public static class ToolNoDataReasonRegistry
{
    private static readonly ReadOnlyCollection<string> RegisteredReasons = Array.AsReadOnly(new[]
    {
        "event_class_not_observed",
        "no_events_in_scope",
        "no_completed_intervals_in_scope",
        "unpaired_endpoints_in_scope",
        "source_events_unattributed",
        "stacks_unavailable",
        "symbols_unresolved",
        "focus_not_found",
        "no_name_match",
        "no_candidates_in_considered_input",
        "no_candidates_in_retained_input",
        "no_capabilities_match_filter",
        "invalid_lifetime_boundaries",
    });

    public static IReadOnlyList<string> Reasons => RegisteredReasons;

    public static bool Contains(string reason) =>
        RegisteredReasons.Contains(reason, StringComparer.Ordinal);
}

public sealed record ToolError
{
    public ToolError(string code, string message, bool retryable)
    {
        ContractGuard.RegisteredCode(code, ToolErrorCodeRegistry.Contains, nameof(code));
        Code = code;
        Message = ContractGuard.NonEmpty(message, nameof(message));
        Retryable = retryable;
    }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; }
}

public sealed record ToolSectionFailure
{
    public ToolSectionFailure(string section, string code, string message, bool retryable)
    {
        Section = ContractGuard.NonEmpty(section, nameof(section));
        ContractGuard.RegisteredCode(code, ToolErrorCodeRegistry.Contains, nameof(code));
        Code = code;
        Message = ContractGuard.NonEmpty(message, nameof(message));
        Retryable = retryable;
    }

    [JsonPropertyName("section")]
    public string Section { get; }

    [JsonPropertyName("code")]
    public string Code { get; }

    [JsonPropertyName("message")]
    public string Message { get; }

    [JsonPropertyName("retryable")]
    public bool Retryable { get; }
}

public sealed record ToolNoData
{
    public ToolNoData(
        string reason,
        string boundaryCode,
        IReadOnlyList<string>? evidenceIds = null)
    {
        ContractGuard.RegisteredCode(reason, ToolNoDataReasonRegistry.Contains, nameof(reason));
        Reason = reason;
        BoundaryCode = ContractGuard.NonEmpty(boundaryCode, nameof(boundaryCode));
        EvidenceIds = ContractGuard.Strings(evidenceIds, nameof(evidenceIds));
    }

    [JsonPropertyName("reason")]
    public string Reason { get; }

    [JsonPropertyName("boundaryCode")]
    public string BoundaryCode { get; }

    [JsonPropertyName("evidenceIds")]
    public IReadOnlyList<string> EvidenceIds { get; }
}

public sealed record ToolSectionPage
{
    public ToolSectionPage(
        string section,
        ToolSectionMode mode,
        long? requested,
        long returned,
        long? totalAvailable,
        ToolSectionTotalState totalState,
        bool hasMore,
        string? sortKey,
        ToolSortDirection sortDirection,
        IReadOnlyList<string>? tieBreakers,
        string? nextCursor,
        string? truncationReason,
        ToolNoData? noData,
        ToolSectionRole role = ToolSectionRole.DomainData,
        IReadOnlyList<string>? evidenceIds = null,
        ToolSectionMoreState? moreState = null,
        bool? continuationAvailable = null,
        MeasurementBasis? measurementBasis = null,
        Relationship? relationship = null,
        ConclusionStatus? conclusionStatus = null)
    {
        Section = ContractGuard.NonEmpty(section, nameof(section));
        ContractGuard.NonNegative(requested, nameof(requested));
        ContractGuard.NonNegative(returned, nameof(returned));
        ContractGuard.NonNegative(totalAvailable, nameof(totalAvailable));

        if (totalState == ToolSectionTotalState.Exact && totalAvailable is null)
            throw new ArgumentException("An exact total requires totalAvailable.", nameof(totalAvailable));
        if (totalState == ToolSectionTotalState.Unknown && totalAvailable is not null)
            throw new ArgumentException("An unknown total cannot publish totalAvailable.", nameof(totalAvailable));
        if (totalAvailable < returned)
            throw new ArgumentException("totalAvailable cannot be less than returned.", nameof(totalAvailable));
        if (requested is not null && returned > requested)
            throw new ArgumentException("returned cannot exceed requested.", nameof(returned));
        var resolvedMoreState = moreState ??
            (hasMore ? ToolSectionMoreState.Present : ToolSectionMoreState.Absent);
        var resolvedContinuationAvailable = continuationAvailable ?? nextCursor is not null;
        if (hasMore != (resolvedMoreState == ToolSectionMoreState.Present))
            throw new ArgumentException("hasMore must mean known-present omitted data.", nameof(hasMore));
        if (resolvedMoreState == ToolSectionMoreState.Unknown && totalState != ToolSectionTotalState.Unknown)
            throw new ArgumentException("Unknown more-state requires an unknown total.", nameof(moreState));
        if (hasMore && totalAvailable is not null && totalAvailable <= returned)
            throw new ArgumentException("A known total must exceed returned when hasMore is true.", nameof(totalAvailable));
        if (mode == ToolSectionMode.None && hasMore)
            throw new ArgumentException("mode=none cannot have a continuation.", nameof(hasMore));
        if (mode == ToolSectionMode.Cursor && hasMore && string.IsNullOrWhiteSpace(nextCursor))
            throw new ArgumentException("A cursor page with more data requires nextCursor.", nameof(nextCursor));
        if (mode != ToolSectionMode.Cursor && nextCursor is not null)
            throw new ArgumentException("Only cursor mode may publish nextCursor.", nameof(nextCursor));
        if (!hasMore && nextCursor is not null)
            throw new ArgumentException("A terminal page cannot publish nextCursor.", nameof(nextCursor));
        if (resolvedContinuationAvailable != (nextCursor is not null))
            throw new ArgumentException("continuationAvailable must match nextCursor presence.", nameof(continuationAvailable));
        if (resolvedMoreState is ToolSectionMoreState.Present or ToolSectionMoreState.Unknown &&
            string.IsNullOrWhiteSpace(truncationReason))
            throw new ArgumentException("Present or unknown omitted-data state requires truncationReason.", nameof(truncationReason));
        var contributesDomainData = role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;
        if (returned == 0 && contributesDomainData && noData is null)
            throw new ArgumentException("An empty domain section requires structured noData.", nameof(noData));
        if (!contributesDomainData && noData is not null)
            throw new ArgumentException("Boundary, provenance, recommendation, and diagnostic sections cannot define domain noData.", nameof(noData));
        if (returned > 0 && noData is not null)
            throw new ArgumentException("A non-empty section cannot also report noData.", nameof(noData));

        var normalizedTieBreakers = ContractGuard.Strings(tieBreakers, nameof(tieBreakers));
        if (string.IsNullOrWhiteSpace(sortKey))
        {
            if (sortDirection != ToolSortDirection.NotApplicable || normalizedTieBreakers.Count != 0)
            {
                throw new ArgumentException(
                    "An unordered section requires sortKey=null, sortDirection=not_applicable, and no tie breakers.",
                    nameof(sortKey));
            }
        }
        else if (sortDirection == ToolSortDirection.NotApplicable)
        {
            throw new ArgumentException(
                "An ordered section requires an applicable sort direction.",
                nameof(sortDirection));
        }
        if (normalizedTieBreakers.Any(item => item is "section_defined_order" or "stable_identity_asc"))
        {
            throw new ArgumentException(
                "Section ordering must expose concrete comparator fields, not placeholder tokens.",
                nameof(tieBreakers));
        }

        var isDomainSection = role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence;
        var isDiagnosticSection = role == ToolSectionRole.Diagnostic;
        var resolvedMeasurementBasis = measurementBasis ??
            (isDomainSection ? MeasurementBasis.Direct :
             isDiagnosticSection ? MeasurementBasis.Metadata : MeasurementBasis.Unmeasured);
        var resolvedRelationship = relationship ?? Relationship.Descriptive;
        var resolvedConclusionStatus = conclusionStatus ??
            (isDomainSection || isDiagnosticSection
                ? returned == 0 ? ConclusionStatus.NotConcluded : ConclusionStatus.Observed
                : ConclusionStatus.NotApplicable);
        if (!isDomainSection && !isDiagnosticSection &&
            (resolvedMeasurementBasis != MeasurementBasis.Unmeasured ||
             resolvedRelationship != Relationship.Descriptive ||
             resolvedConclusionStatus != ConclusionStatus.NotApplicable))
        {
            throw new ArgumentException(
                "Boundary, provenance, and recommendation sections are unmeasured descriptive support with no domain conclusion.",
                nameof(measurementBasis));
        }
        if ((isDomainSection || isDiagnosticSection) && returned == 0 &&
            resolvedConclusionStatus != ConclusionStatus.NotConcluded)
        {
            throw new ArgumentException(
                "An empty evidence-bearing section cannot publish a positive conclusion.",
                nameof(conclusionStatus));
        }
        if ((isDomainSection || isDiagnosticSection) && returned > 0 &&
            resolvedConclusionStatus == ConclusionStatus.NotApplicable)
        {
            throw new ArgumentException(
                "A populated evidence-bearing section requires an explicit evidence conclusion.",
                nameof(conclusionStatus));
        }

        Mode = mode;
        Requested = requested;
        Returned = returned;
        TotalAvailable = totalAvailable;
        TotalState = totalState;
        MoreState = resolvedMoreState;
        HasMore = hasMore;
        ContinuationAvailable = resolvedContinuationAvailable;
        SortKey = ContractGuard.OptionalNonEmpty(sortKey, nameof(sortKey));
        SortDirection = sortDirection;
        TieBreakers = normalizedTieBreakers;
        NextCursor = ContractGuard.OptionalNonEmpty(nextCursor, nameof(nextCursor));
        TruncationReason = ContractGuard.OptionalNonEmpty(truncationReason, nameof(truncationReason));
        NoData = noData;
        Role = role;
        EvidenceIds = ContractGuard.Strings(
            evidenceIds,
            nameof(evidenceIds),
            requireOne: contributesDomainData);
        MeasurementBasis = resolvedMeasurementBasis;
        Relationship = resolvedRelationship;
        ConclusionStatus = resolvedConclusionStatus;
    }

    [JsonPropertyName("section")]
    public string Section { get; }

    [JsonPropertyName("mode")]
    public ToolSectionMode Mode { get; }

    [JsonPropertyName("requested")]
    public long? Requested { get; }

    [JsonPropertyName("returned")]
    public long Returned { get; }

    [JsonPropertyName("totalAvailable")]
    public long? TotalAvailable { get; }

    [JsonPropertyName("totalState")]
    public ToolSectionTotalState TotalState { get; }

    [JsonPropertyName("moreState")]
    [Description("Tri-state omitted-data proof: present is proven omission, absent is proven terminal, unknown means a fixed source limit saturated without a top+1 witness.")]
    public ToolSectionMoreState MoreState { get; }

    [JsonPropertyName("hasMore")]
    [Description("Compatibility boolean: true only when moreState=present. False is not terminal when moreState=unknown.")]
    public bool HasMore { get; }

    [JsonPropertyName("continuationAvailable")]
    public bool ContinuationAvailable { get; }

    [JsonPropertyName("sortKey")]
    public string? SortKey { get; }

    [JsonPropertyName("sortDirection")]
    public ToolSortDirection SortDirection { get; }

    [JsonPropertyName("tieBreakers")]
    public IReadOnlyList<string> TieBreakers { get; }

    [JsonPropertyName("nextCursor")]
    [ToolOpaqueLocator("continuation_cursor", "^(?:qrc|cpc)_[0-9a-f]{32}$")]
    public string? NextCursor { get; }

    [JsonPropertyName("truncationReason")]
    public string? TruncationReason { get; }

    [JsonPropertyName("noData")]
    public ToolNoData? NoData { get; }

    [JsonPropertyName("role")]
    public ToolSectionRole Role { get; }

    [JsonPropertyName("evidenceIds")]
    public IReadOnlyList<string> EvidenceIds { get; }

    [JsonPropertyName("measurementBasis")]
    [Description("How values in this section were obtained. This is section-specific and may be more conservative than another section in the same tool response.")]
    public MeasurementBasis MeasurementBasis { get; }

    [JsonPropertyName("relationship")]
    [Description("Strongest relationship established by this section; association and temporal proximity do not imply causality.")]
    public Relationship Relationship { get; }

    [JsonPropertyName("conclusionStatus")]
    [Description("Section-local conclusion bounded by runtime capability, capture, and no-data state.")]
    public ConclusionStatus ConclusionStatus { get; }
}

public sealed record ToolReference
{
    public ToolReference(string name, IReadOnlyList<string> capabilityIds)
    {
        Name = ContractGuard.NonEmpty(name, nameof(name));
        CapabilityIds = ContractGuard.Strings(capabilityIds, nameof(capabilityIds), requireOne: true);
    }

    [JsonPropertyName("name")]
    public string Name { get; }

    [JsonPropertyName("capabilityIds")]
    public IReadOnlyList<string> CapabilityIds { get; }
}

public sealed record ToolTraceReference
{
    public ToolTraceReference(
        string traceId,
        string? generationAlias,
        string? symbolContextId,
        ToolTraceRefKind refKind)
    {
        TraceId = ContractGuard.OpaqueLocator(traceId, "trc_", nameof(traceId));
        if (generationAlias is not null)
        {
            throw new ArgumentException(
                "Contract 2.0 does not expose a separate trace generation alias.",
                nameof(generationAlias));
        }
        GenerationAlias = null;
        SymbolContextId = symbolContextId is null
            ? null
            : ContractGuard.OpaqueLocator(symbolContextId, "sym_", nameof(symbolContextId));
        RefKind = refKind;
    }

    [JsonPropertyName("traceId")]
    [ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
    public string TraceId { get; }

    [JsonPropertyName("generationAlias")]
    public string? GenerationAlias { get; }

    [JsonPropertyName("symbolContextId")]
    [ToolOpaqueLocator("symbol_context_id", "^sym_[0-9a-f]{32}$")]
    public string? SymbolContextId { get; }

    [JsonPropertyName("refKind")]
    public ToolTraceRefKind RefKind { get; }
}

public sealed record ToolScopeSelector(
    [property: JsonPropertyName("pid")] int? Pid,
    [property: JsonPropertyName("processStartUs")] long? ProcessStartUs,
    [property: JsonPropertyName("tid")] int? Tid,
    [property: JsonPropertyName("threadStartUs")] long? ThreadStartUs,
    [property: JsonPropertyName("threadGeneration")] string? ThreadGeneration,
    [property: JsonPropertyName("windowStartUs")] long? WindowStartUs,
    [property: JsonPropertyName("windowEndUs")] long? WindowEndUs);

public sealed record ToolScopeIdentity(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("processStartUs")] long ProcessStartUs,
    [property: JsonPropertyName("tid")] int? Tid,
    [property: JsonPropertyName("threadStartUs")] long? ThreadStartUs,
    [property: JsonPropertyName("threadGeneration")] string? ThreadGeneration);

public sealed record ToolScope
{
    public ToolScope(
        ToolScopeStatus status,
        ToolScopeMode mode,
        ToolScopeSelector requested,
        ToolScopeIdentity? selected,
        IReadOnlyList<ToolScopeIdentity>? candidates,
        IReadOnlyList<ToolScopeIdentity>? included,
        bool pidReuseObserved,
        bool identityUnresolved,
        int? candidateTotal = null,
        int? includedTotal = null,
        ToolScopeDetailCompleteness detailCompleteness = ToolScopeDetailCompleteness.Complete)
    {
        Status = status;
        Mode = mode;
        Requested = requested ?? throw new ArgumentNullException(nameof(requested));
        Selected = selected;
        Candidates = ContractGuard.Copy(candidates);
        Included = ContractGuard.Copy(included);
        PidReuseObserved = pidReuseObserved;
        IdentityUnresolved = identityUnresolved;
        CandidateTotal = candidateTotal ?? Candidates.Count;
        IncludedTotal = includedTotal ?? Included.Count;
        DetailCompleteness = detailCompleteness;

        if (CandidateTotal < Candidates.Count || IncludedTotal < Included.Count)
            throw new ArgumentOutOfRangeException(nameof(candidateTotal));
        if (detailCompleteness == ToolScopeDetailCompleteness.Complete &&
            (CandidateTotal != Candidates.Count || IncludedTotal != Included.Count))
        {
            throw new ArgumentException(
                "A complete scope must expose every counted candidate and included identity.",
                nameof(detailCompleteness));
        }
        if (detailCompleteness == ToolScopeDetailCompleteness.OmittedDueToResponseBudget &&
            (Candidates.Count != 0 || Included.Count != 0))
        {
            throw new ArgumentException(
                "A budget-omitted scope cannot expose a partial identity sample.",
                nameof(detailCompleteness));
        }

        if (status == ToolScopeStatus.NotApplicable && mode != ToolScopeMode.NotApplicable)
            throw new ArgumentException("A not-applicable scope must use mode=not_applicable.", nameof(mode));
        if (status != ToolScopeStatus.NotApplicable && mode == ToolScopeMode.NotApplicable)
            throw new ArgumentException("Only a not-applicable scope may use mode=not_applicable.", nameof(mode));
        if (status == ToolScopeStatus.Ok && mode == ToolScopeMode.NotApplicable)
            throw new ArgumentException("An applicable scope requires an applicable mode.", nameof(mode));
        if (selected is not null && status != ToolScopeStatus.Ok)
            throw new ArgumentException("Only a resolved scope may publish selected identity.", nameof(selected));
        if (IncludedTotal > 0 && status != ToolScopeStatus.Ok)
            throw new ArgumentException("Only a resolved scope may publish included identities.", nameof(included));
        if (status == ToolScopeStatus.NotApplicable &&
            (CandidateTotal != 0 || IncludedTotal != 0 || pidReuseObserved || identityUnresolved))
        {
            throw new ArgumentException(
                "A not-applicable scope cannot publish candidates, included identities, reuse, or unresolved identity state.",
                nameof(status));
        }
        if ((status is ToolScopeStatus.ProcessStartRequired or
            ToolScopeStatus.AmbiguousProcessInstance or
            ToolScopeStatus.AmbiguousThreadInstance) && CandidateTotal == 0)
        {
            throw new ArgumentException("An ambiguous or process-start-required scope must publish candidates.", nameof(candidates));
        }
        if (status == ToolScopeStatus.Ok &&
            (mode is ToolScopeMode.ProcessInstance or ToolScopeMode.ThreadInstance) &&
            selected is null)
        {
            throw new ArgumentException("A resolved process/thread-instance scope requires selected identity.", nameof(selected));
        }
        if (status == ToolScopeStatus.Ok &&
            (mode is ToolScopeMode.Server or ToolScopeMode.Trace or
                ToolScopeMode.AllProcesses or ToolScopeMode.PidAggregate) &&
            selected is not null)
        {
            throw new ArgumentException("An aggregate, trace, or server scope cannot publish a selected instance.", nameof(selected));
        }
    }

    [JsonPropertyName("status")]
    public ToolScopeStatus Status { get; }

    [JsonPropertyName("mode")]
    public ToolScopeMode Mode { get; }

    [JsonPropertyName("requested")]
    public ToolScopeSelector Requested { get; }

    [JsonPropertyName("selected")]
    public ToolScopeIdentity? Selected { get; }

    [JsonPropertyName("candidates")]
    public IReadOnlyList<ToolScopeIdentity> Candidates { get; }

    [JsonPropertyName("included")]
    public IReadOnlyList<ToolScopeIdentity> Included { get; }

    [JsonPropertyName("pidReuseObserved")]
    public bool PidReuseObserved { get; }

    [JsonPropertyName("identityUnresolved")]
    public bool IdentityUnresolved { get; }

    [JsonPropertyName("candidateTotal")]
    [Description("Exact number of candidate identities before any explicit response-budget omission.")]
    public int CandidateTotal { get; }

    [JsonPropertyName("includedTotal")]
    [Description("Exact number of included identities before any explicit response-budget omission.")]
    public int IncludedTotal { get; }

    [JsonPropertyName("detailCompleteness")]
    [Description("complete means Candidates and Included contain every counted identity; omitted_due_to_response_budget means both arrays are intentionally empty and the exact totals remain authoritative.")]
    public ToolScopeDetailCompleteness DetailCompleteness { get; }
}

public sealed record ToolCapabilityEvidence
{
    public ToolCapabilityEvidence(
        string capabilityId,
        ToolCapabilityStatus traceStatus,
        ToolCapabilityStatus scopedStatus,
        long? totalEventCount,
        long? matchedEventCount,
        ToolCaptureIntegrityStatus captureIntegrity,
        IReadOnlyList<string> evidenceIds,
        long? traceCompletedEvidenceCount = null,
        long? traceUnmatchedEvidenceCount = null,
        long? traceBoundaryEvidenceCount = null,
        ToolEvidenceCompletionState evidenceCompletionState =
            ToolEvidenceCompletionState.NotApplicable,
        string traceEligibleEventCountRepresentation = "legacy_unspecified")
    {
        CapabilityId = ContractGuard.NonEmpty(capabilityId, nameof(capabilityId));
        ContractGuard.NonNegative(totalEventCount, nameof(totalEventCount));
        ContractGuard.NonNegative(matchedEventCount, nameof(matchedEventCount));
        ContractGuard.NonNegative(
            traceCompletedEvidenceCount,
            nameof(traceCompletedEvidenceCount));
        ContractGuard.NonNegative(
            traceUnmatchedEvidenceCount,
            nameof(traceUnmatchedEvidenceCount));
        ContractGuard.NonNegative(
            traceBoundaryEvidenceCount,
            nameof(traceBoundaryEvidenceCount));
        var completionCountsSpecified =
            traceCompletedEvidenceCount.HasValue ||
            traceUnmatchedEvidenceCount.HasValue ||
            traceBoundaryEvidenceCount.HasValue;
        if (evidenceCompletionState == ToolEvidenceCompletionState.NotApplicable)
        {
            if (completionCountsSpecified)
            {
                throw new ArgumentException(
                    "Completion counts must be null when evidenceCompletionState is not_applicable.",
                    nameof(evidenceCompletionState));
            }
        }
        else
        {
            if (!traceCompletedEvidenceCount.HasValue ||
                !traceUnmatchedEvidenceCount.HasValue ||
                !traceBoundaryEvidenceCount.HasValue ||
                !totalEventCount.HasValue)
            {
                throw new ArgumentException(
                    "A completion-aware state requires source, completed, unmatched, and boundary counts.",
                    nameof(evidenceCompletionState));
            }
            if (evidenceCompletionState == ToolEvidenceCompletionState.NoSourceEvidence &&
                (totalEventCount.Value != 0 ||
                 traceCompletedEvidenceCount.Value != 0 ||
                 traceUnmatchedEvidenceCount.Value != 0 ||
                 traceBoundaryEvidenceCount.Value != 0))
            {
                throw new ArgumentException(
                    "no_source_evidence requires every completion-aware count to be zero.",
                    nameof(evidenceCompletionState));
            }
            if (evidenceCompletionState ==
                    ToolEvidenceCompletionState.SourceWithoutCompletedEvidence &&
                (totalEventCount.Value == 0 || traceCompletedEvidenceCount.Value != 0))
            {
                throw new ArgumentException(
                    "source_without_completed_evidence requires positive source count and zero completed count.",
                    nameof(evidenceCompletionState));
            }
            if (evidenceCompletionState ==
                    ToolEvidenceCompletionState.CompletedWithIncompleteEvidence &&
                (traceCompletedEvidenceCount.Value == 0 ||
                 traceUnmatchedEvidenceCount.Value == 0 &&
                 traceBoundaryEvidenceCount.Value == 0))
            {
                throw new ArgumentException(
                    "completed_with_incomplete_evidence requires completed and unmatched/boundary evidence.",
                    nameof(evidenceCompletionState));
            }
            if (evidenceCompletionState == ToolEvidenceCompletionState.Complete &&
                (traceCompletedEvidenceCount.Value == 0 ||
                 traceUnmatchedEvidenceCount.Value != 0 ||
                 traceBoundaryEvidenceCount.Value != 0))
            {
                throw new ArgumentException(
                    "complete requires positive completed count and zero unmatched/boundary evidence.",
                    nameof(evidenceCompletionState));
            }
        }
        TraceStatus = traceStatus;
        ScopedStatus = scopedStatus;
        TraceEligibleEventCount = totalEventCount;
        ScopedMatchedEventCount = matchedEventCount;
        TraceEligibleEventCountRepresentation = ContractGuard.NonEmpty(
            traceEligibleEventCountRepresentation,
            nameof(traceEligibleEventCountRepresentation));
        TotalEventCount = TraceEligibleEventCount;
        MatchedEventCount = ScopedMatchedEventCount;
        CaptureIntegrity = captureIntegrity;
        EvidenceIds = ContractGuard.Strings(evidenceIds, nameof(evidenceIds));
        if (EvidenceIds.Count == 0 &&
            (traceStatus != ToolCapabilityStatus.Unknown ||
             scopedStatus != ToolCapabilityStatus.Unknown ||
             totalEventCount.HasValue ||
             matchedEventCount.HasValue ||
             completionCountsSpecified ||
             evidenceCompletionState != ToolEvidenceCompletionState.NotApplicable ||
             captureIntegrity != ToolCaptureIntegrityStatus.Unknown ||
             !string.Equals(
                 TraceEligibleEventCountRepresentation,
                 "not_measured",
                 StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "An evidence-free capability assessment must be wholly unknown and unmeasured.",
                nameof(evidenceIds));
        }
        TraceCompletedEvidenceCount = traceCompletedEvidenceCount;
        TraceUnmatchedEvidenceCount = traceUnmatchedEvidenceCount;
        TraceBoundaryEvidenceCount = traceBoundaryEvidenceCount;
        EvidenceCompletionState = evidenceCompletionState;
    }

    [JsonPropertyName("capabilityId")]
    public string CapabilityId { get; }

    [JsonPropertyName("traceStatus")]
    public ToolCapabilityStatus TraceStatus { get; }

    [JsonPropertyName("scopedStatus")]
    public ToolCapabilityStatus ScopedStatus { get; }

    [JsonPropertyName("totalEventCount")]
    [Description("Deprecated compatibility alias for TraceEligibleEventCount. This is a whole-trace evaluator count, not a denominator for ScopedMatchedEventCount.")]
    public long? TotalEventCount { get; }

    [JsonPropertyName("matchedEventCount")]
    [Description("Deprecated compatibility alias for ScopedMatchedEventCount. This selected-scope count must not be divided by the whole-trace TotalEventCount alias.")]
    public long? MatchedEventCount { get; }

    [JsonPropertyName("traceEligibleEventCount")]
    [Description("Whole-trace count eligible under the capability evaluator. Its population and representation are declared separately and are not the denominator of ScopedMatchedEventCount.")]
    public long? TraceEligibleEventCount { get; }

    [JsonPropertyName("scopedMatchedEventCount")]
    [Description("Count matched by the tool in the selected identity and requested half-open time window. It is not comparable to TraceEligibleEventCount as a numerator/denominator pair.")]
    public long? ScopedMatchedEventCount { get; }

    [JsonPropertyName("traceEligibleEventCountRepresentation")]
    [Description("Evaluator-declared representation of TraceEligibleEventCount, for example materialized logical events or lifecycle endpoints. It does not describe ScopedMatchedEventCount.")]
    public string TraceEligibleEventCountRepresentation { get; }

    [JsonPropertyName("traceEligibleEventCountScope")]
    [Description("Population scope of TraceEligibleEventCount.")]
    public string TraceEligibleEventCountScope => "whole_trace";

    [JsonPropertyName("scopedMatchedEventCountScope")]
    [Description("Population scope of ScopedMatchedEventCount.")]
    public string ScopedMatchedEventCountScope =>
        "selected_identity_and_requested_half_open_window";

    [JsonPropertyName("crossScopeRatioDenominatorState")]
    [Description("not_defined means this contract defines no ratio between the trace-eligible and scoped-matched counts because their population scopes may differ.")]
    public string CrossScopeRatioDenominatorState => "not_defined";

    [JsonPropertyName("traceCompletedEvidenceCount")]
    [Description("Whole-trace exact completed lifecycle/interval count; null when the evaluator is not completion-aware.")]
    public long? TraceCompletedEvidenceCount { get; }

    [JsonPropertyName("traceUnmatchedEvidenceCount")]
    [Description("Whole-trace unmatched endpoint count; null when the evaluator is not completion-aware.")]
    public long? TraceUnmatchedEvidenceCount { get; }

    [JsonPropertyName("traceBoundaryEvidenceCount")]
    [Description("Whole-trace inferred, unresolved, invalid, or otherwise bounded evidence count; null when the evaluator is not completion-aware.")]
    public long? TraceBoundaryEvidenceCount { get; }

    [JsonPropertyName("evidenceCompletionState")]
    [Description("Completion-aware evaluator state. complete is the only state that permits traceStatus=available.")]
    public ToolEvidenceCompletionState EvidenceCompletionState { get; }

    [JsonPropertyName("captureIntegrity")]
    public ToolCaptureIntegrityStatus CaptureIntegrity { get; }

    [JsonPropertyName("evidenceIds")]
    [Description("References into evidenceBoundary.items. Empty only on a failed, scope-null terminal response that produced no analysis evidence; it never means evidence was omitted from a usable result.")]
    public IReadOnlyList<string> EvidenceIds { get; }
}

public sealed record ToolCompleteness
{
    public ToolCompleteness(
        ToolCompletenessStatus status,
        int requestedSectionCount,
        int sectionsWithData,
        int failedSectionCount,
        bool hasMore)
    {
        ContractGuard.NonNegative(requestedSectionCount, nameof(requestedSectionCount));
        ContractGuard.NonNegative(sectionsWithData, nameof(sectionsWithData));
        ContractGuard.NonNegative(failedSectionCount, nameof(failedSectionCount));
        if (sectionsWithData > requestedSectionCount)
            throw new ArgumentException("sectionsWithData cannot exceed requestedSectionCount.", nameof(sectionsWithData));
        if (failedSectionCount > requestedSectionCount)
            throw new ArgumentException("failedSectionCount cannot exceed requestedSectionCount.", nameof(failedSectionCount));
        if (sectionsWithData + failedSectionCount > requestedSectionCount)
        {
            throw new ArgumentException(
                "Sections with data and failed sections cannot overlap or exceed the requested section count.",
                nameof(failedSectionCount));
        }

        Status = status;
        RequestedSectionCount = requestedSectionCount;
        SectionsWithData = sectionsWithData;
        FailedSectionCount = failedSectionCount;
        HasMore = hasMore;
    }

    [JsonPropertyName("status")]
    public ToolCompletenessStatus Status { get; }

    [JsonPropertyName("requestedSectionCount")]
    [Description("Number of requested domain_data/domain_evidence sections only; support, boundary, provenance, recommendation, and diagnostic sections are excluded.")]
    public int RequestedSectionCount { get; }

    [JsonPropertyName("sectionsWithData")]
    [Description("Number of domain_data/domain_evidence sections with returned data only; support sections are excluded even when populated.")]
    public int SectionsWithData { get; }

    [JsonPropertyName("failedSectionCount")]
    public int FailedSectionCount { get; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; }
}

public sealed record ToolEvidenceProvenance
{
    public ToolEvidenceProvenance(
        string source,
        string parser,
        string evaluator,
        string? ruleId,
        ToolCaptureIntegrityStatus captureIntegrity)
    {
        Source = ContractGuard.NonEmpty(source, nameof(source));
        Parser = ContractGuard.NonEmpty(parser, nameof(parser));
        Evaluator = ContractGuard.NonEmpty(evaluator, nameof(evaluator));
        RuleId = ContractGuard.OptionalNonEmpty(ruleId, nameof(ruleId));
        CaptureIntegrity = captureIntegrity;
    }

    [JsonPropertyName("source")]
    public string Source { get; }

    [JsonPropertyName("parser")]
    public string Parser { get; }

    [JsonPropertyName("evaluator")]
    public string Evaluator { get; }

    [JsonPropertyName("ruleId")]
    public string? RuleId { get; }

    [JsonPropertyName("captureIntegrity")]
    public ToolCaptureIntegrityStatus CaptureIntegrity { get; }
}

public sealed record ToolEvidenceBoundaryItem
{
    public ToolEvidenceBoundaryItem(
        string evidenceId,
        IReadOnlyList<string>? sections,
        MeasurementBasis measurementBasis,
        Relationship relationship,
        ConclusionStatus conclusionStatus,
        IReadOnlyList<string>? doesNotProve,
        ToolEvidenceProvenance provenance)
    {
        EvidenceId = ContractGuard.NonEmpty(evidenceId, nameof(evidenceId));
        Sections = ContractGuard.Strings(sections, nameof(sections));
        ContractGuard.Unique(Sections, nameof(sections));
        MeasurementBasis = measurementBasis;
        Relationship = relationship;
        ConclusionStatus = conclusionStatus;
        DoesNotProve = ContractGuard.Strings(doesNotProve, nameof(doesNotProve));
        Provenance = provenance ?? throw new ArgumentNullException(nameof(provenance));
    }

    public ToolEvidenceBoundaryItem(
        string evidenceId,
        string? section,
        MeasurementBasis measurementBasis,
        Relationship relationship,
        ConclusionStatus conclusionStatus,
        IReadOnlyList<string>? doesNotProve,
        ToolEvidenceProvenance provenance)
        : this(
            evidenceId,
            section is null ? Array.Empty<string>() : [section],
            measurementBasis,
            relationship,
            conclusionStatus,
            doesNotProve,
            provenance)
    {
    }

    [JsonPropertyName("evidenceId")]
    public string EvidenceId { get; }

    [JsonPropertyName("sections")]
    [Description("Every domain section constrained by this evidence item. Empty means the evidence applies only to the tool-level result.")]
    public IReadOnlyList<string> Sections { get; }

    [JsonPropertyName("measurementBasis")]
    public MeasurementBasis MeasurementBasis { get; }

    [JsonPropertyName("relationship")]
    public Relationship Relationship { get; }

    [JsonPropertyName("conclusionStatus")]
    public ConclusionStatus ConclusionStatus { get; }

    [JsonPropertyName("doesNotProve")]
    public IReadOnlyList<string> DoesNotProve { get; }

    [JsonPropertyName("provenance")]
    public ToolEvidenceProvenance Provenance { get; }
}

public sealed record ToolEvidenceBoundary
{
    public ToolEvidenceBoundary(IReadOnlyList<ToolEvidenceBoundaryItem> items)
    {
        Items = ContractGuard.Copy(items);
        ContractGuard.Unique(Items.Select(item => item.EvidenceId), nameof(items));
    }

    [JsonPropertyName("items")]
    [Description("Complete evidence-item registry for this response. Empty only on a failed, scope-null terminal response whose capabilityEvidence entries have empty evidenceIds because no analysis evidence was produced.")]
    public IReadOnlyList<ToolEvidenceBoundaryItem> Items { get; }
}

public sealed record ToolMetricDenominator
{
    public ToolMetricDenominator(string? value, string unit, string scope, ToolMetricPrecision precision)
    {
        Value = ContractGuard.OptionalNonEmpty(value, nameof(value));
        if (Value is not null && !decimal.TryParse(
                Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out _))
        {
            throw new ArgumentException("A denominator value must be an invariant decimal string.", nameof(value));
        }
        Unit = ContractGuard.NonEmpty(unit, nameof(unit));
        Scope = ContractGuard.NonEmpty(scope, nameof(scope));
        Precision = precision;
    }

    [JsonPropertyName("value")]
    public string? Value { get; }

    [JsonPropertyName("unit")]
    public string Unit { get; }

    [JsonPropertyName("scope")]
    public string Scope { get; }

    [JsonPropertyName("precision")]
    public ToolMetricPrecision Precision { get; }
}

public sealed record ToolPrecision
{
    public ToolPrecision(
        ToolIdentifierPrecision identifierPrecision,
        ToolMetricPrecision metricPrecision,
        string? rounding,
        string accounting,
        ToolMetricDenominator? denominator)
    {
        IdentifierPrecision = identifierPrecision;
        MetricPrecision = metricPrecision;
        Rounding = ContractGuard.OptionalNonEmpty(rounding, nameof(rounding));
        Accounting = ContractGuard.NonEmpty(accounting, nameof(accounting));
        Denominator = denominator;
    }

    [JsonPropertyName("identifierPrecision")]
    public ToolIdentifierPrecision IdentifierPrecision { get; }

    [JsonPropertyName("metricPrecision")]
    public ToolMetricPrecision MetricPrecision { get; }

    [JsonPropertyName("rounding")]
    public string? Rounding { get; }

    [JsonPropertyName("accounting")]
    public string Accounting { get; }

    [JsonPropertyName("denominator")]
    public ToolMetricDenominator? Denominator { get; }
}

public sealed class ToolEnvelope<TData> where TData : class
{
    [JsonConstructor]
    public ToolEnvelope(
        string contractVersion,
        ToolCompletionStatus status,
        TData? data,
        ToolError? error,
        IReadOnlyList<ToolSectionFailure> failedSections,
        IReadOnlyList<ToolSectionPage> sections,
        IReadOnlyList<string> warnings,
        bool hasMore,
        ToolReference toolRef,
        ToolTraceReference? traceRef,
        ToolScope? scope,
        IReadOnlyList<ToolCapabilityEvidence> capabilityEvidence,
        ToolCompleteness completeness,
        ToolEvidenceBoundary evidenceBoundary,
        ToolNoData? noData,
        ToolPrecision precision)
    {
        if (!string.Equals(contractVersion, ToolContractVersions.V2, StringComparison.Ordinal))
            throw new ArgumentException("The only active envelope contract is 2.0.", nameof(contractVersion));

        ContractVersion = contractVersion;
        Status = status;
        Data = data;
        Error = error;
        FailedSections = ContractGuard.Copy(failedSections);
        Sections = ContractGuard.Copy(sections);
        Warnings = ContractGuard.Strings(warnings, nameof(warnings));
        HasMore = hasMore;
        ToolRef = toolRef ?? throw new ArgumentNullException(nameof(toolRef));
        TraceRef = traceRef;
        Scope = scope;
        if (status != ToolCompletionStatus.Failed && scope is null)
            throw new ArgumentNullException(nameof(scope), "A non-failed envelope requires a scope contract.");
        CapabilityEvidence = ContractGuard.Copy(capabilityEvidence, requireOne: true);
        Completeness = completeness ?? throw new ArgumentNullException(nameof(completeness));
        EvidenceBoundary = evidenceBoundary ?? throw new ArgumentNullException(nameof(evidenceBoundary));
        NoData = noData;
        Precision = precision ?? throw new ArgumentNullException(nameof(precision));

        Validate();
    }

    [JsonPropertyName("contractVersion")]
    public string ContractVersion { get; }

    [JsonPropertyName("status")]
    public ToolCompletionStatus Status { get; }

    [JsonPropertyName("data")]
    public TData? Data { get; }

    [JsonPropertyName("error")]
    public ToolError? Error { get; }

    [JsonPropertyName("failedSections")]
    public IReadOnlyList<ToolSectionFailure> FailedSections { get; }

    [JsonPropertyName("sections")]
    public IReadOnlyList<ToolSectionPage> Sections { get; }

    [JsonPropertyName("warnings")]
    public IReadOnlyList<string> Warnings { get; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; }

    [JsonPropertyName("toolRef")]
    public ToolReference ToolRef { get; }

    [JsonPropertyName("traceRef")]
    public ToolTraceReference? TraceRef { get; }

    [JsonPropertyName("scope")]
    [Description("Structured analysis scope. Null only for a terminal failure that produced no analyzable scope, such as a response-budget failure.")]
    public ToolScope? Scope { get; }

    [JsonPropertyName("capabilityEvidence")]
    public IReadOnlyList<ToolCapabilityEvidence> CapabilityEvidence { get; }

    [JsonPropertyName("completeness")]
    public ToolCompleteness Completeness { get; }

    [JsonPropertyName("evidenceBoundary")]
    public ToolEvidenceBoundary EvidenceBoundary { get; }

    [JsonPropertyName("noData")]
    public ToolNoData? NoData { get; }

    [JsonPropertyName("precision")]
    public ToolPrecision Precision { get; }

    [JsonIgnore]
    public bool IsError => Status == ToolCompletionStatus.Failed;

    private void Validate()
    {
        switch (Status)
        {
            case ToolCompletionStatus.Succeeded when Data is null || Error is not null || FailedSections.Count != 0:
                throw new ArgumentException("succeeded requires data, no error, and no failed sections.");
            case ToolCompletionStatus.Partial when Data is null || Error is not null || FailedSections.Count == 0:
                throw new ArgumentException("partial requires usable data, no top-level error, and a failed section.");
            case ToolCompletionStatus.Failed when Data is not null || Error is null:
                throw new ArgumentException("failed requires null data and a stable top-level error.");
        }

        var sectionHasMore = Sections.Any(section => section.HasMore);
        var dataHasMoreProperty = Data?.GetType().GetProperty(nameof(HasMore));
        var dataHasMore = dataHasMoreProperty?.PropertyType == typeof(bool)
            ? (bool?)dataHasMoreProperty.GetValue(Data)
            : null;
        if (dataHasMore is not null && HasMore != dataHasMore)
            throw new ArgumentException("hasMore must match the typed data continuation state.", nameof(HasMore));
        if (dataHasMore is null && HasMore != sectionHasMore)
            throw new ArgumentException("hasMore must be derived from section paging state.", nameof(HasMore));
        if (sectionHasMore && !HasMore)
            throw new ArgumentException("A section continuation cannot exceed the response continuation state.", nameof(HasMore));
        if (Completeness.HasMore != HasMore)
            throw new ArgumentException("completeness.hasMore must match the envelope.", nameof(Completeness));
        if (Completeness.FailedSectionCount != FailedSections.Count)
            throw new ArgumentException("completeness.failedSectionCount must match failedSections.", nameof(Completeness));
        var domainSections = Sections
            .Where(section => section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence)
            .ToArray();
        if (domainSections.Length > 0 && Completeness.SectionsWithData != domainSections.Count(section => section.Returned > 0))
            throw new ArgumentException("completeness.sectionsWithData must match section results.", nameof(Completeness));

        var expectedCompleteness = Status switch
        {
            ToolCompletionStatus.Partial => ToolCompletenessStatus.Partial,
            ToolCompletionStatus.Failed => ToolCompletenessStatus.Failed,
            _ when NoData is not null => ToolCompletenessStatus.NoData,
            _ => (ToolCompletenessStatus?)null,
        };
        if (expectedCompleteness is not null && Completeness.Status != expectedCompleteness)
            throw new ArgumentException($"{Status} requires completeness={expectedCompleteness}.", nameof(Completeness));
        if (Status == ToolCompletionStatus.Succeeded && NoData is null && Completeness.Status is ToolCompletenessStatus.Partial or ToolCompletenessStatus.Failed or ToolCompletenessStatus.NoData)
            throw new ArgumentException("succeeded data cannot claim partial, failed, or no-data completeness.", nameof(Completeness));
        if (Status == ToolCompletionStatus.Failed && (HasMore || NoData is not null))
            throw new ArgumentException("A failed envelope cannot page or report successful no-data.");
        if (Status == ToolCompletionStatus.Partial && NoData is not null)
            throw new ArgumentException("partial requires usable domain data and cannot set top-level noData.", nameof(NoData));

        if (domainSections.Length > 0)
        {
            var allEmpty = domainSections.All(section => section.NoData is not null);
            if (allEmpty != (NoData is not null))
                throw new ArgumentException("Top-level noData is required only when every domain data/evidence section has no data.", nameof(NoData));
        }

        ContractGuard.Unique(ToolRef.CapabilityIds, nameof(ToolRef.CapabilityIds));
        ContractGuard.Unique(Sections.Select(section => section.Section), nameof(Sections));
        ContractGuard.Unique(
            FailedSections.Select(section => $"{section.Section}\u001f{section.Code}"),
            nameof(FailedSections));
        ContractGuard.Unique(CapabilityEvidence.Select(item => item.CapabilityId), nameof(CapabilityEvidence));
        var declared = ToolRef.CapabilityIds.ToHashSet(StringComparer.Ordinal);
        var reported = CapabilityEvidence.Select(item => item.CapabilityId).ToHashSet(StringComparer.Ordinal);
        if (!declared.SetEquals(reported))
            throw new ArgumentException("capabilityEvidence must cover every and only toolRef capability.", nameof(CapabilityEvidence));

        var boundaryEvidence = EvidenceBoundary.Items.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var referencedEvidence = CapabilityEvidence.SelectMany(item => item.EvidenceIds).ToHashSet(StringComparer.Ordinal);
        if (!boundaryEvidence.SetEquals(referencedEvidence))
            throw new ArgumentException("Evidence IDs must close between capabilityEvidence and evidenceBoundary.");
        var evidenceFreeCapabilities = CapabilityEvidence
            .Where(item => item.EvidenceIds.Count == 0)
            .ToArray();
        if (evidenceFreeCapabilities.Length > 0 &&
            !(Status == ToolCompletionStatus.Failed &&
              Scope is null &&
              evidenceFreeCapabilities.Length == CapabilityEvidence.Count &&
              EvidenceBoundary.Items.Count == 0))
        {
            throw new ArgumentException(
                "Empty evidence references are valid only when a failed, scope-null terminal response produced no analysis evidence.",
                nameof(CapabilityEvidence));
        }

        var sectionEvidence = Sections.SelectMany(section => section.EvidenceIds);
        if (sectionEvidence.Any(evidenceId => !boundaryEvidence.Contains(evidenceId)))
            throw new ArgumentException("Section evidence IDs must reference evidenceBoundary items.");

        foreach (var boundaryItem in EvidenceBoundary.Items)
        {
            var expectedSections = domainSections
                .Where(section => section.EvidenceIds.Contains(
                    boundaryItem.EvidenceId,
                    StringComparer.Ordinal))
                .Select(section => section.Section)
                .ToHashSet(StringComparer.Ordinal);
            var reportedSections = boundaryItem.Sections.ToHashSet(StringComparer.Ordinal);
            if (!expectedSections.SetEquals(reportedSections))
            {
                throw new ArgumentException(
                    $"Evidence boundary sections must exactly match domain sections for '{boundaryItem.EvidenceId}'.",
                    nameof(EvidenceBoundary));
            }
        }

        var noDataEvidence = Sections
            .Select(section => section.NoData)
            .Append(NoData)
            .Where(item => item is not null)
            .SelectMany(item => item!.EvidenceIds);
        if (noDataEvidence.Any(evidenceId => !boundaryEvidence.Contains(evidenceId)))
            throw new ArgumentException("NoData evidence IDs must reference evidenceBoundary items.");
    }
}

internal static class ContractGuard
{
    internal static string OpaqueLocator(string value, string prefix, string parameterName)
    {
        NonEmpty(value, parameterName);
        if (value.Length != prefix.Length + 32 || !value.StartsWith(prefix, StringComparison.Ordinal))
            throw new ArgumentException($"The value must be a canonical {prefix} opaque locator.", parameterName);

        for (var index = prefix.Length; index < value.Length; index++)
        {
            var character = value[index];
            if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                throw new ArgumentException($"The value must be a canonical {prefix} opaque locator.", parameterName);
        }

        return value;
    }

    internal static string NonEmpty(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A non-empty value is required.", parameterName);
        return value;
    }

    internal static string? OptionalNonEmpty(string? value, string parameterName)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("When present, the value cannot be empty.", parameterName);
        return value;
    }

    internal static void RegisteredCode(string value, Func<string, bool> contains, string parameterName)
    {
        NonEmpty(value, parameterName);
        if (!contains(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "The value is not in the contract-2.0 registry.");
    }

    internal static ReadOnlyCollection<T> Copy<T>(IReadOnlyList<T>? values, bool requireOne = false)
    {
        var copy = values?.ToArray() ?? Array.Empty<T>();
        if (requireOne && copy.Length == 0)
            throw new ArgumentException("At least one item is required.", nameof(values));
        if (copy.Any(value => value is null))
            throw new ArgumentException("Collections cannot contain null items.", nameof(values));
        return Array.AsReadOnly(copy);
    }

    internal static ReadOnlyCollection<string> Strings(
        IReadOnlyList<string>? values,
        string parameterName,
        bool requireOne = false)
    {
        var copy = values?.ToArray() ?? Array.Empty<string>();
        if (requireOne && copy.Length == 0)
            throw new ArgumentException("At least one item is required.", parameterName);
        if (copy.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Collections cannot contain empty items.", parameterName);
        Unique(copy, parameterName);
        return Array.AsReadOnly(copy);
    }

    internal static void Unique(IEnumerable<string> values, string parameterName)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (!seen.Add(value))
                throw new ArgumentException($"Duplicate value '{value}'.", parameterName);
        }
    }

    internal static void NonNegative(long? value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "The value cannot be negative.");
    }

    internal static void NonNegative(int value, string parameterName)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(parameterName, value, "The value cannot be negative.");
    }
}
