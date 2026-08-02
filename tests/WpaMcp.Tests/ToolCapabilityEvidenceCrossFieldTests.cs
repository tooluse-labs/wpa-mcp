using System.Text.Json;
using System.Text.Json.Nodes;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolCapabilityEvidenceCrossFieldTests
{
    [Fact]
    public void ExplicitCounts_KeepTraceAndSelectedScopePopulationsSeparate()
    {
        var evidence = new ToolCapabilityEvidence(
            capabilityId: "cap.test",
            traceStatus: ToolCapabilityStatus.Partial,
            scopedStatus: ToolCapabilityStatus.Available,
            totalEventCount: 2,
            matchedEventCount: 7,
            captureIntegrity: ToolCaptureIntegrityStatus.Unknown,
            evidenceIds: ["evidence.test"],
            traceEligibleEventCountRepresentation:
                "materialized_lifecycle_endpoint_events");

        Assert.Equal(2, evidence.TraceEligibleEventCount);
        Assert.Equal(7, evidence.ScopedMatchedEventCount);
        Assert.Equal(evidence.TraceEligibleEventCount, evidence.TotalEventCount);
        Assert.Equal(evidence.ScopedMatchedEventCount, evidence.MatchedEventCount);
        Assert.Equal(
            "materialized_lifecycle_endpoint_events",
            evidence.TraceEligibleEventCountRepresentation);
        Assert.Equal("whole_trace", evidence.TraceEligibleEventCountScope);
        Assert.Equal(
            "selected_identity_and_requested_half_open_window",
            evidence.ScopedMatchedEventCountScope);
        Assert.Equal("not_defined", evidence.CrossScopeRatioDenominatorState);

        // Different scopes/representations deliberately have no matched <= total invariant.
        Assert.True(evidence.ScopedMatchedEventCount > evidence.TraceEligibleEventCount);
        Assert.Throws<ArgumentException>(() => new ToolCapabilityEvidence(
            "cap.test",
            ToolCapabilityStatus.Unknown,
            ToolCapabilityStatus.Unknown,
            null,
            null,
            ToolCaptureIntegrityStatus.Unknown,
            ["evidence.test"],
            traceEligibleEventCountRepresentation: ""));
    }

    [Fact]
    public void RuntimeProjection_PreservesEvaluatorCountRepresentation()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(candidate =>
            candidate.ToolName == "list_capabilities");
        var capability = Assert.Single(tool.Capabilities);
        var assessment = catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            capability,
            domain: null,
            outcome: null,
            readyFacts: null,
            failed: true);

        var envelope = ToolEnvelopeProjection.Failure(
            tool,
            new ToolError("analysis_failed", "synthetic test failure", false),
            arguments: null,
            [assessment]);
        var projectedEvidence = Assert.IsAssignableFrom<IReadOnlyList<ToolCapabilityEvidence>>(
            envelope.GetType().GetProperty("CapabilityEvidence")!.GetValue(envelope));
        var item = Assert.Single(projectedEvidence);

        Assert.Equal(assessment.CountRepresentation, item.TraceEligibleEventCountRepresentation);
        Assert.Equal(assessment.TraceEligibleEventCount, item.TraceEligibleEventCount);
        Assert.Equal(assessment.ScopedMatchedEventCount, item.ScopedMatchedEventCount);
    }

    [Fact]
    public void EnvelopeSchema_DeprecatesGenericCountAliasesAndDeclaresPopulationAggregations()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<ListCapabilitiesResponse>();
        Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        var properties = OutputSchemaTestResolver.Properties(
            OutputSchemaTestResolver.Items(
                schema["properties"]!["capabilityEvidence"]!));

        var trace = NumericSemantics(schema, properties["traceEligibleEventCount"]!);
        var scoped = NumericSemantics(schema, properties["scopedMatchedEventCount"]!);
        var totalAlias = NumericSemantics(schema, properties["totalEventCount"]!);
        var matchedAlias = NumericSemantics(schema, properties["matchedEventCount"]!);

        Assert.Equal(
            "whole_trace_evaluator_eligible_count",
            trace["aggregation"]!.GetValue<string>());
        Assert.Equal(
            "selected_scope_matched_count",
            scoped["aggregation"]!.GetValue<string>());
        Assert.Null(trace["denominator"]);
        Assert.Null(scoped["denominator"]);
        Assert.True(totalAlias["deprecatedAlias"]!.GetValue<bool>());
        Assert.Equal(
            "traceEligibleEventCount",
            totalAlias["replacement"]!.GetValue<string>());
        Assert.True(matchedAlias["deprecatedAlias"]!.GetValue<bool>());
        Assert.Equal(
            "scopedMatchedEventCount",
            matchedAlias["replacement"]!.GetValue<string>());
        var totalAliasSchema = NonNullSchema(properties["totalEventCount"]!);
        var matchedAliasSchema = NonNullSchema(properties["matchedEventCount"]!);
        Assert.True(totalAliasSchema["deprecated"]!.GetValue<bool>());
        Assert.Equal(
            "traceEligibleEventCount",
            totalAliasSchema["x-replacedBy"]!.GetValue<string>());
        Assert.True(matchedAliasSchema["deprecated"]!.GetValue<bool>());
        Assert.Equal(
            "scopedMatchedEventCount",
            matchedAliasSchema["x-replacedBy"]!.GetValue<string>());
        Assert.Contains(
            "not comparable",
            properties["scopedMatchedEventCount"]!["description"]!.GetValue<string>(),
            StringComparison.OrdinalIgnoreCase);

        totalAliasSchema["x-replacedBy"] = "missingCount";
        var violations = ToolOutputSchemaLinter.LintSchema(schema);
        Assert.Contains(violations, item =>
            item.Code == "numeric_alias_schema_marker_mismatch");
        Assert.Contains(violations, item =>
            item.Code == "missing_deprecated_alias_replacement");
    }

    [Fact]
    public void WorkflowSchema_DeclaresMembershipAndStatusBucketCountSemantics()
    {
        var schema = ToolOutputSchemaFactory.CreateEnvelopeSchema<InspectTraceResponse>();
        Assert.Empty(ToolOutputSchemaLinter.LintSchema(schema));
        var workflows = schema["properties"]!["data"]!;
        var data = NonNullSchema(workflows);
        var workflowProperties = OutputSchemaTestResolver.Properties(data)["traceEvidenceMap"]!;
        var map = NonNullSchema(workflowProperties);
        var item = OutputSchemaTestResolver.Items(
            OutputSchemaTestResolver.Properties(map)["workflows"]!);
        var properties = OutputSchemaTestResolver.Properties(item);

        Assert.Equal(
            "workflow_membership_count",
            NumericSemantics(schema, properties["totalCapabilityCount"]!)["aggregation"]!
                .GetValue<string>());
        Assert.Equal(
            "workflow_trace_status_bucket_count",
            NumericSemantics(schema, properties["notApplicableCapabilityCount"]!)["aggregation"]!
                .GetValue<string>());
    }

    private static JsonObject NumericSemantics(JsonObject root, JsonNode property)
    {
        var schema = property.AsObject();
        if (schema["anyOf"] is JsonArray alternatives)
        {
            schema = alternatives
                .Select(item => item!.AsObject())
                .Single(item => item.ContainsKey("x-metric"));
        }

        var id = schema["x-metric"]!.GetValue<string>();
        return root["x-wpa-numeric-semantics"]![id]!.AsObject();
    }

    private static JsonObject NonNullSchema(JsonNode property)
        => OutputSchemaTestResolver.NonNull(property);
}
