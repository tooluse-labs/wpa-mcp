using WpaMcp.Output;

namespace WpaMcp.Core.Catalog;

internal static class TraceEvidenceMapBuilder
{
    internal const string Ordering = "domain_asc_capability_id_asc";

    internal static TraceEvidenceMapRecord Build(
        ActiveToolCatalog catalog,
        TraceFactsSnapshot facts,
        InspectSymbolQuality symbolQuality)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(symbolQuality);

        var callableToolsByCapability = catalog.Tools
            .SelectMany(tool => tool.Capabilities.Select(capability =>
                (capability.CapabilityId, tool.ToolName)))
            .ToLookup(item => item.CapabilityId, item => item.ToolName, StringComparer.Ordinal);
        var allToolsByCapability = catalog.AllTools
            .SelectMany(tool => tool.Capabilities.Select(capability =>
                (capability.CapabilityId, tool.ToolName)))
            .ToLookup(item => item.CapabilityId, item => item.ToolName, StringComparer.Ordinal);
        var evaluatorById = catalog.Evaluators.ToDictionary(
            evaluator => evaluator.EvaluatorId,
            StringComparer.Ordinal);
        var assessments = catalog.Capabilities.ToDictionary(
            capability => capability.CapabilityId,
            capability => catalog.EvaluatorRegistry.EvaluateTrace(capability, facts),
            StringComparer.Ordinal);

        var capabilities = catalog.Capabilities
            .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => ProjectCapability(
                capability,
                assessments[capability.CapabilityId],
                callableToolsByCapability[capability.CapabilityId],
                allToolsByCapability[capability.CapabilityId],
                catalog.CapabilityPolicy.IsDisabled(capability.CapabilityId)))
            .ToArray();
        var workflows = catalog.Workflows
            .OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .Select(workflow => ProjectWorkflow(
                workflow,
                assessments,
                evaluatorById,
                catalog))
            .ToArray();

        return new TraceEvidenceMapRecord(
            catalog.CatalogScope,
            catalog.ExhaustiveForWpa,
            catalog.UnlistedCapabilityMeaning,
            catalog.CatalogVersion,
            catalog.CapabilityPolicy.ToRecord(),
            "whole_trace_generation",
            Ordering,
            new TraceEvidenceMapFilter(null, null),
            capabilities.Length,
            capabilities.Length,
            capabilities.Length,
            workflows.Length,
            workflows.Length,
            workflows.Length,
            new TraceCaptureEvidenceBoundary(
                facts.CaptureIntegrity.ReportedEventsLost,
                facts.CaptureIntegrity.ReportedEventsLost > 0
                    ? ToolCaptureIntegrityStatus.Partial
                    : ToolCaptureIntegrityStatus.Unknown,
                facts.CaptureIntegrity.State + "_parser_coverage_unknown",
                facts.CaptureIntegrity.MeasurementBasis,
                facts.Provenance.EventCountRepresentation,
                "not_measured",
                "not_computed",
                [
                    "no_reported_event_loss_does_not_prove_complete_capture",
                    "materialized_logical_event_count_does_not_equal_raw_etw_record_count",
                    "event_class_not_observed_does_not_prove_provider_or_keyword_disabled",
                ]),
            new TraceSymbolEvidenceBoundary(
                symbolQuality.ModuleCount,
                symbolQuality.ModulesWithPdbName,
                symbolQuality.ModulesWithCompletePdbIdentity,
                "trace_pdb_identity_metadata_observed",
                symbolQuality.LocalReadinessMeasurementState,
                symbolQuality.FrameResolutionMeasurementState,
                [
                    "pdb_identity_does_not_prove_local_readiness",
                    "local_readiness_does_not_prove_frame_resolution",
                    "inspect_trace_does_not_measure_frame_resolution",
                ],
                symbolQuality.NextStep),
            SelfAttribution(facts.Processes),
            capabilities,
            workflows);
    }

    private static TraceCapabilityEvidenceRecord ProjectCapability(
        CapabilityDefinition capability,
        CapabilityRuntimeAssessment assessment,
        IEnumerable<string> callableToolNames,
        IEnumerable<string> allToolNames,
        bool disabledByPolicy)
    {
        var inference = assessment.Evidence.First();
        var callable = callableToolNames.Order(StringComparer.Ordinal).ToArray();
        var disabled = allToolNames.Except(callable, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new TraceCapabilityEvidenceRecord(
            capability.CapabilityId,
            assessment.EvaluatorId,
            assessment.TraceStatus,
            disabledByPolicy
                ? CapabilityAvailabilityStatus.DisabledByPolicy
                : capability.ProductMaturity == "gap"
                    ? CapabilityAvailabilityStatus.UnavailableByImplementation
                    : capability.LifecycleStatus == "deprecated"
                        ? CapabilityAvailabilityStatus.Deprecated
                        : CapabilityAvailabilityStatus.Callable,
            assessment.TraceEligibleEventCount,
            assessment.CountRepresentation,
            assessment.StackCoverage,
            assessment.UnavailableReason,
            assessment.Warnings,
            inference.MeasurementBasis,
            inference.Relationship,
            inference.ConclusionStatus,
            assessment.CaptureIntegrity,
            callable,
            disabled,
            inference.DoesNotProve,
            $"wpa://capabilities/domain/{capability.Domain}",
            assessment.TraceCompletedEvidenceCount,
            assessment.TraceUnmatchedEvidenceCount,
            assessment.TraceBoundaryEvidenceCount,
            assessment.EvidenceCompletionState);
    }

    private static TraceWorkflowEvidenceRecord ProjectWorkflow(
        CapabilityWorkflowDefinition workflow,
        IReadOnlyDictionary<string, CapabilityRuntimeAssessment> assessments,
        IReadOnlyDictionary<string, CapabilityEvaluatorDefinition> evaluators,
        ActiveToolCatalog catalog)
    {
        var workflowStatuses = workflow.CapabilityIds
            .Select(capabilityId => assessments[capabilityId].TraceStatus)
            .ToArray();
        var available = workflowStatuses.Count(item =>
            item == ToolCapabilityStatus.Available);
        var partial = workflowStatuses.Count(item =>
            item == ToolCapabilityStatus.Partial);
        var unknown = workflowStatuses.Count(item =>
            item == ToolCapabilityStatus.Unknown);
        var unavailable = workflowStatuses.Count(item =>
            item == ToolCapabilityStatus.Unavailable);
        var notApplicable = workflowStatuses.Count(item =>
            item == ToolCapabilityStatus.NotApplicable);
        var total = workflowStatuses.Length;
        var bucketTotal = checked(
            available + partial + unknown + unavailable + notApplicable);
        if (bucketTotal != total)
        {
            throw new InvalidOperationException(
                $"Workflow '{workflow.WorkflowId}' trace-status buckets do not close over its capability membership.");
        }
        var disabledCapabilityIds = workflow.CapabilityIds.Where(
                catalog.CapabilityPolicy.IsDisabled)
            .ToArray();
        var disabledByPolicy = disabledCapabilityIds.Length;
        var gaps = workflow.CapabilityIds.Where(capabilityId =>
                catalog.Capabilities.Single(capability =>
                    capability.CapabilityId == capabilityId).ProductMaturity == "gap")
            .ToArray();
        var suggestedCapabilityIds = workflow.CapabilityIds.Where(capabilityId =>
        {
            if (catalog.CapabilityPolicy.IsDisabled(capabilityId))
                return false;
            var assessment = assessments[capabilityId];
            return assessment.TraceStatus is ToolCapabilityStatus.Available or ToolCapabilityStatus.Partial ||
                   evaluators[assessment.EvaluatorId].Kind == "query_dependent";
        }).ToHashSet(StringComparer.Ordinal);
        var suggestedTools = catalog.Tools.Where(tool =>
                workflow.ToolNames.Contains(tool.ToolName, StringComparer.Ordinal) &&
                tool.Capabilities.Any(capability =>
                    suggestedCapabilityIds.Contains(capability.CapabilityId)))
            .Select(tool => tool.ToolName)
            .ToArray();
        var evidenceState = available + partial > 0
            ? unavailable + unknown > 0
                ? "mixed_observed_and_bounded"
                : notApplicable > 0
                    ? "observed_or_partial_with_trace_not_applicable_members"
                    : "observed_or_partial"
            : unknown > 0
                ? "query_or_capture_evidence_required"
                : unavailable > 0
                    ? notApplicable > 0
                        ? "implementation_gaps_with_trace_not_applicable_members"
                        : "unavailable_by_implementation"
                    : "trace_evidence_not_applicable";
        var doesNotProve = new List<string>
        {
            "workflow_membership_does_not_prove_causal_ranking_or_root_cause",
            "callable_tool_exposure_does_not_prove_trace_evidence_availability",
        };
        if (gaps.Length > 0)
            doesNotProve.Add("implementation_gap_is_not_observed_trace_evidence");
        if (notApplicable > 0)
            doesNotProve.Add("trace_not_applicable_members_are_catalog_members_not_missing_evidence");
        if (disabledByPolicy > 0)
            doesNotProve.Add("policy_disabled_members_are_not_runtime_evidence_unavailability");
        return new TraceWorkflowEvidenceRecord(
            workflow.WorkflowId,
            suggestedTools,
            evidenceState,
            total,
            available,
            partial,
            unknown,
            unavailable,
            notApplicable,
            disabledByPolicy,
            disabledCapabilityIds,
            gaps,
            doesNotProve,
            $"wpa://workflows/{workflow.WorkflowId}");
    }

    private static TraceSelfAttributionEvidence SelfAttribution(
        IReadOnlyList<ProcessRow> processes)
    {
        var exactMatches = processes.Where(process => string.Equals(
                Path.GetFileNameWithoutExtension(process.Name),
                "WpaMcp",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(process => process.StartUs)
            .ThenBy(process => process.Pid)
            .Select(process => new ProcessInstanceKey(process.Pid, process.StartUs))
            .ToArray();
        return exactMatches.Length switch
        {
            1 => new TraceSelfAttributionEvidence(
                "exact_process_instance_observed",
                "case_insensitive_exact_file_name_without_extension_equals_WpaMcp",
                1,
                exactMatches[0],
                exactMatches,
                "observed",
                ["process_name_match_does_not_prove_this_trace_captured_mcp_request_latency"]),
            0 => new TraceSelfAttributionEvidence(
                "not_concluded_exact_process_not_observed",
                "case_insensitive_exact_file_name_without_extension_equals_WpaMcp",
                0,
                null,
                exactMatches,
                "not_concluded",
                ["absence_from_process_inventory_does_not_prove_mcp_performance"]),
            _ => new TraceSelfAttributionEvidence(
                "not_concluded_multiple_exact_process_instances",
                "case_insensitive_exact_file_name_without_extension_equals_WpaMcp",
                exactMatches.Length,
                null,
                exactMatches,
                "not_concluded",
                ["multiple_process_instances_require_an_explicit_processStartUs_selector"]),
        };
    }
}
