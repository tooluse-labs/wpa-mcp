using WpaMcp.Core;

namespace WpaMcp.Output;

public sealed record CapabilityMapFilter(
    string? Domain,
    string? Goal);

public sealed record CapabilityMapTotals(
    int TotalCapabilitiesBeforeFilter,
    int TotalCapabilitiesAfterFilter,
    int ReturnedCapabilities);

public sealed record CapabilityPolicyRecord(
    string ProfileName,
    string ProfileIdentity,
    string ProfileHash,
    string Source,
    string SelectionScope,
    IReadOnlyList<string> DisabledCapabilityIds);

public sealed record CapabilityPolicyResourceReference(
    string ProfileName,
    string ProfileIdentity,
    string ProfileHash,
    string Source,
    string SelectionScope,
    int DisabledCapabilityCount,
    string DisabledCapabilityIdsResourceUri,
    string DisabledCapabilityIdsCompleteness = "complete_in_linked_resource");

public sealed record CapabilityPolicyResourceIndex(
    string ProfileName,
    string ProfileIdentity,
    string ProfileHash,
    string Source,
    string SelectionScope,
    int TotalDisabledCapabilities,
    string Ordering,
    string Completeness,
    IReadOnlyList<CatalogResourcePageRecord> Pages);

public sealed record CapabilityPolicyResourcePage(
    string ProfileIdentity,
    string ProfileHash,
    int Page,
    int TotalDisabledCapabilities,
    int ReturnedDisabledCapabilities,
    string Ordering,
    string Completeness,
    IReadOnlyList<string> DisabledCapabilityIds);

public sealed record CapabilityMapEvidenceReference(
    string EvidenceId,
    string Kind,
    string Path,
    string? Member);

public sealed record ServerCapabilityRecord(
    string CapabilityId,
    string Domain,
    string Title,
    string Summary,
    string LifecycleStatus,
    string ProductMaturity,
    CapabilityAvailabilityStatus AvailabilityState,
    IReadOnlyList<string> QuestionsAnswered,
    IReadOnlyList<string> QuestionsNotAnswered,
    IReadOnlyList<string> ConclusionBoundaryCodes,
    IReadOnlyList<string> RequiredEvents,
    IReadOnlyList<string> RequiredEventStacks,
    IReadOnlyList<string> OptionalEvidence,
    string SymbolRequirement,
    string MaximumRelationship,
    [property: System.ComponentModel.Description("Collective capability applicability. For implemented capabilities, this is the union of mapped tools' selectable scopes: every value is selectable by at least one mapped tool, but not necessarily every mapped tool; gap capabilities describe intended scope.")]
    IReadOnlyList<string> SupportedScopes,
    [property: System.ComponentModel.Description("Stable interpretation of supportedScopes, included so resource and tool consumers do not infer per-tool selector support.")]
    string SupportedScopesSemantics,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> CallableToolNames,
    IReadOnlyList<string> DisabledByPolicyToolNames,
    IReadOnlyList<string> WorkflowIds,
    IReadOnlyList<string> GoalIds,
    string EvaluatorId,
    string CostClass,
    string SideEffectClass,
    string ContractVersion,
    IReadOnlyList<CapabilityMapEvidenceReference> EvidenceReferences,
    string? ReplacedBy,
    string? RemovalContractVersion);

public sealed record ListedCapabilityRecord(
    string CapabilityId,
    string Domain,
    string Title,
    CapabilityAvailabilityStatus AvailabilityState,
    string ProductMaturity,
    IReadOnlyList<string> RequiredEvents,
    IReadOnlyList<string> RequiredEventStacks,
    string SymbolRequirement,
    string MaximumRelationship,
    [property: System.ComponentModel.Description("Collective capability applicability. For implemented capabilities, this is the union of mapped tools' selectable scopes: every value is selectable by at least one mapped tool, but not necessarily every mapped tool; gap capabilities describe intended scope.")]
    IReadOnlyList<string> SupportedScopes,
    [property: System.ComponentModel.Description("Stable interpretation of supportedScopes, included so resource and tool consumers do not infer per-tool selector support.")]
    string SupportedScopesSemantics,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> CallableToolNames,
    IReadOnlyList<string> DisabledByPolicyToolNames,
    IReadOnlyList<string> WorkflowIds,
    IReadOnlyList<string> GoalIds,
    string EvaluatorId,
    string CostClass,
    IReadOnlyList<string> ConclusionBoundaryCodes,
    string DetailsResourceUri,
    string DetailCompleteness = "complete_in_linked_resource");

public sealed record CapabilityGoalRecord(
    string GoalId,
    string Title,
    string Summary,
    IReadOnlyList<string> WorkflowIds);

public sealed record CapabilityWorkflowRecord(
    string WorkflowId,
    string Title,
    string Summary,
    IReadOnlyList<string> GoalIds,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> ToolNames,
    IReadOnlyList<string> CallableToolNames,
    IReadOnlyList<string> DisabledByPolicyToolNames,
    IReadOnlyList<string> DisabledByPolicyCapabilityIds);

public sealed record ServerToolAnnotationRecord(
    bool ReadOnly,
    bool Idempotent,
    bool OpenWorld,
    bool Destructive);

public sealed record ServerToolDeprecationRecord(
    string State,
    string? ReplacedBy,
    string? RemovalContractVersion);

public sealed record PlannerAdmissionRecord(
    string OperationVersion,
    string AdmissionStatus,
    int? PhysicalPassLimit,
    IReadOnlyList<CapabilityMapEvidenceReference> EvidenceReferences,
    IReadOnlyList<string> MissingEvidence);

public sealed record ServerToolSectionContractRecord(
    [property: System.ComponentModel.Description("RFC 6901 JSON pointer identifying the result section governed by this contract.")]
    string SectionPointer,
    ToolSectionRole Role,
    ToolSectionMode Mode,
    [property: System.ComponentModel.Description("How the server proves section completeness: exhaustive, top_plus_one, conservative_limit, fixed_limit_conservative, or domain_cursor.")]
    string ProofMode,
    [property: System.ComponentModel.Description("Stable source of the section limit: none, argument:<name>, fixed:<value>, or cursor:<argument-name>.")]
    string LimitSource,
    string? SortKey,
    ToolSortDirection SortDirection,
    IReadOnlyList<string> TieBreakers,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    [property: System.ComponentModel.Description("Strongest conclusion declared for this section before runtime scope, capture, and evidence boundaries reduce it.")]
    ConclusionStatus DeclaredConclusionStatus,
    IReadOnlyList<string> EvidenceReferenceIds);

public sealed record ServerToolCatalogRecord(
    string ToolName,
    CapabilityAvailabilityStatus AvailabilityState,
    bool Callable,
    string Description,
    string InputType,
    string OutputType,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> RequiredCapabilities,
    [property: System.ComponentModel.Description("Analysis scopes directly selectable through this tool's public input schema. This is not output granularity, evidence completeness, or conclusion strength.")]
    IReadOnlyList<string> SelectableScopes,
    [property: System.ComponentModel.Description("Stable interpretation of selectableScopes, included in catalog projections and resources to prevent selector overclaiming.")]
    string SelectableScopesSemantics,
    ServerToolAnnotationRecord Annotations,
    IReadOnlyList<string> SideEffects,
    string CostClass,
    int DiscoveryPriority,
    string Domain,
    int Ordinal,
    string DefaultOrdering,
    IReadOnlyList<string> TieBreakers,
    IReadOnlyList<string> PageableSections,
    string PaginationMode,
    ServerToolDeprecationRecord Deprecation,
    IReadOnlyList<string> InternalAnalyzerOperations,
    IReadOnlyList<string> AllowedMeasurementBases,
    string MaximumRelationship,
    IReadOnlyList<string> ConclusionRules,
    IReadOnlyList<string> DoesNotProve,
    IReadOnlyList<string> EvidenceReferenceIds,
    [property: System.ComponentModel.Description("Complete per-section ordering, truncation-proof, evidence, measurement, relationship, and conclusion contracts.")]
    IReadOnlyList<ServerToolSectionContractRecord> SectionContracts,
    PlannerAdmissionRecord? PlannerAdmission);

public sealed record ServerToolResourceRecord(
    string ToolName,
    CapabilityAvailabilityStatus AvailabilityState,
    bool Callable,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> RequiredCapabilities,
    [property: System.ComponentModel.Description("Analysis scopes directly selectable through this tool's public input schema. This is not output granularity, evidence completeness, or conclusion strength.")]
    IReadOnlyList<string> SelectableScopes,
    [property: System.ComponentModel.Description("Stable interpretation of selectableScopes, included in catalog projections and resources to prevent selector overclaiming.")]
    string SelectableScopesSemantics,
    ServerToolAnnotationRecord Annotations,
    IReadOnlyList<string> SideEffects,
    string CostClass,
    int DiscoveryPriority,
    string Domain,
    int Ordinal,
    string DefaultOrdering,
    IReadOnlyList<string> TieBreakers,
    IReadOnlyList<string> PageableSections,
    string PaginationMode,
    ServerToolDeprecationRecord Deprecation,
    IReadOnlyList<string> AllowedMeasurementBases,
    string MaximumRelationship,
    IReadOnlyList<string> DoesNotProve,
    [property: System.ComponentModel.Description("Index resource for the complete byte-budgeted per-section ordering, truncation-proof, evidence, measurement, relationship, and conclusion contracts.")]
    string SectionContractsResourceUri,
    PlannerAdmissionRecord? PlannerAdmission,
    string FullContractSource,
    string? OutputContractResourceUri,
    string? OutputContractSha256,
    [property: ToolNumericSemantics("metric", "utf8_bytes", "exact", "complete_contract_byte_count", minimum: 1)]
    int? OutputContractUtf8Bytes,
    string? OutputContractMediaType,
    string SectionContractCompleteness = "complete_in_linked_resource");

public sealed record ListedToolResourceRecord(
    string ToolName,
    string Domain,
    CapabilityAvailabilityStatus AvailabilityState,
    bool Callable,
    IReadOnlyList<string> CapabilityIds,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> SelectableScopes,
    string SelectableScopesSemantics,
    string CostClass,
    int DiscoveryPriority,
    int Ordinal,
    string SectionContractsResourceUri,
    string DetailsResourceUri,
    string DetailCompleteness = "complete_in_linked_resource",
    string SectionContractCompleteness = "complete_in_linked_resource");

public sealed record ServerToolSectionContractPageResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string ToolName,
    int Page,
    int TotalSections,
    int ReturnedSections,
    string Ordering,
    IReadOnlyList<ServerToolSectionContractRecord> SectionContracts);

public sealed record ToolOutputContractResourceIndex(
    string ToolName,
    string ContractVersion,
    string SchemaUri,
    string Sha256,
    string MediaType,
    [property: ToolNumericSemantics("metric", "utf8_bytes", "exact", "complete_contract_byte_count", minimum: 1)]
    int Utf8Bytes,
    [property: ToolNumericSemantics("metric", "pages", "exact", "complete_contract_page_count", minimum: 1)]
    int PageCount,
    string PageUriTemplate,
    string Ordering,
    string AssemblyRule,
    string HashRule);

public sealed record ToolOutputContractResourcePage(
    string ToolName,
    string Sha256,
    [property: ToolNumericSemantics("identifier", "page_number", "exact", "contract_page_identity", minimum: 1)]
    int Page,
    [property: ToolNumericSemantics("metric", "pages", "exact", "complete_contract_page_count", minimum: 1)]
    int PageCount,
    [property: ToolNumericSemantics("offset", "utf8_bytes", "exact", "zero_based_fragment_start", minimum: 0)]
    int StartUtf8Byte,
    [property: ToolNumericSemantics("metric", "utf8_bytes", "exact", "returned_fragment_byte_count", minimum: 1)]
    int ReturnedUtf8Bytes,
    string SchemaFragment);

public sealed record ToolContractPageResponse(
    string ToolName,
    string ContractVersion,
    string SchemaUri,
    string Sha256,
    string MediaType,
    [property: ToolNumericSemantics("metric", "utf8_bytes", "exact", "complete_contract_byte_count", minimum: 1)]
    int Utf8Bytes,
    [property: ToolNumericSemantics("identifier", "page_number", "exact", "contract_page_identity", minimum: 1)]
    int Page,
    [property: ToolNumericSemantics("metric", "pages", "exact", "complete_contract_page_count", minimum: 1)]
    int PageCount,
    [property: ToolNumericSemantics("offset", "utf8_bytes", "exact", "zero_based_fragment_start", minimum: 0)]
    int StartUtf8Byte,
    [property: ToolNumericSemantics("metric", "utf8_bytes", "exact", "returned_fragment_byte_count", minimum: 1)]
    int ReturnedUtf8Bytes,
    string SchemaFragment,
    [property: ToolNumericSemantics("identifier", "page_number", "exact", "next_contract_page_identity", minimum: 1)]
    int? NextPage);

public sealed record ServerToolCatalogResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    IReadOnlyList<ServerToolCatalogRecord> Tools);

public sealed record CatalogResourceShardRecord(
    string Key,
    string Uri,
    int ItemCount);

public sealed record CatalogResourceIndexRecord(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string ResourceKind,
    int TotalItems,
    string ShardingKey,
    string Ordering,
    IReadOnlyList<CatalogResourceShardRecord> Shards);

public sealed record CatalogResourcePageRecord(
    int Page,
    string Uri,
    int ItemCount);

public sealed record CatalogResourcePageIndexRecord(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string ResourceKind,
    string Key,
    int TotalItems,
    string Ordering,
    IReadOnlyList<CatalogResourcePageRecord> Pages);

public sealed record ServerCapabilityCatalogShardResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string Domain,
    int Page,
    int TotalCapabilitiesInDomain,
    int ReturnedCapabilities,
    IReadOnlyList<ListedCapabilityRecord> Capabilities,
    string CapabilityRecordCompleteness = "summary_complete_details_in_linked_resource");

public sealed record ServerToolCatalogShardResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string Domain,
    int TotalToolsInCatalog,
    int ReturnedTools,
    IReadOnlyList<ServerToolCatalogRecord> Tools,
    string? NoDataReason);

public sealed record ServerToolResourceShardResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string Domain,
    int Page,
    int TotalToolsInDomain,
    int ReturnedTools,
    IReadOnlyList<ListedToolResourceRecord> Tools,
    string ToolRecordCompleteness = "summary_complete_details_in_linked_resource");

public sealed record CapabilityWorkflowCatalogShardResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    string WorkflowId,
    IReadOnlyList<CapabilityGoalRecord> Goals,
    CapabilityWorkflowRecord Workflow);

public sealed record CapabilityWorkflowCatalogResource(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyResourceReference CapabilityPolicy,
    IReadOnlyList<CapabilityGoalRecord> Goals,
    IReadOnlyList<CapabilityWorkflowRecord> Workflows);

public sealed record ListCapabilitiesResponse(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    string CanonicalContentHash,
    CapabilityPolicyRecord CapabilityPolicy,
    CapabilityMapFilter NormalizedFilter,
    CapabilityMapTotals Totals,
    string Ordering,
    IReadOnlyList<ListedCapabilityRecord> Capabilities,
    [property: System.ComponentModel.Description("Expanded goal records. Bounded list_capabilities cursor pages leave this empty; follow each capability's GoalIds through the workflow resources. Full catalog resources retain the expansion.")]
    IReadOnlyList<CapabilityGoalRecord> Goals,
    [property: System.ComponentModel.Description("Expanded workflow records. Bounded list_capabilities cursor pages leave this empty; WorkflowIds map to wpa://workflows/{workflowId}. Full catalog resources retain the expansion.")]
    IReadOnlyList<CapabilityWorkflowRecord> Workflows,
    bool HasMore,
    [property: ToolOpaqueLocator("capability_cursor", "^cpc_[0-9a-f]{32}$")]
    string? NextCursor,
    string? NoDataReason);

public sealed record TraceCaptureEvidenceBoundary(
    long ReportedEventsLost,
    ToolCaptureIntegrityStatus CaptureIntegrity,
    string CaptureIntegrityState,
    string MeasurementBasis,
    string EventCountRepresentation,
    string RawEtwRecordCountState,
    string ParserCoverageState,
    IReadOnlyList<string> DoesNotProve);

public sealed record TraceSymbolEvidenceBoundary(
    int ModuleCount,
    int ModulesWithPdbName,
    int ModulesWithCompletePdbIdentity,
    string PdbIdentityMeasurementState,
    string LocalReadinessMeasurementState,
    string FrameResolutionMeasurementState,
    IReadOnlyList<string> DoesNotProve,
    string NextStep);

public sealed record TraceSelfAttributionEvidence(
    string Status,
    string MatchRule,
    int ExactNameMatchCount,
    ProcessInstanceKey? SelectedProcess,
    IReadOnlyList<ProcessInstanceKey> Candidates,
    string ConclusionStatus,
    IReadOnlyList<string> DoesNotProve);

public sealed record TraceCapabilityEvidenceRecord(
    string CapabilityId,
    string EvaluatorId,
    ToolCapabilityStatus TraceStatus,
    CapabilityAvailabilityStatus AvailabilityState,
    long? TraceEligibleEventCount,
    string CountRepresentation,
    DomainStackCoverage? StackCoverage,
    string? UnavailableReason,
    IReadOnlyList<string> Warnings,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus ConclusionStatus,
    ToolCaptureIntegrityStatus CaptureIntegrity,
    IReadOnlyList<string> CallableTools,
    IReadOnlyList<string> DisabledByPolicyTools,
    IReadOnlyList<string> DoesNotProve,
    string DetailsResourceUri,
    [property: System.ComponentModel.Description("Whole-trace count of exact completed lifecycles or intervals for an evidence_completion evaluator; null for other evaluator kinds.")]
    long? TraceCompletedEvidenceCount = null,
    [property: System.ComponentModel.Description("Whole-trace count of unmatched source endpoints for an evidence_completion evaluator; null for other evaluator kinds.")]
    long? TraceUnmatchedEvidenceCount = null,
    [property: System.ComponentModel.Description("Whole-trace count of inferred, identity-unresolved, invalid, or otherwise bounded evidence items for an evidence_completion evaluator; null for other evaluator kinds.")]
    long? TraceBoundaryEvidenceCount = null,
    [property: System.ComponentModel.Description("One of not_applicable, no_source_evidence, source_without_completed_evidence, completed_with_incomplete_evidence, or complete.")]
    ToolEvidenceCompletionState EvidenceCompletionState =
        ToolEvidenceCompletionState.NotApplicable);

public sealed record TraceWorkflowEvidenceRecord(
    string WorkflowId,
    IReadOnlyList<string> SuggestedTools,
    string EvidenceState,
    [property: System.ComponentModel.Description("Exact workflow membership count. It equals the sum of all five mutually exclusive trace-status buckets, including NotApplicableCapabilityCount.")]
    int TotalCapabilityCount,
    int AvailableCapabilityCount,
    int PartialCapabilityCount,
    int UnknownCapabilityCount,
    int UnavailableCapabilityCount,
    [property: System.ComponentModel.Description("Workflow members whose capability evaluator is not applicable to trace evidence, such as server/catalog capabilities. These members are not missing or unavailable trace evidence.")]
    int NotApplicableCapabilityCount,
    [property: System.ComponentModel.Description("Orthogonal startup-policy subset of workflow members. This is not a sixth trace-evidence bucket and is not added to TotalCapabilityCount.")]
    [property: ToolNumericSemantics("metric", "capabilities", "exact", "policy_disabled_membership_count", minimum: 0)]
    int DisabledByPolicyCapabilityCount,
    IReadOnlyList<string> DisabledByPolicyCapabilityIds,
    IReadOnlyList<string> UnavailableByImplementationCapabilityIds,
    IReadOnlyList<string> DoesNotProve,
    string DetailsResourceUri);

public sealed record TraceEvidenceMapFilter(
    string? Domain,
    string? Goal);

public sealed record TraceEvidenceMapRecord(
    string CatalogScope,
    bool ExhaustiveForWpa,
    string UnlistedCapabilityMeaning,
    string CatalogVersion,
    CapabilityPolicyRecord CapabilityPolicy,
    string EvaluationScope,
    string Ordering,
    TraceEvidenceMapFilter Filter,
    int CatalogCapabilityCount,
    int TotalCapabilities,
    int ReturnedCapabilities,
    int CatalogWorkflowCount,
    int TotalWorkflows,
    int ReturnedWorkflows,
    TraceCaptureEvidenceBoundary Capture,
    TraceSymbolEvidenceBoundary Symbols,
    TraceSelfAttributionEvidence SelfAttribution,
    IReadOnlyList<TraceCapabilityEvidenceRecord> Capabilities,
    IReadOnlyList<TraceWorkflowEvidenceRecord> Workflows);

public sealed record InspectTracePageContext(
    string Phase,
    int StartIndex,
    string ContextState,
    bool OrientationIncluded,
    [property: ToolOpaqueLocator("trace_generation_id", "^tgen_[0-9a-f]{32}$")]
    string TraceGenerationId,
    string Ordering,
    string? NormalizedDomain,
    string? NormalizedGoal);
