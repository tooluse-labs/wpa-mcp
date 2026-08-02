# Real MCP E2E Concurrency and Hostile Input Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove the assembled server through a real child-process stdio connection, including protocol lifecycle, active contract/privacy profiles, the six thread-scoped CPU/wait tools, malicious traces, conversion races, cancellation, deadlines, disconnect, worker/quota failures, non-vacuous fixture assertions, and the exact packaged executable.

**Architecture:** Expand Child 5's `StdioMcpClient` into a byte-observing JSON-RPC harness. Normal tests launch the production DLL. Race/fault tests launch a separate test-host executable that calls the same `McpHost.RunAsync` and replaces only explicitly injectable Child 6–8 runtime boundaries; test controls never become production CLI flags. Required repository fixtures fail when absent or hash-mismatched. Every process, pipe, lease, temporary artifact root, and worker is owned by an async fixture and receives a deterministic timeout and teardown assertion.

**Tech Stack:** C# and xUnit on Windows; the exact TFM/SDK/MCP protocol pinned by Child 11A; `System.Diagnostics.Process`; named pipes/events for test barriers; TraceEvent-backed committed ETL fixtures; self-contained `win-x64` package from Child 11B.

## Accepted capability/evidence amendment (2026-08-01)

The real-process suite must prove that runtime `tools/list` equals the single active catalog and that every paged catalog/capability surface can be enumerated to completion with stable ordering, no duplicates, no omissions, and tamper-/context-mismatch-safe cursors. It must also exercise exact counters and identifiers above `2^53`, limit-minus/exact/plus-one response fitting, explicit truncation/continuation metadata, and stale/unknown/unsafe opaque identifiers without lossy numeric coercion or raw-value leakage.

Symbol-context cases must distinguish an explicit valid context, unknown/stale context, policy denial, and no context; the no-context case performs no disk/network probe and reports unmeasured resolution. These checks supplement the hostile-path matrix below and use the production catalog, serializer, pagination codec, and stdio framing rather than reflection-only surrogates.

## Global Constraints

- Load the exact protocol revision/profile from Child 11A's `eng/SelectedPlatform.props`; this plan must not assume that either the stateful or stateless candidate won.
- Normal lifecycle, schema, selector, fixture, and packaged tests launch the production host. Only deterministic race/fault tests launch `WprMcp.ProtocolTestHost`, which changes injectable services and never production arguments/environment behavior.
- No test sends a non-standard `shutdown` method. Stateful flow closes stdin after initialize/initialized/list/call/cancel; a selected discovery flow follows only the ADR-proven sequence.
- Required repository fixtures, NTFS ACL/reparse support, and the Windows release lane fail visibly when unavailable. Dynamic skip is reserved for separately named optional external environments, never for acceptance evidence.
- Every protocol step has a deterministic timeout; every child and descendant worker is owned by a kill-on-close Job Object; every test asserts teardown.
- Hostile tests distinguish ACL denial, sharing violation, malformed bytes, size, path namespace, reparse, and replacement races. One failure mode is never used as a proxy for another.
- Test probes pause or inject only at exact Child 6–8 boundaries and default to a production no-op. There is no production CLI/environment switch for fault injection.
- Thread execution tests cover all six public tools and preserve Child 2's appended selector parameter compatibility.
- Package smoke consumes the immutable Child 11B zip, hashes it before/after, and never publishes, archives, or mutates it.
- A command's expected pass is documented only after the command actually succeeds; flaky repetition is evidence of a defect, not permission to weaken an assertion.
- A unit test may not pass by conditionally returning when a required fixture or domain observation is absent. Required fixtures fail hard; legitimate unavailable/not-concluded branches assert their exact evidence.

**Spec:** `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` at commit `7ef8ff5`.

**Prerequisites:** Children 2–8 are complete. Child 5 created `tests/WprMcp.ProtocolTests`, `StdioMcpClient`, and real schema smoke. Child 6 provides fail-closed trace access and same-object validation. Child 7 provides generation-keyed single-flight conversion/artifact inspection. Child 8 provides operation cancellation/deadlines and worker isolation. Child 11A fixes the protocol profile.

---

## Fixed test-harness contracts

Expand the Child 5 harness to these exact signatures:

```csharp
namespace WprMcp.ProtocolTests;

internal enum McpProtocolFlow
{
    StatefulInitialize,
    StatelessDiscovery
}

internal sealed record McpProtocolProfile(
    string Revision,
    McpProtocolFlow Flow)
{
    public static McpProtocolProfile LoadSelected(string selectedPlatformPropsPath);
}

internal sealed record StdioServerLaunch(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?> Environment,
    TimeSpan StepTimeout,
    TimeSpan ExitTimeout);

internal sealed record ObservedFrame(
    ReadOnlyMemory<byte> Bytes,
    JsonObject Json,
    DateTimeOffset ReceivedAt);

internal sealed class StdioMcpClient : IAsyncDisposable
{
    public static Task<StdioMcpClient> StartAsync(
        StdioServerLaunch launch,
        CancellationToken cancellationToken = default);

    public Task<JsonObject> NegotiateAsync(
        McpProtocolProfile selectedProfile,
        CancellationToken cancellationToken = default);

    public Task<JsonObject> DiscoverAsync(
        CancellationToken cancellationToken = default);

    public Task NotifyInitializedAsync(CancellationToken cancellationToken = default);

    public Task<JsonObject> RequestAsync(
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default);

    public Task<JsonObject> RequestWithIdAsync(
        JsonNode id,
        string method,
        JsonObject? parameters,
        CancellationToken cancellationToken = default);

    public Task NotifyCancelledAsync(
        JsonNode requestId,
        string reason,
        CancellationToken cancellationToken = default);

    public Task WriteRawAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);

    public Task<IReadOnlyList<JsonObject>> ListAllToolsAsync(
        CancellationToken cancellationToken = default);

    public Task CloseInputAndWaitForExitAsync(CancellationToken cancellationToken = default);
    public Task TerminateAndWaitAsync(CancellationToken cancellationToken = default);

    public IReadOnlyList<ObservedFrame> StdoutFrames { get; }
    public ReadOnlyMemory<byte> RawStdout { get; }
    public IReadOnlyList<string> StderrLines { get; }
    public int? ExitCode { get; }
}
```

The harness reads stdout as UTF-8, newline-delimited JSON-RPC. It retains the original bytes, rejects a BOM, blank line, non-object JSON, trailing bytes, duplicate response ID, and response to an unknown request. It drains stderr concurrently so the child cannot deadlock. Default step timeout is 10 seconds; expensive real-fixture calls opt into 120 seconds; clean exit timeout is 15 seconds. It never sends a `shutdown` method.

Create the hostile-file factory with this exact surface:

```csharp
internal sealed class HostileTraceFactory : IAsyncDisposable
{
    public string Root { get; }
    public string AllowedRoot { get; }
    public string ArtifactRoot { get; }

    public string CreateCorrupt(string fileName, int byteCount);
    public string CreateTruncatedCopy(string sourcePath, string fileName, long bytesToKeep);
    public string CreateWrongExtensionCopy(string sourcePath, string extension);
    public string CreateSparseOversized(string fileName, long logicalLength);
    public FileStream HoldExclusively(string path);
    public AclSnapshot DenyReadDataToCurrentUser(string path);
    public void RestoreAcl(string path, AclSnapshot snapshot);
    public string CreateDirectorySymlinkOutsideAllowedRoot(string linkName, string targetRoot);
    public void ReplaceAtomically(string path, ReadOnlySpan<byte> replacement);
    public string UncPathFor(string localPath);

    public ValueTask DisposeAsync();
}

internal sealed record AclSnapshot(
    string OwnerSid,
    string SecurityDescriptorSddl);
```

The test-only server seam is:

```csharp
namespace WprMcp.Core;

internal enum RuntimeTestPoint
{
    TraceValidated,
    ConversionStarted,
    ConversionBeforePublish,
    AnalysisStarted,
    WorkerStarted
}

internal interface IRuntimeTestProbe
{
    ValueTask ReachAsync(
        RuntimeTestPoint point,
        string operationId,
        CancellationToken cancellationToken);
}

internal sealed class NullRuntimeTestProbe : IRuntimeTestProbe
{
    public ValueTask ReachAsync(
        RuntimeTestPoint point,
        string operationId,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}
```

Production always registers `NullRuntimeTestProbe`; there is no production flag or environment variable that selects another implementation. `tests/WprMcp.ProtocolTestHost` replaces it through `McpHost.RunAsync(args, configureServices, cancellationToken)` and accepts a test-control named-pipe handle inherited from the parent test.

The committed thread fixture contract is generated, never hand-edited:

```csharp
internal sealed record ProtocolFixtureManifest(
    string SchemaVersion,
    string RelativePath,
    string Sha256,
    string CaptureRecipe,
    string WorkloadVersion,
    int Pid,
    int UniqueTid,
    long ProcessStartUs,
    long UniqueThreadStartUs,
    int ReusedTid,
    IReadOnlyList<long> ReusedThreadStartUs,
    int BelowTopTid,
    long BelowTopThreadStartUs,
    long FastStartUs,
    long FastEndUs,
    long SlowStartUs,
    long SlowEndUs);
```

`Capture-ProtocolFixture.ps1` writes the actual PID/TID/window values and uppercase SHA-256 atomically only after its verifier proves: one unique generation, at least two generations of the reused TID, a target ranked below process-wide `top=1`, CSwitch events, CPU samples, domain stacks, and fast/slow markers from the same `(pid,tid)`.

---

## File structure overview

| File | Action | Purpose |
|---|---|---|
| `src/WprMcp/McpHost.cs` | Create | Reusable production host bootstrap with test-only DI callback |
| `src/WprMcp/Program.cs` | Modify | Delegate to `McpHost.RunAsync` |
| `src/WprMcp/Core/TraceAccessPolicy.cs` | Modify | Invoke post-validation no-op probe |
| `src/WprMcp/Core/TraceRegistry.cs` | Modify | Carry probe-safe operation identity |
| `src/WprMcp/Core/ArtifactSingleFlight.cs` | Modify | Invoke source-key acquisition/conversion-leader probes |
| `src/WprMcp/Core/TraceArtifactStore.cs` | Modify | Invoke pre-publish probes and expose deterministic publication diagnostics |
| `src/WprMcp/Worker/WorkerConversionExecutor.cs` | Modify | Invoke worker-conversion start probes |
| `src/WprMcp/Core/AnalysisOperationContext.cs` | Modify | Invoke analysis-start probe and expose cleanup counters |
| `src/WprMcp/Core/RuntimeQuotaManager.cs` | Modify | Expose reservation counters/injected quota boundary |
| `src/WprMcp/Worker/WorkerClient.cs` | Modify | Invoke worker-start probe and expose child identity |
| `src/WprMcp/Worker/WorkerHost.cs` | Modify | Preserve crash/cancellation teardown semantics |
| `src/WprMcp/WprMcp.csproj` | Modify | Add `InternalsVisibleTo` for protocol test host/tests |
| `tests/WprMcp.ProtocolTests/StdioMcpClient.cs` | Modify | Full raw-byte lifecycle, IDs, cancellation, teardown |
| `tests/WprMcp.ProtocolTests/ProtocolLifecycleTests.cs` | Create | Initialize/discover/list/call/cancel/EOF protocol matrix |
| `tests/WprMcp.ProtocolTests/RawStdinBoundaryTests.cs` | Create | Public 100,000-byte stdin boundary and malformed UTF-8 matrix |
| `tests/WprMcp.ProtocolTests/ContractProfileTests.cs` | Create | Legacy/v2/paths/strict and annotation/schema matrix |
| `tests/WprMcp.ProtocolTests/ThreadScopeProtocolTests.cs` | Create | Six-tool schema and real execution matrix |
| `tests/WprMcp.ProtocolTests/HostileTraceFactory.cs` | Create | Required corrupt/path/race inputs |
| `tests/WprMcp.ProtocolTests/HostileInputTests.cs` | Create | Corrupt/truncated/extension/access/size/UNC/reparse/replacement |
| `tests/WprMcp.ProtocolTests/ConversionConcurrencyTests.cs` | Create | Same-server and cross-server single-flight checks |
| `tests/WprMcp.ProtocolTests/CancellationIsolationTests.cs` | Create | Cancellation/deadline/disconnect/crash/quota/oversize cleanup |
| `tests/WprMcp.ProtocolTests/PackagedServerTests.cs` | Create | Exact zip layout, version, native DLL, and stdio smoke |
| `tests/WprMcp.ProtocolTests/ProtocolFixture.cs` | Create | Required fixture/manifest loader and hash verification |
| `tests/WprMcp.ProtocolTests/Fixtures/thread-scope.etl` | Create | Committed deterministic thread selector/window fixture |
| `tests/WprMcp.ProtocolTests/Fixtures/thread-scope.manifest.json` | Create | Generated identity/window/hash oracle |
| `tests/WprMcp.ProtocolTests/Fixtures/ThreadScopeCapture.wprp` | Create | CPU/CSwitch/Thread/ReadyThread/stack/custom-marker profile |
| `tests/WprMcp.ProtocolTests/Fixtures/Capture-ProtocolFixture.ps1` | Create | Privileged atomic capture/verify workflow |
| `tools/threadscopefixture/threadscopefixture.csproj` | Create | Deterministic marker/CPU/wait/TID-reuse workload |
| `tools/threadscopefixture/Program.cs` | Create | Workload and machine-readable capture summary |
| `tests/WprMcp.ProtocolTestHost/WprMcp.ProtocolTestHost.csproj` | Create | Child process with injected test probe/fault services |
| `tests/WprMcp.ProtocolTestHost/Program.cs` | Create | Named-pipe controlled host bootstrap |
| `tests/WprMcp.ProtocolTestHost/NamedPipeRuntimeTestProbe.cs` | Create | Deterministic pause/crash/failure protocol |
| `tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj` | Modify | References/content/Windows-only traits |
| `tests/WprMcp.Tests/RequiredFixtureCatalog.cs` | Create | Hard requirements for the committed CPU/wait/file-I/O/mmap fixtures |
| `tests/WprMcp.Tests/TestAssertionGovernanceTests.cs` | Create | Source guard against conditional `return;` passes in the eleven audited suites |
| `WprMcp.sln` | Modify | Add test host and thread workload projects |
| `tests/WprMcp.Tests/AssemblyInfo.cs` | Delete in Task 5 only | Remove global parallel disable after isolation proof |

---

### Task 1: Complete the real stdio protocol lifecycle harness (TDD)

**Files:**
- Create: `src/WprMcp/McpHost.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/WprMcp.csproj`
- Modify: `tests/WprMcp.ProtocolTests/StdioMcpClient.cs`
- Create: `tests/WprMcp.ProtocolTests/ProtocolLifecycleTests.cs`
- Create: `tests/WprMcp.ProtocolTests/RawStdinBoundaryTests.cs`

- [ ] **Step 1: Write failing lifecycle tests**

Add exact tests:

```text
SelectedProfile_NegotiateListCallCancelAndEofExit
SelectedProtocol_NegotiatesExactVersionAndCapabilities
RequestIds_StringAndNumberRoundTripExactly
Stdout_HasNoBomBlankLogOrNonFrameBytes
UnknownJsonRpcMethod_ReturnsMethodNotFoundProtocolError
UnknownTool_ReturnsProtocolErrorSelectedByContract
CloseStdin_ServerExitsCleanlyWithoutShutdownMethod
HarnessTermination_KillsServerAndDescendantWorkers
RawStdin_LimitMinusOneAndExactReturnResponsePlusOneExits64BeforeDispatch
RawStdin_ConfiguredLowerCapMinusOneAndExactReturnResponsePlusOneExits64BeforeDispatch
RawStdin_RequestIdSerialized127And128RoundTrip129Exits65WithoutResponse
RawStdin_MultibytePayloadUsesUtf8ByteCount
RawStdin_CrlfCountsCarriageReturnAndResetsOnlyOnLf
RawStdin_PartialEofNeverInvokesTool
RawStdin_BomAndInvalidUtf8NeverInvokeTool
```

Load `McpProtocolProfile.LoadSelected("eng/SelectedPlatform.props")`. If its flow is `StatelessDiscovery`, add `SelectedProfile_DiscoveryAndPerRequestMetadata`; if it is `StatefulInitialize`, assert the discovery branch is not executed. Do not emulate the unselected protocol or hardcode a candidate revision in this plan. Build valid selected-profile discovery/list requests padded with JSON whitespace so their complete newline-terminated raw frames are exactly 99,999, 100,000, and 100,001 bytes under the default cap. Launch a second real host with `WPRMCP_MAX_JSONRPC_REQUEST_BYTES=8192` and a compatible `WPRMCP_MAX_TOOL_ARGUMENT_BYTES=4096`, then repeat at 8,191, 8,192, and 8,193 bytes. Build valid string IDs whose exact serialized UTF-8 token is 127, 128, and 129 bytes, including escaped and multibyte strings: 127/128 must round-trip exactly; 129 must exit 65 with empty stdout before dispatch or any trace/file/network side effect. Separately send numeric IDs `long.MinValue`, `0`, and `long.MaxValue` and require exact correlation; the selected SDK's numeric request-ID domain is `Int64`, so no oversized numeric token is fabricated. Count `\r` in CRLF and reset only on `\n`. For partial EOF, BOM, and invalid UTF-8, permit only the selected SDK's parse error or bounded connection close; never a `tools/call` result or runtime probe invocation.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "FullyQualifiedName~ProtocolLifecycleTests|FullyQualifiedName~RawStdinBoundaryTests"
```

Expected: the minimal Child 5 client lacks raw-byte/cancellation/EOF assertions and `Program` cannot be bootstrapped with an injected host configuration.

- [ ] **Step 3: Implement host and harness**

Create:

```csharp
internal static class McpHost
{
    internal static Task<int> RunAsync(
        string[] args,
        Action<HostApplicationBuilder>? configureServices = null,
        CancellationToken cancellationToken = default);
}
```

Move current server setup from `Program.Main` without changing production defaults. `Program.Main` retains `--version` and CLI routing, then calls `McpHost.RunAsync`. The harness starts a process with `RedirectStandardInput/Output/Error=true`, `StandardOutputEncoding/StandardErrorEncoding=UTF8`, `CreateNoWindow=true`, and an explicit working directory. Associate the process with a Windows Job Object configured `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` so teardown owns descendants.

Implement exact ID correlation and a single reader task. `WriteRawAsync` writes bytes without `StreamWriter`, encoding, newline insertion, or JSON normalization; `RawStdout` retains every byte even when it is not a valid frame. On stdin EOF, wait up to `ExitTimeout`; if it does not exit, kill the job and fail the test with captured bytes/frames/stderr. Boundary tests launch the real production host and assert Child 5's exact behavior at both configured caps: exact cap receives the selected correlated discovery/list response, cap plus one exits 64 before SDK deserialization and emits no tool response, accepted 127/128-byte string IDs echo byte-for-byte, a 129-byte string ID exits 65 with no stdout or side effect, and all three legal numeric extrema correlate exactly. Each hostile termination is followed by a fresh process completing a legal selected-profile list request.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "FullyQualifiedName~ProtocolLifecycleTests|FullyQualifiedName~RawStdinBoundaryTests"
```

Expected: all lifecycle tests pass and no test leaves a `WprMcp` or worker process.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/McpHost.cs src/WprMcp/Program.cs src/WprMcp/WprMcp.csproj tests/WprMcp.ProtocolTests/StdioMcpClient.cs tests/WprMcp.ProtocolTests/ProtocolLifecycleTests.cs tests/WprMcp.ProtocolTests/RawStdinBoundaryTests.cs
git commit -m "test(protocol): drive the real MCP lifecycle over raw stdio"
```

---

### Task 2: Prove active contract, privacy, annotation, and six-tool schema profiles (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTests/ContractProfileTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json`

- [ ] **Step 1: Add failing profile tests**

Add:

```text
LegacyProfile_ListAndCallUseLegacyShape
V2Profile_ListAndCallUseEnvelopeShape
PathsProfile_RedactsEveryPathSentinelAcrossStdoutAndStderr
StrictProfile_RedactsMarkerHostIpRegistryAndPathSentinels
AnnotationMatrix_MatchesReadOnlyIdempotentOpenWorldAndDestructiveBehavior
SixThreadTools_InputSchemasExposePidTidAndLifetimeSelectors
SixThreadTools_TidWithoutPidReturnsFailedInvalidArgument
SucceededPartialFailedInvalidArgumentUnknownToolAndCancellationUseCorrectLayers
```

Protocol-layer assertions are exact: malformed JSON-RPC/unknown method/unknown tool produce JSON-RPC errors as selected in Child 5; valid tool failures produce `result.isError=true` plus `status=failed`; partial results use `isError=false`; cancellation has either one failed/cancelled result or no result if protocol cancellation suppresses it, never a later success.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ContractProfileTests
```

Expected: failures expose any divergence between reflection snapshots and the real server or missing selectors on one of the six schemas.

- [ ] **Step 3: Fix only observed production gaps**

Collect every `tools/list` page from the actual server. Validate schemas before snapshot comparison. Assert the six names exactly:

```text
wait_analysis
wait_top_stacks
wait_caller_callee
cpu_precise_analysis
cpu_top_functions
cpu_caller_callee
```

For each, require `pid`, `tid`, `processStartUs`, `threadStartUs`, `startUs`, and `endUs` properties with integer-or-null schema. Do not encode the cross-field `tid -> pid` rule only in JSON Schema; the real execution assertion is authoritative.

Update snapshots only after inspecting the diff. Any changed tool name, annotation, required input, stable section, or output field needs a contract note; never accept wholesale snapshot regeneration.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "FullyQualifiedName~ContractProfileTests|FullyQualifiedName~ToolSchemaContractTests"
```

Expected: all active-profile tests pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTests/ContractProfileTests.cs tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json
git commit -m "test(protocol): cover active contract privacy and annotation profiles"
```

---

### Task 3: Add the real six-tool thread-scope execution fixture (TDD)

**Files:**
- Create: `tools/threadscopefixture/threadscopefixture.csproj`
- Create: `tools/threadscopefixture/Program.cs`
- Create: `tests/WprMcp.ProtocolTests/Fixtures/ThreadScopeCapture.wprp`
- Create: `tests/WprMcp.ProtocolTests/Fixtures/Capture-ProtocolFixture.ps1`
- Create: `tests/WprMcp.ProtocolTests/Fixtures/thread-scope.etl`
- Create: `tests/WprMcp.ProtocolTests/Fixtures/thread-scope.manifest.json`
- Create: `tests/WprMcp.ProtocolTests/ProtocolFixture.cs`
- Create: `tests/WprMcp.ProtocolTests/ThreadScopeProtocolTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj`
- Modify: `WprMcp.sln`

- [ ] **Step 1: Write failing fixture/execution tests**

Add:

```text
RequiredThreadFixture_ExistsAndMatchesManifestHash
RequiredThreadFixture_HasUniqueReusedBelowTopAndFastSlowEvidence
SixThreadTools_UniqueThreadReturnsOnlyRequestedInstance
SixThreadTools_ReusedTidWithoutStartSelectorsIsAmbiguous
SixThreadTools_StartSelectorsDisambiguateEachGeneration
SixThreadTools_RequestedBelowTopThreadSurvivesTopOne
SixThreadTools_MissingSymbolsChangeQualityNotSelectorOrTotals
```

Parameterize the last five across all six tool names. Caller/callee obtains its focus function from that same thread's preceding top-stacks/top-functions result, never from a process-wide row.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ThreadScopeProtocolTests
```

Expected: required fixture files/types do not exist.

- [ ] **Step 3: Build and capture the deterministic fixture**

`threadscopefixture` creates one marked worker that alternates a 250 ms CPU spin (`FastStart/FastEnd`) and a 250 ms alertable delay (`SlowStart/SlowEnd`), ten hotter competing threads, one low-ranked target thread, then closes a marked thread and creates/joins replacement threads until Windows reuses its TID. It prints one JSON summary containing process ID, thread IDs, marker sequence, and workload version; it exits nonzero if reuse is not observed within 100,000 creations.

The WPRP enables CPU sampling, CSwitch, ReadyThread, process/thread start-stop, CSwitch/Profile stackwalk, image load, and the workload EventSource. The capture script requires elevation, captures to a candidate, invokes a verifier that resolves marker timestamps and thread generations, runs all six analyzers, writes the manifest from observed values and `Get-FileHash`, and atomically replaces `thread-scope.etl` plus its manifest only when every invariant passes. The script never writes an expected hash before measuring the final file.

Run once from an elevated PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/WprMcp.ProtocolTests/Fixtures/Capture-ProtocolFixture.ps1
```

Commit the resulting ETL and generated manifest. In CI, absence/hash mismatch is a failure, never a dynamic skip.

- [ ] **Step 4: Run GREEN**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ThreadScopeProtocolTests
```

Expected: all six tools pass unique, ambiguous, disambiguated, below-TopN, and symbol-degradation cases.

- [ ] **Step 5: Commit**

```powershell
git add tools/threadscopefixture/threadscopefixture.csproj tools/threadscopefixture/Program.cs tests/WprMcp.ProtocolTests/Fixtures/ThreadScopeCapture.wprp tests/WprMcp.ProtocolTests/Fixtures/Capture-ProtocolFixture.ps1 tests/WprMcp.ProtocolTests/Fixtures/thread-scope.etl tests/WprMcp.ProtocolTests/Fixtures/thread-scope.manifest.json tests/WprMcp.ProtocolTests/ProtocolFixture.cs tests/WprMcp.ProtocolTests/ThreadScopeProtocolTests.cs tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj WprMcp.sln
git commit -m "test(thread): add real six-tool selector and TopN protocol fixture"
```

---

### Task 4: Cover corrupt and adversarial trace inputs deterministically (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTestHost/WprMcp.ProtocolTestHost.csproj`
- Create: `tests/WprMcp.ProtocolTestHost/Program.cs`
- Create: `tests/WprMcp.ProtocolTestHost/NamedPipeRuntimeTestProbe.cs`
- Create: `tests/WprMcp.ProtocolTests/HostileTraceFactory.cs`
- Create: `tests/WprMcp.ProtocolTests/HostileInputTests.cs`
- Modify: `src/WprMcp/McpHost.cs`
- Modify: `src/WprMcp/Core/TraceAccessPolicy.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `WprMcp.sln`

- [ ] **Step 1: Write failing hostile-input tests**

Add exact tests:

```text
CorruptTrace_ReturnsTraceConversionFailedWithoutArtifact
TruncatedTrace_ReturnsTraceConversionFailedWithoutArtifact
WrongExtension_ReturnsTraceAccessDeniedBeforeOpen
ExclusivelyLockedTrace_ReturnsTraceAccessDenied
AclDeniedTrace_ReturnsTraceAccessDeniedAndRestoresAcl
SparseOversizedTrace_ReturnsTraceAccessDeniedBeforeConversion
UncPath_ReturnsTraceAccessDeniedBeforeNetworkAccess
ReparseComponent_ReturnsTraceAccessDeniedBeforeTargetOpen
ReplaceAfterValidation_ParsesOriginalValidatedObjectOnly
UnknownTraceId_NeverFallsBackToRawPath
RejectedInput_CreatesNoSourceSidecarArtifactOrNetworkAttempt
```

All tests assert stable v2 error code/message/retryability, zero `.etlx`/`.new` next to the source, and no privacy sentinel in stderr.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~HostileInputTests
```

Expected: deterministic replacement barrier/test host and hostile factory do not exist.

- [ ] **Step 3: Implement test-only probe and hostile factory**

`ProtocolTestHost` passes a DI callback to `McpHost.RunAsync`; production `Program` cannot select the probe. `NamedPipeRuntimeTestProbe` writes `{ point, operationId }`, waits for an explicit `{ operationId, action }`, and supports only `continue`, `throw_io`, `throw_quota`, and `crash_worker`. The replacement test pauses immediately after the validated handle/private snapshot is established, replaces the source path atomically, resumes, and proves output matches the original SHA rather than replacement bytes.

`HostileTraceFactory.CreateSparseOversized` calls Windows `FSCTL_SET_SPARSE` before `SetLength`, so the 64 GiB+1 logical test does not consume 64 GiB. `CreateDirectorySymlinkOutsideAllowedRoot` is required on the Windows CI runner; inability to create the link fails with an actionable environment error rather than skipping.

Add `DenyReadDataToCurrentUser(string path)` returning an `AclSnapshot` and `RestoreAcl(string path, AclSnapshot snapshot)`. The ACL test runs on an NTFS temp directory, captures owner and the complete original DACL, adds an explicit deny `FileSystemRights.ReadData` ACE for the current SID, performs the real stdio call, and restores owner/DACL in `finally` using retained `WriteDac`/owner authority. Failure to exercise or restore the ACL fails the Windows release lane; the separate exclusive-lock test remains a sharing-violation case and is not treated as ACL evidence.

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: all hostile cases pass and temp/artifact roots are empty after disposal.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTestHost/WprMcp.ProtocolTestHost.csproj tests/WprMcp.ProtocolTestHost/Program.cs tests/WprMcp.ProtocolTestHost/NamedPipeRuntimeTestProbe.cs tests/WprMcp.ProtocolTests/HostileTraceFactory.cs tests/WprMcp.ProtocolTests/HostileInputTests.cs src/WprMcp/McpHost.cs src/WprMcp/Core/TraceAccessPolicy.cs src/WprMcp/Core/TraceRegistry.cs WprMcp.sln
git commit -m "test(security): exercise hostile traces through a real MCP child"
```

---

### Task 5: Prove in-process and cross-process conversion single-flight, then restore xUnit parallelism (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTests/ConversionConcurrencyTests.cs`
- Modify: `src/WprMcp/Core/ArtifactSingleFlight.cs` only if a test exposes a defect
- Modify: `src/WprMcp/Core/TraceArtifactStore.cs` only if a test exposes a defect
- Modify: `src/WprMcp/Worker/WorkerConversionExecutor.cs` only if a test exposes a defect
- Modify: `src/WprMcp/Core/TraceRegistry.cs` only if a test exposes a defect
- Delete: `tests/WprMcp.Tests/AssemblyInfo.cs`

- [ ] **Step 1: Write failing concurrency/isolation tests**

Add:

```text
SameServer_SixConcurrentLoadsPublishOneGeneration
TwoServers_SameSourcePublishOneGeneration
CancelledLeader_AllowsWaitingFollowerToRetry
FailedConversion_LeavesNoPublishedOrNewArtifact
ParallelFixtureTests_UseIsolatedArtifactRootsAndNoSourceSidecars
```

Count completed generation manifests in the isolated artifact root; require exactly one matching source hash/length/TraceEvent version/options version and zero `.new` files. Do not infer single-flight merely from equal trace IDs.

- [ ] **Step 2: Run RED with parallel execution enabled for this class**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ConversionConcurrencyTests
```

Expected: any shared `.etlx.new` or source-side artifact behavior fails.

- [ ] **Step 3: Fix isolation, then remove the global disable**

Use a fresh source copy and artifact root per test, except the two intentional same-source race tests. Repair only an observed defect in `ArtifactSingleFlight`, `TraceArtifactStore`, `WorkerConversionExecutor`, or `TraceRegistry`, according to which deterministic barrier fails. Once all five tests and the existing unit suite pass repeatedly with Child 7's controlled artifact store (which no longer writes source-side `.etlx.new`), delete `tests/WprMcp.Tests/AssemblyInfo.cs`. If an existing test still writes a source sidecar, fix the shared registry/artifact boundary rather than serializing or individually skipping it.

- [ ] **Step 4: Run GREEN repeatedly**

```powershell
1..5 | ForEach-Object { dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ConversionConcurrencyTests }
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release
```

Expected: five concurrency passes and one fully parallel unit-suite pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTests/ConversionConcurrencyTests.cs src/WprMcp/Core/ArtifactSingleFlight.cs src/WprMcp/Core/TraceArtifactStore.cs src/WprMcp/Worker/WorkerConversionExecutor.cs src/WprMcp/Core/TraceRegistry.cs tests/WprMcp.Tests/AssemblyInfo.cs
git commit -m "test(runtime): prove conversion single-flight and restore parallel tests"
```

---

### Task 6: Prove cancellation, deadline, disconnect, worker crash, quota, and cleanup (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTests/CancellationIsolationTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/RawStdinBoundaryTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/StdioMcpClient.cs`
- Modify: `src/WprMcp/Core/AnalysisOperationContext.cs`
- Modify: `src/WprMcp/Core/RuntimeQuotaManager.cs`
- Modify: `src/WprMcp/Worker/WorkerClient.cs`
- Modify: `src/WprMcp/Worker/WorkerHost.cs`
- Modify: `tests/WprMcp.ProtocolTestHost/NamedPipeRuntimeTestProbe.cs`

- [ ] **Step 1: Write failing isolation tests**

Add:

```text
CancellationBeforeEvidence_ReturnsFailedCancelledOrProtocolSuppressionNeverSuccess
CancellationAfterOneCompositeSection_ReturnsPartialAndStopsRemainingSections
Deadline_UsesMonotonicTimeAndReleasesEveryReservation
ClientDisconnect_CancelsOperationAndLeavesNoWorkerOrArtifact
WorkerCrash_ReturnsStableFailureAndNextRequestUsesFreshWorker
ArtifactQuotaFull_ReturnsBudgetExceededAndReleasesReservation
OversizedResponse_ReturnsBoundedTruncatedOrFixedFailureFrame
EveryFault_LeavesNoBackgroundCpuNetworkLeaseQuotaOrTempDirectory
RawStdin_OversizeSegmentedAndUnterminatedInputTearsDownWithinExitTimeout
RawStdin_LoweredConfiguredCapAndRequestIdBoundariesHaveNoSideEffects
RawStdin_AllHostileFramesHaveNoTraceFilesystemOrNetworkSideEffects
RawStdin_AfterHostileExitFreshProcessHandlesLegalRequest
```

After each case, poll only the server's explicit diagnostics/counters (active operations/workers/leases/quota/temp artifacts) until all are zero; do not use a sleep as evidence. Once the transport cancellation token is observed, assert no terminal success or error frame with that request ID is ever emitted; operation-local cancellation may return Child 5's bounded cancelled/partial result only while the response transport remains valid. Re-run the entire Task 1 raw-frame corpus with one-byte and 4,096-byte writes: default 99,999/100,000/100,001; configured 8,191/8,192/8,193; serialized string ID 127/128/129 including escaped/multibyte strings; numeric IDs `long.MinValue`/`0`/`long.MaxValue`; CRLF; multibyte UTF-8; partial EOF; BOM; invalid UTF-8; and both default/configured cap-plus-one without a terminating newline. Snapshot the isolated trace/artifact/temp roots, count runtime-probe reaches, and use a loopback-deny/counting network handler; oversize frames, 129-byte string IDs, partial EOF, BOM, and invalid UTF-8 must produce no trace open, filesystem mutation, DNS/connect attempt, worker, lease, or tool result. Cap-plus-one exits 64, string-ID byte 129 exits 65, both emit empty stdout and one fixed redacted stderr code, every offending process exits within `ExitTimeout`, all legal numeric extrema correlate exactly, and a newly launched process then negotiates and completes one legal list request.

- [ ] **Step 2: Run RED**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "FullyQualifiedName~CancellationIsolationTests|FullyQualifiedName~RawStdinBoundaryTests"
```

Expected: missing deterministic barriers or leaked runtime state identifies the exact Child 8 gap.

- [ ] **Step 3: Implement/fix cancellation-safe cleanup**

Place probe calls at conversion start/before publish, analysis start, and worker start. Use `await using`/`finally` so cancellation, probe exception, quota failure, process exit, and worker crash all release leases and reservations. The test host's `crash_worker` action terminates only the worker process after `WorkerStarted`; the server must survive and create a new worker for the next request.

For disk-full/quota, set a deliberately small isolated artifact-store quota and inject `throw_quota` before publication; do not attempt to fill the CI disk. For raw stdin, use the same Child 5 `JsonRpcFrameLimitingStream` and pre-dispatch request-ID guard through the production/test-host bootstrap; do not add a second limit in the harness. Stop reading and tear down at the active configured request cap plus one even when it arrives across many writes or has no newline; independently tear down on serialized request-ID byte 129. Treat JSON-RPC parse-error frames as protocol output, not tool results; assert no `result` for the hostile request ID and no side-effect counter changes. Fresh-process recovery proves a bad connection does not poison persisted artifacts or global runtime state.

- [ ] **Step 4: Run GREEN**

Run the focused command again. Expected: all fault paths pass and cleanup counters return to zero within their deterministic timeout.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTests/CancellationIsolationTests.cs tests/WprMcp.ProtocolTests/RawStdinBoundaryTests.cs tests/WprMcp.ProtocolTests/StdioMcpClient.cs tests/WprMcp.ProtocolTestHost/NamedPipeRuntimeTestProbe.cs src/WprMcp/Core/AnalysisOperationContext.cs src/WprMcp/Core/RuntimeQuotaManager.cs src/WprMcp/Worker/WorkerClient.cs src/WprMcp/Worker/WorkerHost.cs
git commit -m "test(isolation): cover cancellation worker faults quotas and cleanup"
```

---

### Task 7: Replace all 27 vacuous conditional-return tests with required evidence (TDD)

**Files:**
- Create: `tests/WprMcp.Tests/RequiredFixtureCatalog.cs`
- Create: `tests/WprMcp.Tests/TestAssertionGovernanceTests.cs`
- Modify: `tests/WprMcp.Tests/BlockedTimeStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`
- Modify: `tests/WprMcp.Tests/DiskIoStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/FileIoAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/FileIoStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/ImageLoadAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/ImageLoadStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/MetaToolsTests.cs`
- Modify: `tests/WprMcp.Tests/PageFaultStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/ProcessCreateTimingAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/WaitAnalysisTests.cs`

- [ ] **Step 1: Write the failing fixture and source-governance tests**

Add exact tests:

```text
RequiredFixtureCatalog_CpuWaitFileIoAndMmapFixturesExistAndAreNonEmpty
RequiredFixtureCatalog_FixturesExposePromisedDomainCapabilities
TargetedSuites_ContainNoConditionalVoidReturn
TargetedSuites_ContainNoBareVoidReturn
```

`TestAssertionGovernanceTests` contains the exact eleven relative paths above, locates the repository root, reads each file, and fails with file/line evidence for a conditional void return or any bare void return. It constructs the searched token from separate `"return"` and `";"` literals so the acceptance `rg` does not match the governance test itself. It does not scan generated code or use a skip. `RequiredFixtureCatalog` exposes only `SmallCpu`, `SmallWaitBound`, `SmallFileIo`, and `SmallMmap`; each accessor asserts that the committed file exists and has positive length before returning its path. The capability test opens those required bytes and asserts the catalog promises: CPU samples for `SmallCpu`; CSwitch plus wait-domain stacks for `SmallWaitBound`; FileIO events/stacks for `SmallFileIo`; and process/image-load/hard-fault events for `SmallMmap`.

- [ ] **Step 2: Run RED and record the exact 27 holes**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter FullyQualifiedName~TestAssertionGovernanceTests
$conditionalReturns = @(rg -n "return;" tests/WprMcp.Tests -g "*.cs")
if ($LASTEXITCODE -notin 0, 1) { throw 'rg failed while auditing unit-test returns.' }
if ($conditionalReturns.Count -ne 27) { throw "Expected the reviewed 27 vacuous returns before remediation; observed $($conditionalReturns.Count)." }
```

Expected: the governance tests fail and the source audit reports exactly the 27 reviewed lines, all in the eleven named suites. If the count differs, inspect the actual lines and update the remediation set before editing; do not bless a new early return.

- [ ] **Step 3: Replace every early pass with a positive fixture or explicit unavailable assertion**

Make these exact conversions:

| Suites | Required behavior |
|---|---|
| `BlockedTimeStackAnalysisTests`, `WaitAnalysisTests` | Use `SmallWaitBound` for positive wait rows, histogram, caller/callee, named-reason, and PID-filter cases. Hard-assert rows/totals and choose a PID from the observed wait rows; do not search for an optional non-resident process. |
| `FileIoAnalysisTests`, `FileIoStackAnalysisTests` | Use required `SmallFileIo`; hard-assert event times, bytes, rows, histogram, and caller/callee evidence because this fixture's catalog contract includes FileIO. |
| `ImageLoadAnalysisTests`, `MetaToolsTests`, `PageFaultStackAnalysisTests`, `ProcessCreateTimingAnalysisTests` | Replace every `File.Exists` return with the required `SmallMmap`/`SmallFileIo` accessor. Hard-assert the mmap process spawner, image-load/hard-fault rows, and advertised capabilities that those fixtures are committed to prove. |
| `ImageLoadStackAnalysisTests` | Use `SmallMmap` for positive load/histogram cases. If the pinned capture has loads but no domain stacks, assert `DomainStackCoverageState.NoStacks` and the explicit method-attribution-unavailable evidence; otherwise assert the caller/callee shape. Both branches contain assertions and neither returns early. |
| `DiskIoStackAnalysisTests` | Physical DiskIO may legitimately be absent from the file-I/O capture. In the zero-event branch assert zero totals/rows plus `DomainStackCoverageState.NoEvents` and the exact DiskIO-unavailable warning; in the positive branch assert histogram/caller-callee shape. Neither branch may return early. |
| `DiagnoseToolsTests` | When slow-startup candidates are absent, assert `NotConcluded` code `no_candidates`, preserved provenance, and the executed prerequisite calls. When present, run the existing candidate/wait assertions in the `else` branch. |

Do not turn a missing positive fixture capability into an unavailable branch merely to get green. If `SmallWaitBound`, `SmallFileIo`, or `SmallMmap` violates its stated catalog contract, repair/recapture that pinned fixture through its existing capture recipe and review its hash/provenance before rerunning.

- [ ] **Step 4: Run GREEN and prove no bare test return remains**

```powershell
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --filter "FullyQualifiedName~TestAssertionGovernanceTests|FullyQualifiedName~BlockedTimeStackAnalysisTests|FullyQualifiedName~DiagnoseToolsTests|FullyQualifiedName~DiskIoStackAnalysisTests|FullyQualifiedName~FileIoAnalysisTests|FullyQualifiedName~FileIoStackAnalysisTests|FullyQualifiedName~ImageLoadAnalysisTests|FullyQualifiedName~ImageLoadStackAnalysisTests|FullyQualifiedName~MetaToolsTests|FullyQualifiedName~PageFaultStackAnalysisTests|FullyQualifiedName~ProcessCreateTimingAnalysisTests|FullyQualifiedName~WaitAnalysisTests"
$remainingReturns = @(rg -n "return;" tests/WprMcp.Tests -g "*.cs")
if ($LASTEXITCODE -notin 0, 1) { throw 'rg failed while verifying unit-test returns.' }
if ($remainingReturns.Count -ne 0) { throw "Bare unit-test returns remain:`n$($remainingReturns -join "`n")" }
```

Expected: all focused tests pass and the scan returns zero lines. A fixture/capability mismatch is a red test with actionable evidence, never a dynamic skip or silent pass.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.Tests/RequiredFixtureCatalog.cs tests/WprMcp.Tests/TestAssertionGovernanceTests.cs tests/WprMcp.Tests/BlockedTimeStackAnalysisTests.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs tests/WprMcp.Tests/DiskIoStackAnalysisTests.cs tests/WprMcp.Tests/FileIoAnalysisTests.cs tests/WprMcp.Tests/FileIoStackAnalysisTests.cs tests/WprMcp.Tests/ImageLoadAnalysisTests.cs tests/WprMcp.Tests/ImageLoadStackAnalysisTests.cs tests/WprMcp.Tests/MetaToolsTests.cs tests/WprMcp.Tests/PageFaultStackAnalysisTests.cs tests/WprMcp.Tests/ProcessCreateTimingAnalysisTests.cs tests/WprMcp.Tests/WaitAnalysisTests.cs
git commit -m "test(fixtures): replace vacuous returns with required evidence"
```

---

### Task 8: Author the immutable-package harness and hand real execution to Child 11B (TDD)

**Files:**
- Create: `tests/WprMcp.ProtocolTests/PackagedServerTests.cs`
- Create: `tests/WprMcp.ProtocolTests/PackageHarnessContractTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj`
- Create: `scripts/Test-PackagedServer.ps1`

- [ ] **Step 1: Write the package test first**

Add a required `Category=Package` test named:

```text
ImmutableZip_HasNativeLayoutVersionAndRealStdioSmoke
```

It reads `WPRMCP_PACKAGE_ZIP`, fails if missing, hashes the zip before and after, extracts to a new temp root, requires exactly `root/bin/wpa-mcp.exe`, `root/native/amd64/msdia140.dll`, and `root/native/amd64/KernelTraceControl.dll`, runs `--version`, then runs initialize/list/one `inspect_trace` call/EOF exit through `StdioMcpClient`. It asserts the zip hash is unchanged.

Add non-package `PackageHarnessContractTests` that inspect the test/script contract and require: the package test is tagged `Category=Package`; missing `WPRMCP_PACKAGE_ZIP` fails rather than skips; the script invokes only that category; and neither file contains `dotnet publish`, archive creation, release upload, or zip mutation.

- [ ] **Step 2: Run RED against a deliberately invalid zip path**

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "FullyQualifiedName~PackageHarnessContractTests"
$env:WPRMCP_PACKAGE_ZIP = Join-Path $PWD 'artifacts/release/missing.zip'
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "Category=Package"
```

Expected: the contract test fails until the script exists, and the real package test gives a clear failure naming the missing immutable package. The latter is excluded from every pre-11B aggregate command; Child 11B Task 6 supplies its first real zip and turns that integration test green.

- [ ] **Step 3: Implement the package entry point**

Create `scripts/Test-PackagedServer.ps1`:

```powershell
param(
    [Parameter(Mandatory)][string]$PackagePath,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$ExpectedCommit
)
```

It resolves the three arguments, sets `WPRMCP_PACKAGE_ZIP`, `WPRMCP_EXPECTED_VERSION`, and `WPRMCP_EXPECTED_COMMIT`, then invokes only the package-category test. It never calls `dotnet publish`, `Compress-Archive`, or changes the zip. Child 11B will call this script on the artifact it later uploads.

- [ ] **Step 4: Run the pre-11B GREEN contract gate and defer real-package execution**

```powershell
dotnet build tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "FullyQualifiedName~PackageHarnessContractTests"
```

Expected: the package test and script compile, the non-package contract test passes, and no artifact is fabricated. Do not run `Category=Package` in Child 9's aggregate gate. Child 11B Task 6 owns the first GREEN execution against the immutable candidate it creates in that same task.

- [ ] **Step 5: Commit**

```powershell
git add tests/WprMcp.ProtocolTests/PackagedServerTests.cs tests/WprMcp.ProtocolTests/PackageHarnessContractTests.cs tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj scripts/Test-PackagedServer.ps1
git commit -m "test(package): run native-layout and MCP smoke on immutable zip"
```

---

## Acceptance commands

Run from `D:\wpa-mcp`:

```powershell
dotnet restore WprMcp.sln --locked-mode
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test tests/WprMcp.Tests/WprMcp.Tests.csproj -c Release --no-build
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "Category!=Package"
1..3 | ForEach-Object { dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --no-build --filter "FullyQualifiedName~ConversionConcurrencyTests|FullyQualifiedName~CancellationIsolationTests" }
$traceCacheHits = @(rg -n "TraceCache" src/WprMcp)
if ($LASTEXITCODE -notin 0, 1) { throw "rg failed while checking the retired TraceCache type" }
if ($traceCacheHits.Count -ne 0) { throw "Production code still references retired TraceCache:`n$($traceCacheHits -join "`n")" }
$vacuousTestReturns = @(rg -n "return;" tests/WprMcp.Tests -g "*.cs")
if ($LASTEXITCODE -notin 0, 1) { throw "rg failed while checking unit-test returns" }
if ($vacuousTestReturns.Count -ne 0) { throw "Bare unit-test returns remain:`n$($vacuousTestReturns -join "`n")" }
git diff --check
```

Deferred Child 11B Task 6 command (not part of Child 9 acceptance):

```powershell
$version = (dotnet msbuild src/WprMcp/WprMcp.csproj -getProperty:Version -p:Configuration=Release).Trim()
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/Test-PackagedServer.ps1 -PackagePath artifacts/release/wpa-mcp-win-x64.zip -ExpectedVersion $version -ExpectedCommit (git rev-parse HEAD)
```

Expected: all commands succeed; stdout captured by every protocol test contains only valid JSON-RPC frames; no source directory contains `.etlx` or `.etlx.new`; process/job and runtime counters return to zero.

---

## Dependencies and conflict ownership

- Child 5 creates the protocol project/client. This plan expands those exact files; it must not create a competing harness or schema normalizer.
- Child 2 owns CPU/wait selection and TopN correctness. Task 3 exposes regressions through all six public tools but fixes selector logic in Child 2's shared accumulator/resolver, not in test adapters.
- Child 6 owns trace path, UNC/reparse, same-object, and trace-ID policy. Task 4 may add only the no-op probe call and tests; security logic remains in Child 6.
- Child 7 owns single-flight/artifact state. Task 5 changes it only for a reproduced failing concurrency invariant.
- Child 8 owns operation/worker cleanup. Task 6 adds deterministic probe calls and fixes only evidenced leaks.
- Child 10 consumes `thread-scope.etl` and its manifest for golden and fast/slow evidence. Do not rename or normalize away PID/TID/lifetime/window fields.
- Task 7 owns removal of the reviewed 27 vacuous unit-test returns. A domain child may repair an exposed analyzer/fixture defect, but it may not restore an early return or skip.
- Child 11B owns artifact creation and the first real GREEN execution of `Category=Package`. Task 8 authors and statically gates the consumer only; it must never publish or rebuild a zip.
- Removing `tests/WprMcp.Tests/AssemblyInfo.cs` is conditional on Task 5 passing five consecutive times. If it fails, keep the file, fix isolation, and rerun; do not weaken the concurrency test.

## Final evidence checklist

- The selected protocol's exact handshake/capabilities/IDs are observed over real stdio; no non-standard shutdown is sent.
- Legacy, v2, paths, and strict profiles use their actual advertised schema/annotation matrix.
- All six thread tools cover `tid` without `pid`, unique lifetime, reused-TID ambiguity/disambiguation, and below-process-TopN selection.
- Corrupt, truncated, wrong-extension, inaccessible, oversized, UNC, reparse, and replaced-after-validation traces fail at the intended boundary.
- Same-source loads publish exactly one conversion both within a server and across two processes.
- Cancellation, deadline, disconnect, worker crash, quota, and oversized response leave no operation, worker, lease, quota, temp artifact, or late success.
- Required fixtures are never dynamically skipped.
- The eleven audited unit suites contain no bare void return; absent positive evidence fails or asserts an exact unavailable/not-concluded state.
- The package harness is compiled and statically gated here; Child 11B Task 6 must run it against the same immutable zip it later uploads and prove the digest did not change.
