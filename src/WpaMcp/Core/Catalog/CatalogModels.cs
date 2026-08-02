using System.Collections.Immutable;
using System.Reflection;
using ModelContextProtocol.Protocol;

namespace WpaMcp.Core.Catalog;

internal sealed record CapabilityDefinition(
    string CapabilityId,
    int DefinitionVersion,
    string LifecycleStatus,
    string Domain,
    string Title,
    string Summary,
    ImmutableArray<string> QuestionsAnswered,
    ImmutableArray<string> QuestionsNotAnswered,
    ImmutableArray<string> ConclusionBoundaryCodes,
    ImmutableArray<string> RequiredEvents,
    ImmutableArray<string> RequiredEventStacks,
    ImmutableArray<string> OptionalEvidence,
    string SymbolRequirement,
    string MaximumRelationship,
    ImmutableArray<string> SupportedScopes,
    string CostClass,
    string SideEffectClass,
    string ContractVersion,
    ImmutableArray<string> SourcePaths,
    string? ReplacedBy,
    string? RemovalContractVersion,
    string ProductMaturity,
    ImmutableArray<EvidenceReference> EvidenceReferences,
    ImmutableArray<string> GoalIds,
    ImmutableArray<string> WorkflowIds,
    string EvaluatorId);

internal sealed record CapabilityGoalDefinition(
    string GoalId,
    string Title,
    string Summary,
    ImmutableArray<string> WorkflowIds);

internal sealed record CapabilityWorkflowDefinition(
    string WorkflowId,
    string Title,
    string Summary,
    ImmutableArray<string> GoalIds,
    ImmutableArray<string> CapabilityIds,
    ImmutableArray<string> ToolNames);

internal sealed record CapabilityEvaluatorDefinition(
    string EvaluatorId,
    string Kind,
    ImmutableArray<string> CapabilityIds,
    ImmutableArray<string> EventFlags,
    string? EventCountProperty,
    string? CompletedCountProperty,
    string? UnmatchedCountProperty,
    string? BoundaryCountProperty,
    string? StackDomain,
    string MeasurementBasis,
    string Relationship,
    string ObservedConclusion,
    string CountRepresentation,
    string Provenance);

internal sealed record EvidenceReference(
    string EvidenceId,
    string Kind,
    string Path,
    string? Member);

internal sealed record ActiveToolDefinition(
    string ToolName,
    MethodInfo Method,
    string InputType,
    string OutputType,
    Type OutputDataType,
    ImmutableArray<CapabilityDefinition> Capabilities,
    ImmutableArray<string> RequiredCapabilities,
    ImmutableArray<string> SelectableScopes,
    ToolAnnotations Annotations,
    ImmutableArray<string> SideEffects,
    string CostClass,
    int DiscoveryPriority,
    string Domain,
    int Ordinal,
    string DefaultOrdering,
    ImmutableArray<string> TieBreakers,
    ImmutableArray<string> PageableSections,
    string PaginationMode,
    ToolDeprecation Deprecation,
    ImmutableArray<string> InternalAnalyzerOperations,
    ImmutableArray<string> AllowedMeasurementBases,
    string MaximumRelationship,
    ImmutableArray<string> ConclusionRules,
    ImmutableArray<string> DoesNotProve,
    ImmutableArray<string> EvidenceReferenceIds,
    PlannerAdmissionDefinition? PlannerAdmission);

internal sealed record PlannerAdmissionDefinition(
    string ToolName,
    string CapabilityId,
    string OperationVersion,
    string AdmissionStatus,
    int? PhysicalPassLimit,
    ImmutableArray<EvidenceReference> EvidenceReferences,
    ImmutableArray<string> MissingEvidence);

internal sealed record ToolAnnotations(
    bool ReadOnlyHint,
    bool IdempotentHint,
    bool OpenWorldHint,
    bool DestructiveHint);

internal sealed record ToolDeprecation(
    string State,
    string? ReplacedBy,
    string? RemovalContractVersion);

internal sealed class CatalogValidationException(string message) : InvalidOperationException(message);

internal sealed class CapabilityManifest
{
    public string SchemaVersion { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public string CatalogScope { get; init; } = "";
    public bool ExhaustiveForWpa { get; init; }
    public string UnlistedCapabilityMeaning { get; init; } = "";
    public string CatalogVersionPolicy { get; init; } = "";
    public List<CapabilityGoalManifestEntry> Goals { get; init; } = [];
    public List<CapabilityWorkflowManifestEntry> Workflows { get; init; } = [];
    public List<CapabilityEvaluatorManifestEntry> Evaluators { get; init; } = [];
    public List<CapabilityManifestEntry> Capabilities { get; init; } = [];
}

internal sealed class CapabilityGoalManifestEntry
{
    public string GoalId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
}

internal sealed class CapabilityWorkflowManifestEntry
{
    public string WorkflowId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> GoalIds { get; init; } = [];
    public List<string> CapabilityIds { get; init; } = [];
}

internal sealed class CapabilityEvaluatorManifestEntry
{
    public string EvaluatorId { get; init; } = "";
    public string Kind { get; init; } = "";
    public List<string> CapabilityIds { get; init; } = [];
    public List<string> EventFlags { get; init; } = [];
    public string? EventCountProperty { get; init; }
    public string? CompletedCountProperty { get; init; }
    public string? UnmatchedCountProperty { get; init; }
    public string? BoundaryCountProperty { get; init; }
    public string? StackDomain { get; init; }
    public string MeasurementBasis { get; init; } = "";
    public string Relationship { get; init; } = "";
    public string ObservedConclusion { get; init; } = "";
    public string CountRepresentation { get; init; } = "";
    public string Provenance { get; init; } = "";
}

internal sealed class CapabilityManifestEntry
{
    public string CapabilityId { get; init; } = "";
    public int DefinitionVersion { get; init; }
    public string LifecycleStatus { get; init; } = "";
    public string Domain { get; init; } = "";
    public string Title { get; init; } = "";
    public string Summary { get; init; } = "";
    public List<string> QuestionsAnswered { get; init; } = [];
    public List<string> QuestionsNotAnswered { get; init; } = [];
    public List<string> ConclusionBoundaryCodes { get; init; } = [];
    public List<string> RequiredEvents { get; init; } = [];
    public List<string> RequiredEventStacks { get; init; } = [];
    public List<string> OptionalEvidence { get; init; } = [];
    public string SymbolRequirement { get; init; } = "";
    public string MaximumRelationship { get; init; } = "";
    public List<string> SupportedScopes { get; init; } = [];
    public string CostClass { get; init; } = "";
    public string SideEffectClass { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public List<string> SourcePaths { get; init; } = [];
    public string? ReplacedBy { get; init; }
    public string? RemovalContractVersion { get; init; }
}

internal sealed class ToolContractManifest
{
    public string SchemaVersion { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public List<InputSchemaOverlayManifest> InputSchemaOverlays { get; init; } = [];
    public List<ToolContractManifestEntry> Tools { get; init; } = [];
}

internal sealed class InputSchemaOverlayManifest
{
    public string OverlayId { get; init; } = "";
    public string SelectorParameter { get; init; } = "";
    public string InjectedInputProperty { get; init; } = "";
    public int ExpectedToolCount { get; init; }
}

internal sealed class ToolContractManifestEntry
{
    public string ToolName { get; init; } = "";
    public bool Enabled { get; init; }
    public string ContractVersion { get; init; } = "";
    public string DeclaringType { get; init; } = "";
    public string Method { get; init; } = "";
    public string InputType { get; init; } = "";
    public string OutputType { get; init; } = "";
    public List<string> CapabilityIds { get; init; } = [];
    public List<string> RequiredCapabilities { get; init; } = [];
    public List<string> SelectableScopes { get; init; } = [];
    public ToolAnnotationsManifest Annotations { get; init; } = new();
    public List<string> SideEffects { get; init; } = [];
    public string CostClass { get; init; } = "";
    public int DiscoveryPriority { get; init; }
    public string Domain { get; init; } = "";
    public int Ordinal { get; init; }
    public string DefaultOrdering { get; init; } = "";
    public List<string> TieBreakers { get; init; } = [];
    public List<string> PageableSections { get; init; } = [];
    public string PaginationMode { get; init; } = "";
    public ToolDeprecationManifest Deprecation { get; init; } = new();
    public List<string> InternalAnalyzerOperations { get; init; } = [];
    public List<string> AllowedMeasurementBases { get; init; } = [];
    public string MaximumRelationship { get; init; } = "";
    public List<string> ConclusionRules { get; init; } = [];
    public List<string> DoesNotProve { get; init; } = [];
    public List<string> EvidenceReferences { get; init; } = [];
}

internal static class CatalogScopeSemantics
{
    public const string CapabilitySupportedScopes =
        "Collective capability applicability: for an implemented capability, this is the union of mapped tools' selectable scopes, so each value is selectable by at least one mapped tool but not necessarily every mapped tool; gap capabilities describe intended scope without a callable mapping.";

    public const string ToolSelectableScopes =
        "Each value names an analysis scope callers can select through this tool's public input schema; it does not describe result granularity, evidence completeness, or conclusion strength.";
}

internal sealed class ToolAnnotationsManifest
{
    public bool ReadOnlyHint { get; init; }
    public bool IdempotentHint { get; init; }
    public bool OpenWorldHint { get; init; }
    public bool DestructiveHint { get; init; }
}

internal sealed class ToolDeprecationManifest
{
    public string State { get; init; } = "";
    public string? ReplacedBy { get; init; }
    public string? RemovalContractVersion { get; init; }
}

internal sealed class BenchmarkManifest
{
    public string SchemaVersion { get; init; } = "";
    public List<BenchmarkCapabilityEntry> Capabilities { get; init; } = [];
    public List<PlannerAdmissionManifestEntry> PlannerAdmissions { get; init; } = [];
}

internal sealed class PlannerAdmissionManifestEntry
{
    public string ToolName { get; init; } = "";
    public string CapabilityId { get; init; } = "";
    public string OperationVersion { get; init; } = "";
    public string AdmissionStatus { get; init; } = "";
    public int? PhysicalPassLimit { get; init; }
    public List<EvidenceReferenceManifest> EvidenceReferences { get; init; } = [];
    public List<string> MissingEvidence { get; init; } = [];
}

internal sealed class BenchmarkCapabilityEntry
{
    public string CapabilityId { get; init; } = "";
    public string ProductMaturity { get; init; } = "";
    public List<EvidenceReferenceManifest> EvidenceReferences { get; init; } = [];
}

internal sealed class EvidenceReferenceManifest
{
    public string EvidenceId { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Member { get; init; }
}
