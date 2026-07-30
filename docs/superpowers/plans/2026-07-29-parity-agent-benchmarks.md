# Golden Parity and Agent Benchmark Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish versioned deterministic evidence for fixture integrity, normalized tool output, cross-tool correctness, and supported/preview/gap claims, then add a reproducible S01–S10 agent benchmark whose conclusions are scored by code rather than model self-report.

**Architecture:** A dedicated golden-test project validates fixture hashes, manifest provenance, the explicit v2/strict/ID-only server profile, real schemas/results, reviewed snapshots, cross-tool invariants, and checked-in WPA/PerfView export artifacts on every PR. A raw TraceEvent walker provides an internal event-level cross-check but is never presented as an independent parity oracle because it shares the server's underlying library. The Child 9 `thread-scope.etl` fixture supplies a same-thread fast/slow S03 variant. A separate console benchmark drives the real MCP process in full and tools-only modes, records every model/tool turn, and passes transcripts to deterministic scenario verifiers. Probabilistic promotion compares only runs with identical model/prompt/schema/server-profile/fixture/runner dimensions and remains scheduled/release-only.

**Tech Stack:** C# and xUnit; exact TFM/SDK/package graph from Child 11A; Child 9 raw stdio harness; `System.Text.Json`; TraceEvent internal raw-event cross-check; real WPAExporter/PerfView external exports; HTTP Responses-style model adapter without an additional vendor SDK; PowerShell policy entry points; Windows CI.

## Global Constraints

- Deterministic golden/invariant tests run on every PR and never call an external model or symbol/network service.
- Agent runs use only fixtures marked public or synthetic and run the MCP server in `strict` privacy mode before tool output reaches the external model.
- Every golden, invariant, and agent harness launches the server with the explicit profile `--output-contract v2 --privacy strict --trace-reference-mode id-only`; it never relies on the compatibility-stage or later secure-default no-flag values. For each session/fixture it calls `load_trace` exactly once with the fixture path, keeps the returned `traceId` only in invocation-local state, and gives query tools/the model that ID rather than a raw path. The canonical profile and load strategy are hash-bound evidence.
- No fixture hash, oracle version, external-export result, model snapshot, prompt hash, schema hash, server-profile hash, runner version, trial result, review outcome, or waiver approval is prefilled. Repository commands compute it from the actual artifact/run; the operator reviews and commits that evidence.
- Snapshot normalization may remove only declared volatile transport/privacy fields. PID, TID, process/thread lifetime keys, windows, totals, completion state, coverage, symbol-quality state, ordering, and units are correctness evidence and cannot be normalized away.
- S01–S10 remain the canonical scenario IDs. The thread-window comparison is the `S03-thread-window` variant under S03, not a new scenario.
- At least five trials per scenario and mode are retained individually. Means never replace raw trials.
- A policy threshold cannot be relaxed in the same change that fails it. Promotion tooling compares the policy blob to the merge-base version and fails when both policy and failing evidence changed.
- External-model unavailability fails closed for release unless a non-expired, schema-valid waiver names the exact omitted artifact, reason, approver, expiry, and commit. The plan never fabricates an approval.

**Spec:** `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` at commit `7ef8ff5`.

**Prerequisites:** Child 9 passes. Child 5's v2 schemas/envelopes/privacy are stable. Children 2–4 correctness fields are final. Child 11A pins TFM, MCP SDK, protocol, and package versions.

---

## Fixed manifest, snapshot, and benchmark contracts

Use these exact golden records:

```csharp
namespace WprMcp.GoldenTests;

internal sealed record GoldenManifest(
    string SchemaVersion,
    GoldenServerProfile ServerProfile,
    IReadOnlyList<GoldenFixture> Fixtures,
    IReadOnlyList<GoldenCase> Cases);

internal sealed record GoldenServerProfile(
    string OutputContract,
    string PrivacyMode,
    string TraceReferenceMode,
    bool LoadOncePerFixtureSession,
    string ProfileSha256);

internal sealed record GoldenFixture(
    string Id,
    string RelativePath,
    string Sha256,
    string Provenance,
    string CaptureRecipe,
    string OracleSource,
    string OracleVersion,
    IReadOnlyList<string> ExpectedCapabilities,
    bool PublicOrSynthetic,
    string ExternalModelPrivacyMode);

internal sealed record GoldenCase(
    string Id,
    string FixtureId,
    string ToolName,
    JsonObject Arguments,
    string SnapshotPath,
    GoldenNormalization Normalization,
    IReadOnlyDictionary<string, double> NumericTolerances);

internal sealed record GoldenNormalization(
    IReadOnlyList<string> RemoveJsonPointers,
    IReadOnlyList<string> StableAliasJsonPointers,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SortArraysBy);

internal enum ExternalOracleReviewStatus
{
    Automated,
    HumanReviewed
}

internal sealed record ExternalOracleManifest(
    string SchemaVersion,
    IReadOnlyList<ExternalOracleArtifact> Artifacts);

internal sealed record ExternalOracleArtifact(
    string Id,
    string FixtureId,
    string Domain,
    string Tool,
    string ToolVersion,
    string ToolSha256,
    IReadOnlyList<string> Command,
    int ExitCode,
    string StdoutSha256,
    string StderrSha256,
    DateTimeOffset CapturedUtc,
    string SourceFixtureSha256,
    string ExportPath,
    string ExportSha256,
    string Metric,
    string Unit,
    double AbsoluteTolerance,
    double RelativeTolerance,
    ExternalOracleReviewStatus ReviewStatus,
    string ReviewEvidencePath,
    string ReviewEvidenceSha256);
```

`Update-GoldenEvidence.ps1 -Mode hashes` streams `Get-FileHash -Algorithm SHA256` over the currently committed fixture bytes and writes a candidate manifest. The operator reviews the diff before copying it; no hash is copied into this plan. The `thread-scope.etl` hash and oracle keys are imported verbatim from Child 9's generated `thread-scope.manifest.json`; there is no second hand-maintained value.

`GoldenServerProfile` has exact values `v2`, `strict`, `id-only`, and `true`. `ProfileSha256` is SHA-256 over the canonical UTF-8/LF text `output=v2\nprivacy=strict\ntraceReferences=id-only\nloadOncePerFixtureSession=true\n`. A query `GoldenCase.Arguments.path` is the literal sentinel `$traceId`, never a source path or alias; a `load_trace` case may use only `$fixturePath`. The harness resolves `$fixturePath` from the hash-bound `GoldenFixture`, calls `load_trace` once, replaces `$traceId` only in the invocation-local cloned argument object, and never writes the resulting random ID back to manifests, snapshots, prompts, or baseline dimensions.

External parity evidence is separate. `external-oracles.v1.json` records the observed WPAExporter or PerfView executable version, the complete tokenized export command, source fixture hash, exported artifact hash, metric/unit, reviewed tolerance, and review status. `Automated` artifacts are parsed and compared in CI and may support a `supported` claim. `HumanReviewed` artifacts retain the export and signed review note but may support only `preview`; they never masquerade as a deterministic pass. A domain that WPA/PerfView can express but lacks either form of external evidence is `preview` or `gap`, never `supported` merely because the raw TraceEvent cross-check agrees.

Use these exact agent records:

```csharp
namespace WprMcp.AgentBenchmarks;

public enum BenchmarkMode { FullMcp, ToolsOnly }
public enum ConclusionLabel { Supported, NotConcluded, Unsupported }

public sealed record AgentBenchmarkRunOptions(
    string ScenarioFile,
    string ModelConfigFile,
    string PolicyFile,
    BenchmarkMode Mode,
    int TrialsPerScenario,
    string OutputDirectory,
    double Temperature,
    int? Seed);

public sealed record ModelSnapshotConfig(
    string Provider,
    Uri Endpoint,
    string Snapshot,
    bool SupportsTemperature,
    bool SupportsSeed);

public sealed record AgentMessage(string Role, JsonNode Content);

public sealed record AgentTurn(
    JsonNode? FinalAnswer,
    IReadOnlyList<RequestedToolCall> ToolCalls,
    JsonObject ProviderMetadata);

public sealed record RequestedToolCall(
    string Id, string ToolName, JsonObject Arguments);

public interface IAgentModelClient
{
    Task<AgentTurn> CompleteAsync(
        IReadOnlyList<AgentMessage> messages,
        IReadOnlyList<JsonObject> tools,
        ModelSnapshotConfig model,
        double temperature,
        int? seed,
        CancellationToken cancellationToken);
}

public sealed record ToolCallRecord(
    int Ordinal,
    string Name,
    JsonObject Arguments,
    JsonObject Result,
    bool Relevant,
    string RelevanceReason);

public sealed record VerificationResult(
    bool TaskSuccess,
    bool ConclusionAccurate,
    ConclusionLabel Label,
    IReadOnlyList<string> EvidenceIds,
    IReadOnlyList<string> Failures);

public interface IEvidenceVerifier
{
    string ScenarioId { get; }
    VerificationResult Verify(
        ScenarioDefinition scenario,
        IReadOnlyList<ToolCallRecord> calls,
        JsonNode? finalAnswer);
}

public sealed record BenchmarkTrial(
    string SchemaVersion,
    string ScenarioId,
    string Variant,
    BenchmarkMode Mode,
    int Trial,
    string ModelSnapshot,
    string PromptSha256,
    string ToolSchemaSha256,
    string ServerProfileSha256,
    IReadOnlyDictionary<string, string> FixtureSha256,
    string RunnerVersion,
    double Temperature,
    int? Seed,
    string Commit,
    IReadOnlyList<ToolCallRecord> ToolCalls,
    JsonNode? FinalAnswer,
    VerificationResult Verification);

public sealed record AgentBaselineManifest(
    string SchemaVersion,
    string Commit,
    BenchmarkComparisonDimensions Dimensions,
    IReadOnlyList<RetainedTrial> Trials);

public sealed record BenchmarkComparisonDimensions(
    string ModelSnapshot,
    string PromptSha256,
    string ToolSchemaSha256,
    string ServerProfileSha256,
    IReadOnlyDictionary<string, string> FixtureSha256,
    string RunnerVersion,
    double Temperature,
    int? Seed);

public sealed record RetainedTrial(string Path, string Sha256);
```

Metrics are fixed:

```text
structured_parse_success = parsed trial artifacts / all attempted trials
wrong_tool_rate = irrelevant tool calls / all tool calls
mean_tool_calls = tool calls / verifier-confirmed successful trials only
task_success_rate = verifier-confirmed successful trials / all completed trials
conclusion_accuracy = verifier-confirmed accurate conclusions / all completed trials
overclaim_rate = unsupported or insufficient-evidence trials asserting a supported root cause / such trials
```

Promotion requires structured parse success `1.0`; at least five retained trials for every scenario/mode; wrong-tool rate no more than comparable baseline plus `0.02`; no loss in task success or conclusion accuracy; and at least `0.10` reduction in mean tool calls. The only exception is a recorded accuracy-improvement decision whose reviewed evidence is real and whose policy rule was not changed in the same commit.

---

## File structure overview

| File | Action | Purpose |
|---|---|---|
| `tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj` | Create | Deterministic Windows golden/invariant project |
| `tests/WprMcp.GoldenTests/golden-manifest.v1.json` | Create | Fixture/case/provenance/oracle/tolerance registry |
| `tests/WprMcp.GoldenTests/external-oracles.v1.json` | Create from real exports | WPAExporter/PerfView version/command/hash/tolerance registry |
| `tests/WprMcp.GoldenTests/GoldenManifest.cs` | Create | Strict manifest parser and validation |
| `tests/WprMcp.GoldenTests/ExternalOracle.cs` | Create | Strict external artifact parser/comparator |
| `tests/WprMcp.GoldenTests/GoldenNormalizer.cs` | Create | Whitelist-only canonical JSON normalization |
| `tests/WprMcp.GoldenTests/FixtureIntegrityTests.cs` | Create | Required existence/hash/provenance/capability gates |
| `tests/WprMcp.GoldenTests/ExternalOracleTests.cs` | Create | Version/source/export hash and metric parity gates |
| `tests/WprMcp.GoldenTests/GoldenSnapshotTests.cs` | Create | Real stdio results versus normalized snapshots |
| `tests/WprMcp.GoldenTests/CrossToolInvariantTests.cs` | Create | Identity/window/unit/total/coverage/completion invariants |
| `tests/WprMcp.GoldenTests/ThreadWindowInvariantTests.cs` | Create | Fast/slow same-thread CPU/wait/stack checks |
| `tests/WprMcp.GoldenTests/Snapshots/*.json` | Create | One reviewed normalized file per manifest case |
| `tests/WprMcp.GoldenTests/ExternalOracles/*.json` | Create from real tools | Checked-in WPAExporter/PerfView exports and review records |
| `tests/WprMcp.GoldenTests/ExternalOracles/wpaexporter-thread-scope-cpu-samples.v1.json` | Create from WPAExporter | Normalized CPU-sample export used for automated parity |
| `tests/WprMcp.GoldenTests/ExternalOracles/perfview-thread-scope-cswitch-time.v1.json` | Create from PerfView | Normalized context-switch export used for automated parity |
| `tests/WprMcp.GoldenTests/ExternalOracles/Reviews/external-oracle-review.v1.md` | Create from human review | Reviewed commands, hashes, tolerances, and non-automated limitations |
| `tools/goldenoracle/goldenoracle.csproj` | Create | Internal raw-event cross-check tool |
| `tools/goldenoracle/Program.cs` | Create | Internal raw-event manifest/capability/thread/window cross-check |
| `scripts/Update-GoldenEvidence.ps1` | Create | Atomic hash/oracle/snapshot candidate generation |
| `benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj` | Create | Executable S01–S10 runner |
| `benchmarks/WprMcp.AgentBenchmarks/Program.cs` | Create | `run`, `verify`, `compare`, and `freeze-baseline` verbs |
| `benchmarks/WprMcp.AgentBenchmarks/AgentBenchmarkRunner.cs` | Create | Full/tools-only MCP/model loop and trial retention |
| `benchmarks/WprMcp.AgentBenchmarks/ResponsesApiModelClient.cs` | Create | Raw HTTP model adapter with required snapshot |
| `benchmarks/WprMcp.AgentBenchmarks/EvidenceVerifiers.cs` | Create | S01–S10 and S03-thread-window deterministic verifiers |
| `benchmarks/WprMcp.AgentBenchmarks/BenchmarkMetrics.cs` | Create | Fixed metric formulas/comparability/promotion |
| `benchmarks/agent/scenarios.v1.json` | Create | Canonical S01–S10 prompts, fixtures, allowed/irrelevant tools |
| `benchmarks/agent/prompts/system-v1.md` | Create | Frozen agent system prompt |
| `benchmarks/agent/model-config.v1.json` | Create from an observed baseline run | Exact provider endpoint/snapshot/support flags |
| `benchmarks/agent/policy.v1.json` | Create | Frozen thresholds and privacy/unavailability rules |
| `benchmarks/agent/baseline/manifest.json` | Create from observed trials | Comparable dimensions, aggregate metrics, trial paths |
| `benchmarks/agent/baseline/trials/*.json` | Create from observed trials | Every retained trial, never only averages |
| `benchmarks/agent/waiver.schema.json` | Create | Exact fail-closed waiver shape |
| `benchmarks/capability-matrix.v1.json` | Create | Supported/preview/gap claim-to-evidence registry |
| `tests/WprMcp.GoldenTests/AgentVerifierTests.cs` | Create | Offline verifier/metric/policy tests |
| `tests/WprMcp.GoldenTests/CapabilityMatrixTests.cs` | Create | Every claim links to executable evidence |
| `scripts/Run-AgentBenchmarks.ps1` | Create | Scheduled/release command wrapper |
| `scripts/Test-AgentBenchmarkPolicy.ps1` | Create | Compare current evidence/policy/baseline/waiver |
| `docs/MCP_MEASUREMENT_BASELINE.md` | Modify | Point narrative definitions to executable sources |
| `WprMcp.sln` | Modify | Add golden, oracle, and benchmark projects |

---

### Task 1: Create strict fixture, external-parity, and internal-cross-check manifests (TDD)

**Files:**
- Create: `tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj`
- Create: `tests/WprMcp.GoldenTests/golden-manifest.v1.json`
- Create: `tests/WprMcp.GoldenTests/external-oracles.v1.json` from real export candidates
- Create: `tests/WprMcp.GoldenTests/ExternalOracles/wpaexporter-thread-scope-cpu-samples.v1.json` from a real WPAExporter export
- Create: `tests/WprMcp.GoldenTests/ExternalOracles/perfview-thread-scope-cswitch-time.v1.json` from a real PerfView export
- Create: `tests/WprMcp.GoldenTests/ExternalOracles/Reviews/external-oracle-review.v1.md` from the review of those exports
- Create: `tests/WprMcp.GoldenTests/GoldenManifest.cs`
- Create: `tests/WprMcp.GoldenTests/ExternalOracle.cs`
- Create: `tests/WprMcp.GoldenTests/FixtureIntegrityTests.cs`
- Create: `tests/WprMcp.GoldenTests/ExternalOracleTests.cs`
- Create: `tools/goldenoracle/goldenoracle.csproj`
- Create: `tools/goldenoracle/Program.cs`
- Create: `scripts/Update-GoldenEvidence.ps1` with hash, raw-cross-check, and external-oracle modes
- Modify: `WprMcp.sln`

- [ ] **Step 1: Write failing integrity tests**

Add exact tests:

```text
Manifest_DeserializesWithNoUnknownOrMissingProperties
Manifest_ProfileIsExactlyV2StrictIdOnlyAndHashMatchesCanonicalBytes
QueryCases_UseTraceIdSentinelAndNeverContainRawFixturePaths
RequiredFixtures_ExistAndMatchUppercaseSha256
EveryFixture_HasProvenanceCaptureRecipeOracleVersionAndCapabilities
ExternalModelFixtures_ArePublicOrSyntheticAndRequireStrictPrivacy
ThreadFixture_ImportsChild9HashAndIdentityWithoutDuplication
EverySupportedTool_HasAtLeastOneGoldenCase
NumericTolerances_AreFiniteNonNegativeAndFieldSpecific
ExternalOracle_RecordsExactToolVersionCommandSourceAndExportHashes
ExternalOracle_AutomatedArtifactsParseAndCompareWithinReviewedTolerance
ExternalOracle_HumanReviewCannotSatisfyDeterministicSupportedClaim
RawTraceEventOracle_IsMarkedInternalCrossCheckNotExternalParity
```

The supported-tool coverage test joins the real v2 `tools/list` surface to manifest cases and the capability matrix; preview/gap tools are excluded only through an explicit matrix status.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~FixtureIntegrityTests
```

Expected: project and manifest are absent.

- [ ] **Step 3: Implement external parity and the internal raw-event cross-check**

Use `JsonUnmappedMemberHandling.Disallow`; resolve fixture/export paths relative to repository root; stream SHA-256; reject duplicate fixture/case/oracle IDs, relative-path escape, missing snapshots/exports, unknown capabilities, empty oracle fields, a noncanonical server profile/hash, a query argument containing anything other than `$traceId` for `path`, or blanket tolerance paths such as `/data`. Only `load_trace` may carry `$fixturePath`. Generate the fixture-manifest candidate from the current committed bytes with `Get-FileHash`; review it before copying. Tests bind manifest hashes to the current bytes, so a fixture edit fails until a separately reviewed candidate is accepted.

Create the script with this initial exact interface; Task 2 extends only the `Mode` set:

```powershell
param(
    [ValidateSet('hashes','raw-cross-checks','external-oracles')][string]$Mode = 'hashes',
    [string]$CandidateDirectory = 'artifacts/golden-candidates',
    [string]$ExternalOracleCommandRegistry = 'artifacts/external-oracle-commands.v1.json'
)
```

The local command registry is required for `external-oracles`, is never committed, and contains the reviewed absolute exporter executable, expected file version/hash, and complete argument tokens for the two named exports. The script rejects an absent registry, an executable mismatch, or tokens that do not target the committed `thread-scope.etl` and candidate directory.

`goldenoracle` has exact CLI verbs:

```text
goldenoracle inspect <trace.etl>
goldenoracle thread-window <trace.etl> <pid> <tid> <processStartUs> <threadStartUs> <startUs> <endUs>
goldenoracle fixture-record <trace.etl> <capture-recipe> <oracle-source> <oracle-version>
```

It walks raw TraceEvent events separately from MCP analyzer code and emits canonical JSON for event counts, capabilities, process/thread lifetimes, CPU samples, clipped CSwitch durations, wait reasons, and stack presence. This is an internal cross-check sharing TraceEvent, not an external parity oracle. Its version is the SHA-256 of the tool source plus pinned TraceEvent version, generated rather than typed manually.

Run real WPAExporter and PerfView commands against Child 9's committed `thread-scope.etl`, normalize their CPU-sample and context-switch-time exports into `wpaexporter-thread-scope-cpu-samples.v1.json` and `perfview-thread-scope-cswitch-time.v1.json`, and record both artifacts in `external-oracles.v1.json`. Capture executable file version and SHA-256, complete command tokens, exit code, stdout/stderr hashes, source fixture hash, export hash, metric/unit, and reviewed tolerance. Record the operator, UTC review time, exact artifact IDs, hash checks, tolerance rationale, and any human-only limitation in `ExternalOracles/Reviews/external-oracle-review.v1.md`; no pass, reviewer, time, hash, or tolerance is prefilled. The manifest parser rejects the words `current`, `latest`, or an unversioned executable. For every other WPA/PerfView-expressible supported domain, retain the real normalized export under `ExternalOracles` before claiming support. If automation cannot parse an export, record it as `HumanReviewed`, point `ReviewEvidencePath` at the checked-in review file, and mark the capability `preview`, not `supported`.

Generate candidates before copying reviewed bytes into the repository:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Update-GoldenEvidence.ps1 -Mode hashes -CandidateDirectory artifacts/golden-manifest-candidates
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Update-GoldenEvidence.ps1 -Mode external-oracles -CandidateDirectory artifacts/golden-external-candidates
```

The second command fails if either registered real exporter cannot run, if its executable version/hash changes, or if the source/export/review hashes cannot be recorded. Review the candidate directory, then copy only the two named normalized exports, the review record, and both manifests to their exact repository paths.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter "FullyQualifiedName~FixtureIntegrityTests|FullyQualifiedName~ExternalOracleTests"
```

Expected: all integrity tests pass with hashes computed from the currently committed bytes, the imported Child 9 hash, and real external export artifacts.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj tests/WprMcp.GoldenTests/golden-manifest.v1.json tests/WprMcp.GoldenTests/external-oracles.v1.json tests/WprMcp.GoldenTests/ExternalOracles/wpaexporter-thread-scope-cpu-samples.v1.json tests/WprMcp.GoldenTests/ExternalOracles/perfview-thread-scope-cswitch-time.v1.json tests/WprMcp.GoldenTests/ExternalOracles/Reviews/external-oracle-review.v1.md tests/WprMcp.GoldenTests/GoldenManifest.cs tests/WprMcp.GoldenTests/ExternalOracle.cs tests/WprMcp.GoldenTests/FixtureIntegrityTests.cs tests/WprMcp.GoldenTests/ExternalOracleTests.cs tools/goldenoracle/goldenoracle.csproj tools/goldenoracle/Program.cs scripts/Update-GoldenEvidence.ps1 WprMcp.sln
git commit -m "test(golden): gate external parity and internal cross-check evidence"
```

---

### Task 2: Add whitelist normalization and reviewed golden snapshots (TDD)

**Files:**
- Create: `tests/WprMcp.GoldenTests/GoldenNormalizer.cs`
- Create: `tests/WprMcp.GoldenTests/GoldenSnapshotTests.cs`
- Create: `tests/WprMcp.GoldenTests/Snapshots/*.json`
- Create: `tests/WprMcp.GoldenTests/ExternalOracles/*.json` from real exporter candidates
- Modify: `scripts/Update-GoldenEvidence.ps1` to add snapshot/all modes and reviewed-diff output
- Modify: `tests/WprMcp.GoldenTests/golden-manifest.v1.json`
- Modify: `tests/WprMcp.GoldenTests/external-oracles.v1.json`

- [ ] **Step 1: Write failing normalizer/snapshot tests**

Add:

```text
Normalizer_RemovesOnlyManifestWhitelistedPointers
Normalizer_MapsProcessScopedAliasesByFirstOccurrence
Normalizer_NeverRemovesIdentityWindowTotalsQualityOrCompletion
Normalizer_SortsOnlyArraysWithDeclaredStableKeys
EveryGoldenCase_RealV2ResultMatchesReviewedSnapshot
EverySnapshot_ValidatesAgainstItsRealAdvertisedOutputSchema
SnapshotHarness_LoadsEachFixtureOnceAndQueriesOnlyByTraceId
ExplicitProfileHash_IsStableAcrossHarnessRuns
ExternalExportCandidates_NeverOverwriteReviewedArtifacts
SnapshotUpdate_RefusesDirtyTreeAndWritesCandidatesNotBaselines
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter "FullyQualifiedName~GoldenNormalizer|FullyQualifiedName~GoldenSnapshotTests"
```

Expected: normalizer, update script, and snapshots do not exist.

- [ ] **Step 3: Implement canonicalization and candidate generation**

`GoldenNormalizer.Normalize(JsonNode, GoldenNormalization)` deep-clones input, validates every pointer exists, maps process-random aliases to `alias_001`, `alias_002` in encounter order only at declared pointers, removes declared transport/session timestamps, sorts declared arrays by the complete key tuple, and writes indented UTF-8 with LF and sorted object properties. It throws if a normalization rule touches `pid`, `tid`, `processStartUs`, `threadStartUs`, `startUs`, `endUs`, any `*Us` total, `status`, `error`, `failedSections`, `sections`, `hasMore`, `coverage`, or symbol/thread quality.

`Update-GoldenEvidence.ps1` has this exact interface:

```powershell
param(
    [ValidateSet('hashes','raw-cross-checks','external-oracles','snapshots','all')][string]$Mode = 'all',
    [string]$CandidateDirectory = 'artifacts/golden-candidates',
    [string]$ExternalOracleCommandRegistry = 'artifacts/external-oracle-commands.v1.json'
)
```

It requires a clean tracked tree, builds Release once, launches the real server with exact arguments `--output-contract v2 --privacy strict --trace-reference-mode id-only`, and verifies the observed profile hash before any case. In one stdio session per fixture it invokes `load_trace` exactly once with the hash-bound fixture path, keeps the returned ID only in memory, clones each case's arguments, replaces `$traceId`, and rejects any query that still contains a raw path/`$fixturePath`. It writes candidates under the artifact directory and prints per-file diffs/hashes. External-oracle mode invokes only the exact exporter tool/version/command registered by the operator, captures exit/log/source/export hashes, and writes a manifest candidate. It never overwrites `Snapshots`, `ExternalOracles`, or either manifest. The implementer manually reviews and copies exact candidates before committing.

- [ ] **Step 4: Generate, review, and run GREEN**

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Update-GoldenEvidence.ps1 -Mode all
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter "FullyQualifiedName~GoldenNormalizer|FullyQualifiedName~GoldenSnapshotTests"
```

Expected: candidates contain no privacy sentinel or absolute repository path; after reviewed copies are committed, all snapshots match and validate.

- [ ] **Step 5: Commit**

```powershell
$goldenManifest = Get-Content tests/WprMcp.GoldenTests/golden-manifest.v1.json -Raw | ConvertFrom-Json
$externalManifest = Get-Content tests/WprMcp.GoldenTests/external-oracles.v1.json -Raw | ConvertFrom-Json
$reviewedEvidencePaths = @($goldenManifest.cases.snapshotPath) + @($externalManifest.artifacts.exportPath) + @($externalManifest.artifacts.reviewEvidencePath | Where-Object { $_ })
git add -- tests/WprMcp.GoldenTests/GoldenNormalizer.cs tests/WprMcp.GoldenTests/GoldenSnapshotTests.cs tests/WprMcp.GoldenTests/golden-manifest.v1.json tests/WprMcp.GoldenTests/external-oracles.v1.json scripts/Update-GoldenEvidence.ps1
git add -- $reviewedEvidencePaths
git commit -m "test(golden): add schema-valid normalized MCP snapshots"
```

The just-passed manifest tests prove every staged evidence path is repository-relative, exists, is hash-bound, and cannot escape `Snapshots` or `ExternalOracles`; do not replace the manifest-derived list with directory-wide staging.

---

### Task 3: Enforce cross-tool identity, window, unit, total, coverage, symbol, and completion invariants (TDD)

**Files:**
- Create: `tests/WprMcp.GoldenTests/CrossToolInvariantTests.cs`
- Modify: `tests/WprMcp.GoldenTests/golden-manifest.v1.json`

- [ ] **Step 1: Write the invariant tests**

Add exact tests:

```text
ProcessInstanceKeys_AgreeAcrossListSummaryStacksAndCallerCallee
ThreadInstanceKeys_AgreeAcrossSixThreadTools
HalfOpenWindows_ExcludeEndPointAcrossEveryWindowedTool
AllDurationsAndTimestamps_AreIntegerMicroseconds
WaitSummaryStacksCallerCalleeAndBuckets_ShareTotalBlockedUs
CpuSummaryStacksAndCallerCallee_ShareFilteredSampleOrDurationTotal
TopN_ChangesRowsAndHasMoreButNeverTotalsOrCompletionStatus
DomainCoverage_AgreesWithInspectAndDomainToolEvidence
PdbIdentityAndFrameResolution_DoNotContradictEachOther
PartialFailedAndTruncatedStates_ObeyEnvelopeInvariantsAcrossTools
AutomatedExternalOracle_MatchesSupportedDomainMetricWithinTolerance
```

- [ ] **Step 2: Run RED against the assembled implementation**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~CrossToolInvariantTests
```

Expected: any remaining cross-tool disagreement fails with the two call IDs, selectors, and JSON pointers involved.

- [ ] **Step 3: Fix source defects, not expected values**

Use one explicit v2/strict/ID-only stdio session per fixture, invoke `load_trace` once, and use only its invocation-local trace ID so identities/aliases remain scoped. For wait totals, compare `wait_analysis`, `wait_top_stacks`, `wait_caller_callee`, and sum of every `when` bucket on the same resolved lifetime/window. For CPU, compare sampled tools by samples and precise CPU by microseconds; never compare unlike metrics. Every assertion states its accounting mode. Separately compare each `Automated` WPAExporter/PerfView artifact to the matching MCP domain metric/unit with its reviewed absolute/relative tolerance; never route that comparison through `goldenoracle`.

If a failure reveals a domain bug, fix the owning Child 2–5 source and add its unit regression there before updating any snapshot. Do not increase a tolerance for an exact integer invariant.

- [ ] **Step 4: Run GREEN**

Run the focused command again, then `GoldenSnapshotTests`. Expected: both invariant and snapshots pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.GoldenTests/CrossToolInvariantTests.cs tests/WprMcp.GoldenTests/golden-manifest.v1.json tests/WprMcp.GoldenTests/external-oracles.v1.json
git commit -m "test(parity): enforce cross-tool identity totals and quality invariants"
```

If an invariant exposes a source defect, stop this task, add a focused failing unit test in the owning Child 2–5 file, make and commit that exact fix separately, then rerun this task. Do not hide source changes inside the golden commit.

---

### Task 4: Add the S03 same-thread fast/slow evidence verifier (TDD)

**Files:**
- Create: `tests/WprMcp.GoldenTests/ThreadWindowInvariantTests.cs`
- Modify: `tests/WprMcp.GoldenTests/golden-manifest.v1.json`
- Modify: `benchmarks/agent/scenarios.v1.json` when Task 5 creates it

- [ ] **Step 1: Write failing fast/slow tests**

Add:

```text
FastAndSlowWindows_ResolveTheSameProcessAndThreadInstance
FastWindow_HasMoreCpuThanSlowWindow
SlowWindow_HasMoreBlockedTimeThanFastWindow
SlowWindow_ReportsExpectedDelayWaitReason
BothWindows_StackAttributionIsReadableOrExplicitlyDegraded
SymbolDegradation_NeverChangesSelectorCpuBlockedOrWaitReasonEvidence
RequestedThread_BelowProcessTopOneAppearsInBothWindows
```

Read PID/TID/lifetime/windows only from Child 9's manifest. Analyze all six thread tools with `top=1` and the same selector.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~ThreadWindowInvariantTests
```

Expected: missing verifier/test definitions or a real thread-scoping defect.

- [ ] **Step 3: Implement the exact evidence rules**

The fast window passes when its selected thread's sampled/precise CPU exceeds the slow window's corresponding metric. The slow window passes when `TotalBlockedUs` exceeds fast and includes the fixture-recorded delay reason. The verifier accepts stack attribution in one of three explicit states: resolved names with positive resolved frames; address/`module!?` rows with positive unresolved frames; or `NoStacks` with duration evidence preserved and a `not_concluded` stack claim. It fails an empty successful stack result that claims complete coverage. It compares symbol-on/off calls and requires identical identity, CPU/blocked totals, and wait reasons.

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: all seven tests pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.GoldenTests/ThreadWindowInvariantTests.cs tests/WprMcp.GoldenTests/golden-manifest.v1.json
git commit -m "test(thread): compare fast and slow windows on one resolved thread"
```

---

### Task 5: Make S01–S10 executable in full and tools-only modes (TDD)

**Files:**
- Create: `benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj`
- Create: `benchmarks/WprMcp.AgentBenchmarks/Program.cs`
- Create: `benchmarks/WprMcp.AgentBenchmarks/AgentBenchmarkRunner.cs`
- Create: `benchmarks/WprMcp.AgentBenchmarks/EvidenceVerifiers.cs`
- Create: `benchmarks/agent/scenarios.v1.json`
- Create: `benchmarks/agent/prompts/system-v1.md`
- Create: `tests/WprMcp.GoldenTests/AgentVerifierTests.cs`
- Modify: `WprMcp.sln`

- [ ] **Step 1: Write failing scenario/verifier tests**

Add:

```text
ScenarioFile_ContainsExactlyS01ThroughS10AndS03ThreadWindowVariant
EveryScenario_HasFullAndToolsOnlyDefinitionsAndDeterministicVerifier
UnknownTraceScenarios_RequireInspectTraceWithinFirstThreeCalls
UnsupportedOrInsufficientEvidence_CannotPassWithRootCauseClaim
S03ThreadWindowVerifier_ChecksCpuBlockedWaitReasonAndStackEvidence
ToolsOnlyMode_NeverOffersResourcesOrPrompts
FullMode_OffersOnlyCapabilitiesActuallyAdvertisedByServer
Runner_ProfileIsExplicitV2StrictIdOnlyAndHashBound
Runner_LoadsFixtureOnceAndNeverOffersRawPathToModelOrQueryTools
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
```

Expected: benchmark project, scenarios, and verifiers are absent.

- [ ] **Step 3: Implement executable scenario flow**

Encode the existing S01–S10 goals and acceptable/irrelevant tool families from `docs/MCP_MEASUREMENT_BASELINE.md`. Each scenario declares prompt, fixture, whether trace/capabilities are initially unknown, expected evidence JSON pointers, acceptable tool set, irrelevant tool set, allowed conclusion labels, and variants. `S03-thread-window` points at Child 9's fixture and exact manifest selectors/windows.

The runner starts a real server with `--output-contract v2 --privacy strict --trace-reference-mode id-only`, verifies the canonical profile hash, loads the scenario's hash-bound fixture exactly once, and keeps its trace ID in invocation-local state. Scenario prompts and initial context receive the opaque trace ID, never a fixture path; every model-requested query argument is rejected unless its `path` equals that ID. The runner collects the actual paged `tools/list` without deleting `load_trace`; because no raw fixture path is exposed, an attempted redundant load is recorded as an irrelevant/invalid call rather than receiving hidden setup data. Full mode additionally presents only resources/prompts actually returned by the selected protocol; tools-only mode removes both. For every requested call, it validates the name/arguments, calls MCP, records the complete redacted result, and continues until final answer or a policy maximum of 20 calls. A call is relevant only when the scenario's deterministic rule says so; alternative acceptable paths are not penalized.

`Program` exposes exact verbs:

```text
WprMcp.AgentBenchmarks run --scenarios <json> --model-config <json> --policy <json> --mode full|tools-only --trials <n> --output <dir> --temperature <double> [--seed <int>]
WprMcp.AgentBenchmarks verify --trials <dir> --scenarios <json> --policy <json>
WprMcp.AgentBenchmarks compare --candidate <dir> --baseline <manifest> --policy <json>
WprMcp.AgentBenchmarks freeze-baseline --trials <dir> --output <baseline-dir>
```

- [ ] **Step 4: Run GREEN offline**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
```

Expected: scenario parsing and deterministic synthetic transcript tests pass without network access.

- [ ] **Step 5: Commit**

```powershell
git add benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj benchmarks/WprMcp.AgentBenchmarks/Program.cs benchmarks/WprMcp.AgentBenchmarks/AgentBenchmarkRunner.cs benchmarks/WprMcp.AgentBenchmarks/EvidenceVerifiers.cs benchmarks/agent/scenarios.v1.json benchmarks/agent/prompts/system-v1.md tests/WprMcp.GoldenTests/AgentVerifierTests.cs WprMcp.sln
git commit -m "feat(benchmarks): make S01-S10 executable with evidence verifiers"
```

---

### Task 6: Add a pinned model adapter and retain complete provenance for every trial (TDD)

**Files:**
- Create: `benchmarks/WprMcp.AgentBenchmarks/ResponsesApiModelClient.cs`
- Create: `benchmarks/agent/model-config.v1.json` from the first observed baseline run
- Create: `scripts/Run-AgentBenchmarks.ps1`
- Modify: `benchmarks/WprMcp.AgentBenchmarks/Program.cs`
- Modify: `benchmarks/WprMcp.AgentBenchmarks/AgentBenchmarkRunner.cs`
- Modify: `tests/WprMcp.GoldenTests/AgentVerifierTests.cs`

- [ ] **Step 1: Write failing provenance/client tests**

Add:

```text
ModelConfig_RequiresImmutableSnapshotAndHttpsEndpoint
ModelProbe_UsesReturnedSnapshotAndRejectsMutableOrMissingIdentity
ModelClient_SendsExactSnapshotToolsTemperatureAndSupportedSeed
Trial_RetainsPromptSchemaServerProfileFixtureRunnerCommitAndProviderDimensions
Trial_RetainsEveryToolCallAndFinalAnswer
TrialWriter_UsesCreateNewAndCannotOverwritePriorEvidence
StrictPrivacy_IsRequiredBeforeExternalModelCall
```

Use a local fake `HttpMessageHandler`; no test calls the external endpoint.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
```

Expected: client/config/provenance implementation is absent.

- [ ] **Step 3: Implement the adapter and run wrapper**

`ResponsesApiModelClient` posts to the checked-in HTTPS endpoint with bearer token read only from `WPRMCP_AGENT_API_KEY`; the token is never written to trials/logs. The checked-in config must name an immutable provider snapshot and explicit support flags. Extend `Program` with `probe-model --endpoint <https-uri> --requested-model <provider-model-id> --output <candidate-json>`. The probe sends one minimal authorized request, requires the response metadata to return an immutable snapshot identity, probes temperature and seed support without treating rejected options as success, and writes a candidate with `FileMode.CreateNew`. It fails if the provider returns only the mutable requested alias. The operator verifies the candidate before copying it to `benchmarks/agent/model-config.v1.json`; do not invent a snapshot string.

`Run-AgentBenchmarks.ps1` has:

```powershell
param(
    [ValidateSet('full','tools-only','both')][string]$Mode = 'both',
    [ValidateRange(5,100)][int]$Trials = 5,
    [string]$OutputDirectory = 'artifacts/agent-benchmarks',
    [string]$ModelConfig = 'benchmarks/agent/model-config.v1.json',
    [double]$Temperature = 0,
    [Nullable[int]]$Seed = 0
)
```

It verifies fixture hashes, the canonical v2/strict/ID-only profile hash, and a clean commit, builds once, runs requested modes with the exact supplied model config, and invokes `verify`. Every trial records the same `ServerProfileSha256`; verification rejects a missing/different profile or any transcript query containing a raw fixture path. Each trial path consists of commit, scenario, variant, mode, and trial-number segments beneath the output directory; `FileMode.CreateNew` prevents evidence replacement.

- [ ] **Step 4: Run GREEN offline, then one authorized live smoke**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- probe-model --endpoint $env:WPRMCP_AGENT_ENDPOINT --requested-model $env:WPRMCP_AGENT_REQUESTED_MODEL --output artifacts/model-config.candidate.json
Copy-Item -LiteralPath artifacts/model-config.candidate.json -Destination benchmarks/agent/model-config.v1.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-AgentBenchmarks.ps1 -Mode full -Trials 5 -OutputDirectory artifacts/agent-smoke
```

The probe and benchmark commands require a real API key; the operator inspects the candidate before the explicit copy. Record their real results. If the service is unavailable or the provider returns no immutable snapshot identity, do not create the checked-in config or claim the live smoke passed.

- [ ] **Step 5: Commit**

```powershell
git add benchmarks/WprMcp.AgentBenchmarks/ResponsesApiModelClient.cs benchmarks/WprMcp.AgentBenchmarks/Program.cs benchmarks/WprMcp.AgentBenchmarks/AgentBenchmarkRunner.cs benchmarks/agent/model-config.v1.json scripts/Run-AgentBenchmarks.ps1 tests/WprMcp.GoldenTests/AgentVerifierTests.cs
git commit -m "feat(benchmarks): pin model dimensions and retain every trial"
```

---

### Task 7: Freeze metric, comparability, promotion, and waiver policy (TDD)

**Files:**
- Create: `benchmarks/WprMcp.AgentBenchmarks/BenchmarkMetrics.cs`
- Create: `benchmarks/agent/policy.v1.json`
- Create: `benchmarks/agent/waiver.schema.json`
- Create: `scripts/Test-AgentBenchmarkPolicy.ps1`
- Modify: `tests/WprMcp.GoldenTests/AgentVerifierTests.cs`

- [ ] **Step 1: Write failing metric/policy tests**

Add:

```text
StructuredParseSuccess_IsParsedOverAttemptedAndMustBeOne
WrongToolRate_IsIrrelevantCallsOverAllCalls
MeanToolCalls_UsesVerifierSuccessfulRunsOnly
ComparableBaseline_RequiresMatchingCommitIndependentDimensions
Promotion_RequiresTenPercentCallReductionWithoutAccuracyLoss
WrongToolRegression_AtTwoPointsPassesAboveTwoPointsFails
PolicyChangeAndFailingEvidence_CannotOccurInSameChange
UnavailableService_FailsClosedWithoutValidWaiver
Waiver_RequiresReasonApproverExpiryCommitAndExactOmittedArtifact
ExpiredOrMismatchedWaiver_Fails
EveryTrial_IsRetainedAndMinimumFivePerScenarioMode
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
```

Expected: metric/policy implementation is absent.

- [ ] **Step 3: Implement frozen policy**

`policy.v1.json` contains the numeric thresholds fixed above, maximum 20 tool calls, `minimumTrialsPerScenarioMode=5`, `unavailableService="fail"`, `requiredPrivacyMode="strict"`, the canonical v2/strict/ID-only `requiredServerProfileSha256`, supported conclusion thresholds, and overclaim ceilings. `BenchmarkMetrics` rejects NaN/infinite/missing denominators rather than treating them as zero and rejects comparison before scoring when the server-profile hash differs.

`Test-AgentBenchmarkPolicy.ps1` has:

```powershell
param(
    [Parameter(Mandatory)][string]$CandidateDirectory,
    [string]$BaselineManifest = 'benchmarks/agent/baseline/manifest.json',
    [string]$Policy = 'benchmarks/agent/policy.v1.json',
    [string]$Waiver
)
```

It validates schemas, exact commit and evidence hashes, compares policy SHA with the merge-base copy, invokes `verify` then `compare`, and accepts a waiver only for external-service unavailability and only for the named omitted artifact. A waiver cannot change verifier output or metric thresholds.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter FullyQualifiedName~AgentVerifierTests
```

Expected: all policy tests pass.

- [ ] **Step 5: Commit**

```powershell
git add benchmarks/WprMcp.AgentBenchmarks/BenchmarkMetrics.cs benchmarks/agent/policy.v1.json benchmarks/agent/waiver.schema.json scripts/Test-AgentBenchmarkPolicy.ps1 tests/WprMcp.GoldenTests/AgentVerifierTests.cs
git commit -m "test(benchmarks): freeze metrics promotion and fail-closed policy"
```

---

### Task 8: Record a real baseline and tie every parity claim to evidence (TDD)

**Files:**
- Create: `benchmarks/agent/baseline/manifest.json` from observed trials
- Create: `benchmarks/agent/baseline/trials/*.json` from observed trials
- Create: `benchmarks/capability-matrix.v1.json`
- Create: `tests/WprMcp.GoldenTests/CapabilityMatrixTests.cs`
- Modify: `docs/MCP_MEASUREMENT_BASELINE.md`

- [ ] **Step 1: Write failing matrix/baseline tests**

Add:

```text
Baseline_HasFiveTrialsForEveryS01ThroughS10ModeAndVariant
Baseline_ManifestHashesEveryRetainedTrial
Baseline_DimensionsMatchModelPromptSchemaServerProfileFixtureAndRunnerArtifacts
CapabilityMatrix_UsesOnlySupportedPreviewOrGap
SupportedClaims_LinkToPassingGoldenInvariantOrBenchmarkEvidence
WpaPerfViewExpressibleSupportedClaims_LinkToAutomatedExternalOracle
PreviewClaims_LinkToExecutableEvidenceAndDocumentLimitation
GapClaims_AreNeverScoredAsImplementedParity
DocumentationClaims_ResolveToMatrixIds
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --filter "FullyQualifiedName~CapabilityMatrixTests|FullyQualifiedName~Baseline"
```

Expected: no baseline/matrix exists.

- [ ] **Step 3: Produce and review actual evidence**

Run both modes with at least five trials, verify them, then freeze:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-AgentBenchmarks.ps1 -Mode both -Trials 5 -OutputDirectory artifacts/agent-baseline
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- freeze-baseline --trials artifacts/agent-baseline --output artifacts/frozen-baseline
```

Review every verifier failure, conclusion, and tool-relevance decision. Copy the generated manifest/trials only after review. Never edit failed trials into passing results; fix verifier/tool behavior and rerun with new immutable trial paths.

Create the capability matrix with stable claim IDs, status, scope, limitations, and concrete evidence references of the form test fully-qualified name, golden case ID, external-oracle artifact ID, or benchmark scenario/verifier ID. A `supported` claim for a WPA/PerfView-expressible metric must name an `Automated` external artifact. A `HumanReviewed` artifact supports only `preview`; missing external evidence is `preview` or `gap`. Update `docs/MCP_MEASUREMENT_BASELINE.md` to point S01–S10, formulas, and baseline rules to the executable files, retaining the narrative table as orientation.

- [ ] **Step 4: Run GREEN and compare baseline to itself**

```powershell
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- compare --candidate benchmarks/agent/baseline/trials --baseline benchmarks/agent/baseline/manifest.json --policy benchmarks/agent/policy.v1.json
```

Expected: deterministic suite passes and the baseline is comparable to itself with zero regression.

- [ ] **Step 5: Commit**

```powershell
$baselineManifest = Get-Content benchmarks/agent/baseline/manifest.json -Raw | ConvertFrom-Json
$reviewedTrialPaths = @($baselineManifest.trials.path)
git add -- benchmarks/agent/baseline/manifest.json benchmarks/capability-matrix.v1.json tests/WprMcp.GoldenTests/CapabilityMatrixTests.cs docs/MCP_MEASUREMENT_BASELINE.md
git add -- $reviewedTrialPaths
git commit -m "test(parity): freeze reviewed S01-S10 baseline and claim evidence"
```

The baseline tests prove every staged trial path is repository-relative, exists, hashes to the manifest value, and is under `benchmarks/agent/baseline/trials`; do not stage the whole baseline directory.

---

## Acceptance commands

Run from `D:\wpa-mcp`:

```powershell
dotnet restore WprMcp.sln --locked-mode
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --no-build
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "Category!=Package"
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- verify --trials benchmarks/agent/baseline/trials --scenarios benchmarks/agent/scenarios.v1.json --policy benchmarks/agent/policy.v1.json
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- compare --candidate benchmarks/agent/baseline/trials --baseline benchmarks/agent/baseline/manifest.json --policy benchmarks/agent/policy.v1.json
git diff --check
```

Expected: deterministic tests, retained-baseline verification, and self-comparison pass. A live model run is an additional scheduled/release command and must report its actual result:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-AgentBenchmarks.ps1 -Mode both -Trials 5 -OutputDirectory artifacts/agent-candidate
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-AgentBenchmarkPolicy.ps1 -CandidateDirectory artifacts/agent-candidate
```

If the service is unavailable, the second command fails unless an actually approved, non-expired waiver is supplied. Do not report success without one.

---

## Dependencies and conflict ownership

- Child 9 owns `StdioMcpClient` and `thread-scope.etl`; this plan references them and must not copy or rewrite their identities.
- Children 2–5 own analyzer/contract defects found by invariant tests. Fix the defect with a unit regression in the owning child area before accepting a new golden snapshot.
- Child 11B wires deterministic golden/invariant commands into the reusable quality workflow and live agent commands into scheduled/release workflows. This plan creates commands and policy, not workflow claims.
- `docs/MCP_MEASUREMENT_BASELINE.md` is shared with Child 11 documentation. Land Task 8 before Child 11B's broader rewrite and preserve executable links during conflict resolution.
- Agent baseline artifacts are intentionally commit-specific but comparability excludes only the commit dimension. Model snapshot, prompt hash, tool-schema hash, canonical server-profile hash, fixture hashes, runner version, temperature, and supported seed must match.
- A later model, prompt, schema, fixture, or runner change creates a new baseline directory/manifest; it does not overwrite historical trials.

## Final evidence checklist

- Every required fixture exists, has a verified SHA-256, provenance, capture recipe, expected capabilities, oracle source/version, normalization, and tolerances.
- Real WPAExporter/PerfView artifacts, their executable versions and commands, hashes, reviewed tolerances, and human review record are checked in; raw TraceEvent evidence is labeled only as an internal cross-check.
- Every supported tool has schema-valid reviewed golden evidence; cross-tool identity/window/unit/total/coverage/symbol/completion invariants pass.
- `S03-thread-window` proves fast versus slow CPU, blocked duration, wait reason, and readable-or-explicitly-degraded stack attribution on the same `(pid,tid,lifetime)` and below TopN.
- S01–S10 run in full and tools-only modes; unknown traces inspect within three calls.
- Deterministic verifier labels, not model self-report, decide success/conclusion/overclaim.
- Structured parse success is 100%; wrong-tool and call-count formulas use the fixed denominators; all trials are retained.
- Supported/preview/gap claims link to executable evidence, and gaps never score as parity.
- Any live benchmark, baseline review, accuracy exception, or waiver in the record corresponds to an action and result that actually occurred.
