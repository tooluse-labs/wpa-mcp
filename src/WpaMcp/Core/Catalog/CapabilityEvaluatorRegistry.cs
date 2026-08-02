using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Nodes;
using WpaMcp.Output;

namespace WpaMcp.Core.Catalog;

internal sealed record RuntimeEvidenceInference(
    string EvidenceId,
    string CapabilityId,
    MeasurementBasis MeasurementBasis,
    Relationship Relationship,
    ConclusionStatus ConclusionStatus,
    ImmutableArray<string> DoesNotProve,
    string EvaluatorId,
    string Provenance,
    ToolCaptureIntegrityStatus CaptureIntegrity);

internal sealed record CapabilityRuntimeAssessment(
    string CapabilityId,
    string EvaluatorId,
    ToolCapabilityStatus TraceStatus,
    ToolCapabilityStatus ScopedStatus,
    long? TraceEligibleEventCount,
    long? TraceCompletedEvidenceCount,
    long? TraceUnmatchedEvidenceCount,
    long? TraceBoundaryEvidenceCount,
    ToolEvidenceCompletionState EvidenceCompletionState,
    long? ScopedMatchedEventCount,
    ToolCaptureIntegrityStatus CaptureIntegrity,
    string CaptureIntegrityState,
    string CountRepresentation,
    DomainStackCoverage? StackCoverage,
    string? UnavailableReason,
    ImmutableArray<string> Warnings,
    ImmutableArray<RuntimeEvidenceInference> Evidence);

/// <summary>
/// The one runtime inference registry for both the server/trace capability maps and
/// tool envelopes. Definitions come only from the validated Active Catalog.
/// </summary>
internal sealed class CapabilityEvaluatorRegistry
{
    private readonly IReadOnlyDictionary<string, CapabilityEvaluatorDefinition> _byId;

    internal CapabilityEvaluatorRegistry(
        IReadOnlyList<CapabilityEvaluatorDefinition> evaluators)
    {
        ArgumentNullException.ThrowIfNull(evaluators);
        _byId = evaluators.ToDictionary(
            evaluator => evaluator.EvaluatorId,
            StringComparer.Ordinal);
    }

    internal CapabilityRuntimeAssessment EvaluateTrace(
        CapabilityDefinition capability,
        TraceFactsSnapshot facts) =>
        Evaluate(capability, tool: null, domain: null, outcome: null, facts, failed: false);

    internal CapabilityRuntimeAssessment EvaluateTool(
        ActiveToolDefinition tool,
        CapabilityDefinition capability,
        JsonObject? domain,
        ReviewedToolRuntimeOutcome? outcome,
        TraceFactsSnapshot? readyFacts,
        bool failed) =>
        Evaluate(capability, tool, domain, outcome, readyFacts, failed);

    private CapabilityRuntimeAssessment Evaluate(
        CapabilityDefinition capability,
        ActiveToolDefinition? tool,
        JsonObject? domain,
        ReviewedToolRuntimeOutcome? outcome,
        TraceFactsSnapshot? facts,
        bool failed)
    {
        ArgumentNullException.ThrowIfNull(capability);
        var evaluator = _byId.TryGetValue(capability.EvaluatorId, out var found)
            ? found
            : throw new InvalidOperationException(
                $"Capability '{capability.CapabilityId}' has no validated evaluator.");
        if (!evaluator.CapabilityIds.Contains(capability.CapabilityId, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Evaluator '{evaluator.EvaluatorId}' is not closed over '{capability.CapabilityId}'.");
        }

        var singleCapabilityOutcome = tool is not null &&
            tool.Capabilities.Length == 1 &&
            outcome is not null &&
            !failed;
        var capture = CaptureIntegrity(facts, evaluator.Kind);
        var stackCoverage = facts is not null && evaluator.StackDomain is not null &&
            facts.Capabilities.StackCoverageByDomain is not null &&
            facts.Capabilities.StackCoverageByDomain.TryGetValue(
                evaluator.StackDomain,
                out var measuredCoverage)
                ? measuredCoverage
                : null;
        var completionEvidence = CompletionEvidence(evaluator, facts);
        var traceCount = TraceCount(evaluator, facts, stackCoverage);
        var traceStatus = TraceStatus(
            capability,
            evaluator,
            facts,
            stackCoverage,
            completionEvidence);
        var warnings = new List<string>();
        string? unavailableReason = null;

        if (evaluator.Kind == "gap")
        {
            unavailableReason = "unavailable_by_implementation";
        }
        else if (facts is not null &&
                 evaluator.Kind is "event" or "event_requirements" or "event_count" or "evidence_completion" &&
                 !SourceEvidenceObserved(evaluator, facts))
        {
            warnings.Add("event_class_not_observed_does_not_prove_capture_or_parser_absence");
        }
        else if (completionEvidence is { } incomplete)
        {
            if (incomplete.CompletedCount == 0)
                warnings.Add("source_events_observed_without_completed_evidence");
            if (incomplete.UnmatchedCount > 0)
                warnings.Add($"unmatched_source_evidence_count:{incomplete.UnmatchedCount}");
            if (incomplete.BoundaryCount > 0)
                warnings.Add($"bounded_or_unresolved_evidence_count:{incomplete.BoundaryCount}");
        }
        else if (facts is not null &&
                 evaluator.Kind == "event_requirements" &&
                 !AllEventFlagsObserved(evaluator, facts))
        {
            warnings.Add(
                "partial_event_requirements_missing:" +
                string.Join(",", MissingEventFlags(evaluator, facts)));
        }
        else if (capability.RequiredEventStacks.Length > 0 &&
                 stackCoverage is { TotalEventCount: > 0, StackedEventCount: 0 })
        {
            unavailableReason = "target_event_has_no_stacks";
        }
        if (facts is not null && facts.CaptureIntegrity.ReportedEventsLost > 0)
            warnings.Add("reported_event_loss_may_reduce_observed_evidence");

        long? scopedMatched = null;
        var scopedStatus = evaluator.Kind is "server" or "gap"
            ? ToolCapabilityStatus.NotApplicable
            : tool is null
                ? ToolCapabilityStatus.NotApplicable
                : ToolCapabilityStatus.Unknown;
        if (singleCapabilityOutcome)
        {
            scopedMatched = outcome!.MatchedEventCount;
            scopedStatus = outcome.ScopedCapabilityStatus;
            // A scoped PID/thread/window result cannot upgrade independently measured
            // whole-trace stack coverage. For stack capabilities the immutable TraceFacts
            // snapshot is authoritative whenever it exists; otherwise a full scoped result
            // could falsely turn global partial coverage into available.
            if (facts is null ||
                evaluator.StackDomain is null &&
                evaluator.Kind is not ("evidence_completion" or "event_requirements"))
                traceStatus = CombineTraceStatus(traceStatus, outcome.TraceCapabilityStatus);
            if (facts is not null &&
                evaluator.Kind is ("evidence_completion" or "event_requirements"))
            {
                scopedStatus = BoundScopedStatusByWholeTraceRequirements(
                    traceStatus,
                    scopedStatus);
            }
        }

        // TraceLog reports loss globally rather than by provider/domain.  A positive
        // observation remains evidence that an event occurred, but neither a complete
        // result nor a negative capability conclusion is justified for any
        // trace-dependent evaluator.  Apply this after the tool outcome join so a
        // scoped success cannot promote the generation back to "available".
        if (facts is not null &&
            facts.CaptureIntegrity.ReportedEventsLost > 0 &&
            evaluator.Kind is not ("server" or "gap"))
        {
            traceStatus = BoundStatusForReportedLoss(traceStatus);
            scopedStatus = BoundStatusForReportedLoss(scopedStatus);
            if (unavailableReason is not null && traceStatus == ToolCapabilityStatus.Unknown)
            {
                warnings.Add(
                    $"{unavailableReason}_observed_but_reported_event_loss_prevents_complete_absence_conclusion");
                unavailableReason = null;
            }
        }

        var inference = Evidence(
            capability,
            evaluator,
            tool,
            outcome,
            capture,
            traceStatus,
            scopedStatus,
            failed,
            singleCapabilityOutcome);
        return new CapabilityRuntimeAssessment(
            capability.CapabilityId,
            evaluator.EvaluatorId,
            traceStatus,
            scopedStatus,
            traceCount,
            completionEvidence?.CompletedCount,
            completionEvidence?.UnmatchedCount,
            completionEvidence?.BoundaryCount,
            CompletionState(completionEvidence),
            scopedMatched,
            capture,
            CaptureIntegrityState(facts, evaluator.Kind),
            evaluator.CountRepresentation,
            stackCoverage,
            unavailableReason,
            warnings.ToImmutableArray(),
            inference);
    }

    private static ImmutableArray<RuntimeEvidenceInference> Evidence(
        CapabilityDefinition capability,
        CapabilityEvaluatorDefinition evaluator,
        ActiveToolDefinition? tool,
        ReviewedToolRuntimeOutcome? outcome,
        ToolCaptureIntegrityStatus capture,
        ToolCapabilityStatus traceStatus,
        ToolCapabilityStatus scopedStatus,
        bool failed,
        bool singleCapabilityOutcome)
    {
        var references = capability.EvidenceReferences
            .Where(reference => tool is null ||
                tool.EvidenceReferenceIds.Contains(reference.EvidenceId, StringComparer.Ordinal))
            .ToArray();
        if (references.Length == 0)
        {
            throw new InvalidOperationException(
                $"Capability '{capability.CapabilityId}' has no evidence reference for this projection.");
        }

        var basis = failed
            ? SelectWeakestBasis(tool?.AllowedMeasurementBases)
            : ParseBasis(evaluator.MeasurementBasis);
        if (tool is not null &&
            !tool.AllowedMeasurementBases.Contains(Wire(basis), StringComparer.Ordinal))
        {
            basis = singleCapabilityOutcome && outcome is not null &&
                    tool.AllowedMeasurementBases.Contains(
                        Wire(outcome.MeasurementBasis),
                        StringComparer.Ordinal)
                ? outcome.MeasurementBasis
                : SelectWeakestBasis(tool.AllowedMeasurementBases);
        }

        var relationship = failed
            ? Relationship.Descriptive
            : ParseRelationship(evaluator.Relationship);
        if (RelationshipRank(relationship) >
            RelationshipRank(capability.MaximumRelationship))
        {
            relationship = ParseRelationship(capability.MaximumRelationship);
        }
        if (tool is not null &&
            RelationshipRank(relationship) > RelationshipRank(tool.MaximumRelationship))
        {
            relationship = ParseRelationship(tool.MaximumRelationship);
        }
        var conclusion = failed
            ? ConclusionStatus.NotConcluded
            : ParseConclusion(evaluator.ObservedConclusion);
        if (!singleCapabilityOutcome && tool is not null)
            conclusion = ConclusionStatus.NotConcluded;
        else if (singleCapabilityOutcome && outcome is not null &&
                 outcome.ConclusionStatus is ConclusionStatus.NotConcluded or ConclusionStatus.Partial)
            conclusion = outcome.ConclusionStatus;
        conclusion = BoundConclusionByCapabilityStatus(
            conclusion,
            tool is null ? traceStatus : scopedStatus);

        var boundaries = (tool?.DoesNotProve ?? capability.ConclusionBoundaryCodes)
            .Where(capability.ConclusionBoundaryCodes.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray();
        return references.Select(reference => new RuntimeEvidenceInference(
                reference.EvidenceId,
                capability.CapabilityId,
                basis,
                relationship,
                conclusion,
                boundaries,
                evaluator.EvaluatorId,
                evaluator.Provenance,
                capture))
            .ToImmutableArray();
    }

    private static ConclusionStatus BoundConclusionByCapabilityStatus(
        ConclusionStatus requested,
        ToolCapabilityStatus capabilityStatus) => capabilityStatus switch
        {
            ToolCapabilityStatus.NotApplicable => ConclusionStatus.NotApplicable,
            ToolCapabilityStatus.Unknown or ToolCapabilityStatus.Unavailable =>
                ConclusionStatus.NotConcluded,
            ToolCapabilityStatus.Partial => ConclusionStatus.Partial,
            ToolCapabilityStatus.Available => requested,
            _ => throw new ArgumentOutOfRangeException(
                nameof(capabilityStatus),
                capabilityStatus,
                null),
        };

    private static ToolCapabilityStatus TraceStatus(
        CapabilityDefinition capability,
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot? facts,
        DomainStackCoverage? coverage,
        CompletionEvidenceCounts? completionEvidence)
    {
        if (evaluator.Kind == "server")
            return ToolCapabilityStatus.NotApplicable;
        if (evaluator.Kind == "gap")
            return ToolCapabilityStatus.Unavailable;
        if (facts is null)
            return ToolCapabilityStatus.Unknown;

        var baseStatus = evaluator.Kind switch
        {
            "logical_events" => facts.LogicalEventCount > 0
                ? ToolCapabilityStatus.Available
                : ToolCapabilityStatus.Unknown,
            "process_inventory" => facts.Processes.Count > 0
                ? ToolCapabilityStatus.Available
                : ToolCapabilityStatus.Unknown,
            "event" => AnyEventFlagObserved(evaluator, facts)
                ? ToolCapabilityStatus.Available
                : ToolCapabilityStatus.Unknown,
            "event_requirements" => !AnyEventFlagObserved(evaluator, facts)
                ? ToolCapabilityStatus.Unknown
                : AllEventFlagsObserved(evaluator, facts)
                    ? ToolCapabilityStatus.Available
                    : ToolCapabilityStatus.Partial,
            "event_count" => CountedEventCount(evaluator, facts) > 0
                ? ToolCapabilityStatus.Available
                : ToolCapabilityStatus.Unknown,
            "evidence_completion" => CompletionStatus(completionEvidence),
            "query_dependent" => ToolCapabilityStatus.Unknown,
            _ => ToolCapabilityStatus.Unknown,
        };
        if (baseStatus != ToolCapabilityStatus.Available)
            return baseStatus;
        if (capability.RequiredEventStacks.Length == 0)
            return ToolCapabilityStatus.Available;
        if (coverage is null || coverage.TotalEventCount == 0)
            return ToolCapabilityStatus.Partial;
        if (coverage.StackedEventCount == 0)
            return ToolCapabilityStatus.Unavailable;
        return coverage.StackedEventCount < coverage.TotalEventCount
            ? ToolCapabilityStatus.Partial
            : ToolCapabilityStatus.Available;
    }

    private static long? TraceCount(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot? facts,
        DomainStackCoverage? coverage)
    {
        if (facts is null)
            return null;
        return evaluator.Kind switch
        {
            "logical_events" => facts.LogicalEventCount,
            "process_inventory" => facts.Processes.Count,
            "event" when coverage is not null => coverage.TotalEventCount,
            "event_count" => CountedEventCount(evaluator, facts),
            "evidence_completion" => CountedEventCount(evaluator, facts),
            _ => null,
        };
    }

    private static bool SourceEvidenceObserved(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot facts) => evaluator.Kind switch
        {
            "event" => AnyEventFlagObserved(evaluator, facts),
            "event_requirements" => AnyEventFlagObserved(evaluator, facts),
            "event_count" => CountedEventCount(evaluator, facts) > 0,
            "evidence_completion" => CountedEventCount(evaluator, facts) > 0,
            _ => throw new InvalidOperationException(
                $"Evaluator '{evaluator.EvaluatorId}' is not event-backed."),
        };

    private static long CountedEventCount(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot facts)
    {
        var propertyName = evaluator.EventCountProperty
            ?? throw new InvalidOperationException(
                $"Validated count-backed evaluator '{evaluator.EvaluatorId}' has no source count property.");
        return ReadCapabilityCount(propertyName, facts);
    }

    private static CompletionEvidenceCounts? CompletionEvidence(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot? facts)
    {
        if (evaluator.Kind != "evidence_completion" || facts is null)
            return null;
        return new CompletionEvidenceCounts(
            CountedEventCount(evaluator, facts),
            ReadCapabilityCount(evaluator.CompletedCountProperty!, facts),
            ReadCapabilityCount(evaluator.UnmatchedCountProperty!, facts),
            ReadCapabilityCount(evaluator.BoundaryCountProperty!, facts));
    }

    private static long ReadCapabilityCount(
        string propertyName,
        TraceFactsSnapshot facts)
    {
        var property = typeof(TraceCapabilities).GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                $"Validated event count property '{propertyName}' is no longer present.");
        var count = (long)property.GetValue(facts.Capabilities)!;
        return count >= 0
            ? count
            : throw new InvalidDataException(
                $"Trace capability count '{propertyName}' cannot be negative.");
    }

    private static ToolCapabilityStatus CompletionStatus(
        CompletionEvidenceCounts? evidence)
    {
        if (evidence is null || evidence.Value.SourceCount == 0)
            return ToolCapabilityStatus.Unknown;
        return evidence.Value.CompletedCount > 0 &&
               evidence.Value.UnmatchedCount == 0 &&
               evidence.Value.BoundaryCount == 0
            ? ToolCapabilityStatus.Available
            : ToolCapabilityStatus.Partial;
    }

    private static ToolEvidenceCompletionState CompletionState(
        CompletionEvidenceCounts? evidence) =>
        evidence switch
        {
            null => ToolEvidenceCompletionState.NotApplicable,
            { SourceCount: 0 } => ToolEvidenceCompletionState.NoSourceEvidence,
            { CompletedCount: 0 } =>
                ToolEvidenceCompletionState.SourceWithoutCompletedEvidence,
            { UnmatchedCount: > 0 } or { BoundaryCount: > 0 } =>
                ToolEvidenceCompletionState.CompletedWithIncompleteEvidence,
            _ => ToolEvidenceCompletionState.Complete,
        };

    private static bool AnyEventFlagObserved(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot facts)
    {
        foreach (var propertyName in evaluator.EventFlags)
        {
            var property = typeof(TraceCapabilities).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"Validated event flag '{propertyName}' is no longer present.");
            if ((bool)property.GetValue(facts.Capabilities)!)
                return true;
        }
        return false;
    }

    private static bool AllEventFlagsObserved(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot facts) =>
        MissingEventFlags(evaluator, facts).Count == 0;

    private static IReadOnlyList<string> MissingEventFlags(
        CapabilityEvaluatorDefinition evaluator,
        TraceFactsSnapshot facts)
    {
        var missing = new List<string>();
        foreach (var propertyName in evaluator.EventFlags)
        {
            var property = typeof(TraceCapabilities).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException(
                    $"Validated event flag '{propertyName}' is no longer present.");
            if (!(bool)property.GetValue(facts.Capabilities)!)
                missing.Add(propertyName);
        }
        return missing;
    }

    private readonly record struct CompletionEvidenceCounts(
        long SourceCount,
        long CompletedCount,
        long UnmatchedCount,
        long BoundaryCount);

    private static ToolCaptureIntegrityStatus CaptureIntegrity(
        TraceFactsSnapshot? facts,
        string evaluatorKind)
    {
        if (evaluatorKind is "server" or "gap")
            return ToolCaptureIntegrityStatus.NotApplicable;
        if (facts is null)
            return ToolCaptureIntegrityStatus.Unknown;
        // No reported loss is not proof that the requested provider was enabled or
        // that TraceEvent materialized every raw ETW record.
        return facts.CaptureIntegrity.ReportedEventsLost > 0
            ? ToolCaptureIntegrityStatus.Partial
            : ToolCaptureIntegrityStatus.Unknown;
    }

    private static string CaptureIntegrityState(
        TraceFactsSnapshot? facts,
        string evaluatorKind) => evaluatorKind switch
    {
        "server" or "gap" => "not_applicable",
        _ when facts is null => "not_measured",
        _ when facts.CaptureIntegrity.ReportedEventsLost > 0 =>
            "reported_event_loss_parser_coverage_unknown",
        _ => "no_reported_event_loss_parser_coverage_unknown",
    };

    private static ToolCapabilityStatus CombineTraceStatus(
        ToolCapabilityStatus fromFacts,
        ToolCapabilityStatus fromResult)
    {
        if (fromResult == ToolCapabilityStatus.Available)
            return ToolCapabilityStatus.Available;
        if (fromResult == ToolCapabilityStatus.Partial &&
            fromFacts is ToolCapabilityStatus.Unknown or ToolCapabilityStatus.Unavailable)
            return ToolCapabilityStatus.Partial;
        return fromFacts;
    }

    private static ToolCapabilityStatus BoundScopedStatusByWholeTraceRequirements(
        ToolCapabilityStatus traceStatus,
        ToolCapabilityStatus scopedStatus) => traceStatus switch
    {
        ToolCapabilityStatus.Available => scopedStatus,
        ToolCapabilityStatus.Partial when scopedStatus == ToolCapabilityStatus.Available =>
            ToolCapabilityStatus.Partial,
        ToolCapabilityStatus.Unknown or ToolCapabilityStatus.Unavailable
            when scopedStatus is ToolCapabilityStatus.Available or ToolCapabilityStatus.Partial =>
            ToolCapabilityStatus.Unknown,
        _ => scopedStatus,
    };

    private static ToolCapabilityStatus BoundStatusForReportedLoss(
        ToolCapabilityStatus status) => status switch
    {
        ToolCapabilityStatus.Available => ToolCapabilityStatus.Partial,
        ToolCapabilityStatus.Unavailable => ToolCapabilityStatus.Unknown,
        _ => status,
    };

    private static MeasurementBasis SelectWeakestBasis(
        IReadOnlyList<string>? allowed)
    {
        allowed ??= ["unmeasured"];
        foreach (var candidate in new[]
                 {
                     MeasurementBasis.Unmeasured,
                     MeasurementBasis.Metadata,
                     MeasurementBasis.Heuristic,
                     MeasurementBasis.Derived,
                     MeasurementBasis.Direct,
                 })
        {
            if (allowed.Contains(Wire(candidate), StringComparer.Ordinal))
                return candidate;
        }
        throw new InvalidOperationException("The tool has no supported measurement basis.");
    }

    private static MeasurementBasis ParseBasis(string value) => value switch
    {
        "direct" => MeasurementBasis.Direct,
        "derived" => MeasurementBasis.Derived,
        "heuristic" => MeasurementBasis.Heuristic,
        "metadata" => MeasurementBasis.Metadata,
        "unmeasured" => MeasurementBasis.Unmeasured,
        _ => throw new InvalidOperationException($"Unknown measurement basis '{value}'."),
    };

    private static Relationship ParseRelationship(string value) => value switch
    {
        "descriptive" => Relationship.Descriptive,
        "temporal" => Relationship.Temporal,
        "association" => Relationship.Association,
        "attribution" => Relationship.Attribution,
        "causal" => Relationship.Causal,
        _ => throw new InvalidOperationException($"Unknown relationship '{value}'."),
    };

    private static ConclusionStatus ParseConclusion(string value) => value switch
    {
        "observed" => ConclusionStatus.Observed,
        "supported" => ConclusionStatus.Supported,
        "partial" => ConclusionStatus.Partial,
        "not_concluded" => ConclusionStatus.NotConcluded,
        "not_applicable" => ConclusionStatus.NotApplicable,
        _ => throw new InvalidOperationException($"Unknown conclusion status '{value}'."),
    };

    private static int RelationshipRank(Relationship value) => value switch
    {
        Relationship.Descriptive => 0,
        Relationship.Temporal => 1,
        Relationship.Association => 2,
        Relationship.Attribution => 3,
        Relationship.Causal => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static int RelationshipRank(string value) =>
        RelationshipRank(ParseRelationship(value));

    private static string Wire(MeasurementBasis value) => value switch
    {
        MeasurementBasis.Direct => "direct",
        MeasurementBasis.Derived => "derived",
        MeasurementBasis.Heuristic => "heuristic",
        MeasurementBasis.Metadata => "metadata",
        MeasurementBasis.Unmeasured => "unmeasured",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };
}
