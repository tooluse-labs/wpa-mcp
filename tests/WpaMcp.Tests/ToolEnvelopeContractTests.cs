using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ToolEnvelopeContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void StableRegistries_AreExactlyTheAdr0003Values()
    {
        Assert.Equal(new[]
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
        }, ToolErrorCodeRegistry.Codes);
        Assert.Equal(ToolErrorCodeRegistry.Codes.Count, ToolErrorCodeRegistry.Codes.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(new[]
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
        }, ToolNoDataReasonRegistry.Reasons);
        Assert.Equal(ToolNoDataReasonRegistry.Reasons.Count, ToolNoDataReasonRegistry.Reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void InspectGuidance_SeparatesNoDataReasonsFromScopeFailureErrors()
    {
        var noData = new NoDataReasonGuidance();
        var noDataValues = typeof(NoDataReasonGuidance)
            .GetProperties()
            .Select(property => Assert.IsType<string>(property.GetValue(noData)))
            .ToArray();
        Assert.Equal(ToolNoDataReasonRegistry.Reasons, noDataValues);

        var scopeFailures = new ScopeFailureErrorGuidance();
        var scopeFailureValues = typeof(ScopeFailureErrorGuidance)
            .GetProperties()
            .Select(property => Assert.IsType<string>(property.GetValue(scopeFailures)))
            .ToArray();
        Assert.Equal(new[]
        {
            "process_instance_not_found",
            "process_start_required",
            "ambiguous_process_instance",
            "thread_instance_not_found",
            "ambiguous_thread_instance",
        }, scopeFailureValues);
        Assert.All(scopeFailureValues, code => Assert.True(ToolErrorCodeRegistry.Contains(code)));
        Assert.DoesNotContain(scopeFailureValues, ToolNoDataReasonRegistry.Contains);
    }

    [Fact]
    public void UnregisteredErrorAndNoDataCodes_AreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ToolError("invented", "Public message.", false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolSectionFailure("rows", "startup_window_truncated", "Public message.", false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ToolNoData("not_concluded", "NO_CONCLUSION", Array.Empty<string>()));
    }

    [Fact]
    public void SucceededTopN_HasDataIsNotErrorAndDoesNotBecomePartial()
    {
        var section = Page(returned: 1, total: 2, hasMore: true);
        var envelope = Envelope(
            ToolCompletionStatus.Succeeded,
            data: new TestData(new[] { new TestRow(1) }, "9007199254740992"),
            error: null,
            failed: Array.Empty<ToolSectionFailure>(),
            sections: new[] { section },
            completeness: new ToolCompleteness(ToolCompletenessStatus.TopN, 1, 1, 0, true));

        Assert.Equal(ToolCompletionStatus.Succeeded, envelope.Status);
        Assert.False(envelope.IsError);
        Assert.True(envelope.HasMore);
        Assert.NotNull(envelope.Data);
    }

    [Fact]
    public void Partial_RequiresUsableDataAndFailedSectionAndIsNotError()
    {
        var failure = new ToolSectionFailure("symbols", "analysis_failed", "Symbol section failed.", false);
        var envelope = Envelope(
            ToolCompletionStatus.Partial,
            data: new TestData(new[] { new TestRow(1) }, "1"),
            error: null,
            failed: new[] { failure },
            sections: new[] { Page(1, 1, false) },
            completeness: new ToolCompleteness(ToolCompletenessStatus.Partial, 2, 1, 1, false));

        Assert.False(envelope.IsError);
        Assert.Single(envelope.FailedSections);

        Assert.Throws<ArgumentException>(() => Envelope(
            ToolCompletionStatus.Partial,
            data: new TestData(Array.Empty<TestRow>(), "1"),
            error: null,
            failed: Array.Empty<ToolSectionFailure>(),
            sections: new[] { Page(1, 1, false) },
            completeness: new ToolCompleteness(ToolCompletenessStatus.Partial, 1, 1, 0, false)));
    }

    [Fact]
    public void Failed_RequiresNullDataStableErrorAndMapsToIsError()
    {
        var envelope = Envelope(
            ToolCompletionStatus.Failed,
            data: null,
            error: new ToolError("trace_not_loaded", "Load a trace before analysis.", true),
            failed: Array.Empty<ToolSectionFailure>(),
            sections: Array.Empty<ToolSectionPage>(),
            completeness: new ToolCompleteness(ToolCompletenessStatus.Failed, 1, 0, 0, false));

        Assert.True(envelope.IsError);
        Assert.Null(envelope.Data);
        Assert.Equal("trace_not_loaded", envelope.Error!.Code);

        Assert.Throws<ArgumentException>(() => Envelope(
            ToolCompletionStatus.Failed,
            data: new TestData(Array.Empty<TestRow>(), "1"),
            error: new ToolError("analysis_failed", "Analysis failed.", false),
            failed: Array.Empty<ToolSectionFailure>(),
            sections: Array.Empty<ToolSectionPage>(),
            completeness: new ToolCompleteness(ToolCompletenessStatus.Failed, 1, 0, 0, false)));
    }

    [Fact]
    public void NoData_IsTopLevelOnlyWhenEveryReturnedSectionIsEmpty()
    {
        var noData = new ToolNoData("no_events_in_scope", "SCOPED_EVENT_COUNT_ZERO", new[] { "evidence.test" });
        var empty = new ToolSectionPage(
            "rows", ToolSectionMode.None, null, 0, 0, ToolSectionTotalState.Exact,
            false, null, ToolSortDirection.NotApplicable, Array.Empty<string>(), null, null, noData,
            ToolSectionRole.DomainData, new[] { "evidence.test" });
        var envelope = Envelope(
            ToolCompletionStatus.Succeeded,
            data: new TestData(Array.Empty<TestRow>(), "1"),
            error: null,
            failed: Array.Empty<ToolSectionFailure>(),
            sections: new[] { empty },
            completeness: new ToolCompleteness(ToolCompletenessStatus.NoData, 1, 0, 0, false),
            noData: noData);

        Assert.Equal("no_events_in_scope", envelope.NoData!.Reason);

        Assert.Throws<ArgumentException>(() => Envelope(
            ToolCompletionStatus.Succeeded,
            data: new TestData(Array.Empty<TestRow>(), "1"),
            error: null,
            failed: Array.Empty<ToolSectionFailure>(),
            sections: new[] { empty },
            completeness: new ToolCompleteness(ToolCompletenessStatus.Complete, 1, 0, 0, false),
            noData: null));
    }

    [Fact]
    public void EvidenceBoundary_PreservesMixedSectionSemanticsWithoutStrongTopLevelOverride()
    {
        var evidence = new[]
        {
            new ToolCapabilityEvidence(
                "cap.test", ToolCapabilityStatus.Available, ToolCapabilityStatus.Partial,
                10, 4, ToolCaptureIntegrityStatus.Complete,
                new[] { "evidence.direct", "evidence.associated" }),
        };
        var boundary = new ToolEvidenceBoundary(new[]
        {
            Boundary("evidence.direct", "rows", MeasurementBasis.Direct,
                Relationship.Descriptive, ConclusionStatus.Observed),
            Boundary("evidence.associated", "rows", MeasurementBasis.Direct,
                Relationship.Association, ConclusionStatus.NotConcluded),
        });
        var envelope = Envelope(
            ToolCompletionStatus.Succeeded,
            new TestData(new[] { new TestRow(4) }, "1"),
            null,
            Array.Empty<ToolSectionFailure>(),
            new[] { Page(1, 1, false, new[] { "evidence.direct", "evidence.associated" }) },
            new ToolCompleteness(ToolCompletenessStatus.Complete, 1, 1, 0, false),
            evidenceBoundary: boundary,
            capabilityEvidence: evidence);

        var json = JsonSerializer.SerializeToNode(envelope, WebJson)!.AsObject();
        var evidenceObject = json["evidenceBoundary"]!.AsObject();
        Assert.False(evidenceObject.ContainsKey("relationship"));
        var items = evidenceObject["items"]!.AsArray();
        Assert.Equal("descriptive", items[0]!["relationship"]!.GetValue<string>());
        Assert.Equal("association", items[1]!["relationship"]!.GetValue<string>());
        Assert.Equal("not_concluded", items[1]!["conclusionStatus"]!.GetValue<string>());
        Assert.False(items[0]!.AsObject().ContainsKey("section"));
        Assert.Equal(
            ["rows"],
            items[0]!["sections"]!.AsArray().Select(item => item!.GetValue<string>()));
    }

    [Fact]
    public void WireNamesEnumsAndRequiredNullableFields_AreStableCamelCase()
    {
        var failed = Envelope(
            ToolCompletionStatus.Failed,
            null,
            new ToolError("analysis_failed", "Analysis failed.", false),
            Array.Empty<ToolSectionFailure>(),
            Array.Empty<ToolSectionPage>(),
            new ToolCompleteness(ToolCompletenessStatus.Failed, 1, 0, 0, false),
            traceRef: null);

        var json = JsonSerializer.SerializeToNode(failed, WebJson)!.AsObject();
        Assert.Equal("2.0", json["contractVersion"]!.GetValue<string>());
        Assert.Equal("failed", json["status"]!.GetValue<string>());
        Assert.True(json.ContainsKey("data"));
        Assert.Null(json["data"]);
        Assert.True(json.ContainsKey("traceRef"));
        Assert.Null(json["traceRef"]);
        Assert.True(json.ContainsKey("noData"));
        Assert.Null(json["noData"]);
        Assert.True(json.ContainsKey("capabilityEvidence"));
        Assert.True(json.ContainsKey("evidenceBoundary"));
        Assert.False(json.ContainsKey("isError"));
    }

    [Fact]
    public void ExactPublicIdentifiers_PreserveTwoToThe53AndUlongMaxAsStrings()
    {
        const ulong twoToThe53 = 9_007_199_254_740_992;
        Assert.Equal("9007199254740992", PublicIdentifierFormatter.UnsignedDecimal(twoToThe53));
        Assert.Equal("18446744073709551615", PublicIdentifierFormatter.UnsignedDecimal(ulong.MaxValue));
        Assert.Equal("0xffffffffffffffff", PublicIdentifierFormatter.Pointer(ulong.MaxValue));
        Assert.Null(PublicIdentifierFormatter.DeprecatedSafeNumericProjection(twoToThe53));
        Assert.Equal(9_007_199_254_740_991L,
            PublicIdentifierFormatter.DeprecatedSafeNumericProjection(PublicIdentifierFormatter.JavaScriptMaxSafeInteger));

        var data = new TestData(Array.Empty<TestRow>(), PublicIdentifierFormatter.UnsignedDecimal(ulong.MaxValue));
        var json = JsonSerializer.SerializeToNode(data, WebJson)!.AsObject();
        Assert.Equal("18446744073709551615", json["connectionId"]!.GetValue<string>());
    }

    [Fact]
    public void Completeness_RejectsOverlappingDataAndFailureCounts()
    {
        Assert.Throws<ArgumentException>(() =>
            new ToolCompleteness(ToolCompletenessStatus.Partial, 2, 2, 1, false));
    }

    [Fact]
    public void Completeness_CountsOnlyDomainSections_NotSupportSections()
    {
        var domainA = Page(1, 1, false);
        var domainB = new ToolSectionPage(
            "evidence", ToolSectionMode.None, null, 1, 1, ToolSectionTotalState.Exact,
            false, null, ToolSortDirection.NotApplicable, Array.Empty<string>(), null, null, null,
            ToolSectionRole.DomainEvidence, new[] { "evidence.test" });
        var support = new[]
        {
            SupportPage("boundary", ToolSectionRole.Boundary),
            SupportPage("provenance", ToolSectionRole.Provenance),
            SupportPage("recommendation", ToolSectionRole.Recommendation),
        };
        var completeness = new ToolCompleteness(ToolCompletenessStatus.Complete, 2, 2, 0, false);

        var envelope = Envelope(
            ToolCompletionStatus.Succeeded,
            new TestData(new[] { new TestRow(1) }, "1"),
            null,
            Array.Empty<ToolSectionFailure>(),
            new[] { domainA, domainB }.Concat(support).ToArray(),
            completeness);

        Assert.Equal(5, envelope.Sections.Count);
        Assert.Equal(2, envelope.Completeness.RequestedSectionCount);
        Assert.Equal(2, envelope.Completeness.SectionsWithData);
        Assert.All(support, section =>
        {
            Assert.Equal(MeasurementBasis.Unmeasured, section.MeasurementBasis);
            Assert.Equal(Relationship.Descriptive, section.Relationship);
            Assert.Equal(ConclusionStatus.NotApplicable, section.ConclusionStatus);
            Assert.Empty(section.EvidenceIds);
        });
    }

    [Fact]
    public void BudgetOmittedScope_PreservesExactTotalsAndPublishesNoPartialSample()
    {
        var requested = new ToolScopeSelector(42, null, null, null, null, null, null);
        var omitted = new ToolScope(
            ToolScopeStatus.ProcessStartRequired,
            ToolScopeMode.ProcessInstance,
            requested,
            null,
            Array.Empty<ToolScopeIdentity>(),
            Array.Empty<ToolScopeIdentity>(),
            pidReuseObserved: true,
            identityUnresolved: false,
            candidateTotal: 9,
            includedTotal: 0,
            detailCompleteness: ToolScopeDetailCompleteness.OmittedDueToResponseBudget);

        Assert.Empty(omitted.Candidates);
        Assert.Empty(omitted.Included);
        Assert.Equal(9, omitted.CandidateTotal);
        Assert.Equal(0, omitted.IncludedTotal);
        Assert.Equal(ToolScopeDetailCompleteness.OmittedDueToResponseBudget, omitted.DetailCompleteness);
    }

    [Fact]
    public void FrameBudgetScopeOmission_IsAllOrNoneAndKeepsExactTotals()
    {
        var envelope = new JsonObject
        {
            ["scope"] = new JsonObject
            {
                ["candidates"] = new JsonArray(
                    new JsonObject { ["pid"] = 41 },
                    new JsonObject { ["pid"] = 42 }),
                ["included"] = new JsonArray(
                    new JsonObject { ["pid"] = 43 }),
                ["candidateTotal"] = 2,
                ["includedTotal"] = 1,
                ["detailCompleteness"] = "complete",
            },
            ["warnings"] = new JsonArray(),
        };

        Assert.True(ToolResponseFrameFitter.OmitScopeIdentityDetailsForBudget(envelope));

        var scope = envelope["scope"]!.AsObject();
        Assert.Empty(scope["candidates"]!.AsArray());
        Assert.Empty(scope["included"]!.AsArray());
        Assert.Equal(2, scope["candidateTotal"]!.GetValue<int>());
        Assert.Equal(1, scope["includedTotal"]!.GetValue<int>());
        Assert.Equal(
            "omitted_due_to_response_budget",
            scope["detailCompleteness"]!.GetValue<string>());
        Assert.Single(envelope["warnings"]!.AsArray());
        Assert.False(ToolResponseFrameFitter.OmitScopeIdentityDetailsForBudget(envelope));
    }

    [Fact]
    public void EvidenceIds_AreGloballyUniqueAcrossSections()
    {
        Assert.Throws<ArgumentException>(() => new ToolEvidenceBoundary(new[]
        {
            Boundary("evidence.same", "rows", MeasurementBasis.Direct,
                Relationship.Descriptive, ConclusionStatus.Observed),
            Boundary("evidence.same", "stacks", MeasurementBasis.Direct,
                Relationship.Association, ConclusionStatus.NotConcluded),
        }));
    }

    [Fact]
    public void ScopeStateMachine_RejectsContradictoryPublicState()
    {
        var requested = new ToolScopeSelector(42, null, null, null, null, null, null);
        var candidate = new ToolScopeIdentity(42, 100, null, null, null);

        Assert.Throws<ArgumentException>(() => new ToolScope(
            ToolScopeStatus.NotApplicable,
            ToolScopeMode.NotApplicable,
            requested,
            null,
            new[] { candidate },
            Array.Empty<ToolScopeIdentity>(),
            false,
            false));
        Assert.Throws<ArgumentException>(() => new ToolScope(
            ToolScopeStatus.ProcessStartRequired,
            ToolScopeMode.ProcessInstance,
            requested,
            null,
            Array.Empty<ToolScopeIdentity>(),
            Array.Empty<ToolScopeIdentity>(),
            false,
            true));
        Assert.Throws<ArgumentException>(() => new ToolScope(
            ToolScopeStatus.AmbiguousProcessInstance,
            ToolScopeMode.ProcessInstance,
            requested,
            null,
            new[] { candidate },
            new[] { candidate },
            true,
            true));
        Assert.Throws<ArgumentException>(() => new ToolScope(
            ToolScopeStatus.Ok,
            ToolScopeMode.ProcessInstance,
            requested,
            null,
            Array.Empty<ToolScopeIdentity>(),
            Array.Empty<ToolScopeIdentity>(),
            false,
            false));
    }

    [Fact]
    public void TraceReferences_AcceptOnlyCanonicalOpaqueLocatorsAndNoGenerationAlias()
    {
        const string traceId = "trc_0123456789abcdef0123456789abcdef";
        const string symbolId = "sym_fedcba9876543210fedcba9876543210";
        var traceReference = new ToolTraceReference(traceId, null, symbolId, ToolTraceRefKind.Canonical);
        Assert.Equal(traceId, traceReference.TraceId);
        Assert.Equal(symbolId, traceReference.SymbolContextId);

        Assert.Throws<ArgumentException>(() =>
            new ToolTraceReference(@"C:\\private\\capture.etl", null, null, ToolTraceRefKind.Ephemeral));
        Assert.Throws<ArgumentException>(() =>
            new ToolTraceReference("trc_0123456789ABCDEF0123456789ABCDEF", null, null, ToolTraceRefKind.Canonical));
        Assert.Throws<ArgumentException>(() =>
            new ToolTraceReference(traceId, "generation-1", null, ToolTraceRefKind.Canonical));
        Assert.Throws<ArgumentException>(() =>
            new ToolTraceReference(traceId, null, "sym_short", ToolTraceRefKind.Canonical));
    }

    private static ToolEnvelope<TestData> Envelope(
        ToolCompletionStatus status,
        TestData? data,
        ToolError? error,
        IReadOnlyList<ToolSectionFailure> failed,
        IReadOnlyList<ToolSectionPage> sections,
        ToolCompleteness completeness,
        ToolNoData? noData = null,
        ToolTraceReference? traceRef = null,
        ToolEvidenceBoundary? evidenceBoundary = null,
        IReadOnlyList<ToolCapabilityEvidence>? capabilityEvidence = null)
    {
        var defaultBoundarySections = sections
            .Where(section => section.Role is ToolSectionRole.DomainData or ToolSectionRole.DomainEvidence)
            .Where(section => section.EvidenceIds.Contains("evidence.test", StringComparer.Ordinal))
            .Select(section => section.Section)
            .ToArray();
        return new(
            ToolContractVersions.V2,
            status,
            data,
            error,
            failed,
            sections,
            Array.Empty<string>(),
            sections.Any(section => section.HasMore),
            new ToolReference("test_tool", new[] { "cap.test" }),
            traceRef,
            Scope(),
            capabilityEvidence ?? Evidence(),
            completeness,
            evidenceBoundary ?? new ToolEvidenceBoundary(new[]
            {
                BoundaryForSections(
                    "evidence.test",
                    defaultBoundarySections,
                    MeasurementBasis.Direct,
                    Relationship.Descriptive,
                    ConclusionStatus.Observed),
            }),
            noData,
            Precision());
    }

    private static ToolSectionPage Page(
        long returned,
        long total,
        bool hasMore,
        IReadOnlyList<string>? evidenceIds = null) =>
        new(
            "rows",
            ToolSectionMode.TopN,
            10,
            returned,
            total,
            ToolSectionTotalState.Exact,
            hasMore,
            "count",
            ToolSortDirection.Descending,
            new[] { "row_id_asc" },
            null,
            hasMore ? "requested_top" : null,
            null,
            ToolSectionRole.DomainData,
            evidenceIds ?? new[] { "evidence.test" });

    private static ToolSectionPage SupportPage(string section, ToolSectionRole role) =>
        new(
            section,
            ToolSectionMode.None,
            null,
            1,
            1,
            ToolSectionTotalState.Exact,
            false,
            null,
            ToolSortDirection.NotApplicable,
            Array.Empty<string>(),
            null,
            null,
            null,
            role,
            Array.Empty<string>());

    private static ToolScope Scope() =>
        new(
            ToolScopeStatus.Ok,
            ToolScopeMode.AllProcesses,
            new ToolScopeSelector(null, null, null, null, null, 0, 10),
            null,
            Array.Empty<ToolScopeIdentity>(),
            Array.Empty<ToolScopeIdentity>(),
            false,
            false);

    private static IReadOnlyList<ToolCapabilityEvidence> Evidence() => new[]
    {
        new ToolCapabilityEvidence(
            "cap.test",
            ToolCapabilityStatus.Available,
            ToolCapabilityStatus.Available,
            1,
            1,
            ToolCaptureIntegrityStatus.Complete,
            new[] { "evidence.test" }),
    };

    private static ToolEvidenceBoundaryItem Boundary(
        string evidenceId,
        string? section,
        MeasurementBasis basis,
        Relationship relationship,
        ConclusionStatus conclusion) =>
        new(
            evidenceId,
            section,
            basis,
            relationship,
            conclusion,
            relationship == Relationship.Association ? new[] { "ASSOCIATION_NOT_CAUSATION" } : Array.Empty<string>(),
            new ToolEvidenceProvenance(
                "trace_events",
                "TraceEvent",
                "test_evaluator",
                null,
                ToolCaptureIntegrityStatus.Complete));

    private static ToolEvidenceBoundaryItem BoundaryForSections(
        string evidenceId,
        IReadOnlyList<string> sections,
        MeasurementBasis basis,
        Relationship relationship,
        ConclusionStatus conclusion) =>
        new(
            evidenceId,
            sections,
            basis,
            relationship,
            conclusion,
            relationship == Relationship.Association ? new[] { "ASSOCIATION_NOT_CAUSATION" } : Array.Empty<string>(),
            new ToolEvidenceProvenance(
                "trace_events",
                "TraceEvent",
                "test_evaluator",
                null,
                ToolCaptureIntegrityStatus.Complete));

    private static ToolPrecision Precision() =>
        new(
            ToolIdentifierPrecision.Exact,
            ToolMetricPrecision.Exact,
            null,
            "checked_int64",
            new ToolMetricDenominator("1", "events", "requested_scope", ToolMetricPrecision.Exact));

    public sealed record TestData(
        [property: JsonPropertyName("rows")] IReadOnlyList<TestRow> Rows,
        [property: JsonPropertyName("connectionId")] string ConnectionId);

    public sealed record TestRow([property: JsonPropertyName("count")] long Count);
}
