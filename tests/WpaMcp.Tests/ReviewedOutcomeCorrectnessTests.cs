using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;

namespace WpaMcp.Tests;

public sealed class ReviewedOutcomeCorrectnessTests
{
    [Fact]
    public void CompositePlans_DoNotOverfetchAtomicCandidateGraphs()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var registry = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools);

        var highWait = registry.Plan(
            catalog.Tools.Single(tool => tool.ToolName == "diagnose_high_wait"),
            new Dictionary<string, JsonElement>
            {
                ["maxCandidates"] = JsonSerializer.SerializeToElement(2),
            });
        var slow = registry.Plan(
            catalog.Tools.Single(tool => tool.ToolName == "diagnose_slow_startup"),
            new Dictionary<string, JsonElement>
            {
                ["maxCandidates"] = JsonSerializer.SerializeToElement(2),
                ["topWindowEvidence"] = JsonSerializer.SerializeToElement(3),
            });
        var window = registry.Plan(
            catalog.Tools.Single(tool => tool.ToolName == "diagnose_window"),
            new Dictionary<string, JsonElement>
            {
                ["top"] = JsonSerializer.SerializeToElement(4),
            });

        Assert.Equal(2, highWait.InnerArguments["maxCandidates"].GetInt32());
        Assert.Equal(2, slow.InnerArguments["maxCandidates"].GetInt32());
        Assert.Equal(3, slow.InnerArguments["topWindowEvidence"].GetInt32());
        Assert.Equal(4, window.InnerArguments["top"].GetInt32());
    }

    [Fact]
    public void InspectFixedCollections_AdvertiseExactTotalsAndDeterministicOrdering()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var rules = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .SectionRulesFor("inspect_trace")
            .ToDictionary(rule => rule.Pointer, StringComparer.Ordinal);

        void AssertFixedExact(string pointer, string sortKey, params string[] ties)
        {
            var rule = rules[pointer];
            Assert.Equal(ReviewedSectionProofMode.FixedLimitExactTotal, rule.ProofMode);
            Assert.Equal(sortKey, rule.SortKey);
            Assert.Equal(ties, rule.TieBreakers);
        }

        AssertFixedExact(
            "/metadata/providerEvents/topProviders",
            "event_count_desc",
            "provider_name_ordinal_ignore_case_asc");
        AssertFixedExact(
            "/metadata/drivers/topDrivers",
            "module_name_ordinal_ignore_case_asc");
        AssertFixedExact(
            "/symbolQuality/topModulesMissingPdbName",
            "module_name_ordinal_ignore_case_asc");
        AssertFixedExact(
            "/symbolQuality/frameResolution/topUnresolvedModules",
            "unresolved_frame_count_desc",
            "module_name_ordinal_asc");
        Assert.Equal(
            ReviewedSectionProofMode.Exhaustive,
            rules["/symbolQuality/topUnresolvedModules"].ProofMode);
    }

    [Fact]
    public void CompositeCandidates_UseTypedExactTotalsAndRejectOrderingDrift()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "diagnose_high_wait");
        var plan = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools).Plan(
            tool,
            new Dictionary<string, JsonElement>
            {
                ["maxCandidates"] = JsonSerializer.SerializeToElement(2),
            });
        JsonObject Domain(JsonObject boundary) => new()
        {
            ["candidates"] = new JsonArray(new JsonObject(), new JsonObject()),
            ["candidateBoundary"] = boundary,
            ["evidence"] = new JsonArray(),
            ["notConcluded"] = new JsonArray(),
            ["nextTools"] = new JsonArray(),
            ["executedToolCalls"] = new JsonArray(),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["pidReuseObserved"] = false,
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 3,
            ["noDataReason"] = null,
        };

        var exact = plan.Adapt(Domain(CandidateBoundary(
            requested: 2,
            returned: 2,
            total: 3,
            sortKey: "total_blocked_us_desc")));
        var section = exact.Sections.Single(item => item.Pointer == "/candidates");
        Assert.Equal(2, section.Returned);
        Assert.Equal(3, section.TotalAvailable);
        Assert.Equal("exact", section.TotalState);
        Assert.True(section.HasMore);
        Assert.Null(section.NextCursor);

        var drifted = CandidateBoundary(2, 2, 3, "total_blocked_us_desc");
        drifted["tieBreakers"] = new JsonArray("pid_desc");
        Assert.Throws<InvalidOperationException>(() => plan.Adapt(Domain(drifted)));
    }

    [Fact]
    public void HistogramBuckets_AreExactRequestedAndNeverOverfetched()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "alpc_top_stacks");
        var plan = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools).Plan(
            tool,
            new Dictionary<string, JsonElement>
            {
                ["top"] = JsonSerializer.SerializeToElement(1),
                ["whenBuckets"] = JsonSerializer.SerializeToElement(3),
            });
        Assert.Equal(3, plan.InnerArguments["whenBuckets"].GetInt32());

        var result = plan.Adapt(new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject()),
            ["stats"] = new JsonObject
            {
                ["topUnresolvedModules"] = new JsonArray(),
                ["unresolvedModuleCount"] = 0,
            },
            ["when"] = new JsonObject
            {
                ["buckets"] = new JsonArray(1, 2, 3),
            },
            ["stackCoverage"] = StackCoverage(1, 1),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 1,
        });
        var section = result.Sections.Single(item => item.Pointer == "/when/buckets");
        Assert.Equal(3, section.Requested);
        Assert.Equal(3, section.TotalAvailable);
        Assert.Equal("exact", section.TotalState);
        Assert.False(section.HasMore);
    }

    [Fact]
    public void SymbolStatsFixedLimit_UsesExactPreCapModuleTotal()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "cpu_top_functions");
        var unresolved = new JsonArray(Enumerable.Range(0, 10)
            .Select(index => (JsonNode)new JsonObject { ["module"] = $"m{index}" }).ToArray());
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>())
            .Adapt(new JsonObject
            {
                ["rows"] = new JsonArray(new JsonObject()),
                ["stats"] = new JsonObject
                {
                    ["topUnresolvedModules"] = unresolved,
                    ["unresolvedModuleCount"] = 12,
                },
                ["stackCoverage"] = StackCoverage(1, 1),
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 1,
            });
        var section = result.Sections.Single(item => item.Pointer == "/stats/topUnresolvedModules");
        Assert.Equal(12, section.TotalAvailable);
        Assert.Equal("exact", section.TotalState);
        Assert.True(section.HasMore);
        Assert.Equal("fixed_source_limit", section.TruncationReason);
    }

    [Fact]
    public void SecurityTopPlusOne_SynchronizesCallerVisibleCompatibilityFlags()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "security_scan_analysis");
        var arguments = new Dictionary<string, JsonElement>
        {
            ["top"] = JsonSerializer.SerializeToElement(3),
        };
        static JsonArray FourRows() => new(
            new JsonObject(), new JsonObject(), new JsonObject(), new JsonObject());
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, arguments)
            .Adapt(new JsonObject
            {
                ["rows"] = FourRows(),
                ["slowScans"] = FourRows(),
                ["providers"] = FourRows(),
                ["rowsHasMore"] = false,
                ["slowScansHasMore"] = false,
                ["providersHasMore"] = false,
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 12L,
                ["noDataReason"] = null,
            });

        var projected = result.Domain.AsObject();
        Assert.Equal(3, projected["rows"]!.AsArray().Count);
        Assert.Equal(3, projected["slowScans"]!.AsArray().Count);
        Assert.Equal(3, projected["providers"]!.AsArray().Count);
        Assert.True(projected["rowsHasMore"]!.GetValue<bool>());
        Assert.True(projected["slowScansHasMore"]!.GetValue<bool>());
        Assert.True(projected["providersHasMore"]!.GetValue<bool>());
    }

    [Fact]
    public void MemoryPressureLists_ParticipateInTopPlusOneProofAndTrim()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "memory_resource_analysis");
        static JsonArray Three() => new(new JsonObject(), new JsonObject(), new JsonObject());
        var plan = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools).Plan(
            tool,
            new Dictionary<string, JsonElement>
            {
                ["top"] = JsonSerializer.SerializeToElement(2),
            });
        Assert.Equal(3, plan.InnerArguments["top"].GetInt32());
        var result = plan.Adapt(new JsonObject
        {
            ["processes"] = Three(),
            ["poolTags"] = Three(),
            ["poolProcesses"] = Three(),
            ["handles"] = Three(),
            ["pressure"] = new JsonObject
            {
                ["topPeakWorkingSetProcesses"] = Three(),
                ["topPeakCommitProcesses"] = Three(),
            },
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 6,
        });

        foreach (var pointer in new[]
        {
            "/pressure/topPeakWorkingSetProcesses",
            "/pressure/topPeakCommitProcesses",
        })
        {
            var section = result.Sections.Single(item => item.Pointer == pointer);
            Assert.Equal(2, section.Returned);
            Assert.True(section.HasMore);
            Assert.Equal("lower_bound", section.TotalState);
        }
    }

    [Fact]
    public void RootHasMore_IsDerivedFromCallerVisibleTopPlusOneSections()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var registry = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools);
        var arguments = new Dictionary<string, JsonElement>
        {
            ["top"] = JsonSerializer.SerializeToElement(3),
        };
        static JsonArray FourRows() => new(
            new JsonObject(), new JsonObject(), new JsonObject(), new JsonObject());

        var jit = registry.Plan(
                catalog.Tools.Single(item => item.ToolName == "clr_jit_analysis"),
                arguments)
            .Adapt(new JsonObject
            {
                ["topMethods"] = FourRows(),
                ["hasMore"] = false,
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 8L,
                ["noDataReason"] = null,
            });
        var contention = registry.Plan(
                catalog.Tools.Single(item => item.ToolName == "clr_contention_top_stacks"),
                arguments)
            .Adapt(new JsonObject
            {
                ["rows"] = FourRows(),
                ["hasMore"] = false,
                ["stackCoverage"] = StackCoverage(total: 8, stacked: 8),
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 8L,
                ["noDataReason"] = null,
            });

        Assert.True(jit.Domain["hasMore"]!.GetValue<bool>());
        Assert.True(contention.Domain["hasMore"]!.GetValue<bool>());
        Assert.True(Assert.Single(jit.Sections).HasMore);
        Assert.True(Assert.Single(contention.Sections).HasMore);
    }

    [Fact]
    public void SlowStartupExclusionSample_UsesExactTotalAndFixedBoundarySection()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "diagnose_slow_startup");
        var samples = new JsonArray(Enumerable.Range(
                0,
                StartupDiscoverySummary.ExcludedSampleLimit)
            .Select(index => (JsonNode)new JsonObject
            {
                ["processStartUs"] = index,
                ["pid"] = index + 1,
            })
            .ToArray());
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>
            {
                ["maxCandidates"] = JsonSerializer.SerializeToElement(1),
                ["topWindowEvidence"] = JsonSerializer.SerializeToElement(1),
            })
            .Adapt(new JsonObject
            {
                ["candidates"] = new JsonArray(),
                ["candidateBoundary"] = CandidateBoundary(
                    requested: 1,
                    returned: 0,
                    total: 0,
                    sortKey: "startup_wait_ratio_desc"),
                ["evidence"] = new JsonArray(),
                ["notConcluded"] = new JsonArray(),
                ["nextTools"] = new JsonArray(),
                ["executedToolCalls"] = new JsonArray(),
                ["firstImageLoadGapEvidence"] = new JsonArray(),
                ["discovery"] = new JsonObject
                {
                    ["excludedStartupInstanceCount"] = 25,
                    ["excludedSamples"] = samples,
                    ["excludedSamplesHasMore"] = true,
                },
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "unknown",
                ["matchedEventCount"] = 0L,
                ["noDataReason"] = "no_candidates_in_considered_input",
            });

        var section = Assert.Single(
            result.Sections,
            item => item.Pointer == "/discovery/excludedSamples");
        Assert.Equal(StartupDiscoverySummary.ExcludedSampleLimit, section.Returned);
        Assert.Equal(25, section.TotalAvailable);
        Assert.Equal("exact", section.TotalState);
        Assert.True(section.HasMore);
        Assert.Equal("fixed_sample_limit", section.TruncationReason);
        Assert.Equal(ToolSectionRole.Boundary, section.Role);
    }

    [Fact]
    public void FixedLimitSaturation_DoesNotInventContinuation()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "clr_finalizer_analysis");
        var plan = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools).Plan(
            tool,
            new Dictionary<string, JsonElement>());
        var rows = new JsonArray(
            Enumerable.Range(0, 20)
                .Select(index => (JsonNode?)new JsonObject { ["typeName"] = $"type-{index}" })
                .ToArray());
        var result = plan.Adapt(new JsonObject
        {
            ["batches"] = new JsonArray(),
            ["topTypes"] = rows,
        });

        var proof = Assert.Single(result.Sections, section => section.Pointer == "/topTypes");
        Assert.Equal(20, proof.Requested);
        Assert.Equal(20, proof.Returned);
        Assert.Null(proof.TotalAvailable);
        Assert.Equal("unknown", proof.TotalState);
        Assert.False(proof.HasMore);
        Assert.Equal("source_limit_saturated", proof.TruncationReason);
    }

    [Fact]
    public void NetConnections_SectionOrderingMatchesAnalyzerContract()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "net_connections");
        var arguments = new Dictionary<string, JsonElement>
        {
            ["top"] = JsonSerializer.SerializeToElement(3),
        };
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, arguments)
            .Adapt(new JsonObject
            {
                ["connections"] = new JsonArray(
                    new JsonObject { ["durationUs"] = 70L, ["openTimeUs"] = 30L },
                    new JsonObject { ["durationUs"] = 20L, ["openTimeUs"] = 5L },
                    new JsonObject { ["durationUs"] = null, ["openTimeUs"] = 110L }),
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 5L,
                ["noDataReason"] = null,
            });

        var section = Assert.Single(result.Sections);
        Assert.Equal("observed_duration_us_desc_nulls_last", section.SortKey);
        Assert.Equal(ToolSortDirection.Descending, section.SortDirection);
        Assert.Equal(
            ["open_time_us_asc", "pid_asc", "process_start_us_asc", "conn_id_text_asc"],
            section.TieBreakers);
    }

    [Fact]
    public void SupportSections_DoNotTurnEmptyCompositeIntoUsableData()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "diagnose_high_wait");
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>())
            .Adapt(new JsonObject
            {
                ["candidates"] = new JsonArray(),
                ["candidateBoundary"] = CandidateBoundary(
                    requested: 5,
                    returned: 0,
                    total: 0,
                    sortKey: "total_blocked_us_desc"),
                ["evidence"] = new JsonArray(),
                ["notConcluded"] = new JsonArray(),
                ["nextTools"] = new JsonArray(new JsonObject { ["toolName"] = "wait_analysis" }),
                ["executedToolCalls"] = new JsonArray(),
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "unknown",
                ["matchedEventCount"] = 0,
                ["noDataReason"] = "no_candidates_in_considered_input",
            });

        Assert.False(result.Outcome.HasUsableData);
        Assert.Equal(
            ToolSectionRole.Recommendation,
            result.Sections.Single(section => section.Pointer == "/nextTools").Role);
        Assert.Equal(
            ToolSectionRole.Provenance,
            result.Sections.Single(section => section.Pointer == "/executedToolCalls").Role);
        Assert.Equal(
            ToolSectionRole.Boundary,
            result.Sections.Single(section => section.Pointer == "/notConcluded").Role);
        Assert.All(
            result.Sections.Where(section => section.Role is
                ToolSectionRole.Boundary or ToolSectionRole.Provenance or ToolSectionRole.Recommendation),
            section => Assert.Empty(section.EvidenceIds));
        Assert.Equal(
            "total_blocked_us_desc",
            result.Sections.Single(section => section.Pointer == "/candidates").SortKey);
        Assert.Equal(
            "construction_sequence_asc",
            result.Sections.Single(section => section.Pointer == "/notConcluded").SortKey);
    }

    [Fact]
    public void StackRowsAndCallerCallee_ExposeDifferentActualRankingMetrics()
    {
        var top = AdaptReviewed("file_io_top_stacks", new JsonObject
        {
            ["rows"] = new JsonArray(new JsonObject { ["function"] = "module!Leaf" }),
            ["stackCoverage"] = StackCoverage(total: 12, stacked: 12),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 12L,
        });
        var caller = AdaptReviewed("file_io_caller_callee", new JsonObject
        {
            ["focusFunction"] = "module!Focus",
            ["focusInclusiveMetric"] = 12L,
            ["callers"] = new JsonArray(new JsonObject { ["function"] = "module!Caller" }),
            ["callees"] = new JsonArray(),
            ["stackCoverage"] = StackCoverage(total: 12, stacked: 12),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 12L,
        });

        var topRows = Assert.Single(top.Sections, section => section.Pointer == "/rows");
        Assert.Equal("exclusive_metric_desc", topRows.SortKey);
        Assert.Equal(["function_ordinal_asc"], topRows.TieBreakers);
        Assert.Equal(MeasurementBasis.Derived, topRows.MeasurementBasis);
        Assert.Equal(ConclusionStatus.NotConcluded, topRows.ConclusionStatus);

        var callerRows = Assert.Single(caller.Sections, section => section.Pointer == "/callers");
        Assert.Equal("inclusive_metric_desc", callerRows.SortKey);
        Assert.Equal(["function_ordinal_asc"], callerRows.TieBreakers);
    }

    [Fact]
    public void CompletedIntervalNoData_PreservesObservedEndpointBoundary()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "wait_analysis");
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>())
            .Adapt(new JsonObject
            {
                ["rows"] = new JsonArray(),
                ["scopeStatus"] = "ok",
                ["scopeMode"] = "all_processes",
                ["pidReuseObserved"] = false,
                ["capabilityStatus"] = "observed",
                ["matchedEventCount"] = 7L,
                ["noDataReason"] = "no_completed_intervals_in_scope",
            });

        Assert.False(result.Outcome.HasUsableData);
        Assert.Equal(7, result.Outcome.MatchedEventCount);
        Assert.Equal("no_completed_intervals_in_scope", result.Outcome.NoDataReason);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.ScopedCapabilityStatus);
        Assert.Equal(
            "no_completed_intervals_in_scope",
            ToolEnvelopeProjection.NormalizeNoDataReason(result.Outcome.NoDataReason));
    }

    [Fact]
    public void MarkerNoMatch_PreservesReviewedNoDataInsteadOfBecomingAnalysisFailure()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "find_marker");
        var result = new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>())
            .Adapt(new JsonObject
            {
                ["mode"] = "rows",
                ["rows"] = new JsonArray(),
                ["counts"] = null,
                ["totalMatched"] = 0L,
                ["scopeStatus"] = "ok",
                ["capabilityStatus"] = "not_observed",
                ["matchedEventCount"] = 0L,
                ["noDataReason"] = "no_name_match",
                ["warnings"] = new JsonArray(
                    "no_name_match: no materialized event name or task matched the requested substring."),
            });

        Assert.False(result.Outcome.HasUsableData);
        Assert.Equal("no_name_match", result.Outcome.NoDataReason);
        Assert.Equal(
            "no_name_match",
            ToolEnvelopeProjection.NormalizeNoDataReason(result.Outcome.NoDataReason));
    }

    [Fact]
    public void CpuBatch_AllZeroSamples_IsNoDataAndDoesNotClaimCapability()
    {
        var result = AdaptCpuBatch(
            CpuBatchScope("completed_no_samples", matchedSamples: 0, noDataReason: "no_events_in_scope"));

        Assert.False(result.Outcome.HasUsableData);
        Assert.False(result.Outcome.Partial);
        Assert.Equal("no_events_in_scope", result.Outcome.NoDataReason);
        Assert.Equal(0, result.Outcome.MatchedEventCount);
        Assert.Equal(ToolCapabilityStatus.Unknown, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Unknown, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void CpuBatch_UsableAndMissingScopes_IsPartialWithoutHidingMatchedSamples()
    {
        var result = AdaptCpuBatch(
            CpuBatchScope("completed", matchedSamples: 7),
            CpuBatchScope("scope_not_found", matchedSamples: 0, noDataReason: "scope_not_found"));

        Assert.True(result.Outcome.HasUsableData);
        Assert.True(result.Outcome.Partial);
        Assert.Null(result.Outcome.NoDataReason);
        Assert.Equal("process_instance_not_found", result.Outcome.PartialErrorCode);
        Assert.Equal(7, result.Outcome.MatchedEventCount);
        Assert.Equal(ToolCapabilityStatus.Available, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void CpuBatch_AllMissingScopes_IsStructuredNoDataWithoutAvailabilityClaim()
    {
        var result = AdaptCpuBatch(
            CpuBatchScope("scope_not_found", matchedSamples: 0, noDataReason: "scope_not_found"));

        Assert.False(result.Outcome.HasUsableData);
        Assert.False(result.Outcome.Partial);
        Assert.Equal("no_candidates_in_considered_input", result.Outcome.NoDataReason);
        Assert.Equal(ToolCapabilityStatus.Unknown, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Unknown, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void CpuBatch_BudgetSkippedScope_CannotPromoteAnEmptyBatchToUsable()
    {
        var allSkipped = Assert.Throws<ReviewedToolTerminalException>(() => AdaptCpuBatch(
            CpuBatchScope("budget_skipped", matchedSamples: 0, noDataReason: "budget_exhausted")));
        Assert.Equal("budget_exceeded", allSkipped.Code);

        var mixed = AdaptCpuBatch(
            CpuBatchScope("completed", matchedSamples: 3),
            CpuBatchScope("budget_skipped", matchedSamples: 0, noDataReason: "budget_exhausted"));
        Assert.True(mixed.Outcome.HasUsableData);
        Assert.True(mixed.Outcome.Partial);
        Assert.Equal("budget_exceeded", mixed.Outcome.PartialErrorCode);
        Assert.Equal(ToolCapabilityStatus.Partial, mixed.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void DiagnoseHighWait_TimeBudgetBoundary_IsTypedPartialWork()
    {
        static JsonObject Result(bool partial, string? partialCode) => new()
        {
            ["candidates"] = new JsonArray(new JsonObject { ["pid"] = 42 }),
            ["candidateBoundary"] = CandidateBoundary(
                requested: 5,
                returned: 1,
                total: 1,
                sortKey: "total_blocked_us_desc"),
            ["evidence"] = new JsonArray(),
            ["notConcluded"] = new JsonArray(),
            ["nextTools"] = new JsonArray(),
            ["executedToolCalls"] = new JsonArray(),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 1L,
            ["noDataReason"] = null,
            ["partial"] = partial,
            ["partialCode"] = partialCode,
        };

        var bounded = AdaptReviewed(
            "diagnose_high_wait",
            Result(partial: true, partialCode: "time_budget_exhausted"));

        Assert.True(bounded.Outcome.HasUsableData);
        Assert.True(bounded.Outcome.Partial);
        Assert.Equal("budget_exceeded", bounded.Outcome.PartialErrorCode);
        Assert.Throws<InvalidOperationException>(() => AdaptReviewed(
            "diagnose_high_wait",
            Result(partial: true, partialCode: null)));
        Assert.Throws<InvalidOperationException>(() => AdaptReviewed(
            "diagnose_high_wait",
            Result(partial: false, partialCode: "time_budget_exhausted")));
    }

    [Fact]
    public void CpuBatch_SampledEventsWithoutStacks_AreNotUsableStackEvidence()
    {
        var result = AdaptCpuBatch(
            CpuBatchScope("completed", matchedSamples: 9, stackedSamples: 0));

        Assert.False(result.Outcome.HasUsableData);
        Assert.Equal("stacks_unavailable", result.Outcome.NoDataReason);
        Assert.Equal(ToolCapabilityStatus.Unavailable, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Unavailable, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void CpuBatch_PartialStackCoverage_CannotBecomeAvailable()
    {
        var result = AdaptCpuBatch(
            CpuBatchScope("completed", matchedSamples: 9, stackedSamples: 4));

        Assert.True(result.Outcome.HasUsableData);
        Assert.Null(result.Outcome.NoDataReason);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void TopStacks_ContradictoryRawStatus_FailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => AdaptReviewed("file_io_top_stacks", new JsonObject
        {
            ["rows"] = new JsonArray(),
            ["stackCoverage"] = StackCoverage(total: 12, stacked: 0),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "observed",
            ["matchedEventCount"] = 12L,
            ["noDataReason"] = "stacks_unavailable",
        }));
    }

    [Fact]
    public void TopStacks_PartialAndFullCoverage_KeepTheirDistinctCapabilityStates()
    {
        JsonObject Result(long total, long stacked) => new()
        {
            ["rows"] = new JsonArray(new JsonObject { ["function"] = "module!Captured" }),
            ["stackCoverage"] = StackCoverage(total, stacked),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = stacked < total ? "partial" : "observed",
            ["matchedEventCount"] = total,
        };

        var partial = AdaptReviewed("file_io_top_stacks", Result(total: 12, stacked: 5));
        Assert.True(partial.Outcome.HasUsableData);
        Assert.Equal(ToolCapabilityStatus.Partial, partial.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, partial.Outcome.ScopedCapabilityStatus);

        var full = AdaptReviewed("file_io_top_stacks", Result(total: 12, stacked: 12));
        Assert.True(full.Outcome.HasUsableData);
        Assert.Equal(ToolCapabilityStatus.Available, full.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Available, full.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void CallerCallee_SyntheticUnknownFocus_CannotCloseStackEvidence()
    {
        var result = AdaptReviewed("file_io_caller_callee", new JsonObject
        {
            ["focusFunction"] = "?!?",
            ["focusInclusiveMetric"] = 0L,
            ["callers"] = new JsonArray(),
            ["callees"] = new JsonArray(),
            ["stackCoverage"] = StackCoverage(total: 12, stacked: 5),
            ["scopeStatus"] = "ok",
            ["scopeMode"] = "all_processes",
            ["capabilityStatus"] = "partial",
            ["matchedEventCount"] = 12L,
            ["noDataReason"] = "focus_not_found",
        });

        Assert.False(result.Outcome.HasUsableData);
        Assert.Equal("focus_not_found", result.Outcome.NoDataReason);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.TraceCapabilityStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, result.Outcome.ScopedCapabilityStatus);
    }

    [Fact]
    public void ScopeModeAndStatus_RejectMissingOrUnknownValues()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "wait_analysis");
        var requested = new ToolScopeSelector(42, null, null, null, null, null, null);

        Assert.Throws<InvalidOperationException>(() =>
            ToolEnvelopeProjection.ParseScopeMode(tool, null, requested));
        Assert.Throws<InvalidOperationException>(() =>
            ToolEnvelopeProjection.ParseScopeMode(tool, "best_effort_process", requested));
        Assert.Throws<InvalidOperationException>(() =>
            ToolEnvelopeProjection.ParseScopeStatus(null, threadRequested: false));
        Assert.Throws<InvalidOperationException>(() =>
            ToolEnvelopeProjection.ParseScopeStatus("maybe_ok", threadRequested: false));
    }

    [Theory]
    [InlineData("scopedIdentityUnresolvedEventCount")]
    [InlineData("scopedIdentityUnresolvedEndpointCount")]
    [InlineData("scopedIdentityUnresolvedCSwitchSideCount")]
    [InlineData("scopedUnattributedEventCount")]
    public void ScopeIdentityUnresolved_RecognizesEveryReviewedCounterFamily(string field)
    {
        Assert.True(ToolEnvelopeProjection.HasScopedIdentityUnresolvedEvidence(
            new JsonObject { [field] = 1L }));
        Assert.False(ToolEnvelopeProjection.HasScopedIdentityUnresolvedEvidence(
            new JsonObject { [field] = 0L }));
    }

    [Fact]
    public void ScopeIdentityUnresolved_SourceUnattributedReasonCannotProjectFalse()
    {
        Assert.True(ToolEnvelopeProjection.HasScopedIdentityUnresolvedEvidence(
            new JsonObject { ["noDataReason"] = "source_events_unattributed" }));
    }

    [Fact]
    public async Task DiagnoseWindowWideGuard_IsStructuredInvalidArgumentWithRequestedScope()
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var services = new ServiceCollection();
        services.AddSingleton(_ => new TraceCache());
        services.AddSingleton<SymbolService>();
        using var provider = services.BuildServiceProvider();
        var tool = catalog.CreateServerTools(provider).Single(candidate =>
            candidate.ProtocolTool.Name == "diagnose_window");
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(provider);
        var parameters = new CallToolRequestParams
        {
            Name = tool.ProtocolTool.Name,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["path"] = JsonSerializer.SerializeToElement(
                    "trc_0123456789abcdef0123456789abcdef"),
                ["startUs"] = JsonSerializer.SerializeToElement(100L),
                ["endUs"] = JsonSerializer.SerializeToElement(1_100L),
                ["maxWindowDurationUs"] = JsonSerializer.SerializeToElement(100L),
            },
        };
        var request = new JsonRpcRequest
        {
            Id = new RequestId("wide-window-guard"),
            Method = RequestMethods.ToolsCall,
            Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
        };

        var result = await tool.InvokeAsync(
            new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
            CancellationToken.None);

        Assert.True(result.IsError);
        var envelope = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!.AsObject();
        Assert.Equal("invalid_argument", envelope["error"]!["code"]!.GetValue<string>());
        Assert.Equal("not_evaluated", envelope["scope"]!["status"]!.GetValue<string>());
        Assert.Equal("trace", envelope["scope"]!["mode"]!.GetValue<string>());
        Assert.Equal("100", envelope["scope"]!["requested"]!["windowStartUs"]!.GetValue<string>());
        Assert.Equal("1100", envelope["scope"]!["requested"]!["windowEndUs"]!.GetValue<string>());
        Assert.Empty(envelope["scope"]!["candidates"]!.AsArray());
        Assert.Null(envelope["data"]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArtifactRootInitializationFailure_IsScopedTraceAccessDeniedWithoutDetailLeak(
        bool unauthorized)
    {
        var privateDetail = "private artifact-root ACL detail";
        var store = new OwnedTraceArtifactStore(
            "ignored-test-root",
            maxInputBytes: 1,
            maxStoreBytes: 1,
            maxObjects: 1,
            createTrustedRoot: _ => throw (unauthorized
                ? new UnauthorizedAccessException(privateDetail)
                : new IOException(privateDetail)));
        var root = typeof(OwnedTraceArtifactStore).GetProperty(
            "Root",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var invocation = Assert.Throws<TargetInvocationException>(() => root.GetValue(store));
        var scoped = Assert.IsType<TraceAccessException>(invocation.InnerException);
        Assert.Equal("trace_access_denied", scoped.Code);
        var error = ContractMcpServerTool.MapException(scoped);

        Assert.Equal("trace_access_denied", error.Code);
        Assert.False(error.Retryable);
        Assert.DoesNotContain("artifact-root", error.Message, StringComparison.Ordinal);
    }

    private static ReviewedToolResult AdaptCpuBatch(params JsonObject[] rows)
    {
        var scopeResults = new JsonArray();
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            var pid = 42 + index;
            row["pid"] = pid;
            var status = row["resultStatus"]!.GetValue<string>();
            var matched = row["matchedSampleCount"]!.GetValue<long>();
            var stacked = row["_testStackedSampleCount"]?.GetValue<long>() ?? matched;
            row.Remove("_testStackedSampleCount");
            if (status is "completed" or "completed_no_samples")
            {
                row["result"] = new JsonObject
                {
                    ["rows"] = new JsonArray(),
                    ["stats"] = new JsonObject
                    {
                        ["topUnresolvedModules"] = new JsonArray(),
                        ["unresolvedModuleCount"] = 0,
                    },
                    ["stackCoverage"] = new JsonObject
                    {
                        ["totalEventCount"] = matched,
                        ["stackedEventCount"] = stacked,
                        ["coverageState"] = matched == 0
                            ? "no_events"
                            : stacked == 0 ? "no_stacks" : stacked == matched ? "full" : "partial",
                    },
                };
            }
            var available = status is "completed" or "completed_no_samples";
            static JsonObject Boundary(string pointer, bool available) => new()
            {
                ["sectionPointer"] = pointer,
                ["requested"] = pointer.EndsWith("topUnresolvedModules", StringComparison.Ordinal) ? 10 : 30,
                ["returned"] = 0,
                ["totalAvailable"] = available ? 0 : null,
                ["totalState"] = available ? "exact" : "unknown",
                ["moreState"] = available ? "absent" : "unknown",
                ["hasMore"] = false,
                ["continuationAvailable"] = false,
                ["truncationReason"] = available ? null : "analysis_unavailable",
            };
            row["rowsBoundary"] = Boundary("/scopeResults/result/rows", available);
            row["topUnresolvedModulesBoundary"] = Boundary(
                "/scopeResults/result/stats/topUnresolvedModules", available);
            scopeResults.Add(row);
        }
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == "cpu_top_functions_batch");
        return new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>
            {
                ["pageSize"] = JsonSerializer.SerializeToElement(100),
            })
            .Adapt(new JsonObject
            {
                ["warnings"] = new JsonArray(),
                ["partial"] = rows.Any(row => row["resultStatus"]!.GetValue<string>() is
                    "completed" or "completed_no_samples") && rows.Any(row =>
                    row["resultStatus"]!.GetValue<string>() is "scope_not_found" or
                        "ambiguous_process_instance" or "budget_skipped" or "analysis_failed"),
                ["partialErrorCode"] = rows.Any(row =>
                    row["resultStatus"]!.GetValue<string>() is "completed" or
                        "completed_no_samples") && rows.Any(row =>
                    row["resultStatus"]!.GetValue<string>() == "budget_skipped")
                    ? "budget_exceeded"
                    : null,
                ["scopeResults"] = scopeResults,
                ["requestedPidCount"] = rows.Length,
                ["completedPidCount"] = rows.Count(row =>
                    row["resultStatus"]!.GetValue<string>() is "completed" or "completed_no_samples"),
                ["pageContext"] = new JsonObject
                {
                    ["traceId"] = "trc_test",
                    ["traceGenerationId"] = "gen_test",
                    ["toolName"] = TimelinePagination.CpuTopFunctionsBatchTool,
                    ["contractVersion"] = ToolContractVersions.V2,
                    ["symbolContextId"] = null,
                    ["queryHash"] = new string('a', 64),
                    ["ordering"] = TimelinePagination.CpuTopFunctionsBatchOrdering,
                    ["startIndex"] = 0,
                    ["requestedPageSize"] = 100,
                    ["totalCount"] = rows.Length,
                    ["returnedCount"] = rows.Length,
                },
                ["returnedCount"] = rows.Length,
                ["hasMore"] = false,
                ["nextCursor"] = null,
                ["resultSetId"] = "cbr_test",
            });
    }

    private static JsonObject CpuBatchScope(
        string resultStatus,
        long matchedSamples,
        string? noDataReason = null,
        long? stackedSamples = null) => new()
    {
        ["resultStatus"] = resultStatus,
        ["matchedSampleCount"] = matchedSamples,
        ["noDataReason"] = noDataReason,
        ["_testStackedSampleCount"] = stackedSamples,
    };

    private static ReviewedToolResult AdaptReviewed(string toolName, JsonObject domain)
    {
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = catalog.Tools.Single(item => item.ToolName == toolName);
        return new ReviewedToolOutcomeAdapterRegistry(catalog.Tools)
            .Plan(tool, new Dictionary<string, JsonElement>())
            .Adapt(domain);
    }

    private static JsonObject CandidateBoundary(
        int requested,
        int returned,
        int? total,
        string sortKey)
    {
        var hasMore = total > returned;
        var tieBreakers = sortKey == "startup_wait_ratio_desc"
            ? new JsonArray("observed_startup_wall_us_desc", "process_start_us_asc", "pid_asc")
            : new JsonArray("wait_ratio_desc_nulls_last", "pid_asc", "process_start_us_asc");
        return new JsonObject
        {
            ["sectionPointer"] = "/candidates",
            ["requested"] = requested,
            ["returned"] = returned,
            ["totalAvailable"] = total,
            ["totalState"] = total.HasValue ? "exact" : "unknown",
            ["moreState"] = total.HasValue ? hasMore ? "present" : "absent" : "unknown",
            ["hasMore"] = hasMore,
            ["continuationAvailable"] = false,
            ["truncationReason"] = total.HasValue
                ? hasMore ? "fixed_source_limit" : null
                : "source_limit_saturated",
            ["sortKey"] = sortKey,
            ["sortDirection"] = "descending",
            ["tieBreakers"] = tieBreakers,
        };
    }

    private static JsonObject StackCoverage(long total, long stacked) => new()
    {
        ["totalEventCount"] = total,
        ["stackedEventCount"] = stacked,
        ["coverageState"] = total == 0
            ? "no_events"
            : stacked == 0 ? "no_stacks" : stacked == total ? "full" : "partial",
    };
}
