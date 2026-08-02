using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class CompositeResultContractValidatorTests
{
    [Fact]
    public void EmbeddedTopNBoundary_RejectsUnwitnessedOrContradictoryStates()
    {
        Assert.Throws<ArgumentException>(() => new EmbeddedTopNBoundary(
            "/rows", 1, 1, 2, ToolSectionTotalState.Exact,
            ToolSectionMoreState.Absent, false, false, null,
            "metric_desc", ToolSortDirection.Descending, []));
        Assert.Throws<ArgumentException>(() => new EmbeddedTopNBoundary(
            "/rows", 1, 1, 2, ToolSectionTotalState.LowerBound,
            ToolSectionMoreState.Unknown, false, false, "source_limit_saturated",
            "metric_desc", ToolSortDirection.Descending, []));
        Assert.Throws<ArgumentException>(() => new EmbeddedTopNBoundary(
            "/rows", 1, 1, 1, ToolSectionTotalState.Unknown,
            ToolSectionMoreState.Unknown, false, false, "source_limit_saturated",
            "metric_desc", ToolSortDirection.Descending, []));

        var witnessed = new EmbeddedTopNBoundary(
            "/rows", 1, 1, 2, ToolSectionTotalState.LowerBound,
            ToolSectionMoreState.Present, true, false, "source_top_plus_one_witness",
            "metric_desc", ToolSortDirection.Descending, []);
        Assert.Equal(ToolSectionTotalState.LowerBound, witnessed.TotalState);
        Assert.True(witnessed.HasMore);
        Assert.False(witnessed.ContinuationAvailable);
    }

    [Fact]
    public void HighWait_RejectsDanglingEvidenceCallId()
    {
        var response = EmptyHighWait() with
        {
            Evidence =
            [
                new CompositeEvidence(
                    "evidence-1",
                    "missing-call",
                    "wait_reason",
                    null,
                    null,
                    null,
                    "reason",
                    "blockedUs",
                    1,
                    "us",
                    Array.Empty<WaitReasonBucket>(),
                    Array.Empty<FrameMetric>()),
            ],
        };

        Assert.Throws<InvalidOperationException>(() =>
            CompositeResultContractValidator.Validate(response));
    }

    [Fact]
    public void HighWait_RejectsCandidateCallOutsideExecutedRegistry()
    {
        var response = EmptyHighWait() with
        {
            Candidates =
            [
                new HighWaitCandidate(
                    42,
                    "process",
                    1,
                    2,
                    2,
                    1,
                    Array.Empty<WaitReasonBucket>(),
                    "missing-wait-call",
                    null,
                    null,
                    10),
            ],
        };

        Assert.Throws<InvalidOperationException>(() =>
            CompositeResultContractValidator.Validate(response));
    }

    [Fact]
    public void NotConcludedBoundaryId_IsNotMisinterpretedAsEvidenceReference()
    {
        var response = EmptyHighWait() with
        {
            NotConcluded =
            [
                new CompositeNotConcluded(
                    "no_candidate",
                    "No candidate met the reviewed threshold.",
                    null,
                    null,
                    null,
                    BoundaryId: "boundary.no-candidate"),
            ],
        };

        CompositeResultContractValidator.Validate(response);
    }

    [Fact]
    public void ValidHighWait_ClosesCandidateEvidenceAndBoundaryCalls()
    {
        var call = new CompositeToolCall(
            "wait-call",
            "wait_analysis",
            42,
            null,
            0,
            10,
            10,
            null,
            null,
            null,
            Array.Empty<string>());
        var response = EmptyHighWait() with
        {
            ExecutedToolCalls = [call],
            Candidates =
            [
                new HighWaitCandidate(
                    42,
                    "process",
                    1,
                    2,
                    2,
                    1,
                    Array.Empty<WaitReasonBucket>(),
                    call.CallId,
                    null,
                    null,
                    10),
            ],
            CandidateBoundary = new EmbeddedTopNBoundary(
                "/candidates",
                5,
                1,
                1,
                ToolSectionTotalState.Exact,
                ToolSectionMoreState.Absent,
                false,
                false,
                null,
                "total_blocked_us_desc",
                ToolSortDirection.Descending,
                ["wait_ratio_desc_nulls_last", "pid_asc", "process_start_us_asc"]),
            Evidence =
            [
                new CompositeEvidence(
                    "evidence-1",
                    call.CallId,
                    "wait_reason",
                    42,
                    null,
                    "process",
                    "reason",
                    "blockedUs",
                    2,
                    "us",
                    Array.Empty<WaitReasonBucket>(),
                    Array.Empty<FrameMetric>(),
                    10,
                    ExactEmpty("/frames"),
                    ExactEmpty("/topWaitReasons")),
            ],
            NotConcluded =
            [
                new CompositeNotConcluded(
                    "branch_not_run",
                    "The optional branch was not run.",
                    42,
                    null,
                    call.CallId,
                    ProcessStartUs: 10,
                    BoundaryId: "boundary.branch-not-run"),
            ],
        };

        CompositeResultContractValidator.Validate(response);
    }

    private static EmbeddedTopNBoundary ExactEmpty(string pointer) => new(
        pointer,
        0,
        0,
        0,
        ToolSectionTotalState.Exact,
        ToolSectionMoreState.Absent,
        false,
        false,
        null,
        "construction_sequence_asc",
        ToolSortDirection.Ascending,
        Array.Empty<string>());

    [Fact]
    public void HighWait_PartialStateExactlyMatchesTimeBudgetBoundary()
    {
        var budgetBoundary = new CompositeNotConcluded(
            "time_budget_exhausted",
            "The post-wait fan-out budget omitted requested stack work.",
            null,
            null,
            null);
        var valid = EmptyHighWait() with
        {
            NotConcluded = [budgetBoundary],
            Partial = true,
            PartialCode = "time_budget_exhausted",
        };

        CompositeResultContractValidator.Validate(valid);
        Assert.Throws<InvalidOperationException>(() =>
            CompositeResultContractValidator.Validate(valid with
            {
                NotConcluded = Array.Empty<CompositeNotConcluded>(),
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CompositeResultContractValidator.Validate(valid with
            {
                Partial = false,
                PartialCode = null,
            }));
        Assert.Throws<InvalidOperationException>(() =>
            CompositeResultContractValidator.Validate(EmptyHighWait() with
            {
                PartialCode = "time_budget_exhausted",
            }));
    }

    [Fact]
    public void OversizedComposite_FailsAtomicallyWithoutPublishingPartialReferences()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(candidate =>
            candidate.ToolName == "diagnose_high_wait");
        var response = EmptyHighWait() with
        {
            Warnings = [new string('x', 20_000)],
        };
        var plan = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>());
        var domain = JsonSerializer.SerializeToNode(
            response,
            McpJsonUtilities.DefaultOptions)!;
        var reviewed = plan.Adapt(domain);
        var assessments = tool.Capabilities.Select(capability =>
            catalog.EvaluatorRegistry.EvaluateTool(
                tool,
                capability,
                reviewed.Domain as JsonObject,
                reviewed.Outcome,
                readyFacts: null,
                failed: false)).ToArray();
        var envelope = ToolEnvelopeProjection.Success(
            tool,
            response,
            reviewed,
            plan.PublicArguments,
            assessments);
        var projected = ToolWireJson.ProjectEnvelope(envelope, tool.OutputDataType);
        var scope = projected["scope"]!.AsObject();
        scope["candidates"] = new JsonArray(Enumerable.Range(0, 9)
            .Select(index => (JsonNode)new JsonObject
            {
                ["pid"] = 4_000 + index,
                ["processStartUs"] = (100L + index).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["tid"] = null,
                ["threadStartUs"] = null,
                ["threadGeneration"] = null,
            })
            .ToArray());
        scope["included"] = scope["candidates"]!.DeepClone();
        scope["candidateTotal"] = 9;
        scope["includedTotal"] = 9;
        scope["detailCompleteness"] = "complete";
        var fitter = new ToolResponseFrameFitter(
            new ToolResponseBudgetOptions(ToolResponseBudgetOptions.MinimumResponseFrameBytes),
            new ToolPrivacyRedactor(ToolPrivacyMode.Off));

        var fitted = fitter.Fit(
            new RequestId("composite-budget"),
            projected,
            ToolOutputSchemaFactory.CreateEnvelopeSchema<DiagnoseHighWaitResponse>(),
            tool,
            plan.PublicArguments);

        Assert.True(fitted.Result.IsError);
        Assert.False(fitted.RowsTruncated);
        Assert.True(fitted.FrameBytes <= ToolResponseBudgetOptions.MinimumResponseFrameBytes);
        var failure = JsonNode.Parse(
            fitted.Result.StructuredContent!.Value.GetRawText())!.AsObject();
        Assert.Equal("response_too_large", failure["error"]!["code"]!.GetValue<string>());
        Assert.Null(failure["data"]);
        Assert.Empty(failure["sections"]!.AsArray());
        Assert.Null(failure["scope"]);
        Assert.Equal("unknown", failure["capabilityEvidence"]![0]!["scopedStatus"]!.GetValue<string>());
        Assert.Empty(failure["evidenceBoundary"]!["items"]!.AsArray());
    }

    private static DiagnoseHighWaitResponse EmptyHighWait() => new(
        Array.Empty<HighWaitCandidate>(),
        new EmbeddedTopNBoundary(
            "/candidates",
            5,
            0,
            0,
            ToolSectionTotalState.Exact,
            ToolSectionMoreState.Absent,
            false,
            false,
            null,
            "total_blocked_us_desc",
            ToolSortDirection.Descending,
            ["wait_ratio_desc_nulls_last", "pid_asc", "process_start_us_asc"]),
        Array.Empty<CompositeEvidence>(),
        Array.Empty<CompositeNotConcluded>(),
        Array.Empty<CompositeNextTool>(),
        Array.Empty<CompositeToolCall>(),
        Array.Empty<string>());
}
