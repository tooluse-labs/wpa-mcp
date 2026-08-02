# Cancellation, Budgets, Progress, and Worker Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bound every trace operation by cancellation, deadline, work, output, process, and IPC limits while running all untrusted parsing/analysis/symbol work in a recoverable no-network worker by default.

**Architecture:** MCP entrypoints build one `AnalysisOperationContext` whose cancellation, monotonic deadline, progress sink, symbol snapshot, and atomic budget flow through composites and analyzers. Every query is a named, versioned `TraceOperation<TArgs,TResult>` executed by `ITraceBackend`: the in-process backend invokes it against an entry-owned trace session, while the isolated backend launches an operation-scoped worker session that owns its private `TraceLog` and identity index and exchanges strict bounded IPC for exactly one invoke. Secure-default always uses a restricted worker for conversion, ETLX parsing, every analyzer, and symbol resolution; the parent alone validates paths, owns quotas/artifacts, and brokers policy-approved symbol HTTP. The worker is a hidden mode of the same `WprMcp.exe`, launched with an AppContainer security capability list, an exact inherited-handle list, and a Job Object. A cached trace entry retains immutable artifact identity and backend routing state, never a live worker or a standing committed-memory reservation.

**Tech Stack:** The C# version, TFM, MCP SDK, and TraceEvent package selected by Child 11A; the current baseline is C# 12, .NET 8, ModelContextProtocol 1.2.0, and TraceEvent 3.2.2. Windows isolation uses `STARTUPINFOEX`, `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES`, `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, AppContainer SIDs, Job Objects, anonymous pipes, source-generated `System.Text.Json`, TraceEvent/DIA native assets, and xUnit process/concurrency tests.

## Accepted capability/evidence amendment (2026-08-01)

The Phase 6 planner defined by [`MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md`](../../MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md) must reuse this plan's `AnalysisOperationContext`, cancellation/deadline/work/output budgets, progress channel, operation catalog, worker isolation, and shared prerequisite scans. It is an orchestration layer over declared operations, not a second execution engine, private stopwatch, unbounded discovery scan, or alternate cache.

Planner output must preserve the same partial/failure, budget-exhaustion, provenance, and section-level evidence boundaries as direct tool calls. Any planner step that cannot be represented by the registered bounded operation/manifest mechanisms is an explicit capability gap rather than hidden work.

## Global Constraints

- This plan starts after Child 6 and Child 7. It preserves `TraceAccessPolicy`, immutable artifacts, symbol allowlists, trace IDs, `ITraceReferenceResolver`, entry state, artifact leases, `IRuntimeQuotaManager`, and caller-side `await using TraceLease`.
- Secure-default is the startup default and has no in-process parser fallback. ETL conversion, ETLX parsing, metadata extraction, every analyzer, stack walking, DIA/symbol resolution, and serialization of worker results run in a restricted worker. A `TraceLog`, parser, stack source, symbol reader, identity index, exception object, or raw trace path never crosses IPC.
- Trusted-local is explicit startup configuration. Its default analysis mode remains isolated. `trusted-local + in-process` starts only when a checked-in capability record exactly matches the current TFM, RID, TraceEvent version, operation-catalog version, executable informational version, capture-recipe version, and a measured cancellation p95 at or below two seconds. Missing/stale/failed evidence is a startup error, not an isolated-mode downgrade hidden from the operator.
- All symbol requests remain under Child 6 policy in both profiles. Secure workers have no network capability and receive no ACL to configured local symbol roots; their only symbol egress is a typed `SymbolHttpRequest` to the parent broker. The request carries only an exact PDB identity `(fileName, GUID, age)`, an approved server ordinal from the immutable operation symbol grant, and that grant's broker capability token—never a local path, URL, host, header, or method. The parent binds the token to the operation/correlation/symbol-context digest, searches policy-approved local roots through validated handles first, maps the ordinal to a Child 6-approved server, constructs the canonical symbol-server URI itself, and revalidates origin, DNS address, redirect, byte quota, and cache path at every hop. Before publication or return it validates that downloaded bytes contain the requested PDB identity, then returns bounded bytes through `SymbolHttpResponse`/`SymbolHttpChunk`.
- The worker executable is the current `Environment.ProcessPath` only when it is the matching WprMcp apphost, with hidden first argument `--wprmcp-worker-v1`. A framework-dependent `dotnet WprMcp.dll` launch must locate and fingerprint the adjacent matching `WprMcp.exe` apphost or fail secure-default startup; it must never relaunch `dotnet` as if it were the worker. `Program.Main` branches on the hidden argument before normal CLI/MCP option parsing, logging, host construction, directory creation, or environment import. Release artifacts continue to contain one `WprMcp.exe` plus the existing `native/amd64` files; no second worker binary is introduced.
- Worker handles are inherited only through `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Secure conversion receives a validated input handle and parent-owned output handle; it copies input into its private scratch, converts there, validates there, and copies bytes to the output handle. Secure analysis receives an immutable ETLX handle, copies it into private scratch, and opens only that copy. No worker receives a user-controlled source path.
- Worker scratch and parent staging bytes are hard-accounted disk usage, not invisible temporary overhead. Before conversion the parent atomically reserves `inputBytes + 2 * outputGrant` artifact bytes, where `outputGrant = min(MaxConversionOutputBytes, (remainingArtifactQuota - inputBytes) / 2)`; the two output terms cover worker ETLX plus parent staging while both exist. Before analysis it reserves one artifact-length scratch copy. Task 1 must prove that TraceEvent writes through a `QuotaWriteStream`/equivalent bounded destination, or that the worker scratch resides on an OS-enforced per-worker quota/dedicated bounded volume whose next byte fails with disk-full. A polling `WorkerScratchMonitor` is defense in depth only and cannot satisfy the hard cap. The parent reconciles to actual retained bytes only after bounded cleanup; cancellation, crash, or publication failure releases the transient reservation and removes only owned temporary files.
- Every conversion launch reserves one `ConversionWorkers`, its granted `WorkerCommittedBytes` and `WorkerCpuTicks`, and the conversion scratch/staging `ArtifactBytes` in Child 7's single `IRuntimeQuotaManager`; every analysis launch analogously reserves one `AnalysisWorkers`, the worker memory/CPU grant, and one artifact-length scratch copy. The operation reservation begins before process creation and remains owned until the Job has exited and all pipes, handles, scratch, and staging are cleaned. Job memory/process-time limits are derived from that exact reservation—not reread from mutable configuration. At exit the parent queries Job peak committed memory and CPU time, rejects/reports any value beyond the grant, records actual usage, and reconciles all ephemeral worker/scratch fields to zero; a retained artifact is promoted to its separate persistent object charge. Two concurrent workers therefore consume two slots and the checked sum of both memory/CPU/disk grants. A trace registry entry never starts or retains a worker merely because the entry is cached.
- Cancellation has one outcome. Once transport cancellation is observed, no later success/error frame is sent for that JSON-RPC request. If a tool response is still permitted, no usable completed section maps to `Failed/cancelled`; usable completed sections map to `Partial` with a cancelled section. Cancellation/timeout releases queue, worker, artifact, symbol, and budget reservations plus owned temp files within the default five-second cleanup deadline.
- Deadline and work accounting are shared across prerequisite scans, composite sections, stack work, symbol work, and serialization. `diagnose_high_wait` and CPU batch may not create private stopwatch budgets or reset counters between sections.
- The parent enforces worker-frame length before allocation. The worker enforces the same limit before allocation. Unknown message kinds/properties, duplicate correlation IDs, invalid state transitions, oversize frames/chunks, bad nonces, and protocol/version mismatch terminate only that worker Job and fail the operation with a stable local code; a subsequent request starts a clean worker.
- Independently of worker IPC, preserve Child 5's existing `JsonRpcFrameLimitingStream`, configured raw-stdin request cap (100,000-byte default/hard ceiling), and serialized request-ID boundary before SDK execution. Child 8 must not add a second request limit, option, flag, reader, or transport wrapper. Its regression matrix re-exercises both default and lowered configured cap minus/exact/plus-one, bounded IDs, and huge-unterminated-frame behavior; Child 9 owns the complete end-to-end stdio matrix and recovery checks. Child 5's `MaxToolArgumentBytes` remains the separate post-deserialization per-tool-argument limit.
- Child 9 owns the full stdio protocol matrix and may remove the global xUnit parallelism guard. This child still supplies deterministic worker-process tests and must leave `tests/WprMcp.Tests/AssemblyInfo.cs` unchanged.

## Configuration, Budgets, and Stable Interfaces

Add these records and startup flags:

```csharp
internal enum ParserExecutionProfile { SecureDefault, TrustedLocal }
internal enum TrustedLocalAnalysisMode { Isolated, InProcess }

internal sealed record AnalysisRuntimeOptions(
    ParserExecutionProfile Profile = ParserExecutionProfile.SecureDefault,
    TrustedLocalAnalysisMode TrustedLocalMode = TrustedLocalAnalysisMode.Isolated,
    int MaxConversionWorkers = 1,
    int MaxAnalysisWorkers = 2,
    TimeSpan OperationWallTime = default,
    long MaxConversionOutputBytes = 16L * 1024 * 1024 * 1024,
    long WorkerCommittedBytes = 4L * 1024 * 1024 * 1024,
    TimeSpan WorkerCpuTime = default,
    int MaxIpcFrameBytes = 1024 * 1024,
    int ProgressPerSecond = 2,
    long MaxEventVisits = 100_000_000,
    long MaxStackNodeVisits = 10_000_000,
    long MaxSymbolAttempts = 100_000,
    TimeSpan WorkerCleanupTimeout = default,
    TimeSpan TrustedLocalCancellationSla = default)
{
    internal static AnalysisRuntimeOptions Defaults => new(
        OperationWallTime: TimeSpan.FromMinutes(10),
        WorkerCpuTime: TimeSpan.FromMinutes(10),
        WorkerCleanupTimeout: TimeSpan.FromSeconds(5),
        TrustedLocalCancellationSla: TimeSpan.FromSeconds(2));
}

internal sealed record McpServerOptions(
    string[] HostArgs,
    ToolExecutionOptions ToolExecution,
    TraceAndSymbolPolicyOptions TracePolicy,
    TraceRegistryOptions Registry,
    AnalysisRuntimeOptions Analysis);
```

Child 8 adds only nested `AnalysisRuntimeOptions Analysis` to Child 7's final shape (`HostArgs`, `ToolExecution`, `TracePolicy`, `Registry`). It preserves Child 5's derived `ContractMode`/privacy/budget compatibility accessors and the Child 6/7 nested records without flattening, reconstructing, or re-owning them. The `--symbol-path` and `--cache-size` CLI flags remain supported by their owning parsers, but `McpServerOptions` has neither scalar property: their values already live solely in `TracePolicy.Symbols.InitialSymbolPath` and `Registry.MaxEntries`.

Child 8 also preserves Child 7's single symbol-cache quota ownership: final `SymbolPolicyOptions` has no `MaxCacheBytes`, every total-cache reservation/status/eviction path reads `Registry.MaxSymbolCacheBytes`, and `RuntimeHardLimits.MaxSymbolCacheBytes` is validation-only. `AnalysisRuntimeOptions` must not duplicate that quota.

Flags are `--execution-profile secure-default|trusted-local`, `--trusted-local-analysis isolated|in-process`, `--max-conversion-workers <1..4>`, `--max-analysis-workers <1..8>`, `--operation-wall-time <00:00:01..01:00:00>`, `--max-conversion-output-bytes <1..68719476736>`, `--worker-committed-bytes <1..17179869184>`, `--worker-cpu-time <00:00:01..01:00:00>`, `--ipc-frame-bytes <1024..4194304>`, `--progress-per-second <1..10>`, `--event-visit-budget <1..1000000000>`, `--stack-node-budget <1..100000000>`, `--symbol-attempt-budget <1..1000000>`, `--worker-cleanup-timeout <00:00:01..00:00:30>`, and `--trusted-local-cancellation-sla <00:00:00.100..00:00:02>`. The per-conversion output hard ceiling is 64 GiB. The parser rejects ceiling-plus-one values before creating directories or processes.

```csharp
internal enum TraceProgressPhase
{
    Validation, Conversion, Metadata, Scan, Symbols, Serialization, Completion
}

internal sealed record TraceProgress(
    TraceProgressPhase Phase,
    double Progress,
    double? Total,
    string MessageCode);

internal enum AnalysisChargeKind
{
    EventVisits, StackNodeVisits, SymbolAttempts, OutputBytes
}

internal sealed record AnalysisOperationContext(
    CancellationToken CancellationToken,
    TimeProvider TimeProvider,
    long DeadlineTimestamp,
    IProgress<TraceProgress>? Progress,
    AnalysisBudget Budget,
    SymbolContext SymbolContext,
    TextWriter DiagnosticWriter)
{
    internal void ThrowIfCancellationOrDeadlineExceeded();
}

internal sealed class AnalysisBudget
{
    internal void ChargeOrThrow(AnalysisChargeKind kind, long amount);
    internal AnalysisBudgetSnapshot Snapshot();
    internal AnalysisBudgetGrant CreateWorkerGrant(AnalysisBudgetLimits requested);
    internal void Reconcile(AnalysisBudgetGrant grant, AnalysisBudgetUsage actual);
}
```

`CreateWorkerGrant` atomically reserves the granted maxima from the parent budget before dispatch; the worker has an independent counter bounded by that grant. `Reconcile` validates non-negative usage no greater than the grant, permanently charges actual usage, and releases unused reservation exactly once. Malformed or missing usage is a worker-protocol failure and keeps the conservative reservation until worker cleanup, when it is released without recording success.

The `Invoke` boundary carries one immutable, source-generated grant rather than parent process state:

```csharp
internal sealed record WorkerSymbolGrant(
    string BrokerCapabilityToken,
    string SymbolContextDigest,
    int[] ApprovedServerOrdinals);

internal sealed record WorkerInvokeGrant(
    string GrantId,
    TimeSpan RemainingDeadline,
    AnalysisBudgetLimits BudgetLimits,
    ResourceVector ReservedResources,
    WorkerSymbolGrant Symbols);
```

The parent measures `RemainingDeadline` immediately before send from its monotonic deadline and never serializes a `TimeProvider` timestamp. It must be positive and no greater than the parent remainder. The worker validates every limit as non-negative and within protocol hard ceilings, creates a local linked cancellation/deadline source for that duration, constructs a fresh `AnalysisBudget` from `BudgetLimits`, and accepts symbol requests only under the bound broker token/ordinal set. `Result` and `Error` both echo `GrantId` and carry `AnalysisBudgetUsage`; the parent accepts and reconciles usage only once, only for the current correlation/grant, and only when every counter is within the grant. Cancellation with no terminal frame retains the conservative grant until Job cleanup and then releases it.

Use a worker-local trace session and typed operations:

```csharp
internal sealed class TraceAnalysisSession : IAsyncDisposable
{
    internal TraceLog Trace { get; }
    internal TraceIdentityIndex GetIdentities(AnalysisOperationContext context);
    internal SymbolContext Symbols { get; }
    public ValueTask DisposeAsync();
}

internal sealed record TraceOperation<TArgs, TResult>(
    string Name,
    int Version,
    JsonTypeInfo<TArgs> ArgsType,
    JsonTypeInfo<TResult> ResultType,
    Func<TResult, int, TResult> LimitForIpc,
    Func<TraceAnalysisSession, TArgs, AnalysisOperationContext, TResult> Invoke);

internal interface ITraceBackend : IAsyncDisposable
{
    ValueTask<TResult> ExecuteAsync<TArgs, TResult>(
        TraceOperation<TArgs, TResult> operation,
        TArgs arguments,
        AnalysisOperationContext context);
}
```

`TraceOperationCatalog` registers every operation by exact ordinal `(Name, Version)` and rejects duplicates at startup. Isolated dispatch serializes `Name`, `Version`, typed arguments, and the exact `WorkerInvokeGrant` (grant ID, parent-measured remaining-duration, budget/resource grant, and immutable symbol broker snapshot); it never serializes a parent deadline timestamp or `TimeProvider`. The worker resolves its own delegate from the same catalog. `Result`/`Error` echo the matching grant ID and bounded usage for reconciliation. `TraceLog` and the successfully published `TraceIdentityIndex` remain owned by `TraceAnalysisSession` in the worker or trusted-local backend. `GetIdentities(context)` performs the first identity scan with that operation's cancellation/deadline/event budget, publishes only a completed immutable index, and removes a cancelled/faulted build so the next operation can retry; no constructor, load path, or context-free `Lazy.Value` may trigger the scan.

IPC frames are four-byte unsigned little-endian payload length followed by one UTF-8 JSON object. The default length is 1 MiB and hard ceiling 4 MiB, checked before renting or allocating. Protocol version is literal `1`. CLR message types are exactly `Hello`, `Ready`, `Invoke`, `Cancel`, `Progress`, `SymbolHttpRequest`, `SymbolHttpResponse`, `SymbolHttpChunk`, `Result`, `Error`, and `Shutdown`; their wire discriminators are the lower-case values shown in Task 6. Every post-handshake message has a 128-bit lower-case-hex correlation ID; handshake messages carry a 256-bit random nonce. JSON uses source-generated metadata, case-sensitive property names, string enum converters with integers forbidden, `UnmappedMemberHandling.Disallow`, and no runtime CLR type-name handling. A pre-deserialization `Utf8JsonReader` pass enforces `MaxDepth=32`, at most 128 properties/elements in each object/array, Child 5's 4,096-character ordinary-string limit, and no duplicate ordinal property name at any nesting level. The sole string-size exception is `SymbolHttpChunk.dataBase64`: at most 87,384 ASCII characters, valid canonical base64, and at most 65,536 decoded bytes; it remains subject to the frame limit.

An operation whose valid result would contain more than 128 rows in any collection must project at a row/section boundary before IPC, retain deterministic ordering, and set Child 5 `HasMore` plus exact `TotalAvailable`/section metadata. The public `top` maximum may remain 1,000, but one worker result collection never exceeds 128; in-process and isolated backends use the same projection so profiles do not diverge.

```csharp
internal interface IWorkerScratchQuota
{
    string Mechanism { get; }
    ValueTask<WorkerScratchAllocation> CreateAsync(
        string operationId,
        long inputLimitBytes,
        long outputLimitBytes,
        CancellationToken cancellationToken);
}

internal abstract class WorkerScratchAllocation : IAsyncDisposable
{
    internal abstract string InputPath { get; }
    internal abstract string ConversionOutputPath { get; }
    internal abstract Stream OpenBoundedInputWriter();
    internal abstract Stream OpenBoundedParentOutputWriter(SafeFileHandle parentOutput);
    internal abstract void AssertWithinLimits();
    public abstract ValueTask DisposeAsync();
}
```

For `bounded_conversion_stream`, the converter receives the capped stream associated with `ConversionOutputPath`; for `os_bounded_scratch`, that path is on the dedicated hard-cap allocation. Both mechanisms must make the offending write fail, and `DisposeAsync` removes only the operation's allocation.

---

### Task 1: Spike Job Object, inherited-handle IPC, AppContainer networking, hard scratch quota, DIA, and SDK cancellation

**Files:**

- Create: `src/WprMcp/Worker/WorkerCapabilityProbe.cs`
- Create: `src/WprMcp/Worker/WorkerProbeEntrypoint.cs`
- Create: `src/WprMcp/Worker/WindowsWorkerSandboxProbe.cs`
- Create: `src/WprMcp/Worker/WorkerScratchQuotaProbe.cs`
- Modify: `src/WprMcp/Program.cs`
- Create: `tests/WprMcp.Tests/WorkerIsolationSpikeTests.cs`
- Create: `tests/WprMcp.Tests/WorkerProbeProcessHost.cs`
- Modify: `tests/WprMcp.Tests/McpSdkSurfaceTests.cs`
- Create: `docs/architecture/worker-isolation-spike.md`

**Interfaces:**

- Consumes: the Child 11A-selected runtime/package set, current `WprMcp.exe`, `native/amd64/msdia140.dll`, a checked-in ETL fixture, and a loopback test server.
- Produces: `WorkerIsolationCapabilities ProbeAsync(CancellationToken)` and a committed architecture record with actual API choices, test commands/results, measured cancellation samples, and a binary pass/block decision for secure-default.

- [ ] **Step 1: Write failing executable capability tests before the sandbox implementation.** Launch the same executable in hidden probe mode and require: nonce/version echo over two anonymous pipe handles; a non-listed inheritable sentinel handle is inaccessible; an explicitly listed read handle works; worker TCP connection to a loopback listener fails and the listener accepts nothing; a brokered parent request returns bounded bytes; ActiveProcessLimit=1 prevents a child; a low Job memory limit terminates a deliberate allocator; a low process CPU-time limit terminates a spinner; closing the Job kills the worker; the worker loads `native/amd64/msdia140.dll` and resolves its `DllGetClassObject` export; TraceEvent opens the private fixture copy and `StopProcessing` ends a real dispatcher traversal; and the selected MCP SDK injects `CancellationToken` plus `IProgress<ProgressNotificationValue>` without exposing either in `tools/list` input schema. For disk enforcement, first probe whether the selected TraceEvent converter can target a stream wrapped by a write-counting hard-limit stream; if not, probe a per-worker OS quota/dedicated bounded scratch volume. In either case, a controlled conversion at grant plus one must fail the write itself with no observed file/volume usage above the grant. A monitor that detects growth after the write does not pass.

```csharp
internal sealed record WorkerIsolationCapabilities(
    bool SameExecutableEntrypoint,
    bool ExactHandleInheritance,
    bool AppContainerNoNetwork,
    bool BrokeredEgress,
    bool JobMemoryLimit,
    bool JobCpuTimeLimit,
    bool JobActiveProcessLimit,
    bool KillOnJobClose,
    bool HardScratchWriteLimit,
    bool DiaLoaded,
    bool TraceEventStopProcessing,
    bool McpCancellationInjected,
    bool McpProgressInjected);
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~WorkerIsolationSpikeTests|FullyQualifiedName~McpSdkSurfaceTests"` on the supported Windows RID and save the expected failures. Do not proceed to production worker code on a non-Windows substitute.**

- [ ] **Step 3: Implement the smallest real probe.** Branch in `Program.Main` before normal parsing. Use `DeriveAppContainerSidFromAppContainerName`/`CreateAppContainerProfile` and `PROC_THREAD_ATTRIBUTE_SECURITY_CAPABILITIES` with zero network capability SIDs; use `UpdateProcThreadAttribute` for the exact handle list; create suspended, assign a Job configured with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, `JOB_OBJECT_LIMIT_ACTIVE_PROCESS`, `JOB_OBJECT_LIMIT_PROCESS_MEMORY`, and `JOB_OBJECT_LIMIT_PROCESS_TIME`, then resume. The probe frame is the production four-byte framing algorithm even though the full codec arrives in Task 6. Locate DIA relative to `Environment.ProcessPath`, load it inside the sandbox, and exercise the export. Register cancellation to call `TraceEventDispatcher.StopProcessing`, then rethrow cancellation after `Process` returns. `WorkerScratchQuotaProbe` records which hard path passed: `bounded_conversion_stream` when the converter's actual output calls a capped `Stream.Write`; otherwise `os_bounded_scratch` only when a dedicated quota/volume returns disk-full on byte `grant + 1`. It records `none` when neither is enforceable; file polling cannot change that result.

- [ ] **Step 4: Run the focused command 10 times. Populate `docs/architecture/worker-isolation-spike.md` with the selected Win32 calls, exact inherited handle set, package versions/TFM/RID, the hard scratch mechanism and its grant/exact/plus-one measurements, each observed result, and all ten cancellation latencies. Mark secure-default `pass` only if every boolean—including `HardScratchWriteLimit`—is true; otherwise record `blocked` and stop this plan before Task 2.**

- [ ] **Step 5: Commit the spike independently.**

```powershell
git add src/WprMcp/Worker/WorkerCapabilityProbe.cs src/WprMcp/Worker/WorkerProbeEntrypoint.cs src/WprMcp/Worker/WindowsWorkerSandboxProbe.cs src/WprMcp/Worker/WorkerScratchQuotaProbe.cs src/WprMcp/Program.cs tests/WprMcp.Tests/WorkerIsolationSpikeTests.cs tests/WprMcp.Tests/WorkerProbeProcessHost.cs tests/WprMcp.Tests/McpSdkSurfaceTests.cs docs/architecture/worker-isolation-spike.md
git commit -m "spike: prove windows worker isolation and cancellation"
```

### Task 2: Create the shared operation context, deadline, and atomic work budget

**Files:**

- Create: `src/WprMcp/Core/AnalysisOperationContext.cs`
- Create: `src/WprMcp/Core/AnalysisBudget.cs`
- Create: `src/WprMcp/Core/AnalysisBudgetGrant.cs`
- Create: `src/WprMcp/Core/AnalysisDeadline.cs`
- Modify: `src/WprMcp/Core/RuntimeHardLimits.cs`
- Modify: `src/WprMcp/McpServerOptions.cs`
- Create: `tests/WprMcp.Tests/AnalysisOperationContextTests.cs`
- Create: `tests/WprMcp.Tests/AnalysisBudgetTests.cs`
- Modify: `tests/WprMcp.Tests/McpServerOptionsTests.cs`

**Interfaces:**

- Consumes: client token, host-shutdown token, `TimeProvider`, `AnalysisRuntimeOptions`, Child 5 output limit, and Child 7 quota reservation.
- Produces: one linked `AnalysisOperationContext`, atomic charge/snapshot/grant/reconcile operations, and stable `cancelled`/`budget_exceeded`/deadline mapping.

- [ ] **Step 1: Write failing deterministic tests.** With `FakeTimeProvider`, test cancellation and deadline independently and together. For event, stack-node, symbol-attempt, and output-byte counters, test limit minus one, exact limit, plus one, negative/overflow charges, 64 concurrent exact-boundary charges, worker grant reservation, exact/partial reconciliation, double reconciliation, and worker-reported usage above grant. Test `--max-conversion-output-bytes` at 64 GiB and 64 GiB plus one alongside every other runtime ceiling. Add an options-shape regression asserting Child 8's record is exactly `HostArgs`, `ToolExecution`, `TracePolicy`, `Registry`, `Analysis`; `SymbolPath`, `CacheSize`, and `TracePolicy.Symbols.MaxCacheBytes` are absent; `--symbol-path`, `--cache-size`, and `--symbol-cache-bytes` still populate `InitialSymbolPath`, `Registry.MaxEntries`, and `Registry.MaxSymbolCacheBytes` respectively. Assert rejected arguments and a queued cancellation consume zero quota and do not start deadline timers or workers.

```csharp
[Theory]
[InlineData(99, false)]
[InlineData(100, false)]
[InlineData(101, true)]
public void ChargeOrThrow_HasInclusiveLimit(long amount, bool throws)
{
    var budget = AnalysisBudget.ForTests(eventVisits: 100);
    Action charge = () => budget.ChargeOrThrow(AnalysisChargeKind.EventVisits, amount);
    if (throws) Assert.Throws<AnalysisBudgetExceededException>(charge); else charge();
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~AnalysisOperationContextTests|FullyQualifiedName~AnalysisBudgetTests|FullyQualifiedName~McpServerOptionsTests"` and verify missing context/budget failures.**

- [ ] **Step 3: Implement checked atomic counters and monotonic deadline.** Use `Interlocked.CompareExchange` loops with `checked` addition. A charge succeeds when resulting usage equals the limit and fails without mutation when it exceeds. Add `MaxConversionOutputBytes = 64L * 1024 * 1024 * 1024` to `RuntimeHardLimits` and validate the option purely at startup. `AnalysisDeadline` uses `TimeProvider.GetTimestamp()`/`GetElapsedTime` and a linked token created with the provider-aware timer; never compare `DateTime`. Reserve worker grants before Child 7 worker-slot admission, reconcile actual usage on a validated result, and idempotently release unused/error reservations in `finally`.

- [ ] **Step 4: Run the focused tests 20 times, then `dotnet build WprMcp.sln -c Release`; require deterministic counts and no public analyzer change yet.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/AnalysisOperationContext.cs src/WprMcp/Core/AnalysisBudget.cs src/WprMcp/Core/AnalysisBudgetGrant.cs src/WprMcp/Core/AnalysisDeadline.cs src/WprMcp/Core/RuntimeHardLimits.cs src/WprMcp/McpServerOptions.cs tests/WprMcp.Tests/AnalysisOperationContextTests.cs tests/WprMcp.Tests/AnalysisBudgetTests.cs tests/WprMcp.Tests/McpServerOptionsTests.cs
git commit -m "feat: add shared analysis deadline and budget"
```

### Task 3: Make every TraceEvent traversal cooperatively cancellable and charged

**Files:**

- Modify: `src/WprMcp/Analyzers/KernelEventWalker.cs`
- Modify: `src/WprMcp/Analyzers/ClrEventWalker.cs`
- Create: `src/WprMcp/Analyzers/AnalysisEvents.cs`
- Modify: `src/WprMcp/Core/InProcessTraceBackend.cs`
- Modify: `src/WprMcp/Analyzers/AlpcStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ClrAllocStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ClrContentionStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ClrExceptionStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/CpuAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/CpuPreciseAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/DiskIoStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/FileIoAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/FileIoStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/FileObjectResolver.cs`
- Modify: `src/WprMcp/Analyzers/FinalizerAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/GcAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/GcHeapStatsAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/GenericEventStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/HardFaultByFileAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/HeapAllocStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ImageLoadAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ImageLoadStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/InterruptStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/JitAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/MarkerSearch.cs`
- Modify: `src/WprMcp/Analyzers/MemoryResourceAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/NetConnectionAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/NetIoStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/PageFaultStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ReadyThreadStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/RegistryStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/SecurityScanAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/StackProbeAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/TraceIdentityIndex.cs`
- Modify: `src/WprMcp/Analyzers/TraceIdentityIndexFactory.cs`
- Modify: `src/WprMcp/Analyzers/TraceCapabilitiesDetector.cs`
- Modify: `src/WprMcp/Analyzers/TraceMetadataAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/SchedulerIntervalTraceReader.cs`
- Modify: `src/WprMcp/Analyzers/StartupImageLoadAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/StartupProcessCatalog.cs`
- Modify: `src/WprMcp/Analyzers/VirtualAllocStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/WaitAnalysis.cs`
- Create: `tests/WprMcp.Tests/AnalysisEventsTests.cs`
- Create: `tests/WprMcp.Tests/TraversalCancellationTests.cs`
- Create: `tests/WprMcp.Tests/AnalyzerContextSurfaceTests.cs`

**Interfaces:**

- Consumes: the same `AnalysisOperationContext` for the complete analyzer invocation.
- Produces: `KernelEventWalker.Walk(trace, context, configure)`, `ClrEventWalker.Walk(trace, context, configure)`, `AnalysisEvents.Enumerate(trace, context)`, `AnalysisEvents.Enumerate<T>(source, context)`, and context-bound `TraceIdentityIndexFactory.Create(trace, context)`; every visited event/trace-collection row, including the first identity/startup/scheduler scan, charges exactly one `EventVisits` unit.

- [ ] **Step 1: Write failing cancellation, identity-build, and architecture tests.** Use callback barriers rather than sleeps to cancel before dispatcher/source acquisition, before cancellation registration, immediately before `Process()`, during the first callback, at the exact event budget, and at budget plus one. Assert pre-cancelled contexts never register or call `Process`; `StopProcessing` is called from cancellation registration once traversal starts; after `Process()` returns, cancellation is rethrown; a false return without cancellation is `analysis_failed`; registrations are disposed; and no callbacks occur after return. Cancel the first `TraceIdentityIndex` build, assert no index is cached, retry under a fresh context, and assert one successful publish; verify its process/thread events charge the initiating budget and a later cached read performs no scan. Reflection/source tests fail for any analyzer/trace-reader entrypoint lacking `AnalysisOperationContext`, any context-free identity factory/`Lazy.Value`, any direct `trace.Events` enumeration outside `AnalysisEvents.cs`, or any direct `.Process()` outside the two walkers and a single low-level helper. The scan includes files added by Children 1–4, especially `TraceIdentityIndex.cs`, `SchedulerIntervalTraceReader.cs`, `StartupImageLoadAnalysis.cs`, and `StartupProcessCatalog.cs`.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~AnalysisEventsTests|FullyQualifiedName~TraversalCancellationTests|FullyQualifiedName~AnalyzerContextSurfaceTests"` and verify it identifies the direct traversals and missing parameters.**

- [ ] **Step 3: Implement the cancellation pattern once and migrate callers.** Both walkers call `context.ThrowIfCancellationOrDeadlineExceeded()` before obtaining the dispatcher/source, again before registering `StopProcessing`, and immediately before `Process()`; they charge/check in the common callback, then check once more after `Process()` returns. This prevents a context that expired during setup from entering TraceEvent at all. A non-cancelled false result throws a stable traversal exception. `AnalysisEvents.Enumerate` and its generic collection overload check context and charge before yielding every row. Change Child 7's identity factory to `Create(TraceLog, AnalysisOperationContext)` and make all of its process/thread traversal use the same walkers/enumerator; at this stage `InProcessTraceBackend.GetIdentities(context)` serializes the first build, caches only success, and clears a cancelled/faulted owner before retry. Task 6 moves the same ownership into `TraceAnalysisSession` without changing behavior. Replace direct source-processing in `GenericEventStackAnalysis`, `HeapAllocStackAnalysis`, `StackProbeAnalysis`, `TraceCapabilitiesDetector`, `SchedulerIntervalTraceReader`, and `StartupImageLoadAnalysis`; replace direct event/trace-collection enumeration in `CpuAnalysis`, `MarkerSearch`, `MemoryResourceAnalysis`, `SecurityScanAnalysis`, `StackProbeAnalysis`, `TraceCapabilitiesDetector`, `TraceMetadataAnalysis`, `StartupProcessCatalog`, and every additional post-Child-4 match found by the architecture test. Thread the context through every walker caller listed in Files without constructing a child context.

```csharp
context.ThrowIfCancellationOrDeadlineExceeded();
TraceEventDispatcher source = GetDispatcher(trace);
context.ThrowIfCancellationOrDeadlineExceeded();
using CancellationTokenRegistration registration = context.CancellationToken.Register(
    static state => ((TraceEventDispatcher)state!).StopProcessing(), source);
context.ThrowIfCancellationOrDeadlineExceeded();
bool completed = source.Process();
context.ThrowIfCancellationOrDeadlineExceeded();
if (!completed)
    throw new TraceTraversalException("trace_dispatch_incomplete");
```

- [ ] **Step 4: Run the focused tests and all analyzer tests: `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Analysis|FullyQualifiedName~Traversal|FullyQualifiedName~TraceIdentityIndex|FullyQualifiedName~Startup|FullyQualifiedName~SchedulerInterval|FullyQualifiedName~TraceCapabilities|FullyQualifiedName~TraceMetadata|FullyQualifiedName~MarkerSearch|FullyQualifiedName~SecurityScan"`. Then run `rg -n "trace\.Events|source\.Process\(\)|\.GetSource\(\)|TraceIdentityIndex\.For|Lazy<TraceIdentityIndex>" src/WprMcp/Analyzers src/WprMcp/Core` and require traversal matches only in `AnalysisEvents.cs`, `KernelEventWalker.cs`, and `ClrEventWalker.cs`, with no context-free identity build.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Analyzers/KernelEventWalker.cs src/WprMcp/Analyzers/ClrEventWalker.cs src/WprMcp/Analyzers/AnalysisEvents.cs src/WprMcp/Core/InProcessTraceBackend.cs src/WprMcp/Analyzers/AlpcStackAnalysis.cs src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs src/WprMcp/Analyzers/ClrAllocStackAnalysis.cs src/WprMcp/Analyzers/ClrContentionStackAnalysis.cs src/WprMcp/Analyzers/ClrExceptionStackAnalysis.cs src/WprMcp/Analyzers/CpuAnalysis.cs src/WprMcp/Analyzers/CpuPreciseAnalysis.cs src/WprMcp/Analyzers/DiskIoStackAnalysis.cs src/WprMcp/Analyzers/FileIoAnalysis.cs src/WprMcp/Analyzers/FileIoStackAnalysis.cs src/WprMcp/Analyzers/FileObjectResolver.cs src/WprMcp/Analyzers/FinalizerAnalysis.cs src/WprMcp/Analyzers/GcAnalysis.cs src/WprMcp/Analyzers/GcHeapStatsAnalysis.cs src/WprMcp/Analyzers/GenericEventStackAnalysis.cs src/WprMcp/Analyzers/HardFaultByFileAnalysis.cs src/WprMcp/Analyzers/HeapAllocStackAnalysis.cs src/WprMcp/Analyzers/ImageLoadAnalysis.cs src/WprMcp/Analyzers/ImageLoadStackAnalysis.cs src/WprMcp/Analyzers/InterruptStackAnalysis.cs src/WprMcp/Analyzers/JitAnalysis.cs src/WprMcp/Analyzers/MarkerSearch.cs src/WprMcp/Analyzers/MemoryResourceAnalysis.cs src/WprMcp/Analyzers/NetConnectionAnalysis.cs src/WprMcp/Analyzers/NetIoStackAnalysis.cs src/WprMcp/Analyzers/PageFaultStackAnalysis.cs src/WprMcp/Analyzers/ReadyThreadStackAnalysis.cs src/WprMcp/Analyzers/RegistryStackAnalysis.cs src/WprMcp/Analyzers/SecurityScanAnalysis.cs src/WprMcp/Analyzers/StackProbeAnalysis.cs src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs src/WprMcp/Analyzers/TraceIdentityIndex.cs src/WprMcp/Analyzers/TraceIdentityIndexFactory.cs src/WprMcp/Analyzers/TraceCapabilitiesDetector.cs src/WprMcp/Analyzers/TraceMetadataAnalysis.cs src/WprMcp/Analyzers/SchedulerIntervalTraceReader.cs src/WprMcp/Analyzers/StartupImageLoadAnalysis.cs src/WprMcp/Analyzers/StartupProcessCatalog.cs src/WprMcp/Analyzers/VirtualAllocStackAnalysis.cs src/WprMcp/Analyzers/WaitAnalysis.cs tests/WprMcp.Tests/AnalysisEventsTests.cs tests/WprMcp.Tests/TraversalCancellationTests.cs tests/WprMcp.Tests/AnalyzerContextSurfaceTests.cs
git commit -m "refactor: propagate cancellation through trace traversal"
```

### Task 4: Share budgets across stacks, symbols, CPU batch, and composite diagnostics

**Files:**

- Modify: `src/WprMcp/Analyzers/StackSourceTopN.cs`
- Modify: `src/WprMcp/Core/SymbolService.cs`
- Modify: `src/WprMcp/Core/PolicySymbolReaderFactory.cs`
- Modify: `src/WprMcp/Analyzers/CpuAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/CpuPreciseAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/GenericEventStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/HeapAllocStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/StackProbeAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/WaitAnalysis.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Tools/DiagnoseTools.cs`
- Create: `tests/WprMcp.Tests/CompositeBudgetTests.cs`
- Modify: `tests/WprMcp.Tests/CpuAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`
- Modify: `tests/WprMcp.Tests/StackSourceTopNTests.cs`
- Modify: `tests/WprMcp.Tests/SymbolServiceTests.cs`

**Interfaces:**

- Consumes: the operation's single `AnalysisBudget` and deadline.
- Produces: stack-node charging at every node visit, symbol-attempt charging before every lookup/download, output-byte charging after privacy redaction, and composite sections that share the parent counters.

- [ ] **Step 1: Write failing shared-accounting tests.** Set a budget that permits the `diagnose_high_wait` prerequisite wait scan but not its first stack node; assert a partial result and total counters containing both phases. Set a CPU batch budget exhausted by the prerequisite full scan and assert no per-PID work starts. Verify section order does not reset counters. For stack nodes and symbol attempts, cover limit minus one/exact/plus one and a denied symbol request that charges one attempt but zero network bytes. Verify cancellation or budget exhaustion after a completed composite section preserves only that usable section as `Partial` and never reports a clean success.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~CompositeBudgetTests|FullyQualifiedName~CpuAnalysisTests|FullyQualifiedName~DiagnoseToolsTests|FullyQualifiedName~StackSourceTopNTests|FullyQualifiedName~SymbolServiceTests"` and capture the independent-stopwatch/reset failures.**

- [ ] **Step 3: Remove private budget clocks and charge at the work site.** Delete the `Stopwatch` budget in `CpuAnalysis` and the post-wait `Stopwatch` in `DiagnoseTools`. Pass the original context through every child call. Charge a stack node before inspecting it, charge a symbol attempt before local/DIA/remote resolution, and call the context deadline check around potentially blocking symbol operations. Child 5's serializer charges final UTF-8 output after privacy transformation; do not estimate characters. Composite error mapping reads the shared snapshot and records stable failed-section codes.

- [ ] **Step 4: Run the focused tests, then `rg -n "Stopwatch|new AnalysisBudget|AnalysisBudget\.For" src/WprMcp/Analyzers src/WprMcp/Tools` and require no analyzer/tool-created budget or stopwatch except telemetry timing explicitly allowlisted in `AnalyzerContextSurfaceTests`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Analyzers/StackSourceTopN.cs src/WprMcp/Core/SymbolService.cs src/WprMcp/Core/PolicySymbolReaderFactory.cs src/WprMcp/Analyzers/CpuAnalysis.cs src/WprMcp/Analyzers/CpuPreciseAnalysis.cs src/WprMcp/Analyzers/GenericEventStackAnalysis.cs src/WprMcp/Analyzers/HeapAllocStackAnalysis.cs src/WprMcp/Analyzers/StackProbeAnalysis.cs src/WprMcp/Analyzers/WaitAnalysis.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/DiagnoseTools.cs tests/WprMcp.Tests/CompositeBudgetTests.cs tests/WprMcp.Tests/CpuAnalysisTests.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs tests/WprMcp.Tests/StackSourceTopNTests.cs tests/WprMcp.Tests/SymbolServiceTests.cs
git commit -m "feat: share analysis budgets across composite work"
```

### Task 5: Report bounded monotonic MCP progress

**Files:**

- Create: `src/WprMcp/Core/AnalysisProgressReporter.cs`
- Create: `src/WprMcp/Core/McpProgressAdapter.cs`
- Modify: `src/WprMcp/Core/AnalysisOperationContext.cs`
- Modify: `src/WprMcp/McpServerOptions.cs`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `tests/WprMcp.Tests/McpSdkSurfaceTests.cs`
- Create: `tests/WprMcp.Tests/AnalysisProgressReporterTests.cs`
- Create: `tests/WprMcp.Tests/McpProgressIntegrationTests.cs`

**Interfaces:**

- Consumes: SDK-injected optional `IProgress<ProgressNotificationValue>`, `TimeProvider`, configured notifications/second, privacy mode, cancellation, and phase transitions.
- Produces: a thread-safe `IProgress<TraceProgress>` with monotonic fixed phases and an SDK adapter excluded from input schema.

- [ ] **Step 1: Write failing rate/privacy/schema tests.** Assert no progress object means zero work. With fake time, emit 100 reports in one second and require at most the configured rate, while always allowing one terminal completion report. Progress values never decrease, phase order never regresses, and completion is exactly `1/1`. Each serialized notification is at most 512 UTF-8 bytes and all notifications total at most 16 KiB. Only stable codes such as `validation_started`, `conversion_running`, `scan_running`, `symbols_running`, and `serialization_running` appear; path, module, symbol, user, host, IP, marker, and exception sentinels never appear. Cancellation/completion atomically closes the reporter and drops later reports. Reflection and real `tools/list` tests show injected cancellation/progress parameters absent from input schema.

```csharp
private static readonly IReadOnlyDictionary<TraceProgressPhase, (double Start, double End)> Ranges =
    new Dictionary<TraceProgressPhase, (double, double)>
    {
        [TraceProgressPhase.Validation] = (0.00, 0.10),
        [TraceProgressPhase.Conversion] = (0.10, 0.30),
        [TraceProgressPhase.Metadata] = (0.30, 0.40),
        [TraceProgressPhase.Scan] = (0.40, 0.75),
        [TraceProgressPhase.Symbols] = (0.75, 0.90),
        [TraceProgressPhase.Serialization] = (0.90, 0.99),
        [TraceProgressPhase.Completion] = (1.00, 1.00)
    };
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~AnalysisProgressReporterTests|FullyQualifiedName~McpProgressIntegrationTests|FullyQualifiedName~McpSdkSurfaceTests"` and verify absent injection/rate behavior.**

- [ ] **Step 3: Implement one reporter per operation.** Map phase-local fractions into the fixed ranges, clamp but never decrease using an atomic last-value field, use `TimeProvider` timestamps for a token bucket capped by configured rate (hard maximum 10/s), measure serialized UTF-8 bytes before sending, and redact to stable codes before the SDK adapter. Append `CancellationToken cancellationToken = default, IProgress<ProgressNotificationValue>? progress = null` to the representative `load_trace`/metadata and CPU tools; Child 8 Task 8 applies the signature to the remaining surface.

- [ ] **Step 4: Run the focused command and `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TelemetryTests`; require no progress after cancellation and no privacy sentinel in captured transport/log output.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/AnalysisProgressReporter.cs src/WprMcp/Core/McpProgressAdapter.cs src/WprMcp/Core/AnalysisOperationContext.cs src/WprMcp/McpServerOptions.cs src/WprMcp/Tools/MetaTools.cs src/WprMcp/Tools/CpuTools.cs tests/WprMcp.Tests/McpSdkSurfaceTests.cs tests/WprMcp.Tests/AnalysisProgressReporterTests.cs tests/WprMcp.Tests/McpProgressIntegrationTests.cs
git commit -m "feat: report bounded analysis progress"
```

### Task 6: Define the typed operation catalog and strict bounded IPC

**Files:**

- Create: `src/WprMcp/Core/TraceAnalysisSession.cs`
- Create: `src/WprMcp/Core/TraceOperation.cs`
- Create: `src/WprMcp/Core/TraceOperationCatalog.cs`
- Modify: `src/WprMcp/Core/InProcessTraceBackend.cs`
- Modify: `src/WprMcp/Core/TraceLease.cs`
- Create: `src/WprMcp/Worker/WorkerProtocol.cs`
- Create: `src/WprMcp/Worker/WorkerConversionOperation.cs`
- Create: `src/WprMcp/Worker/WorkerFrameCodec.cs`
- Create: `src/WprMcp/Worker/WorkerResultLimiter.cs`
- Create: `src/WprMcp/Worker/WorkerJsonContext.cs`
- Create: `src/WprMcp/Worker/WorkerProtocolStateMachine.cs`
- Create: `tests/WprMcp.Tests/TraceOperationCatalogTests.cs`
- Create: `tests/WprMcp.Tests/WorkerFrameCodecTests.cs`
- Create: `tests/WprMcp.Tests/WorkerProtocolStateMachineTests.cs`

**Interfaces:**

- Consumes: Child 7 backend/entry-owned `TraceLog` and identity index, source-generated JSON metadata for operation DTOs, and the configured IPC-frame ceiling.
- Produces: the typed `ITraceBackend.ExecuteAsync` contract, an ordinal analysis-operation catalog, one reserved typed `convert_trace/v1` operation that does not require an open trace session, strict version-1 messages, and a state machine usable by both parent and worker.

- [ ] **Step 1: Write failing catalog/codec/state tests.** Reject duplicate names/versions and unregistered analysis operations. Prove the in-process backend invokes an analysis operation against exactly its owned session/index and cannot be used after disposal. Gate two same-trace operations and assert only one enters TraceEvent/session code at a time; cancellation/deadline while waiting for that gate starts no scan and releases its Child 7 analysis-slot reservation. Prove `convert_trace/v1` is the sole reserved pre-session operation: it accepts only its source-generated argument/result DTOs and declared inherited-handle roles, rejects catalog shadowing, every other pre-session name/version, missing input/output grants, and a result whose length/hash/duration conflicts with the bounded output handle. Feed `WorkerResultLimiter` 127, 128, and 129 deterministically ordered rows; the last case returns 128, `HasMore=true`, and exact total 129 in both backends. For frames, cover payload length limit minus one/exact/plus one, zero length, truncated prefix/body, invalid UTF-8, unknown fields, duplicate top-level and nested properties, depth 32/33, collection 127/128/129, ordinary string 4,095/4,096/4,097 characters, symbol chunks at decoded 65,535/65,536/65,537 bytes, invalid/non-canonical base64, unknown/string/numeric message kinds, malformed correlation IDs, nonce/version mismatch, result before invoke, duplicate terminal frames, progress after terminal, symbol response without request, and cancel for another correlation. Assert the length check happens before an `ArrayPool` rent using an injected allocator. For every `Invoke`, including conversion, reject missing/duplicate/malformed grant IDs, zero/negative/over-parent remaining durations, negative/overflow/over-hard-limit budget or resource grants, mutable/unknown symbol snapshots, and broker tokens not bound to correlation plus context digest. Accept only a duration no greater than the parent-measured remainder. Reject `Result`/`Error` with missing/wrong/replayed grant ID, missing/negative/over-grant usage, or duplicate reconciliation.

```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind", UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(HelloMessage), "hello")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(InvokeMessage), "invoke")]
[JsonDerivedType(typeof(CancelMessage), "cancel")]
[JsonDerivedType(typeof(ProgressMessage), "progress")]
[JsonDerivedType(typeof(SymbolHttpRequestMessage), "symbol_http_request")]
[JsonDerivedType(typeof(SymbolHttpResponseMessage), "symbol_http_response")]
[JsonDerivedType(typeof(SymbolHttpChunkMessage), "symbol_http_chunk")]
[JsonDerivedType(typeof(ResultMessage), "result")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(ShutdownMessage), "shutdown")]
internal abstract record WorkerMessage(int ProtocolVersion, string CorrelationId);

internal sealed record InvokeMessage(
    int ProtocolVersion,
    string CorrelationId,
    string OperationName,
    int OperationVersion,
    JsonElement Arguments,
    WorkerInvokeGrant Grant) : WorkerMessage(ProtocolVersion, CorrelationId);

internal static class WorkerOperationNames
{
    internal const string ConvertTrace = "convert_trace";
    internal const int ConvertTraceVersion = 1;
}

internal sealed record ConvertTraceArgs(
    long InputBytes,
    long OutputGrantBytes,
    string InputHandleRole,
    string OutputHandleRole,
    string ExpectedInputSha256);

internal sealed record ConvertTraceResult(
    long OutputBytes,
    string OutputSha256,
    long TraceDurationUs);
```

`InvokeMessage` is the only work-dispatch envelope. `convert_trace/v1` is decoded with source-generated metadata and dispatched by `WorkerConversionOperation` before any `TraceAnalysisSession` exists; it may use only the inherited input/output handle roles bound during launch. All other names/versions must resolve through `TraceOperationCatalog` and require an opened worker-local analysis session. `ResultMessage` carries the source-generated `ConvertTraceResult` or catalog result plus the same grant ID and bounded `AnalysisBudgetUsage`; conversion is not an out-of-band command and follows the identical state, cancellation, deadline, quota-reconciliation, and terminal-frame rules.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceOperationCatalogTests|FullyQualifiedName~WorkerFrameCodecTests|FullyQualifiedName~WorkerProtocolStateMachineTests"` and verify absent catalog/codec failures.**

- [ ] **Step 3: Implement typed local dispatch, grant reconciliation, and frame parsing.** `TraceAnalysisSession` owns the `TraceLog`, the context-bound retryable identity-index owner from Task 3, and captured symbol context; dispose them through one compare/exchange path. The in-process session uses one cancellable operation gate acquired within the operation deadline. Each isolated session is operation-scoped and accepts exactly one invoke, so no cached entry retains a worker; global analysis-worker admission remains Child 7's quota. Catalog lookup compares ordinal names and exact integer version; the reserved conversion dispatch follows the pre-session rules above and cannot be registered or overridden by the analysis catalog. Acquire exactly one parent `AnalysisBudgetGrant`, then exactly one Child 7 runtime `ResourceVector`, before process creation; Task 7 represents the latter as an immutable `WorkerLaunchReservation`. Immediately before send, perform no reservation: recompute `WorkerInvokeGrant.RemainingDeadline` from the monotonic deadline, bind the random grant ID and symbol broker token to correlation/context digest, and copy the already-reserved analysis limits and launch resource grant into the immutable `WorkerInvokeGrant`. Worker validation creates its own linked CTS/timer and budget solely from that duration and those limits. Apply Job memory/CPU limits from the same immutable prelaunch runtime reservation. A terminal `Result` or `Error` must echo the grant ID and `AnalysisBudgetUsage`; validate it against the analysis grant and reconcile that grant once, while Task 7 reconciles runtime resources once after Job exit and owned cleanup. Missing/malformed terminal usage, crash, or cancellation without a terminal frame keeps both conservative reservations until Job exit/cleanup, then releases them without recording success. Spy tests require one analysis-budget reservation, one runtime reservation, and one identity-matched reconciliation of each per launch; either denial starts zero processes, and no send path may call either reserve method again. Apply `WorkerResultLimiter` before either backend serializes/returns so no result collection exceeds 128 and Child 5 section pagination remains exact. Frame reads use a four-byte stack buffer, `BinaryPrimitives.ReadUInt32LittleEndian`, reject over-limit before `ArrayPool<byte>.Shared.Rent`, read exactly, and reject invalid UTF-8. Before source-generated deserialization, stream the bounded payload through `Utf8JsonReader(MaxDepth=32)` with one ordinal property-name set per open object and element/property counters per container; reject duplicates and Child 5 string/collection limit violations, applying only the exact `dataBase64` exception above and validating its decoded length/canonical encoding. Then deserialize with the source-generated context and unmapped-member rejection. The protocol state machine permits progress/symbol submessages only between one invoke and one result/error.

- [ ] **Step 4: Run the focused tests 20 times and `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceRegistryTests|FullyQualifiedName~TraceIdentityIndexTests"`; require one session/index disposal and no backend surface regression.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceAnalysisSession.cs src/WprMcp/Core/TraceOperation.cs src/WprMcp/Core/TraceOperationCatalog.cs src/WprMcp/Core/InProcessTraceBackend.cs src/WprMcp/Core/TraceLease.cs src/WprMcp/Worker/WorkerProtocol.cs src/WprMcp/Worker/WorkerConversionOperation.cs src/WprMcp/Worker/WorkerFrameCodec.cs src/WprMcp/Worker/WorkerResultLimiter.cs src/WprMcp/Worker/WorkerJsonContext.cs src/WprMcp/Worker/WorkerProtocolStateMachine.cs tests/WprMcp.Tests/TraceOperationCatalogTests.cs tests/WprMcp.Tests/WorkerFrameCodecTests.cs tests/WprMcp.Tests/WorkerProtocolStateMachineTests.cs
git commit -m "feat: define typed trace operations and worker ipc"
```

### Task 7: Run conversion, analysis, and symbols in a restricted same-executable worker

**Files:**

- Create: `src/WprMcp/Worker/WorkerEntrypoint.cs`
- Create: `src/WprMcp/Worker/WorkerHost.cs`
- Create: `src/WprMcp/Worker/WorkerClient.cs`
- Create: `src/WprMcp/Worker/WorkerProcess.cs`
- Create: `src/WprMcp/Worker/WorkerExecutableLocator.cs`
- Create: `src/WprMcp/Worker/WindowsWorkerSandbox.cs`
- Create: `src/WprMcp/Worker/WorkerJob.cs`
- Create: `src/WprMcp/Worker/ActiveWorkerJobRegistry.cs`
- Create: `src/WprMcp/Worker/InheritedHandleList.cs`
- Create: `src/WprMcp/Worker/WorkerScratchDirectory.cs`
- Create: `src/WprMcp/Worker/WorkerScratchQuota.cs`
- Create: `src/WprMcp/Worker/WorkerScratchMonitor.cs`
- Create: `src/WprMcp/Worker/WorkerSymbolBroker.cs`
- Create: `src/WprMcp/Worker/BrokeredSymbolHttpHandler.cs`
- Create: `src/WprMcp/Worker/PdbIdentityValidator.cs`
- Create: `src/WprMcp/Worker/WorkerConversionExecutor.cs`
- Create: `src/WprMcp/Core/IsolatedTraceBackend.cs`
- Modify: `src/WprMcp/Core/TraceArtifactStore.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/RuntimeShutdownCoordinator.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/WprMcp.csproj`
- Create: `tests/WprMcp.Tests/WorkerHostTests.cs`
- Create: `tests/WprMcp.Tests/WorkerConversionExecutorTests.cs`
- Create: `tests/WprMcp.Tests/WorkerSymbolBrokerTests.cs`
- Create: `tests/WprMcp.Tests/WorkerRecoveryTests.cs`
- Create: `tests/WprMcp.Tests/SecureDefaultParserBoundaryTests.cs`

**Interfaces:**

- Consumes: validated source/artifact handles, worker quota reservation, operation/budget grant, immutable symbol policy, exact IPC handles, and hidden worker arguments.
- Produces: `ITraceConversionExecutor`, `IsolatedTraceBackend : IIsolatedTraceBackend`, recoverable operation-scoped conversion/analysis workers, and parent-brokered symbol transport.

- [ ] **Step 1: Write failing secure-boundary, reservation, disk-boundary, and recovery tests.** For conversion, require an ordinary `Invoke` carrying exact `convert_trace/v1`, source-generated `ConvertTraceArgs`, the once-reserved prelaunch analysis/resource grants, and the inherited input/output roles; reject any conversion before `Invoke`, any attempt to construct `TraceAnalysisSession` first, wrong role/grant/hash/length/version, or result/usage mismatch. Replace the source path after policy validation and assert worker output still hashes to handle bytes; assert no source sibling and no worker-created final artifact. Inject parser/converter factories into parent and worker and prove secure-default invokes every TraceEvent/`TraceLog` factory only in the worker PID, including ETLX validation, duration/metadata extraction, startup artifact revalidation, and `load_trace` orientation. Exercise the Task 1-selected `IWorkerScratchQuota` with output grant minus one, exact grant, and plus one; assert the plus-one `Write` fails before length/volume usage exceeds the grant, publishes nothing, and releases input/worker-output/parent-staging reservations and files. Assert peak ledger reservation is `inputBytes + 2 * outputGrant`, checked arithmetic rejects overflow, two conversions cannot make temporary plus retained artifacts exceed the global quota, and cancellation/crash at each copy/publication boundary restores the prior disk snapshot. For both conversion and analysis, gate process creation/Job exit/cleanup and assert exactly one analysis-budget grant and exactly one Child 7 runtime reservation are acquired before launch and held/reconciled under the Task 6 rules; either denial starts zero processes, the send path makes zero additional reservations, and runtime reconciliation occurs once after Job cleanup. Two simultaneous operations reserve two appropriate worker slots plus the checked aggregate `WorkerCommittedBytes`, `WorkerCpuTicks`, and scratch bytes; a third over quota starts no process. Register both Job/correlation pairs, cancel one, and prove only its exact Job terminates while the other completes; shutdown snapshots and terminates both, and identity-matched cleanup removes neither replacement registration. Assert Job memory/CPU limits equal the grant, queried peak/CPU usage is within and reconciled once, and cached registry entries retain zero worker slot/memory/CPU reservation after an operation. For analysis, deny worker access to the original trace/artifact path and configured local symbol roots, pass only the ETLX handle, reserve its scratch-copy length, and successfully run metadata; cancellation removes/reconciles that copy. Inspect worker token/capabilities and inherited handles. Assert worker direct DNS/TCP fails. Exercise a PDB identity resolved from an approved local root through the parent broker and an allowed remote request identified only by approved server ordinal/token; reject any worker URL, host, local path, header, method, unapproved ordinal, stale/replayed token, or wrong identity. Cover every local reparse/root and remote redirect/DNS/size denial from Child 6, downloaded-PDB GUID/age/name mismatch, chunk limit minus one/exact/plus one, cancellation between chunks, and no cache/scratch write on denial. Inject crash, hang, malformed/oversized frame, bad nonce, CPU/memory limit, parent disconnect, and Job termination; require bounded cleanup of scratch/reservations and a succeeding subsequent request. Assert parent cancellation sends one `Cancel`, waits `WorkerCleanupTimeout`, then terminates only the correlation-matched Job and ignores any late terminal frame.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~WorkerHostTests|FullyQualifiedName~WorkerConversionExecutorTests|FullyQualifiedName~WorkerSymbolBrokerTests|FullyQualifiedName~WorkerRecoveryTests|FullyQualifiedName~SecureDefaultParserBoundaryTests"` and verify the production worker classes are missing.**

- [ ] **Step 3: Implement hardened launch, exact handle flow, operation-scoped reservations, and transient disk enforcement.** Reuse only APIs proven in Task 1. `WorkerExecutableLocator` accepts `Environment.ProcessPath` only when its file name, embedded product, informational version, and assembly location identify the current WprMcp apphost; otherwise it accepts an adjacent matching `WprMcp.exe` or fails startup. Generate a random worker nonce and private scratch name; ACL only the AppContainer SID and parent; create anonymous pipe/source/output handles non-inheritable by default, duplicate only the exact child handles as inheritable, and pass those handles via `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`. Before every launch, atomically reserve the checked scratch formula plus exactly one conversion/analysis slot and the granted worker committed-memory/CPU ticks through Child 7's quota manager; fail before process creation if any component is unavailable. Hold that single immutable `WorkerLaunchReservation` until Job exit and all owned handles/pipes/scratch/staging are cleaned; copy its resource grant into the later `WorkerInvokeGrant` and never reacquire it at send. Derive Job process-memory/time limits from the same reservation, query Job peak committed memory/CPU at exit, validate them against the grant, record actuals, then reconcile ephemeral resources to zero once. `ActiveWorkerJobRegistry` owns a correlation-keyed concurrent map shared by conversion and analysis launches. Register the exact Job identity after assignment and before resume; per-request cancellation may signal/terminate only the matching entry, shutdown snapshots and terminates every entry, and final cleanup removes only an identity-matched entry after Job exit and resource cleanup. `IsolatedTraceBackend` stores only artifact identity/routing and uses that registry; it launches one worker per invoke and never parks a worker or memory reservation on a cached entry. Instantiate only the `IWorkerScratchQuota` mechanism recorded as passing in Task 1 and fail secure-default startup when it is unavailable. Create suspended, assign the Job, register it, resume, and require `Hello -> Ready` nonce/version handshake before work. For conversion, send exactly one `Invoke(convert_trace, 1, ConvertTraceArgs, WorkerInvokeGrant)` before opening a trace session; cap the input-copy destination at `inputBytes`, the converter's actual destination at `outputGrant` (bounded converter stream or OS-bounded scratch, exactly as proven), and the inherited parent-output copy at `outputGrant`; no intermediate is merely observed after an unbounded write. Worker validates the typed arguments/handle roles/grant, opens the bounded ETLX in the worker to validate and obtain duration, copies the completed bytes through the capped inherited parent-output handle, flushes and verifies the observed length/hash, and only then emits the typed length/hash/duration result plus bounded usage. Parent validates that result against the completed handle and grant before publication. `WorkerScratchMonitor` polls owned lengths only as defense in depth and treats any mismatch as worker compromise; it is not the cap. Parent checks only reported-versus-observed length/hash plus manifest schema, fsyncs, atomically publishes, removes worker scratch, and promotes only the retained artifact into its separate persistent object charge; it never calls TraceEvent in secure-default. Startup artifact revalidation checks manifest/hash in the parent and defers parser validity to the operation worker. For analysis, reserve the artifact-length duplicate in the same prelaunch reservation, copy the immutable ETLX handle to scratch under its hard quota, construct `TraceAnalysisSession`, execute the one catalog operation, and never send parsed objects; cleanup releases the duplicate reservation.

- [ ] **Step 4: Implement identity-only symbol brokering and cancellation cleanup.** `BrokeredSymbolHttpHandler` emits only exact PDB identity `(fileName, GUID, age)`, an approved server ordinal from `WorkerSymbolGrant`, and its correlation/grant-bound broker token. Parent `WorkerSymbolBroker` validates the token and ordinal, first resolves that identity beneath Child 6-approved local roots using reparse-safe handle validation, and otherwise asks the immutable Child 6 symbol catalog to construct the canonical URI for that ordinal and identity. It then reuses Child 6 `SymbolPolicyHttpHandler` for every network hop. It rejects any worker-supplied URL/host/path/header/method and validates downloaded bytes with `PdbIdentityValidator` before cache publication or worker return. It reserves parent cache/download bytes plus the worker's bounded PDB scratch copy and sends bytes in chunks no larger than `min(64 KiB, MaxIpcFrameBytes - envelopeOverhead)`. On cancellation/deadline/protocol error, stop broker file/network reads, cancel operation, close pipes, terminate the Job after grace, release Child 7 reservations, and remove only the operation scratch directory. `RuntimeShutdownCoordinator` invokes `IIsolatedTraceBackend.TerminateAsync` at deadline.

- [ ] **Step 5: Run the focused command 20 times, then `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~SecureDefaultParserBoundaryTests|FullyQualifiedName~TraceArtifactStoreTests|FullyQualifiedName~SymbolPolicy|FullyQualifiedName~RuntimeQuota|FullyQualifiedName~RuntimeShutdown"`. Use Process Explorer-equivalent test inspection from `WorkerHostTests` to assert the exact handle/capability list.**

- [ ] **Step 6: Commit.**

```powershell
git add src/WprMcp/Worker/WorkerEntrypoint.cs src/WprMcp/Worker/WorkerHost.cs src/WprMcp/Worker/WorkerClient.cs src/WprMcp/Worker/WorkerProcess.cs src/WprMcp/Worker/WorkerExecutableLocator.cs src/WprMcp/Worker/WindowsWorkerSandbox.cs src/WprMcp/Worker/WorkerJob.cs src/WprMcp/Worker/ActiveWorkerJobRegistry.cs src/WprMcp/Worker/InheritedHandleList.cs src/WprMcp/Worker/WorkerScratchDirectory.cs src/WprMcp/Worker/WorkerScratchQuota.cs src/WprMcp/Worker/WorkerScratchMonitor.cs src/WprMcp/Worker/WorkerSymbolBroker.cs src/WprMcp/Worker/BrokeredSymbolHttpHandler.cs src/WprMcp/Worker/PdbIdentityValidator.cs src/WprMcp/Worker/WorkerConversionExecutor.cs src/WprMcp/Core/IsolatedTraceBackend.cs src/WprMcp/Core/TraceArtifactStore.cs src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/RuntimeShutdownCoordinator.cs src/WprMcp/Program.cs src/WprMcp/WprMcp.csproj tests/WprMcp.Tests/WorkerHostTests.cs tests/WprMcp.Tests/WorkerConversionExecutorTests.cs tests/WprMcp.Tests/WorkerSymbolBrokerTests.cs tests/WprMcp.Tests/WorkerRecoveryTests.cs tests/WprMcp.Tests/SecureDefaultParserBoundaryTests.cs
git commit -m "feat: isolate trace work in restricted workers"
```

### Task 8: Route every tool by profile, prove cancellation SLA, and preserve one-executable packaging

**Files:**

- Create: `src/WprMcp/Core/TraceBackendRouter.cs`
- Create: `src/WprMcp/Worker/TrustedLocalCapabilityRecord.cs`
- Create: `src/WprMcp/Worker/trusted-local-capability.json`
- Modify: `src/WprMcp/Core/TraceOperationCatalog.cs`
- Modify: `src/WprMcp/Tools/AlpcTools.cs`
- Modify: `src/WprMcp/Tools/ClrTools.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Tools/DiagnoseTools.cs`
- Modify: `src/WprMcp/Tools/GenericProviderTools.cs`
- Modify: `src/WprMcp/Tools/HardFaultTools.cs`
- Modify: `src/WprMcp/Tools/HeapTools.cs`
- Modify: `src/WprMcp/Tools/ImageLoadTools.cs`
- Modify: `src/WprMcp/Tools/InterruptTools.cs`
- Modify: `src/WprMcp/Tools/IoTools.cs`
- Modify: `src/WprMcp/Tools/MarkerTools.cs`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Modify: `src/WprMcp/Tools/NetIoTools.cs`
- Modify: `src/WprMcp/Tools/ReadyThreadTools.cs`
- Modify: `src/WprMcp/Tools/RegistryTools.cs`
- Modify: `src/WprMcp/Tools/SecurityTools.cs`
- Modify: `src/WprMcp/Tools/SymbolTools.cs`
- Modify: `src/WprMcp/Tools/TraceLifecycleTools.cs`
- Modify: `src/WprMcp/Tools/VirtualMemoryTools.cs`
- Modify: `src/WprMcp/Tools/WaitTools.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/WprMcp.csproj`
- Create: `tests/WprMcp.Tests/TraceBackendRouterTests.cs`
- Create: `tests/WprMcp.Tests/ToolOperationSurfaceTests.cs`
- Create: `tests/WprMcp.Tests/CancellationPhaseMatrixTests.cs`
- Create: `tests/WprMcp.Tests/TrustedLocalCancellationSlaTests.cs`
- Create: `tests/performance/Capture-LargeCancellationFixture.ps1`
- Create: `tests/performance/Measure-TrustedLocalCancellation.ps1`
- Modify: `.github/workflows/quality.yml`

**Interfaces:**

- Consumes: startup execution profile, exact capability fingerprint, operation catalog, all tool DTOs, SDK cancellation/progress injection, and release-native layout.
- Produces: every query as exactly one typed backend operation; secure-default/isolated trusted-local routing; gated trusted-local in-process routing; a generated reviewed p95 record; and one self-contained `WprMcp.exe` worker/host package.

- [ ] **Step 1: Write failing routing and whole-surface tests.** Reflection over every `[McpServerTool]` method requires injected `CancellationToken cancellationToken = default` and optional `IProgress<ProgressNotificationValue>? progress = null`, both absent from its advertised input schema. Every trace query additionally keeps public parameter `path`, invokes exactly one catalog operation, and contains no `lease.Backend.Trace`, `TraceLog`, direct analyzer call, or new child budget. Control tools `load_trace`, `unload_trace`, `trace_cache_status`, `set_symbol_path`, and `add_symbol_server` are excluded only from the one-operation rule; load still uses isolated conversion in secure-default, unload observes cancellation, and no-op progress remains silent. Router tests assert secure-default always creates isolated conversion and analysis; trusted-local defaults isolated; explicit in-process succeeds only on exact capability match; every mismatched fingerprint field—including executable SHA-256—or p95 above 2 seconds fails startup before trace access.

```csharp
internal sealed record TrustedLocalCapabilityRecord(
    int SchemaVersion,
    string TargetFramework,
    string RuntimeIdentifier,
    string TraceEventVersion,
    int OperationCatalogVersion,
    string AssemblyInformationalVersion,
    string ExecutableSha256,
    string CaptureRecipeVersion,
    int SampleCount,
    double P95CancellationMilliseconds,
    DateTimeOffset MeasuredAtUtc);
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceBackendRouterTests|FullyQualifiedName~ToolOperationSurfaceTests|FullyQualifiedName~CancellationPhaseMatrixTests|FullyQualifiedName~TrustedLocalCancellationSlaTests"` and verify direct-tool/backend and missing capability failures.**

- [ ] **Step 3: Register and route every operation.** For each existing query, use its exact advertised `[McpServerTool]` snake-case tool name as `TraceOperation.Name` and version `1`; the startup catalog test rejects any query whose advertised name and operation key differ. Its delegate runs the current analyzer against `TraceAnalysisSession` plus the caller's one context. Change every tool file listed in this task to resolve public `path` through `ITraceReferenceResolver`, `await using` its lease, create one operation context, and call `lease.Backend.ExecuteAsync`. Preserve compatibility warnings and ID-only no-implicit-write behavior from Child 6. After `load_trace` completes isolated conversion/registration, acquire its new ID and execute the internal exact key `load_trace_orientation` version `1` for duration, capabilities, metadata, and symbol recommendations so those parser reads also remain in the worker; the control tool may therefore perform conversion plus this named operation even though ordinary queries perform exactly one. Register secure conversion executor/`IsolatedTraceBackend` for `SecureDefault`; register the same defaults for `TrustedLocal/Isolated`; register the in-process converter/backend only after capability validation.

```csharp
ResolvedTraceReference resolved = await _traceReferences.ResolveQueryAsync(path, cancellationToken);
await using TraceLease lease = resolved.Lease;
AnalysisOperationContext context = _operationContexts.Create(cancellationToken, progress);
CpuTopFunctionsResponse data = await lease.Backend.ExecuteAsync(
    TraceOperations.CpuTopFunctions,
    new CpuTopFunctionsArgs(pid, startUs, endUs, top),
    context);
```

- [ ] **Step 4: Test every cancellation phase and hard boundary.** Use barriers to cancel while queued, copying input, converting/growing worker output, copying to parent staging, publishing, parsing metadata, building identities, scanning, resolving a brokered symbol, serializing, and waiting for worker cleanup. For each, assert the Child 5 `Failed/cancelled` versus usable-section `Partial` contract, no late transport success, progress closure, cleanup within five seconds, no live background CPU/network, no temp/artifact/reference/quota leak, and a succeeding subsequent request. Test limit minus one/exact/plus one for conversion workers, analysis workers, operation wall time, conversion output bytes, combined transient scratch/staging plus retained artifact bytes, committed memory, CPU time, IPC frame, progress rate, event visits, stack-node visits, symbol attempts, symbol download, artifact bytes, and final output bytes. As a dependency regression only, run Child 5's existing `JsonRpcFrameLimitingStream` tests at the default 99,999/100,000/100,001 bytes, one lowered configured cap boundary, 127/128/129-byte serialized IDs, and huge unterminated input; do not replace its wrapper or introduce another stdin limit. Child 9 repeats this through the real stdio process and proves clean-process recovery.

- [ ] **Step 5: Capture and gate trusted-local p95.** `Capture-LargeCancellationFixture.ps1` records a 90-second CPU+disk+CLR+CSwitch ETW workload with repository WPR profiles into `artifacts/cancellation-corpus/large-cancellation.etl` and writes capture recipe version `large-cancel-v1`. `Measure-TrustedLocalCancellation.ps1` runs 30 process-isolated in-process trials, cancels after traversal starts, sorts elapsed milliseconds with nearest-rank p95, and writes `trusted-local-capability.json` with the exact build/package/RID fingerprint. Review the generated measurements into source control only when p95 is at most 2000 ms; otherwise set no in-process capability and assert startup rejects `--trusted-local-analysis in-process`. CI always tests record matching; the dedicated release runner regenerates the capture/measurement and compares the checked-in p95 gate.

- [ ] **Step 6: Verify published recovery and append the reusable quality gate.** Add the worker/cancellation suites and the local `win-x64` publish/recovery smoke to Child 11A's `.github/workflows/quality.yml`; leave `.github/workflows/ci.yml` as a trigger-only caller and leave `.github/workflows/release.yml` untouched until Child 11B. Publish self-contained to `artifacts/child8-publish`; assert exactly one executable role (`WprMcp.exe`), the existing `native/amd64` directory, and the capability JSON copied beside the executable. Run host `--version`, then launch hidden worker mode without the required inherited handles and require a bounded nonzero exit with no MCP stdout frame. Run one secure-default fixture load/query from the published executable and kill its worker mid-query; the next query must succeed. This is a child-level smoke directory, not the immutable release zip; only Child 11B packages/uploads release bytes.

```powershell
dotnet test WprMcp.sln -c Release
dotnet publish src/WprMcp/WprMcp.csproj -c Release -r win-x64 --self-contained true -o artifacts/child8-publish
artifacts/child8-publish/WprMcp.exe --version
Get-ChildItem artifacts/child8-publish -Recurse | Select-Object FullName
```

- [ ] **Step 7: Commit the complete routing and release gate.**

```powershell
git add src/WprMcp/Core/TraceBackendRouter.cs src/WprMcp/Core/TraceOperationCatalog.cs src/WprMcp/Worker/TrustedLocalCapabilityRecord.cs src/WprMcp/Worker/trusted-local-capability.json src/WprMcp/Tools/AlpcTools.cs src/WprMcp/Tools/ClrTools.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Tools/GenericProviderTools.cs src/WprMcp/Tools/HardFaultTools.cs src/WprMcp/Tools/HeapTools.cs src/WprMcp/Tools/ImageLoadTools.cs src/WprMcp/Tools/InterruptTools.cs src/WprMcp/Tools/IoTools.cs src/WprMcp/Tools/MarkerTools.cs src/WprMcp/Tools/MetaTools.cs src/WprMcp/Tools/NetIoTools.cs src/WprMcp/Tools/ReadyThreadTools.cs src/WprMcp/Tools/RegistryTools.cs src/WprMcp/Tools/SecurityTools.cs src/WprMcp/Tools/SymbolTools.cs src/WprMcp/Tools/TraceLifecycleTools.cs src/WprMcp/Tools/VirtualMemoryTools.cs src/WprMcp/Tools/WaitTools.cs src/WprMcp/Program.cs src/WprMcp/WprMcp.csproj tests/WprMcp.Tests/TraceBackendRouterTests.cs tests/WprMcp.Tests/ToolOperationSurfaceTests.cs tests/WprMcp.Tests/CancellationPhaseMatrixTests.cs tests/WprMcp.Tests/TrustedLocalCancellationSlaTests.cs tests/performance/Capture-LargeCancellationFixture.ps1 tests/performance/Measure-TrustedLocalCancellation.ps1 .github/workflows/quality.yml
git commit -m "feat: route all trace work through bounded worker backends"
```

## Acceptance Gate and Handoff

Run from repository root on the supported Windows RID:

```powershell
dotnet restore WprMcp.sln
dotnet build WprMcp.sln -c Release --no-restore
dotnet test WprMcp.sln -c Release --no-build
rg -n "lease\.Backend\.Trace|trace\.Events|source\.Process\(\)|new AnalysisBudget|Stopwatch" src/WprMcp/Tools src/WprMcp/Analyzers
dotnet publish src/WprMcp/WprMcp.csproj -c Release -r win-x64 --self-contained true -o artifacts/child8-publish
artifacts/child8-publish/WprMcp.exe --version
git status --short
```

The gate passes only when secure-default conversion, parsing, all analysis, and symbols execute in a no-network restricted worker; only exact handles are inherited; the converter's offending scratch/output write is denied by the proven hard mechanism; temporary and retained disk, memory, CPU, child, time, frame, work, download, and output limits hold at minus-one/exact/plus-one boundaries; cancellation in every phase stops progress and background work and cleans resources within five seconds; no transport-cancelled request emits a later success; composite prerequisites share a budget; malformed/crashed/timed-out workers do not poison later requests; and trusted-local in-process is unavailable unless exact current p95 evidence is at most two seconds.

Child 8 deliberately changes Child 6's provisional `ITraceBackend` from `Trace` exposure to typed `ExecuteAsync`, while preserving Child 7's lease and registry state machine. `TraceAnalysisSession` owns Child 7's identity index inside the chosen process; isolated mode never recreates a parent-side static index. Rebase conflicts are expected in `Program.cs`, `McpServerOptions.cs`, `TraceRegistry.cs`, `TraceArtifactStore.cs`, `InProcessTraceBackend.cs`, `TraceLease.cs`, `SymbolService.cs`, the 19 tool files, and release workflows; integrate in Child 6 -> Child 7 -> Child 8 order. Child 9 extends this with real MCP stdio cancellation/client-disconnect tests, and Child 11A's version ADR is authoritative over the baseline versions printed in this plan.

## Mandatory Spikes and Stop Conditions

- Task 1 is a hard gate and the first implementation commit. Failure to enforce AppContainer no-network, exact handle inheritance, Job memory/CPU/active-process/kill scope, same-executable IPC, DIA loading, or real TraceEvent stop behavior blocks secure-default; do not replace an enforcement failure with documentation or an in-process fallback.
- Secure-default conversion also remains blocked unless Task 1 proves a hard write-time scratch/output cap through the actual TraceEvent conversion path or an OS-enforced per-worker bounded scratch volume. Logical quota reservation, free-space checks, post-write length checks, polling, and worker termination after an overshoot are useful defense in depth but do not pass this gate.
- If the selected MCP SDK does not inject cancellation/progress exactly as proven by real `tools/list` and invocation, adapt the tool binding at the SDK boundary before migrating analyzers. Do not add cancellation/progress as user-supplied schema fields.
- If DIA or TraceEvent cannot operate under the no-network AppContainer from the shipped single-executable/native layout, test a brokered symbol byte stream and private scratch ACL within the same threat boundary. If that still fails, block symbolized secure-default analysis; never grant the worker ambient network or arbitrary filesystem access.
- If cooperative cancellation exceeds two seconds p95 on the generated representative large trace, isolated execution is mandatory in trusted-local too. The two-second measurement can allow in-process only for trusted-local; it can never waive secure-default isolation.
- A worker protocol, quota ledger, artifact, symbol, or cleanup invariant that cannot be proven deterministically is a production gate failure. Increasing limits, cleanup time, or retry counts is not a substitute for ownership evidence.
