# Platform Protocol Release and Documentation Governance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the TFM, .NET SDK, MCP SDK/protocol, dependency graph, CI gates, release bytes, provenance, compatibility claims, and documentation reproducible and evidence-backed from development through GitHub Release.

**Architecture:** Child 11A runs before analyzer/runtime remediation. It defines three exact candidates, executes normal and `win-x64` proof matrices, freezes one observed decision in an ADR, then pins SDK/packages/locks/actions and establishes an early reusable workflow. Child 11B runs only after Children 2–10: it first performs the reviewed secure-default transition to v2 output and ID-only query references while retaining explicit legacy/compatibility switches, composes every deterministic and release benchmark gate, publishes once into one immutable zip, smokes that zip, uploads it as a workflow artifact, and has the tag release job download/verify/attest/upload those same bytes without rebuilding or recompressing.

**Tech Stack:** PowerShell; C#/.NET; NuGet lock files and central package management; Model Context Protocol C# SDK; GitHub Actions on `windows-latest`; GitHub artifact attestation and `gh` CLI; xUnit protocol/golden/benchmark suites produced by Children 5, 9, and 10.

## Accepted capability/evidence amendment (2026-08-01)

Release provenance now includes hashes of the single active tool catalog, complete capability manifest, and every advertised input/output schema set for each public contract profile. Packaging and tag promotion must fail when production registration, `tools/list`, capability-map pages, committed snapshots, or recorded hashes diverge.

The release claim gate also fails closed on capability overclaim: `supported` requires the checked-in capture/analyzer/environment/scope evidence and adversarial results owned by Children 9–10; otherwise the claim is `preview` or `gap`. Hash changes, capability promotions, and waivers are reviewed versioned evidence and cannot be regenerated or relaxed inside the release job merely to pass.

## Global Constraints

- Execute Tasks 1–4 (11A) before Child 1 or any runtime/analyzer implementation relies on a TFM, SDK, protocol handshake, cancellation, task, or schema behavior.
- Execute Tasks 5–9 (11B) only after Children 2–10 pass locally on the selected platform.
- Child 5 and Child 6 intentionally land one compatibility stage. Only Task 5, after Child 9/10 evidence passes, changes the no-flag defaults to `OutputContractMode.V2` and `TraceReferenceMode.IdOnly`. Explicit `--output-contract legacy` and `--trace-reference-mode compatibility` remain migration switches until the next major version.
- Candidate outcome, probe result, action commit SHA, external review, benchmark result, waiver approval, release digest, and attestation are evidence: commands generate them and humans may review them, but the repository must never contain invented passing output.
- `.NET 10` is the default candidate because it is active LTS, not an automatic decision. The ADR must compare all viable candidates and prove normal/RID restore, Release build/test, every golden TraceEvent read, Windows DIA/PDB resolution, self-contained stdio, native layout, protocol behavior, and the supported Windows/architecture matrix.
- A platform candidate is ineligible unless the committed SDK probe proves, through public SDK seams, the selected profile handshake, delegated typed-tool structured output, `CancellationToken`/`IProgress<ProgressNotificationValue>` injection without schema leakage, configurable raw framing, and a parsed pre-dispatch 128-byte decoded-string request-ID guard in all three exact host modes. A probe that can run only after tool binding or that replaces/forks the SDK JSON-RPC implementation is a failure, not a compatibility workaround.
- Every PackageReference uses one exact version. Wildcards/ranges are forbidden. `global.json` and workflow setup use the same exact SDK. Every CI/fixture/release project has a committed normal lock; the server also has a committed `win-x64` release lock.
- Every third-party GitHub Action reference is a verified full 40-hex commit SHA. Tag names may appear only in comments recording the resolved release.
- A tag cannot bypass the reusable quality workflow. Release has no `dotnet restore`, `build`, `test`, `publish`, or archive command.
- `New-ReleaseArtifact.ps1` is the only publish/archive entry point. It calls `dotnet publish` once. Package smoke, hash, attestation, and release upload consume the unchanged zip.
- External review/cleanup is advisory unless a checked-in record pins tool version, command, input scope, and deterministic pass criterion. Absence of an advisory review never masquerades as a pass.
- Documentation claims use `supported`, `preview`, or `gap` and link to Child 10 evidence. Sharing TraceEvent with PerfView is never itself evidence of equivalent analysis.

**Spec:** `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` at commit `7ef8ff5`.

---

## Approved 11A preflight clarification (2026-07-29)

The user approved these binding clarifications after a public-surface preflight of the exact MCP SDK candidates:

- A string request ID is measured after JSON parsing and unescaping as `Encoding.UTF8.GetByteCount(requestIdString)`. JSON quotes, property names, and escape syntax do not count. ASCII IDs of 127 and 128 bytes are accepted; 129 bytes is rejected before routing, binding, or handler invocation. Direct UTF-8 and `\uXXXX` forms that decode to the same string have the same measured length. Numeric IDs are outside the string quota and remain limited to the SDK's `Int64` domain. The incoming filter rejects without invoking `next`, and the handler side-effect counter remains zero.
- Input is UTF-8 NDJSON. A frame payload is the raw byte sequence between delimiters. `LF` and `CRLF` are accepted delimiters and do not count; an isolated `CR` is payload. UTF-8 BOM is rejected at stream start or anywhere else. The production cap is 100000 payload bytes; byte 100001 fails immediately before SDK deserialization, routing, binding, or handler invocation. Each negative case runs in a separate child process, adds no stdout bytes, leaves the handler counter at zero, exits with code 2, and writes exactly one fixed input-free stderr line: `sdkcandidateprobe: frame limit exceeded` or `sdkcandidateprobe: request id limit exceeded`.
- The `2026-07-28` stateless sequence is `server/discover`, `tools/list`, `tools/call`, with no `initialize` or `notifications/initialized`. Per-request metadata is produced through the rc.1 public `MetaKeys.ProtocolVersion`, `MetaKeys.ClientInfo`, and `MetaKeys.ClientCapabilities` constants; evidence records the actual serialized wire keys. If the required constants or discovery metadata cannot be established from public rc.1 APIs/source, that candidate fails rather than guessing wire literals.
- 11A qualifies exactly one ordered architecture cell: Windows/X64 process on Windows/X64 OS with RID `win-x64`. Actual OS description/version and runner image are observations. `win-arm64`, `win-x86`, and cross-architecture emulation remain explicit gaps. The three SDK host modes are executions within this one cell, not separate architectures.
- Task 4's repository-wide full-SHA invariant also owns the existing `.github/workflows/release.yml` and adds `softprops/action-gh-release@v2` to the exact action-pin inputs. Task 9 may later restructure that already-pinned workflow.

These clarifications preserve the original security boundary: raw frame bytes are bounded before deserialization, while the only ID representation available at the SDK's public parsed-message hook is bounded before dispatch. Re-parsing or replacing the SDK JSON-RPC implementation remains forbidden.

---

## 11A exact candidate matrix and decision inputs

Create `eng/platform-candidates.v1.json` with these three exact candidates:

```json
{
  "schemaVersion": "1.0",
  "planDateEvidence": {
    "observedDate": "2026-07-29",
    "dotnetSupportPolicyUrl": "https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core",
    "dotnet10DownloadUrl": "https://dotnet.microsoft.com/en-us/download/dotnet/10.0",
    "mcpStablePackageUrl": "https://www.nuget.org/packages/ModelContextProtocol/1.4.1",
    "mcpPrereleasePackageUrl": "https://www.nuget.org/packages/ModelContextProtocol/2.0.0-rc.1"
  },
  "requiredProbeNames": [
    "nuget-package-existence-hash",
    "normal-restore",
    "win-x64-restore",
    "release-build",
    "release-unit-tests",
    "golden-traceevent-reads",
    "windows-dia-pdb-resolution",
    "self-contained-publish",
    "self-contained-stdio",
    "native-layout",
    "selected-profile-handshake",
    "delegated-typed-tool-structured-output",
    "cancellation-progress-injection-schema",
    "raw-framing-request-id-guard-seam",
    "tools-list-output-schema",
    "windows-architecture-matrix"
  ],
  "sdkSurfaceProbeNames": [
    "selected-profile-handshake",
    "delegated-typed-tool-structured-output",
    "cancellation-progress-injection-schema",
    "raw-framing-request-id-guard-seam"
  ],
  "sdkSurfaceHostModes": [
    "normal",
    "win-x64-framework-dependent",
    "win-x64-self-contained"
  ],
  "windowsArchitectureMatrix": [
    {
      "id": "windows-x64",
      "osPlatform": "Windows",
      "osArchitecture": "X64",
      "processArchitecture": "X64",
      "runtimeIdentifier": "win-x64"
    }
  ],
  "candidates": [
    {
      "id": "net8-stable-stateful",
      "sdkVersion": "8.0.420",
      "targetFramework": "net8.0",
      "mcpSdkVersion": "1.4.1",
      "protocolRevision": "2025-11-25",
      "protocolProfile": "stateful"
    },
    {
      "id": "net10-stable-stateful",
      "sdkVersion": "10.0.302",
      "targetFramework": "net10.0",
      "mcpSdkVersion": "1.4.1",
      "protocolRevision": "2025-11-25",
      "protocolProfile": "stateful"
    },
    {
      "id": "net10-next-stateless",
      "sdkVersion": "10.0.302",
      "targetFramework": "net10.0",
      "mcpSdkVersion": "2.0.0-rc.1",
      "protocolRevision": "2026-07-28",
      "protocolProfile": "stateless-discovery"
    }
  ]
}
```

`requiredProbeNames`, `sdkSurfaceProbeNames`, `sdkSurfaceHostModes`, and `windowsArchitectureMatrix` are ordered, immutable runner/freeze inputs. Both scripts read them from this matrix; neither script carries a second private list. `PlatformDecisionTests` asserts that the SDK list is the exact ordered subset shown, that the architecture list is exactly the single approved `windows-x64` cell, and that every candidate is evaluated against the same arrays.

These versions are candidate inputs, not declared winners. `net10-next-stateless` is prerelease and cannot win unless every proof passes and the ADR explicitly accepts prerelease operational risk. Plan-date evidence (`2026-07-29`) is the official [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0), [ModelContextProtocol 1.4.1 package page](https://www.nuget.org/packages/ModelContextProtocol/1.4.1), and [ModelContextProtocol 2.0.0-rc.1 package page](https://www.nuget.org/packages/ModelContextProtocol/2.0.0-rc.1). The decision record must preserve those URLs, the observation UTC timestamp, and that .NET 8 support ends `2026-11-10` while .NET 10 LTS support ends `2028-11-14`; links are evidence, not a substitute for runtime verification.

`Freeze-PlatformDecision.ps1` writes `eng/SelectedPlatform.props` only after one fixed candidate has every required proof marked passed. It creates one MSBuild property group containing `WprMcpSdkVersion`, `WprMcpTargetFramework`, `WprMcpMcpSdkVersion`, `WprMcpProtocolRevision`, and `WprMcpProtocolProfile`, copying each value byte-for-byte from that candidate's immutable result. It refuses every other candidate ID and any missing, extra, or failed probe; the generated file contains no descriptive or unmeasured value.

Central exact package versions after selection are:

```text
Microsoft.Diagnostics.Tracing.TraceEvent 3.2.2
Microsoft.Extensions.Hosting             10.0.7
ModelContextProtocol                     selected candidate's exact version
coverlet.collector                       6.0.0
Microsoft.NET.Test.Sdk                   17.8.0
Moq                                      4.20.72
xunit                                    2.5.3
xunit.runner.visualstudio                2.5.3
```

Any package added by Children 5, 9, or 10 must be added to `Directory.Packages.props` with an exact version and its lock files in the same commit.

---

## Fixed proof, package, and provenance interfaces

```powershell
# scripts/Test-PlatformCandidate.ps1
param(
    [Parameter(Mandatory)][ValidateSet(
        'net8-stable-stateful',
        'net10-stable-stateful',
        'net10-next-stateless')]
    [string]$CandidateId,
    [string]$OutputDirectory = 'artifacts/platform-matrix'
)

# scripts/Freeze-PlatformDecision.ps1
param(
    [Parameter(Mandatory)][ValidateSet(
        'net8-stable-stateful',
        'net10-stable-stateful',
        'net10-next-stateless')]
    [string]$CandidateId,
    [string]$ResultsDirectory = 'artifacts/platform-matrix'
)

# scripts/New-ReleaseArtifact.ps1
param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')),
    [string]$OutputDirectory = 'artifacts/release',
    [string]$RuntimeIdentifier = 'win-x64',
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$Tag
)
```

`Test-PlatformCandidate.ps1` emits one JSON result with this strict shape:

```csharp
internal sealed record PlatformCandidateResult(
    string SchemaVersion,
    string CandidateId,
    string SdkVersion,
    string TargetFramework,
    string McpSdkVersion,
    string ProtocolRevision,
    string ProtocolProfile,
    string Commit,
    DateTimeOffset StartedUtc,
    DateTimeOffset CompletedUtc,
    IReadOnlyList<PlatformProbeResult> Probes);

internal sealed record PlatformProbeResult(
    string Name,
    string Command,
    int ExitCode,
    string StdoutSha256,
    string StderrSha256,
    bool Passed,
    IReadOnlyDictionary<string, string> ArtifactSha256,
    IReadOnlyList<PlatformProbeCaseResult> Cases,
    NuGetPackageVerification? NuGetPackage);

internal sealed record PlatformProbeCaseResult(
    string HostMode,
    string Scenario,
    string Command,
    int ExitCode,
    string StdoutSha256,
    string StderrSha256,
    bool Passed,
    IReadOnlyDictionary<string, string> ArtifactSha256);

internal sealed record NuGetPackageVerification(
    string PackageId,
    string PackageVersion,
    string RegistrationUrl,
    string PackageContentUrl,
    string HashAlgorithm,
    string PublishedHashBase64,
    string DownloadedHashBase64,
    DateTimeOffset ObservedUtc,
    PackageRetrievalSource RetrievalSource);

internal enum PackageRetrievalSource { Network, VerifiedCache }
```

Required probe names are exactly:

```text
nuget-package-existence-hash
normal-restore
win-x64-restore
release-build
release-unit-tests
golden-traceevent-reads
windows-dia-pdb-resolution
self-contained-publish
self-contained-stdio
native-layout
selected-profile-handshake
delegated-typed-tool-structured-output
cancellation-progress-injection-schema
raw-framing-request-id-guard-seam
tools-list-output-schema
windows-architecture-matrix
```

The four SDK-surface probes from `selected-profile-handshake` through `raw-framing-request-id-guard-seam` each have exactly one `PlatformProbeCaseResult` for each of these host modes, in this order:

```text
normal
win-x64-framework-dependent
win-x64-self-contained
```

`normal` launches the normal Release framework-dependent `sdkcandidateprobe` build; `win-x64-framework-dependent` launches its dedicated `dotnet publish -r win-x64 --self-contained false` apphost; `win-x64-self-contained` directly launches its dedicated `dotnet publish -r win-x64 --self-contained true` executable, without `dotnet` as the command. Each case records and verifies the exact launched `sdkcandidateprobe` binary hash. These dedicated SDK-fixture publishes do not replace, satisfy, or share output with the production server's separate `self-contained-publish`, `self-contained-stdio`, and `native-layout` probes. Every SDK case must execute a real stdio initialize/discovery/call exchange against its recorded fixture bytes. A parent probe is passed only when its exit code is zero, its case list has those three unique modes and no others, and all cases passed; regular non-matrix probes have one case named `candidate-worktree`. The runner rejects duplicate probe names, duplicate case modes, success with missing case evidence, and artifact/log hashes that do not match retained bytes.

The SDK cases have these non-negotiable assertions:

- `selected-profile-handshake` uses the candidate's exact `protocolRevision` and `protocolProfile`; stateful candidates complete initialize then `notifications/initialized`, while `stateless-discovery` sends `server/discover`, `tools/list`, and `tools/call` in order, sends no initialize/initialized message, and builds request metadata with the rc.1 public `MetaKeys.ProtocolVersion`, `MetaKeys.ClientInfo`, and `MetaKeys.ClientCapabilities` constants. The evidence records the offered/accepted revision, actual serialized metadata keys, and ordered message-method transcript, with payload values redacted before hashing. Inability to establish the public metadata/discovery contract is a failed candidate, never a reason to guess wire literals.
- `delegated-typed-tool-structured-output` creates one typed `sdk_probe_echo` handler with `UseStructuredContent=true`, obtains the SDK `McpServerTool`, invokes it through a delegating wrapper, and proves that the wrapper can inspect and replace both text content and `StructuredContent` while retaining the generated input schema, output schema, annotations, and error state. A candidate fails if the only working design changes the typed domain method to return a protocol `CallToolResult`.
- `cancellation-progress-injection-schema` gives that handler SDK-injected `CancellationToken cancellationToken` and `IProgress<ProgressNotificationValue>? progress`; a real call must emit one progress notification and a cancelled call must observe the supplied token before completing. The real `tools/list` input schema must contain only the public `value` field and must not contain either injected parameter under any casing or naming policy.
- `raw-framing-request-id-guard-seam` configures the SDK with a wrapping input stream and proves the approved UTF-8 NDJSON payload rules at a lowered test cap and at the production `100000`-byte cap. `LF`/`CRLF` delimiters are excluded, isolated `CR` is payload, every BOM is rejected, and byte 100001 fails immediately before deserialization. A public parsed-message/pre-dispatch hook measures `Encoding.UTF8.GetByteCount` over the decoded/unescaped string ID. ASCII string IDs of 127 and 128 bytes reach `sdk_probe_echo`; 129 bytes is rejected without invoking `next` or the handler, and direct UTF-8 versus `\uXXXX` encodings of the same value measure equally. Numeric IDs `long.MinValue`, `0`, and `long.MaxValue` reach the handler and correlate exactly; the fixture does not fabricate a numeric token outside the SDK's `Int64` request-ID domain. Each negative case is isolated, adds no stdout bytes, leaves the handler counter at zero, exits 2, and emits only its approved fixed stderr line. If public SDK hooks cannot enforce the decoded ID limit before routing/binding/dispatch, or the wrapping stream cannot enforce the raw frame limit before deserialization, the case and candidate fail.

Use this release provenance shape:

```csharp
internal sealed record ReleaseProvenance(
    string SchemaVersion,
    string Tag,
    string Version,
    string Commit,
    string TargetFramework,
    string SdkVersion,
    string McpSdkVersion,
    string ProtocolRevision,
    string ProtocolProfile,
    string RuntimeIdentifier,
    string PackageFile,
    string PackageSha256,
    IReadOnlyDictionary<string, string> NativeFileSha256);
```

The immutable release payload is only `wpa-mcp-win-x64.zip`. `wpa-mcp-win-x64.provenance.json`, `wpa-mcp-win-x64.sha256`, and the GitHub attestation are metadata for those bytes, not independently built executables.

---

## File structure overview

| File | Phase | Action | Purpose |
|---|---|---|---|
| `eng/platform-candidates.v1.json` | 11A | Create | Exact candidate inputs |
| `eng/SelectedPlatform.props` | 11A | Create from passing result | Single selected TFM/MCP/protocol source |
| `scripts/Test-PlatformCandidate.ps1` | 11A | Create | Execute required candidate proof matrix |
| `tools/sdkcandidateprobe/*` | 11A | Create | Minimal public-SDK behavioral probe for the three exact host modes |
| `scripts/Freeze-PlatformDecision.ps1` | 11A | Create | Validate results and atomically emit selection/ADR inputs |
| `tests/WprMcp.Tests/PlatformDecisionTests.cs` | 11A | Create | Matrix/result/ADR/config consistency |
| `docs/decisions/0001-platform-protocol.md` | 11A | Create from observed evidence | Versioned TFM/MCP/protocol decision |
| `global.json` | 11A | Create | Exact SDK with `rollForward=disable` |
| `Directory.Build.props` | 11A | Create | Selected TFM/import/deterministic CI settings |
| `Directory.Packages.props` | 11A | Create | Exact central package versions |
| every current and later `*.csproj` | 11A/ongoing | Modify | Remove inline versions/use selected TFM/lock generation |
| every project `packages.lock.json` | 11A/ongoing | Create | Normal locked graph |
| `src/WprMcp/packages.win-x64.lock.json` | 11A | Create | Release RID graph |
| `tests/WprMcp.Tests/DependencyGovernanceTests.cs` | 11A | Create | Exact-version/lock/SDK/action enforcement |
| `eng/action-pin-inputs.v1.json` | 11A | Create | Fixed third-party action repository/tag inputs |
| `scripts/Resolve-ActionPins.ps1` | 11A | Create | Resolve and verify upstream tag commit SHAs |
| `.github/actions/setup-wprmcp/action.yml` | 11A | Create | Composite exact SDK/cache/locked restore setup |
| `.github/workflows/quality.yml` | 11A then 11B | Create/Modify | Reusable single quality implementation |
| `.github/workflows/ci.yml` | 11A | Modify | Call reusable workflow only |
| `.github/workflows/agent-benchmarks.yml` | 11B | Create | Scheduled probabilistic gate |
| `scripts/New-ReleaseArtifact.ps1` | 11B | Create | Only publish/archive command |
| `scripts/Test-ReleaseProvenance.ps1` | 11B | Create | Tag/version/commit/hash/native agreement |
| `tests/WprMcp.Tests/ReleaseArtifactTests.cs` | 11B | Create | Immutable packaging/workflow static gates |
| `scripts/install.ps1` | 11B | Modify | Zip-only install and digest validation |
| `tests/WprMcp.Tests/InstallerScriptTests.cs` | 11B | Modify | Remove legacy single-exe fallback claims |
| `.github/workflows/release.yml` | 11A + 11B | Modify | Pin existing actions in 11A; later call quality, download, verify, attest, upload in 11B |
| `eng/advisory-tools.v1.json` | 11B | Create | Optional review tool governance |
| `README.md`, `README.zh-CN.md` | 11B | Modify | Accurate install/status/contract/privacy/compatibility claims |
| `docs/ARCHITECTURE.md` | 11B | Modify | Target runtime, isolation, contract and release flow |
| `CONTRIBUTING.md` | 11B | Modify | Exact build/test/lock/snapshot/release commands |
| `docs/CAPABILITY_GAPS.md`, `docs/CAPABILITY_GAPS.zh-CN.md` | 11B | Modify | Evidence-backed supported/preview/gap matrix |
| `docs/TIME_SEMANTICS.md` | 11B | Create | Half-open integer-us/process/thread identity rules |
| `docs/PRIVACY.md` | 11B | Create | Off/paths/strict taxonomy, aliases, logs, external-model rule |
| `docs/COMPATIBILITY.md` | 11B | Create | Windows/architecture/TFM/MCP/protocol/package matrix |
| `CHANGELOG.md` | 11B | Modify | Contract/runtime/security/release migration notes |
| `tests/WprMcp.Tests/DocumentationGovernanceTests.cs` | 11B | Create | Claims/evidence/version/path link validation |

---

# Child 11A: Early platform/protocol/dependency gate

### Task 1: Make the candidate matrix executable before choosing (TDD)

**Files:**
- Create: `eng/platform-candidates.v1.json`
- Create: `scripts/Test-PlatformCandidate.ps1`
- Create: `tools/sdkcandidateprobe/sdkcandidateprobe.csproj`
- Create: `tools/sdkcandidateprobe/Program.cs`
- Create: `tools/sdkcandidateprobe/SdkProbeTool.cs`
- Create: `tests/WprMcp.Tests/PlatformDecisionTests.cs`
- Modify: `src/WprMcp/WprMcp.csproj`
- Modify: `tests/WprMcp.Tests/WprMcp.Tests.csproj`
- Modify: `tools/etlshrink/etlshrink.csproj`
- Modify: `tools/interruptfixture/interruptfixture.csproj`
- Modify: `WprMcp.sln`

- [ ] **Step 1: Write failing matrix tests**

Add exact tests:

```text
CandidateMatrix_HasExactUniqueCandidateIdsAndVersions
CandidateMatrix_CoversNet8Net10StableAndNet10NextProtocol
CandidateMatrix_RecordsPlanDateOfficialEvidenceUrls
CandidateMatrix_DeclaresExactRequiredProbeSdkSubsetAndHostModeArrays
CandidateMatrix_DeclaresExactWindowsX64ArchitectureMatrix
CandidateRunner_DeclaresEveryRequiredProbe
CandidateRunner_DeclaresExactSdkSurfaceProbeNamesAndHostModes
CandidateRunner_VerifiesNuGetPackageExistenceAndSha512BeforeRestore
CandidateRunner_UsesIsolatedOutputAndDoesNotEditTrackedFiles
CandidateResult_RecordsCommandsExitCodesHashesAndCommit
CandidateResult_RecordsExactProbeCasesAndRejectsMissingDuplicateOrExtraModes
SdkCandidateProbe_UsesSelectedRevisionProfileAndPublicSdkSeamsOnly
SdkCandidateProbe_DelegatesTypedStructuredToolWithoutCallToolResultDomainReturn
SdkCandidateProbe_InjectsCancellationAndProgressWithoutInputSchemaProperties
SdkCandidateProbe_ProvesConfiguredFrameAndDecodedRequestIdBoundariesBeforeDispatch
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~PlatformDecisionTests
```

Expected: candidate matrix/runner does not exist.

- [ ] **Step 3: Implement isolated candidate execution**

Make the four current projects consume overrideable `WprMcpTargetFramework` and `WprMcpMcpSdkVersion` MSBuild properties while keeping their current defaults until selection. Add both existing tools and `tools/sdkcandidateprobe/sdkcandidateprobe.csproj` to `WprMcp.sln` so fixture, release, and SDK-surface projects participate in proof. The probe project consumes the same two overrideable properties and no MCP package other than the candidate's exact `ModelContextProtocol` version.

`sdkcandidateprobe` is a committed minimal executable, not generated source and not production transport code. Its exact CLI modes are `--serve` and `--run-suite`; `--run-suite` requires `--host-mode normal|win-x64-framework-dependent|win-x64-self-contained`, `--host-command <absolute-path>`, `--protocol-revision <value>`, `--protocol-profile <value>`, and `--evidence <absolute-path>`. `Program.cs` owns this argument parsing, launches `--serve` through redirected binary-safe stdin/stdout/stderr, and writes evidence with `FileMode.CreateNew`. `SdkProbeTool.cs` owns one public `sdk_probe_echo` tool whose domain signature returns `Task<ProbeOutput>` and accepts public `string value`, SDK-injected `CancellationToken cancellationToken`, and SDK-injected `IProgress<ProgressNotificationValue>? progress`; it increments an invocation counter only after binding and cancellation checks.

The fixture builds the tool as a typed SDK `McpServerTool` with `UseStructuredContent=true`, then registers a delegating `McpServerTool` wrapper that records and replaces text plus `StructuredContent` without changing the typed domain return to `CallToolResult`. It starts the server with a caller-supplied wrapping input stream and the candidate SDK's public parsed-message hook before tool binding. The suite performs the exact four SDK scenarios and boundary assertions specified above. It may use conditional compilation keyed only by the fixed `protocolProfile` to accommodate the stable and prerelease SDK APIs, but both branches implement the same observable CLI/evidence contract; reflection into non-public SDK members, patching package bytes, or substituting another JSON-RPC dispatcher fails the probe.

For each candidate, the runner creates a temporary copied worktree under the supplied output directory, writes an exact temporary `global.json`, and first resolves the exact `ModelContextProtocol` version through the NuGet v3 registration resource. It downloads the exact `.nupkg` from the package base address, rejects a missing or unlisted version, verifies the downloaded bytes against NuGet's published SHA-512 metadata, and records registration URL, package URL, SHA-512, observation UTC time, and cache-versus-network source as the `nuget-package-existence-hash` probe. Only then does it restore to a candidate-specific packages directory, execute every remaining named probe, hash stdout/stderr/artifacts, and write `<candidate-id>.result.json` with `FileMode.CreateNew`. An offline run may use a previously downloaded package only when its retained registration metadata and SHA-512 verify; otherwise that probe fails. It cleans the copied worktree but retains results/logs, never edits the caller's tracked files, and never converts a failed exit code into a pass.

For each candidate, publish `sdkcandidateprobe` as normal framework-dependent, `win-x64` framework-dependent, and `win-x64` self-contained outputs before running the four SDK-surface probes. Pass the candidate's exact profile/revision to every suite invocation and retain one evidence JSON plus stdout/stderr per `(probeName,hostMode)`. The framework-dependent RID case must launch the published apphost and the self-contained case must launch its published executable directly. The runner verifies that the launched path/hash belongs to the recorded publish output, that all four probes contain the exact three case modes, and that `Passed` equals the conjunction of case outcomes. Compile failure, launch failure, profile mismatch, schema leakage, missing structured content, late cancellation, absent progress, handler invocation after an oversized frame/ID, or lack of a public pre-dispatch hook produces a failed case and a nonzero runner exit. No candidate may be marked passed from API-name reflection alone.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~PlatformDecisionTests
```

Expected: static/serialization tests pass; candidate execution itself occurs in Task 2.

- [ ] **Step 5: Commit**

```powershell
git add eng/platform-candidates.v1.json scripts/Test-PlatformCandidate.ps1 tools/sdkcandidateprobe/sdkcandidateprobe.csproj tools/sdkcandidateprobe/Program.cs tools/sdkcandidateprobe/SdkProbeTool.cs tests/WprMcp.Tests/PlatformDecisionTests.cs src/WprMcp/WprMcp.csproj tests/WprMcp.Tests/WprMcp.Tests.csproj tools/etlshrink/etlshrink.csproj tools/interruptfixture/interruptfixture.csproj WprMcp.sln
git commit -m "build(platform): add executable TFM and MCP candidate matrix"
```

---

### Task 2: Run every candidate and freeze the observed ADR decision (TDD)

**Files:**
- Create: `scripts/Freeze-PlatformDecision.ps1`
- Create: `eng/SelectedPlatform.props` from one passing candidate
- Create: `docs/decisions/0001-platform-protocol.md` from observed result files
- Modify: `tests/WprMcp.Tests/PlatformDecisionTests.cs`

- [ ] **Step 1: Add failing decision-evidence tests**

Add:

```text
SelectedPlatform_ReferencesOnePassingCandidate
SelectedPlatform_ValuesExactlyMatchCandidateResult
DecisionRecord_ContainsEveryRequiredProbeCommandAndObservedOutcome
DecisionRecord_RecordsOfficialEvidenceUrlsAndNuGetVerificationUtc
DecisionRecord_ExplainsRejectedCandidatesAndPrereleaseRisk
DecisionRecord_DefinesExactProtocolE2eMatrix
DecisionRecord_RecordsSdkSurfaceCasesForEveryHostModeAndCandidate
DecisionRecord_RecordsSelectedProfileStructuredInjectionAndGuardEvidence
DecisionRecord_ListsSupportedWindowsAndArchitectureMatrix
DecisionRecord_ContainsNoUnverifiedPassOrReviewClaim
Freeze_RejectsMissingDuplicateExtraOrFailedRequiredProbe
Freeze_RejectsSdkSurfaceProbeWithMissingDuplicateExtraOrFailedHostMode
Freeze_RejectsSdkEvidenceForWrongProfileRevisionBinaryOrBoundary
```

- [ ] **Step 2: Run the real matrix; failures remain evidence**

```powershell
Remove-Item Env:WPRMCP_PLATFORM_CANDIDATE -ErrorAction SilentlyContinue
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PlatformCandidate.ps1 -CandidateId net8-stable-stateful
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PlatformCandidate.ps1 -CandidateId net10-stable-stateful
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PlatformCandidate.ps1 -CandidateId net10-next-stateless
```

Expected: three immutable result files. Do not write an expected all-pass outcome. Inspect each command, exit code, log hash, golden read, DIA result, native layout, stdio result, schema result, and Windows architecture claim. For every candidate, inspect all twelve SDK-surface case results (four exact probe names times three exact host modes), their launched binary hashes, selected profile/revision transcript, delegated typed structured output, injected-parameter schema exclusion, observed cancellation/progress, 100000-byte configurable frame support, the 127/128/129-byte string request-ID boundary, and exact `Int64` minimum/zero/maximum numeric correlation. A missing public pre-dispatch seam is an observed failed candidate, not a reason to omit the probe.

- [ ] **Step 3: Freeze one actually passing decision**

After review, run `Freeze-PlatformDecision.ps1` with exactly one candidate ID whose required probes all passed. Before writing anything, the script validates result commit equals `git rev-parse HEAD`; candidate identity/version/profile/revision equals the immutable matrix; probe names equal `requiredProbeNames` from `eng/platform-candidates.v1.json` exactly with no missing, duplicate, or extra item; every parent result passed; every probe named by `sdkSurfaceProbeNames` has the exact ordered `sdkSurfaceHostModes` case set with every case passed; and every referenced command, stdout/stderr, launched-binary, transcript, schema, structured-output, cancellation/progress, and framing/request-ID evidence file exists and matches its recorded hash. It additionally parses the SDK evidence and requires the candidate profile/revision, selected launched binary hash, `UseStructuredContent=true` wrapper result, input-schema exclusion list, observed cancellation/progress markers, configurable `100000` frame limit, string-ID results `127=accepted`, `128=accepted`, `129=rejected-before-dispatch`, and numeric-ID results `long.MinValue/0/long.MaxValue=accepted-and-correlated`. Boolean `Passed=true` without these fields is invalid.

Only after those validations does the script write `eng/SelectedPlatform.props` and an ADR candidate containing the plan-date official evidence URLs, each NuGet verification observation UTC time/hash/source, all probe commands/outcomes, the complete per-candidate/per-host SDK matrix, and reasons. The ADR states whether each rejected candidate failed compilation, launch, profile handshake, delegated structured wrapping, cancellation/progress injection/schema, framing, pre-dispatch ID guarding, or another named probe; it does not collapse these into “SDK incompatible.” The selected-candidate section names the exact public SDK seams demonstrated and links their retained evidence hashes. The operator reviews factual wording before committing. If no candidate passes, stop 11A and fix the proof environment/compatibility issue; do not select one anyway and do not defer any SDK-surface proof to Child 5 or Child 8.

Run, with the reviewed passing ID from the fixed candidate set:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Freeze-PlatformDecision.ps1 -CandidateId (Get-Content artifacts/platform-matrix/reviewed-selection.txt -Raw).Trim()
```

`reviewed-selection.txt` is a local evidence input containing exactly one allowed candidate ID; it is not committed. The script rejects whitespace beyond trimming, unknown IDs, and any failed required probe.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~PlatformDecisionTests
```

Expected: decision/config/result consistency passes.

- [ ] **Step 5: Commit**

```powershell
git add scripts/Freeze-PlatformDecision.ps1 eng/SelectedPlatform.props docs/decisions/0001-platform-protocol.md tests/WprMcp.Tests/PlatformDecisionTests.cs
git commit -m "docs(platform): freeze evidenced TFM MCP and protocol decision"
```

---

### Task 3: Apply exact SDK/package pins and normal plus RID lock files (TDD)

**Files:**
- Create: `global.json`
- Create: `Directory.Build.props`
- Create: `Directory.Packages.props`
- Create: `src/WprMcp/packages.lock.json`
- Create: `src/WprMcp/packages.win-x64.lock.json`
- Create: `tests/WprMcp.Tests/packages.lock.json`
- Create: `tools/etlshrink/packages.lock.json`
- Create: `tools/interruptfixture/packages.lock.json`
- Create: `tools/sdkcandidateprobe/packages.lock.json`
- Create: `tests/WprMcp.Tests/DependencyGovernanceTests.cs`
- Modify: `src/WprMcp/WprMcp.csproj`
- Modify: `tests/WprMcp.Tests/WprMcp.Tests.csproj`
- Modify: `tools/etlshrink/etlshrink.csproj`
- Modify: `tools/interruptfixture/interruptfixture.csproj`
- Modify: `tools/sdkcandidateprobe/sdkcandidateprobe.csproj`

- [ ] **Step 1: Write failing dependency tests**

Add:

```text
GlobalJson_MatchesSelectedSdkAndDisablesRollForward
EveryProject_UsesSelectedTargetFramework
EveryPackageVersion_IsExactAndCentral
Moq_IsPinnedTo42072NotWildcard
EveryCurrentProject_HasNormalLockFile
Server_HasSeparateWinX64LockFile
NormalAndRidLockedRestore_DoNotChangeLockFiles
SelectedSdkProbeProject_UsesSelectedTfmMcpPackageAndNormalLock
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DependencyGovernanceTests
```

Expected: no global/central config or locks; Moq is `4.20.*`.

- [ ] **Step 3: Apply generated selection and create locks**

`global.json` uses the exact `WprMcpSdkVersion`, `rollForward: "disable"`, and `allowPrerelease` true only if the selected SDK itself is prerelease. `Directory.Build.props` imports `eng/SelectedPlatform.props`, sets `TargetFramework=$(WprMcpTargetFramework)`, `RestorePackagesWithLockFile=true`, `ContinuousIntegrationBuild` from CI, deterministic source paths, nullable, and warnings as errors in CI.

Move all version attributes into `Directory.Packages.props` with the exact list above and selected MCP SDK. Generate locks, inspect diffs, then verify:

```powershell
dotnet restore WprMcp.sln --force-evaluate
dotnet restore src/WprMcp/WprMcp.csproj -r win-x64 --force-evaluate -p:NuGetLockFilePath=packages.win-x64.lock.json
dotnet restore WprMcp.sln --locked-mode
dotnet restore src/WprMcp/WprMcp.csproj -r win-x64 --locked-mode -p:NuGetLockFilePath=packages.win-x64.lock.json
```

- [ ] **Step 4: Run GREEN and prove no lock churn**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DependencyGovernanceTests
git diff --exit-code -- global.json Directory.Build.props Directory.Packages.props src/WprMcp/packages.lock.json src/WprMcp/packages.win-x64.lock.json tests/WprMcp.Tests/packages.lock.json tools/etlshrink/packages.lock.json tools/interruptfixture/packages.lock.json tools/sdkcandidateprobe/packages.lock.json
```

Expected: tests pass and the second locked restore changed nothing.

- [ ] **Step 5: Commit**

```powershell
git add global.json Directory.Build.props Directory.Packages.props src/WprMcp/WprMcp.csproj src/WprMcp/packages.lock.json src/WprMcp/packages.win-x64.lock.json tests/WprMcp.Tests/WprMcp.Tests.csproj tests/WprMcp.Tests/packages.lock.json tests/WprMcp.Tests/DependencyGovernanceTests.cs tools/etlshrink/etlshrink.csproj tools/etlshrink/packages.lock.json tools/interruptfixture/interruptfixture.csproj tools/interruptfixture/packages.lock.json tools/sdkcandidateprobe/sdkcandidateprobe.csproj tools/sdkcandidateprobe/packages.lock.json
git commit -m "build(deps): pin SDK packages and locked restore graphs"
```

---

### Task 4: Pin actions and establish the early reusable quality workflow (TDD)

**Files:**
- Create: `eng/action-pin-inputs.v1.json`
- Create: `scripts/Resolve-ActionPins.ps1`
- Create: `.github/actions/setup-wprmcp/action.yml`
- Create: `.github/workflows/quality.yml`
- Modify: `.github/workflows/ci.yml`
- Modify: `.github/workflows/release.yml`
- Modify: `tests/WprMcp.Tests/DependencyGovernanceTests.cs`

- [ ] **Step 1: Add failing workflow governance tests**

Add:

```text
EveryThirdPartyAction_UsesFullFortyHexCommitSha
ActionPinInputs_ContainOnlyApprovedRepositoryAndMajorTagPairs
ActionPinComments_RecordResolvedRepositoryAndTag
WorkflowSdk_EqualsGlobalJsonExactly
Ci_CallsReusableQualityWorkflowWithoutDuplicatingBuildSteps
EarlyQuality_RunsNormalAndWinX64LockedRestoreBuildAndNonPackageTests
EarlyQuality_ReservesPackageCategoryFor11BArtifactStage
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DependencyGovernanceTests
```

Expected: workflows use floating `@v4`/`@v2` tags and duplicate inline steps.

- [ ] **Step 3: Resolve real action commits and create reusable workflow**

Create `eng/action-pin-inputs.v1.json` with exactly `actions/checkout@v4`, `actions/setup-dotnet@v4`, `actions/cache@v4`, `actions/upload-artifact@v4`, `actions/download-artifact@v4`, `actions/attest-build-provenance@v3`, and `softprops/action-gh-release@v2`. `Resolve-ActionPins.ps1` has exact parameters `InputPath='eng/action-pin-inputs.v1.json'` and `OutputPath='artifacts/action-pins.candidate.json'`. For each allowlisted pair it calls `git ls-remote https://github.com/{owner}/{repository}.git refs/tags/{tag} refs/tags/{tag}^{}`, selects the peeled object for an annotated tag, rejects missing, ambiguous, or non-40-hex results, repeats the lookup, and requires the same commit. It writes a candidate JSON containing repository, tag, resolved 40-hex commit, retrieval UTC time, and exact command. Review that file, then place the observed SHAs in YAML with repository/tag comments. Never write a guessed SHA. Task 4 pins all existing uses in both `ci.yml` and `release.yml`; Task 9 may later change workflow structure but must retain full-SHA references.

Run the resolver before editing YAML:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Resolve-ActionPins.ps1 -InputPath eng/action-pin-inputs.v1.json -OutputPath artifacts/action-pins.candidate.json
```

The early `quality.yml` supports `workflow_call` and `workflow_dispatch`, checks out, sets the exact selected SDK, performs normal and RID locked restores, Release build, and every currently existing non-package test. Its solution-wide test command always includes `--filter "Category!=Package"`, reserving `Category=Package` for the later task that has produced an immutable candidate; this is harmless before such tests exist and prevents Child 9's future package consumer from creating a pre-11B CI cycle. `ci.yml` retains push/PR/manual triggers and only calls `./.github/workflows/quality.yml` with least privileges.

- [ ] **Step 4: Run GREEN and YAML/static validation**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DependencyGovernanceTests
dotnet restore WprMcp.sln --locked-mode
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test WprMcp.sln -c Release --no-build --filter "Category!=Package"
```

Expected: all local gates pass. Record the actual first GitHub workflow run separately after push; do not claim it before it exists.

- [ ] **Step 5: Commit**

```powershell
git add eng/action-pin-inputs.v1.json scripts/Resolve-ActionPins.ps1 .github/actions/setup-wprmcp/action.yml .github/workflows/quality.yml .github/workflows/ci.yml .github/workflows/release.yml tests/WprMcp.Tests/DependencyGovernanceTests.cs
git commit -m "ci(governance): pin actions and reuse the locked quality workflow"
```

---

# Child 11B: Final quality and immutable release gate

### Task 5: Freeze secure defaults and compose all deterministic and probabilistic gates (TDD)

**Files:**
- Modify: `src/WprMcp/McpServerOptions.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/Core/McpToolCatalog.cs`
- Modify: `.github/workflows/quality.yml`
- Create: `.github/workflows/agent-benchmarks.yml`
- Modify: `tests/WprMcp.Tests/McpServerOptionsTests.cs`
- Modify: `tests/WprMcp.Tests/TraceReferenceSurfaceTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/ContractProfileTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs`
- Modify: `src/WprMcp/Core/tool-contracts.v2.json`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json`
- Create: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.secure-default.json`
- Modify: `tests/WprMcp.Tests/DependencyGovernanceTests.cs`
- Modify: `tests/WprMcp.GoldenTests/CapabilityMatrixTests.cs`

- [ ] **Step 1: Write failing complete-gate tests**

Add:

```text
QualityWorkflow_RunsLockedRestoreBuildUnitProtocolGoldenAndInvariantGates
QualityWorkflow_RunsHostileConcurrencyOnWindowsAndExcludesPackageCategory
QualityWorkflow_DoesNotReferenceReleaseScriptsBeforeTask6
ScheduledWorkflow_RunsBothAgentModesWithFiveTrials
ReleaseInvocation_EnablesFailClosedAgentPolicy
PullRequestInvocation_DoesNotRequireExternalModel
EveryLaterProject_HasExactVersionsAndLockFiles
NoFlagServer_DefaultsToV2OutputAndIdOnlyTraceReferences
NoFlagServer_RejectsRawQueryPathBeforeFileAccess
SecureDefaultToolsList_MatchesReviewedV2ReadOnlySnapshot
SecureDefaultTransition_PreservesChild10ExplicitV2StrictIdOnlyProfileHash
QualityWorkflow_ReusesChild10VerifyAndCompareCommandsUnchanged
ExplicitLegacyCompatibilitySwitches_RemainAvailableAndDeprecated
FinalContractAudit_IncludesUnloadAndCacheStatusAcrossManifestAndAllProfiles
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~DependencyGovernanceTests|FullyQualifiedName~CapabilityMatrixTests"
```

Expected: early workflow lacks Child 9/10 deterministic and benchmark gates; no-flag option/profile assertions still observe one or both compatibility-stage defaults.

- [ ] **Step 3: Perform the reviewed default switch, then extend the single workflow**

Change only the no-flag defaults: `McpServerOptions.Parse` selects `OutputContractMode.V2` and `TraceReferenceMode.IdOnly`; explicit `--output-contract legacy` and `--trace-reference-mode compatibility` still select the compatibility behavior proven by Child 9. Do not rename the public `path` parameter or remove either switch. With no trace-reference flag, every query rejects a raw path before filesystem access, while `load_trace` remains the sole raw-path entry and returns `trc_` IDs. The active no-flag `tools/list` exposes v2 output schemas and read-only query annotations. Capture that real surface in `tools-list.secure-default.json`; compare it to the explicit v2/ID-only surface and inspect every diff. Child 10 remains explicit `v2 + strict + id-only`, calls `load_trace` once per fixture/session, and hash-binds that profile; this task must not rewrite its manifests, raw-path-substitute its query arguments, or change its launch flags. Rerun Child 10's unchanged golden `verify`/`compare` commands after the default switch and require the same profile hash. As the final Child 7 surface audit, require `unload_trace` and `trace_cache_status` exactly once in `tool-contracts.v2.json` and in the active legacy, explicit-v2, and secure-default lists; validate their output schemas and destructive/read-only annotations through real `tools/list`. Inspect and explain every snapshot diff rather than regenerating wholesale. This is the approved compatibility event, so the commit and Task 8 changelog must call out the default change.

Add `workflow_call` inputs:

```yaml
inputs:
  release_tag:
    type: string
    required: false
    default: ''
  run_agent_benchmarks:
    type: boolean
    required: false
    default: false
```

Jobs in this task are ordered only through the pre-package gate: locked restore; Release build; unit tests; protocol E2E/hostile/concurrency with `Category!=Package`; golden/invariants; optional agent benchmark/policy. This commit must not reference `New-ReleaseArtifact.ps1`, `Test-ReleaseProvenance.ps1`, a package job, packaged smoke, provenance upload, or a release artifact because those files do not exist until Task 6. PR passes `run_agent_benchmarks=false`; scheduled and release pass true. External-service failure follows Child 10 policy and only accepts a valid checked-in waiver for the exact tag/commit/artifact.

Pin every newly added action through the Task 4 resolver and commit each project's new lock file if Children 5/9/10 did not already do so.

- [ ] **Step 4: Run GREEN locally**

```powershell
dotnet restore WprMcp.sln --locked-mode
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --no-build
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "Category!=Package"
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --no-build
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- verify --trials benchmarks/agent/baseline/trials --scenarios benchmarks/agent/scenarios.v1.json --policy benchmarks/agent/policy.v1.json
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- compare --candidate benchmarks/agent/baseline/trials --baseline benchmarks/agent/baseline/manifest.json --policy benchmarks/agent/policy.v1.json
```

Expected: all deterministic gates pass. A live scheduled/release benchmark result is reported only after an actual run.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/McpServerOptions.cs src/WprMcp/Program.cs src/WprMcp/Core/McpToolCatalog.cs src/WprMcp/Core/tool-contracts.v2.json .github/workflows/quality.yml .github/workflows/agent-benchmarks.yml tests/WprMcp.Tests/McpServerOptionsTests.cs tests/WprMcp.Tests/TraceReferenceSurfaceTests.cs tests/WprMcp.ProtocolTests/ContractProfileTests.cs tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json tests/WprMcp.ProtocolTests/Snapshots/tools-list.secure-default.json tests/WprMcp.Tests/DependencyGovernanceTests.cs tests/WprMcp.GoldenTests/CapabilityMatrixTests.cs
git commit -m "feat(defaults): enable gated v2 and ID-only secure defaults"
```

---

### Task 6: Create and smoke one immutable zip without republishing (TDD)

**Files:**
- Create: `scripts/New-ReleaseArtifact.ps1`
- Create: `scripts/Test-ReleaseProvenance.ps1`
- Create: `tests/WprMcp.Tests/ReleaseArtifactTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/PackagedServerTests.cs`
- Modify: `scripts/install.ps1`
- Modify: `tests/WprMcp.Tests/InstallerScriptTests.cs`
- Modify: `.github/workflows/quality.yml`

- [ ] **Step 1: Write failing artifact/install tests**

Add:

```text
ArtifactScript_ContainsExactlyOneDotnetPublishInvocation
ArtifactScript_StagesRequiredExeAndNativeDllsThenCreatesOneZip
ArtifactArchive_SortsEntriesAndUsesCommitTimestamp
ArtifactScript_EmitsHashAndProvenanceAfterArchive
PackageSmoke_NeverPublishesRecompressesOrMutatesZip
PackagedServer_NoFlagProfileIsV2IdOnlyAndRawQueriesRequireLoadTrace
Provenance_TagVersionCommitTfmSdkMcpProtocolAndHashAgree
Installer_UsesZipOnlyAndVerifiesReleaseDigest
Installer_HasNoLegacySingleExeFallback
Installer_DoesNotForceLegacyOutputOrCompatibilityTraceMode
Quality_PackagesOnceBeforeSmokeAndUploadsSamePathAfterSmoke
Quality_PackageJobsAppearOnlyWithReleaseScriptsInThisTask
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseArtifactTests|FullyQualifiedName~InstallerScriptTests"
```

Expected: current release builds inline, publishes both zip and historical exe, and has no immutable provenance script.

- [ ] **Step 3: Implement the only package path**

`New-ReleaseArtifact.ps1` validates a clean output directory, selected platform, full commit, version, optional `Tag` equal to the literal `v` concatenated with `Version`, and locked RID restore already present. It sets `$publishDirectory = [IO.Path]::GetFullPath((Join-Path $OutputDirectory 'publish-win-x64'), $RepositoryRoot)` and invokes exactly:

```powershell
dotnet publish src/WprMcp/WprMcp.csproj -c Release -r win-x64 --self-contained true --no-restore -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:Version=$Version -p:SourceRevisionId=$Commit -o $publishDirectory
```

It stages `root/bin/wpa-mcp.exe` and all `amd64/*.dll`, requires `msdia140.dll` and `KernelTraceControl.dll`, then creates `wpa-mcp-win-x64.zip` once with ordinally sorted relative entry names and every ZIP timestamp set to the selected commit time (clamped only to the ZIP-supported range). It closes every stream, computes SHA-256, and writes provenance/hash metadata. It never opens the zip for update and does not use `Compress-Archive`.

Extend `quality.yml` in this same commit, after the Task 5 pre-package jobs, with the ordered package-once, packaged-smoke, provenance-verification, and artifact-upload jobs. Package waits for every preceding required job. The upload step creates exactly one workflow artifact named `wpa-mcp-win-x64-gated`, containing the existing zip, provenance JSON, and SHA-256 metadata, with `if-no-files-found: error` and `retention-days: 14`. The workflow captures the pre-smoke hash, calls Child 9's `scripts/Test-PackagedServer.ps1`, recaptures the hash, calls `Test-ReleaseProvenance.ps1`, and uploads the existing zip/metadata. The packaged test launches without contract/reference flags, asserts v2 schemas and ID-only raw-query rejection, then proves a `load_trace` followed by trace-ID query succeeds. Update `install.ps1` to require the zip and GitHub asset digest; remove `$exeAssetName`, `Test-InstalledBinaryMatchesRelease`, single-exe download, and degraded-native fallback. The installer must not add legacy-output or compatibility-reference switches. Preserve ASCII/no-BOM and atomic install tests.

- [ ] **Step 4: Run GREEN on a local artifact**

```powershell
$version=(dotnet msbuild src/WprMcp/WprMcp.csproj -getProperty:Version -p:Configuration=Release).Trim()
$commit=git rev-parse HEAD
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/New-ReleaseArtifact.ps1 -Version $version -Commit $commit
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PackagedServer.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ExpectedVersion $version -ExpectedCommit $commit
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ReleaseProvenance.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ProvenancePath artifacts/release/wpa-mcp-win-x64.provenance.json
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseArtifactTests|FullyQualifiedName~InstallerScriptTests"
```

Expected: all pass and zip hash stays unchanged across both verification scripts.

- [ ] **Step 5: Commit**

```powershell
git add scripts/New-ReleaseArtifact.ps1 scripts/Test-ReleaseProvenance.ps1 tests/WprMcp.Tests/ReleaseArtifactTests.cs tests/WprMcp.ProtocolTests/PackagedServerTests.cs scripts/install.ps1 tests/WprMcp.Tests/InstallerScriptTests.cs .github/workflows/quality.yml
git commit -m "build(release): publish smoke and hash one immutable zip"
```

---

### Task 7: Make tag release download, verify, attest, and upload only gated bytes (TDD)

**Files:**
- Modify: `.github/workflows/release.yml`
- Modify: `tests/WprMcp.Tests/ReleaseArtifactTests.cs`
- Modify: `tests/WprMcp.Tests/DependencyGovernanceTests.cs`

- [ ] **Step 1: Add failing release workflow tests**

Add:

```text
Release_HasOnlyTagTriggerAndNoManualBypass
Release_CallsReusableQualityForSameTagCommit
ReleaseJob_NeedsQualityAndDownloadsItsNamedArtifact
ReleaseJob_HasNoDotnetOrArchiveCommand
Release_VerifiesTagVersionCommitDigestAndProvenanceBeforeUpload
Release_AttestsAndUploadsTheExactDownloadedZip
Release_DoesNotUploadHistoricalStandaloneExe
ReleaseArtifactTests_RequireSecureDefaultV2IdOnlyEvidence
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseArtifactTests|FullyQualifiedName~DependencyGovernanceTests"
```

Expected: current `release.yml` restores/publishes/stages inside the release job and uses floating actions.

- [ ] **Step 3: Replace release with gated artifact consumption**

`release.yml` retains only `push.tags: ['v*']`. Its `quality` job calls `./.github/workflows/quality.yml` with `release_tag=${{ github.ref_name }}`, `run_agent_benchmarks=true`, and inherited required secrets. `publish` has `needs: quality`; downloads only `wpa-mcp-win-x64-gated` from the same run; rejects unexpected or missing files; recomputes zip SHA; validates provenance tag/version/commit/digest; creates the GitHub artifact attestation for that zip; verifies the attestation; then uses `gh release create`/`gh release upload` for zip, provenance, and sha metadata. The upload command relies on the default refusal to replace an existing asset and does not pass `--clobber`.

There is no checkout/setup-dotnet/restore/build/test/publish/compress in `publish`. Grant `contents: write`, `id-token: write`, and `attestations: write` only to `publish`; all other jobs stay least-privilege. Resolve/pin download/upload/attestation action SHAs with Task 4's resolver and record actual tag-to-SHA comments.

- [ ] **Step 4: Run GREEN static gates and a non-publishing rehearsal**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~ReleaseArtifactTests|FullyQualifiedName~DependencyGovernanceTests"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ReleaseProvenance.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ProvenancePath artifacts/release/wpa-mcp-win-x64.provenance.json
```

Expected: tests/provenance pass. Do not claim GitHub attestation/release success until a real tag workflow completes.

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows/release.yml tests/WprMcp.Tests/ReleaseArtifactTests.cs tests/WprMcp.Tests/DependencyGovernanceTests.cs
git commit -m "ci(release): upload only quality-gated attested artifact bytes"
```

---

### Task 8: Replace parity overclaims with evidence-linked compatibility, time, privacy, and capability docs (TDD)

**Files:**
- Create: `docs/TIME_SEMANTICS.md`
- Create: `docs/PRIVACY.md`
- Create: `docs/COMPATIBILITY.md`
- Create: `tests/WprMcp.Tests/DocumentationGovernanceTests.cs`
- Modify: `README.md`
- Modify: `README.zh-CN.md`
- Modify: `docs/ARCHITECTURE.md`
- Modify: `CONTRIBUTING.md`
- Modify: `docs/CAPABILITY_GAPS.md`
- Modify: `docs/CAPABILITY_GAPS.zh-CN.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Write failing documentation tests**

Add:

```text
Docs_DoNotClaimPerfViewEquivalentFromSharedTraceEventDependency
Docs_SelectedTfmSdkMcpAndProtocolMatchDecisionAndBuild
Docs_EverySupportedPreviewGapClaimLinksToCapabilityMatrixEvidence
TimeSemantics_DocCoversHalfOpenUsProcessAndThreadLifetimeSelectors
Privacy_DocCoversModesTaxonomyAliasesLogsTelemetryAndExternalModels
Compatibility_DocCoversWindowsArchitectureProtocolAndNativeLayout
Readmes_DescribeZipOnlyInstallAndLegacyV2Migration
Readmes_DescribeV2IdOnlyDefaultsAndExplicitCompatibilitySwitches
Contributing_UsesLockedExactQualityCommands
Changelog_RecordsBreakingContractAndSecurityChanges
EveryMarkdownEvidenceLink_Resolves
```

- [ ] **Step 2: Run RED and locate current overclaims**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DocumentationGovernanceTests
rg -n -i "analysis quality matches PerfView|分析能力等同 PerfView|most other tools are 1:1|PerfView-equivalent" README.md README.zh-CN.md CONTRIBUTING.md docs
```

Expected: README English/Chinese and contributing/tool descriptions contain unsupported equivalence/parity claims or stale .NET 8/single-project details.

- [ ] **Step 3: Write evidence-backed documentation**

Document exact selected values from `eng/SelectedPlatform.props`/ADR, not copied alternatives. `TIME_SEMANTICS.md` defines integer microseconds, half-open `[startUs,endUs)`, clipping, one-sided resolution after descriptor acquisition, `ProcessInstanceKey`, `ThreadInstanceKey`, `pid+processStartUs`, shared `ThreadSelector`, thread errors, and filter-before-TopN.

`PRIVACY.md` defines `off`, `paths`, `strict`, field taxonomy, HMAC alias scope/bounds, inbound revalidation, stderr/progress/telemetry coverage, and strict-only external agent runs. `COMPATIBILITY.md` lists only matrix-proven Windows/architecture/native/protocol profiles and legacy/v2 startup flags.

`README*`, `COMPATIBILITY.md`, and `CHANGELOG.md` identify this release as the separately reviewed secure-default transition: no-flag MCP output is v2 and no-flag query references are ID-only; raw paths enter only through `load_trace`; explicit `--output-contract legacy` and `--trace-reference-mode compatibility` are temporary migration switches with the structured deprecation warning; the next major version removes them. Link the real `tools-list.secure-default.json`, default-profile protocol tests, packaged smoke, and capability matrix rather than claiming the switch from prose alone.

Rewrite capability gaps into supported/preview/gap claims keyed to `benchmarks/capability-matrix.v1.json`. “PerfView equivalent” may remain only as a UI-navigation analogy attached to evidence/limitations, never a blanket quality claim. Update install docs to zip-only release and exact native layout. Update architecture/contributing from single `.NET 8` PoC assumptions to the selected runtime/registry/artifact/worker/contract boundaries.

- [ ] **Step 4: Run GREEN in both languages**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~DocumentationGovernanceTests
$unsupportedClaims = @(rg -n -i "analysis quality matches PerfView|分析能力等同 PerfView|most other tools are 1:1" README.md README.zh-CN.md CONTRIBUTING.md docs)
if ($LASTEXITCODE -notin 0, 1) { throw 'rg failed while checking documentation claims.' }
if ($unsupportedClaims.Count -ne 0) { throw "Unsupported blanket parity claims remain:`n$($unsupportedClaims -join "`n")" }
```

Expected: tests pass and the scan finds no unsupported blanket claim. Evidence-qualified view names may still contain the words `PerfView equivalent` only where the governance test accepts a matrix link on the same row.

- [ ] **Step 5: Commit**

```powershell
git add docs/TIME_SEMANTICS.md docs/PRIVACY.md docs/COMPATIBILITY.md tests/WprMcp.Tests/DocumentationGovernanceTests.cs README.md README.zh-CN.md docs/ARCHITECTURE.md CONTRIBUTING.md docs/CAPABILITY_GAPS.md docs/CAPABILITY_GAPS.zh-CN.md CHANGELOG.md
git commit -m "docs(governance): publish evidenced compatibility privacy and capability claims"
```

---

### Task 9: Enforce advisory-tool governance and rehearse the complete final gate (TDD)

**Files:**
- Create: `eng/advisory-tools.v1.json`
- Modify: `tests/WprMcp.Tests/DependencyGovernanceTests.cs`
- Modify: `tests/WprMcp.Tests/ReleaseArtifactTests.cs`
- Modify: `CONTRIBUTING.md`

- [ ] **Step 1: Write failing final-governance tests**

Add:

```text
AdvisoryTools_RequireExactVersionCommandScopeAndPassCriterion
AdvisoryResults_CannotBeMarkedPassedWithoutArtifactHashAndExitCode
FinalGate_ContainsEveryRequiredAcceptanceCommandInOrder
ReleaseArtifactPath_IsProducedOnceAndConsumedEverywhere
NoWorkflowRepublishesAfterPackageSmoke
```

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~DependencyGovernanceTests|FullyQualifiedName~ReleaseArtifactTests"
```

Expected: advisory-tool registry/final command contract is absent.

- [ ] **Step 3: Implement registry and run the final rehearsal**

`eng/advisory-tools.v1.json` is an array of optional tool definitions with `id`, exact `version`, command token array, repository-relative input paths, deterministic pass exit codes/output predicates, and evidence artifact path. An empty array is valid and means no advisory review is claimed. A result may say passed only when the evidence file exists and its hash/exit code match; human “looks good” text is not machine pass evidence.

Add the exact final commands below to `CONTRIBUTING.md`, then run them on one commit. If a command fails, retain the failure log, fix, and restart from locked restore; do not edit a checklist to green.

- [ ] **Step 4: Run full GREEN and immutable-package hash comparison**

```powershell
dotnet restore WprMcp.sln --locked-mode
dotnet restore src/WprMcp/WprMcp.csproj -r win-x64 --locked-mode -p:NuGetLockFilePath=packages.win-x64.lock.json
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --no-build
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "Category!=Package"
dotnet test tests/WprMcp.GoldenTests/WprMcp.GoldenTests.csproj -c Release --no-build
$version=(dotnet msbuild src/WprMcp/WprMcp.csproj -getProperty:Version -p:Configuration=Release).Trim()
$commit=git rev-parse HEAD
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/New-ReleaseArtifact.ps1 -Version $version -Commit $commit
$before=(Get-FileHash artifacts/release/wpa-mcp-win-x64.zip -Algorithm SHA256).Hash
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PackagedServer.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ExpectedVersion $version -ExpectedCommit $commit
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-ReleaseProvenance.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ProvenancePath artifacts/release/wpa-mcp-win-x64.provenance.json
$after=(Get-FileHash artifacts/release/wpa-mcp-win-x64.zip -Algorithm SHA256).Hash
if ($before -ne $after) { throw 'Immutable release package changed after smoke.' }
git diff --check
```

Expected: all deterministic gates pass and hashes match. Run Child 10's live agent policy command for a release candidate; report its actual pass, failure, or validated waiver state. GitHub attestation/upload remains unclaimed until the real tag workflow completes.

- [ ] **Step 5: Commit**

```powershell
git add eng/advisory-tools.v1.json tests/WprMcp.Tests/DependencyGovernanceTests.cs tests/WprMcp.Tests/ReleaseArtifactTests.cs CONTRIBUTING.md
git commit -m "chore(release): enforce final governance and immutable gate rehearsal"
```

---

## Final acceptance commands

The Task 9 sequence is the deterministic local final gate. For a release candidate also run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Run-AgentBenchmarks.ps1 -Mode both -Trials 5 -OutputDirectory artifacts/release-agent-benchmarks
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-AgentBenchmarkPolicy.ps1 -CandidateDirectory artifacts/release-agent-benchmarks
```

After pushing the tag formed from the literal `v` plus the selected version, verify actual external state rather than assuming it:

```powershell
$commit = git rev-parse HEAD
$version = (dotnet msbuild src/WprMcp/WprMcp.csproj -getProperty:Version -p:Configuration=Release).Trim()
$tag = "v$version"
gh run list --workflow release.yml --commit $commit --limit 1 --json databaseId,headSha,conclusion,event,url
git ls-remote origin "refs/tags/$tag" "refs/tags/$tag^{}"
gh release view $tag --json tagName,targetCommitish,assets,url
gh attestation verify artifacts/release/wpa-mcp-win-x64.zip --repo tooluse-labs/wpa-mcp
```

Success requires the release workflow conclusion `success`, tag target equal to the gated commit, release asset digest equal to local provenance, and attestation verification of that exact zip. If these commands were not run or did not succeed, state that the external release is not yet verified.

---

## Dependencies and conflict ownership

- 11A Tasks 1–4 precede Child 1. Once the ADR is committed, later children consume `eng/SelectedPlatform.props`; they do not independently upgrade TFM/MCP/protocol.
- Children 5, 9, and 10 add projects/packages/locks and tests. Child 11B's Task 5 audits and composes them but does not rewrite their test logic.
- Child 5/6 compatibility defaults remain in place only through Child 9/10 evidence generation. Task 5 owns the reviewed switch to v2/ID-only defaults and must preserve the explicit legacy/compatibility flags until the next major version.
- Child 5/9 protocol profile enum and handshake matrix must be generated from the ADR's selected `WprMcpProtocolProfile`/`WprMcpProtocolRevision`, not hardcoded to an unselected candidate.
- Child 9 owns `scripts/Test-PackagedServer.ps1`; Child 11B calls it and owns only artifact creation/provenance/workflows.
- Child 10 owns benchmark policy/evidence and capability matrix. Child 11B owns scheduling/release wiring and documentation links; it cannot weaken thresholds or relabel gaps.
- `src/WprMcp/WprMcp.csproj`, `WprMcp.sln`, package locks, workflows, installer tests, README files, `CONTRIBUTING.md`, and `CHANGELOG.md` are high-conflict files. Land 11A first, then domain children, then 11B; regenerate locks only after all project changes are present.
- Release metadata is not a second publish artifact. Do not restore the historical standalone `.exe` asset or make installers silently fall back to it.

## Final evidence checklist

- One ADR ties selected TFM/MCP/protocol to complete observed proof and rejected-candidate rationale.
- Exact SDK, package versions, normal locks, RID lock, and action SHAs agree across repository and workflows.
- The final no-flag server and packaged artifact use v2 output plus ID-only query references; legacy output and raw query paths require the documented explicit migration switches.
- CI and release call one reusable quality workflow; a tag cannot reach publish without every required gate.
- One zip is published once, smoked, hashed, attested, and uploaded without mutation or republish.
- Tag, version, commit, TFM, SDK, MCP SDK, protocol, package hash, native hashes, and attestation agree.
- Documentation contains no blanket PerfView parity claim and every support status links to executable evidence.
- External advisory reviews, live model runs, waivers, workflow runs, release upload, and attestation are reported only when actually observed.
