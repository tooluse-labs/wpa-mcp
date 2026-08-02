using System.Diagnostics;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record PlannedQuery<T>(
    T Value,
    PlannerExecutionTelemetry Telemetry);

/// <summary>
/// Typed admission boundary for shared trace operations. Only operations approved
/// by the active catalog may execute through this class.
/// </summary>
internal sealed class QueryPlanner(ActiveToolCatalog catalog)
{
    internal PlannedQuery<T> ExecuteTraceFacts<T>(
        TraceLease traceLease,
        string toolName,
        Func<TraceFactsSnapshot, T> projection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(traceLease);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        ArgumentNullException.ThrowIfNull(projection);

        var admissionClock = Stopwatch.StartNew();
        var tool = catalog.Tools.SingleOrDefault(candidate =>
            string.Equals(candidate.ToolName, toolName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The active catalog does not contain planner tool '{toolName}'.");
        var admission = tool.PlannerAdmission
            ?? throw new InvalidOperationException(
                $"The active catalog does not declare planner admission for '{toolName}'.");
        if (!string.Equals(admission.AdmissionStatus, "approved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Planner operation '{toolName}' is not admitted: {admission.AdmissionStatus}.");
        }
        admissionClock.Stop();

        var factsClock = Stopwatch.StartNew();
        var acquisition = traceLease.GetFactsAcquisition(cancellationToken);
        factsClock.Stop();

        var projectionClock = Stopwatch.StartNew();
        var value = projection(acquisition.Snapshot);
        projectionClock.Stop();

        var physicalPassCount = acquisition.ParticipatingPhysicalPassCount;
        if (admission.PhysicalPassLimit is { } limit && physicalPassCount > limit)
        {
            throw new InvalidOperationException(
                $"Planner operation '{toolName}' exceeded its admitted physical-pass limit.");
        }

        return new PlannedQuery<T>(
            value,
            new PlannerExecutionTelemetry(
                ToolName: toolName,
                OperationVersion: admission.OperationVersion,
                AdmissionStatus: admission.AdmissionStatus,
                AdmissionEvidence: admission.EvidenceReferences.Select(ToPublic).ToArray(),
                MissingEvidence: admission.MissingEvidence,
                ExecutionStatus: "completed",
                SnapshotAcquisition: AcquisitionName(acquisition.Kind),
                LogicalAnalyzersExecuted: [admission.OperationVersion],
                PhysicalTracePassCount: physicalPassCount,
                PhysicalTracePassCountState: "measured_current_call_participation",
                ScannedEventCount: acquisition.Snapshot.LogicalEventCount,
                ScannedEventCountState: "measured_generation_snapshot",
                MatchedEventCount: null,
                MatchedEventCountState: "not_applicable_no_scoped_match_predicate",
                MeasurementBasis: new PlannerExecutionMeasurementBasis(
                    PhysicalTracePassCount:
                        "current_call_participation_in_generation_facts_physical_passes",
                    ScannedEventCount: acquisition.Snapshot.Provenance.EventCountRepresentation,
                    MatchedEventCount: "not_applicable_inspect_trace_has_no_scoped_event_predicate",
                    PhaseDurations: "current_call_stopwatch_elapsed_only",
                    Admission: "validated_active_catalog_and_benchmark_manifest"),
                PhaseDurations:
                [
                    Phase("planner_admission", admissionClock.Elapsed),
                    Phase("trace_facts_acquisition", factsClock.Elapsed),
                    Phase("result_projection", projectionClock.Elapsed),
                ],
                BudgetTerminationStatus: "not_terminated",
                BudgetTerminationReason: null,
                PhysicalPassLimit: admission.PhysicalPassLimit,
                EvidenceBoundaries:
                [
                    "physical_pass_count_is_current_call_participation_not_generation_history",
                    "ready_snapshot_reuse_reports_zero_new_or_joined_physical_passes",
                    "scanned_event_count_is_generation_snapshot_materialized_logical_events",
                    "matched_event_count_is_not_applicable_without_a_scoped_match_predicate",
                    "phase_durations_measure_current_call_only",
                    "generation_snapshot_build_duration_is_not_replayed_as_current_call_time",
                ]));
    }

    internal PlannerExecutionTelemetry DescribeNotAdmitted(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var tool = catalog.Tools.SingleOrDefault(candidate =>
            string.Equals(candidate.ToolName, toolName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"The active catalog does not contain planner tool '{toolName}'.");
        var admission = tool.PlannerAdmission
            ?? throw new InvalidOperationException(
                $"The active catalog does not declare planner admission for '{toolName}'.");
        if (string.Equals(admission.AdmissionStatus, "approved", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Planner operation '{toolName}' is admitted and cannot be described as not admitted.");
        }

        return new PlannerExecutionTelemetry(
            ToolName: toolName,
            OperationVersion: admission.OperationVersion,
            AdmissionStatus: admission.AdmissionStatus,
            AdmissionEvidence: admission.EvidenceReferences.Select(ToPublic).ToArray(),
            MissingEvidence: admission.MissingEvidence,
            ExecutionStatus: "direct_tool_execution_planner_not_admitted",
            SnapshotAcquisition: "not_admitted",
            LogicalAnalyzersExecuted: [],
            PhysicalTracePassCount: null,
            PhysicalTracePassCountState: "unavailable_not_admitted",
            ScannedEventCount: null,
            ScannedEventCountState: "unavailable_not_admitted",
            MatchedEventCount: null,
            MatchedEventCountState: "unavailable_not_admitted",
            MeasurementBasis: new PlannerExecutionMeasurementBasis(
                PhysicalTracePassCount: "unavailable_not_admitted",
                ScannedEventCount: "unavailable_not_admitted",
                MatchedEventCount: "unavailable_not_admitted",
                PhaseDurations: "not_applicable_planner_not_executed",
                Admission: "validated_active_catalog_and_benchmark_manifest"),
            PhaseDurations: [],
            BudgetTerminationStatus: "not_managed_by_planner",
            BudgetTerminationReason: null,
            PhysicalPassLimit: admission.PhysicalPassLimit,
            EvidenceBoundaries:
            [
                "planner_not_executed",
                "no_single_dispatch_claim",
                "logical_and_physical_counts_are_unavailable_not_zero",
                "direct_composite_execution_remains_available_outside_the_planner",
            ]);
    }

    private static PlannerPhaseDuration Phase(string phase, TimeSpan elapsed) =>
        new(phase, DurationUs(elapsed));

    private static long DurationUs(TimeSpan elapsed) =>
        (long)Math.Round(
            elapsed.TotalMicroseconds,
            MidpointRounding.AwayFromZero);

    private static string AcquisitionName(TraceFactsAcquisitionKind kind) => kind switch
    {
        TraceFactsAcquisitionKind.ReadySnapshotReuse => "ready_snapshot_reuse",
        TraceFactsAcquisitionKind.JoinedInFlight => "joined_in_flight",
        TraceFactsAcquisitionKind.StartedNewBuild => "started_new_build",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static CapabilityMapEvidenceReference ToPublic(EvidenceReference reference) =>
        new(reference.EvidenceId, reference.Kind, reference.Path, reference.Member);
}
