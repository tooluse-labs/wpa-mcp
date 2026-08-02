using System.ComponentModel;
using WpaMcp.Core;

namespace WpaMcp.Output;

public sealed record PlannerPhaseDuration(
    string Phase,
    [property: Description("Monotonic elapsed time spent by the current tool call in this phase, rounded to the nearest integer microsecond with midpoint values away from zero. It never replays a duration recorded by an earlier generation-facts build.")]
    [property: ToolNumericSemantics("metric", "microseconds", "rounded_integer", "current_call_elapsed")]
    long DurationUs);

public sealed record PlannerExecutionMeasurementBasis(
    string PhysicalTracePassCount,
    string ScannedEventCount,
    string MatchedEventCount,
    string PhaseDurations,
    string Admission);

/// <summary>
/// Public, per-call planner evidence. Nullable counters are paired with an explicit
/// state so unavailable/not-applicable evidence can never be mistaken for zero.
/// </summary>
public sealed record PlannerExecutionTelemetry(
    string ToolName,
    string OperationVersion,
    string AdmissionStatus,
    IReadOnlyList<CapabilityMapEvidenceReference> AdmissionEvidence,
    IReadOnlyList<string> MissingEvidence,
    string ExecutionStatus,
    string SnapshotAcquisition,
    IReadOnlyList<string> LogicalAnalyzersExecuted,
    [property: Description("Physical facts-scan passes in which this call participated. Ready-snapshot reuse is measured as zero; this is not a generation-lifetime cumulative count.")]
    [property: ToolNumericSemantics("metric", "physical_passes", "exact", "current_call_participating_pass_count", minimum: 0)]
    int? PhysicalTracePassCount,
    string PhysicalTracePassCountState,
    [property: Description("TraceLog/ETLX materialized logical events represented by the generation-bound facts snapshot. This can remain measured when the current call reuses the snapshot without a new pass.")]
    [property: ToolNumericSemantics("metric", "materialized_logical_events", "exact", "generation_snapshot_count", minimum: 0)]
    long? ScannedEventCount,
    string ScannedEventCountState,
    [property: Description("Events matching a scoped analyzer predicate. Null with state=not_applicable for inspect_trace and state=unavailable_not_admitted for composites outside planner admission.")]
    [property: ToolNumericSemantics("metric", "events", "exact", "current_call_scoped_match_count", minimum: 0)]
    long? MatchedEventCount,
    string MatchedEventCountState,
    PlannerExecutionMeasurementBasis MeasurementBasis,
    IReadOnlyList<PlannerPhaseDuration> PhaseDurations,
    string BudgetTerminationStatus,
    string? BudgetTerminationReason,
    [property: Description("Approved upper bound on physical passes, or null when the operation has not been admitted.")]
    [property: ToolNumericSemantics("metric", "physical_passes", "exact", "admission_upper_bound", minimum: 1)]
    int? PhysicalPassLimit,
    IReadOnlyList<string> EvidenceBoundaries);
