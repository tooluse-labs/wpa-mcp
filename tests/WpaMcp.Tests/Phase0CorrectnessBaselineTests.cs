using System.Text.Json;
using WpaMcp.Core;
using WpaMcp.Tests.ContractBaselines;

namespace WpaMcp.Tests;

public sealed class Phase0CorrectnessBaselineTests
{
    private static readonly IReadOnlyDictionary<string, string> ImplementedIssueStatuses =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["COR-ATTRIBUTION-001"] = "fixed_contract2_exact_process_instance_self_attribution_guard_and_not_concluded_boundary_verified",
            ["COR-CAUSAL-001"] = "fixed_contract2_association_ceiling_and_structured_does_not_prove_boundary_verified",
            ["COR-COUNT-001"] = "fixed_contract2_parsed_raw_and_parser_measurement_provenance_boundary_verified",
            ["COR-HEURISTIC-001"] = "fixed_contract2_heuristic_basis_provenance_and_not_concluded_boundary_verified",
            ["COR-ID-001"] = "fixed_contract2_canonical_string_and_safe_legacy_projection_live_schema_verified",
            ["COR-NODATA-001"] = "fixed_contract2_structured_scope_capability_no_data_and_partial_failure_envelope_verified",
            ["COR-PAGING-001"] = "fixed_contract2_section_and_reachable_collection_completeness_verified",
            ["COR-SIDEFX-001"] = "fixed_active_secure_query_path_trace_id_and_immutable_symbol_context_no_environment_mutation_verified_legacy_symbol_mutators_inactive",
            ["COR-TRUNCATION-001"] = "fixed_contract2_typed_nested_samples_boundary_verified",
        };

    private static readonly IReadOnlyDictionary<string, string> ExpectedFieldImplementationStates =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["capability_and_no_data"] = "contract2_structured_scope_capability_no_data_verified",
            ["capture_and_parser_integrity"] = "contract2_capture_provenance_boundary_verified",
            ["collection_completeness"] = "contract2_section_and_reachable_collection_completeness_verified",
            ["domain_stack_coverage"] = "preserve",
            ["evidence_inference_boundary"] = "contract2_structured_inference_boundary_verified",
            ["interval_integrity"] = "preserve",
            ["metric_precision"] = "phase1_fixed_verified",
            ["nested_sample_boundary"] = "contract2_typed_samples_boundary_verified",
            ["opaque_identifier_precision"] = "contract2_canonical_string_and_safe_legacy_projection_verified",
            ["process_instance_scope"] = "preserve",
            ["qualified_denominators"] = "preserve",
            ["scope_outcome"] = "preserve",
            ["self_performance_attribution"] = "contract2_exact_process_instance_attribution_guard_verified",
            ["stack_metric_semantics"] = "preserve",
            ["symbol_frame_resolution"] = "preserve",
            ["temporal_file_identity"] = "preserve",
            ["thread_instance_scope"] = "preserve",
            ["virtual_alloc_accounting"] = "preserve",
        };

    private static readonly string[] ExpectedFieldGroupIds =
    [
        "capability_and_no_data",
        "capture_and_parser_integrity",
        "collection_completeness",
        "domain_stack_coverage",
        "evidence_inference_boundary",
        "interval_integrity",
        "metric_precision",
        "nested_sample_boundary",
        "opaque_identifier_precision",
        "process_instance_scope",
        "qualified_denominators",
        "scope_outcome",
        "self_performance_attribution",
        "stack_metric_semantics",
        "symbol_frame_resolution",
        "temporal_file_identity",
        "thread_instance_scope",
        "virtual_alloc_accounting",
    ];

    private static readonly string[] ExpectedIssueIds =
    [
        "COR-ATTRIBUTION-001",
        "COR-CACHE-001",
        "COR-CAUSAL-001",
        "COR-COUNT-001",
        "COR-FILEMAP-001",
        "COR-HEURISTIC-001",
        "COR-ID-001",
        "COR-NODATA-001",
        "COR-PAGING-001",
        "COR-PRECISION-001",
        "COR-SCOPE-001",
        "COR-SCOPE-002",
        "COR-SIDEFX-001",
        "COR-STACK-001",
        "COR-SYMBOL-001",
        "COR-TRUNCATION-001",
        "COR-VM-001",
        "COR-WAIT-001",
    ];

    [Fact]
    public void CorrectnessDispositionBaseline_IsCompleteAndTraceable()
    {
        var root = LocateRepoRoot();
        var baselinePath = Path.Combine(root, "eng", "contract-baselines", "correctness-disposition.v1.json");
        using var baseline = JsonDocument.Parse(File.ReadAllText(baselinePath));
        var document = baseline.RootElement;

        Assert.Equal(1, document.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("2026-08-02", document.GetProperty("baselineDate").GetString());

        var designPath = document.GetProperty("design").GetString()!;
        var decisionPath = document.GetProperty("acceptedDecision").GetString()!;
        Assert.True(File.Exists(RepoPath(root, designPath)), designPath);
        Assert.True(File.Exists(RepoPath(root, decisionPath)), decisionPath);
        var design = File.ReadAllText(RepoPath(root, designPath));

        var dispositions = document.GetProperty("dispositions").EnumerateArray().ToArray();
        var actualIssueIds = dispositions
            .Select(item => item.GetProperty("issueId").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedIssueIds, actualIssueIds);
        Assert.Equal(actualIssueIds.Length, actualIssueIds.Distinct(StringComparer.Ordinal).Count());

        Assert.Equal(11, dispositions.Count(item => item.GetProperty("disposition").GetString() == "preserve"));
        Assert.Equal(2, dispositions.Count(item => item.GetProperty("disposition").GetString() == "normalize_only"));
        Assert.Equal(5, dispositions.Count(item => item.GetProperty("disposition").GetString() == "known_incorrect_must_change"));

        var unresolvedGates = document.GetProperty("unresolvedProgramGates")
            .EnumerateArray()
            .ToDictionary(
                gate => gate.GetProperty("id").GetString()!,
                gate => gate.GetProperty("status").GetString()!,
                StringComparer.Ordinal);
        Assert.DoesNotContain("third_party_client_matrix", unresolvedGates.Keys);
        Assert.Equal("accepted_residual_risk_non_blocking", unresolvedGates["opaque_converter_physical_peak"]);
        Assert.Equal("rollout_telemetry_pending", unresolvedGates["raw_path_0_5_deprecation_telemetry"]);
        Assert.Equal("release_approval_pending", unresolvedGates["release_approval_tag_and_assets"]);

        foreach (var item in dispositions)
        {
            var issueId = item.GetProperty("issueId").GetString()!;
            var implementationStatus = item.GetProperty("currentImplementationStatus").GetString()!;
            Assert.Contains(issueId, design, StringComparison.Ordinal);
            Assert.InRange(item.GetProperty("targetPhase").GetInt32(), 1, 7);
            Assert.NotEmpty(item.GetProperty("authoritativeFacts").EnumerateArray());
            Assert.False(string.IsNullOrWhiteSpace(item.GetProperty("migrationRule").GetString()));

            if (ImplementedIssueStatuses.TryGetValue(issueId, out var expectedStatus))
            {
                Assert.Equal(expectedStatus, implementationStatus);
                Assert.DoesNotContain("pending", implementationStatus, StringComparison.Ordinal);
                Assert.DoesNotContain("not_implemented", implementationStatus, StringComparison.Ordinal);
            }

            if (issueId == "COR-PAGING-001")
            {
                Assert.Empty(item.GetProperty("knownRemainingGaps").EnumerateArray());
                Assert.Contains(
                    "tests/WpaMcp.Tests/ActiveReachableCollectionClosureTests.cs",
                    item.GetProperty("sourcePaths")
                        .EnumerateArray()
                        .Select(value => value.GetString()));
            }

            if (issueId == "COR-TRUNCATION-001")
            {
                Assert.Empty(item.GetProperty("knownRemainingGaps").EnumerateArray());
                Assert.Contains(
                    "tests/WpaMcp.Tests/SecurityScanAnalysisTests.cs",
                    item.GetProperty("sourcePaths")
                        .EnumerateArray()
                        .Select(value => value.GetString()));
            }

            foreach (var sourcePath in item.GetProperty("sourcePaths").EnumerateArray())
            {
                var relativePath = sourcePath.GetString()!;
                Assert.True(File.Exists(RepoPath(root, relativePath)), $"{issueId}: missing {relativePath}");
            }
        }
    }

    [Fact]
    public void CorrectnessFieldMatrix_PreservesEvidenceBoundariesWithoutGuessingVNextWirePaths()
    {
        var root = LocateRepoRoot();
        var matrixPath = Path.Combine(root, "eng", "contract-baselines", "correctness-field-matrix.v1.json");
        using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
        var document = matrix.RootElement;

        Assert.Equal(1, document.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "contract_2_0_field_placement_locked_by_adr_0003",
            document.GetProperty("contractDecisionState").GetString());

        var groups = document.GetProperty("fieldGroups").EnumerateArray().ToArray();
        var actualIds = groups
            .Select(group => group.GetProperty("id").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(ExpectedFieldGroupIds, actualIds);
        Assert.Equal(actualIds.Length, actualIds.Distinct(StringComparer.Ordinal).Count());

        foreach (var group in groups)
        {
            var id = group.GetProperty("id").GetString()!;
            var source = group.GetProperty("authoritativeSource").GetString()!;
            Assert.True(File.Exists(RepoPath(root, source)), $"{id}: missing {source}");
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("applicableWhen").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("unit").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("vNextSemanticSlot").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty("requiredSemantics").GetString()));
            Assert.Equal(
                ExpectedFieldImplementationStates[id],
                group.GetProperty("implementationState").GetString());

            if (group.TryGetProperty("verificationSources", out var verificationSources))
            {
                Assert.NotEmpty(verificationSources.EnumerateArray());
                foreach (var evidencePath in verificationSources.EnumerateArray())
                {
                    var relativePath = evidencePath.GetString()!;
                    Assert.True(File.Exists(RepoPath(root, relativePath)), $"{id}: missing {relativePath}");
                }
            }
        }
    }

    [Fact]
    public void ToolListPayloadBudget_RecordsCorrectnessGrowthWithoutWeakeningTheContract()
    {
        var root = LocateRepoRoot();
        var budgetPath = Path.Combine(
            root,
            "eng",
            "contract-baselines",
            "tool-list-payload-budget.v1.json");
        using var budget = JsonDocument.Parse(File.ReadAllText(budgetPath));
        var document = budget.RootElement;
        var legacy = document.GetProperty("legacyActiveSnapshot");
        var corrected = document.GetProperty("correctedPhase1Observation");
        var active = document.GetProperty("activeContract2Observation");
        var disposition = document.GetProperty("reviewDisposition");

        Assert.Equal("tool-list-payload-budget.v1", document.GetProperty("formatVersion").GetString());
        Assert.True(File.Exists(RepoPath(root, legacy.GetProperty("artifact").GetString()!)));
        Assert.Equal(61, legacy.GetProperty("toolCount").GetInt32());
        Assert.Equal(61, corrected.GetProperty("toolCount").GetInt32());
        Assert.Equal(
            corrected.GetProperty("catalogBytes").GetInt32() -
            legacy.GetProperty("catalogBytes").GetInt32(),
            corrected.GetProperty("deltaBytes").GetInt32());
        Assert.Equal(
            ToolListPayload.BaselineGuardPayloadBytes,
            document.GetProperty("legacyTransitionGuardBytes").GetInt32());
        Assert.Equal(
            ToolListPayload.DefaultMaxPayloadBytes,
            document.GetProperty("aggregateWarningThresholdBytes").GetInt32());
        Assert.False(disposition.GetProperty("toolsHidden").GetBoolean());
        Assert.False(disposition.GetProperty("fullSchemasRemovedFromContract").GetBoolean());
        Assert.True(disposition.GetProperty("fullSchemasOmittedFromDefaultDiscovery").GetBoolean());
        Assert.False(disposition.GetProperty("typedFieldsReplacedWithOpenDictionary").GetBoolean());
        Assert.True(disposition.GetProperty("correctnessFieldsPreserved").GetBoolean());

        var snapshot = LegacyActiveToolSnapshotBuilder.Build();
        var current = ToolListPayload.MeasureCurrentAssembly();
        var tools = ToolListPayload.MeasureCurrentTools();
        var preflight = ToolsListPageFitter.Preflight(
            tools,
            ToolsListPaginationOptions.HardMaxResponseFrameBytes);
        Assert.Equal(snapshot.ToolCount, current.ToolCount);
        Assert.Equal(snapshot.ToolCount, active.GetProperty("toolCount").GetInt32());
        Assert.Equal(snapshot.StructuredToolNames.Count,
            active.GetProperty("structuredToolCount").GetInt32());
        Assert.Equal(snapshot.ToolCount, snapshot.StructuredToolNames.Count);
        Assert.Equal(snapshot.CatalogBytes,
            active.GetProperty("aggregateCatalogResultBytes").GetInt32());
        Assert.Equal(snapshot.CatalogSha256,
            active.GetProperty("aggregateCatalogSha256").GetString());
        Assert.Equal(snapshot.CatalogBytes, current.PayloadBytes);
        Assert.Equal(current.PayloadBytes, preflight.AggregateCatalogResultBytes);
        Assert.Equal(
            ToolsListPaginationOptions.HardMaxResponseFrameBytes,
            active.GetProperty("pageFrameLimitBytes").GetInt32());
        Assert.True(preflight.MinimumViableFrameBytes <= preflight.MaxResponseFrameBytes);
        Assert.False(current.ExceedsLimit);
        Assert.True(current.PayloadBytes <= ToolListPayload.DefaultMaxPayloadBytes);
        Assert.All(tools, tool => Assert.Null(tool.OutputSchema));
        Assert.Equal(
            tools.Count,
            tools.Count(tool => tool.Meta?[ToolOutputContract.MetadataKey] is not null));
        Assert.Equal(0, active.GetProperty("embeddedOutputSchemaCount").GetInt32());
        Assert.Equal(tools.Count,
            active.GetProperty("outputContractMetadataCount").GetInt32());
        Assert.True(active.GetProperty("withinLeanDiscoveryBudget").GetBoolean());
        Assert.Equal(
            "eng/contract-baselines/tool-output-contract-registry.v1.json",
            active.GetProperty("fullContractRegistryArtifact").GetString());
        Assert.True(File.Exists(RepoPath(
            root,
            active.GetProperty("fullContractRegistryArtifact").GetString()!)));
        using var registry = JsonDocument.Parse(File.ReadAllBytes(RepoPath(
            root,
            active.GetProperty("fullContractRegistryArtifact").GetString()!)));
        Assert.Equal(
            active.GetProperty("fullContractRegistryCanonicalUtf8Bytes").GetInt64(),
            registry.RootElement.GetProperty("totalCanonicalUtf8Bytes").GetInt64());
        Assert.Equal(
            "lean_discovery_with_on_demand_full_contracts",
            active.GetProperty("aggregatePromptCostState").GetString());
    }

    private static string RepoPath(string root, string relativePath) =>
        Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private static string LocateRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WpaMcp.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root from the test output directory.");
    }
}
