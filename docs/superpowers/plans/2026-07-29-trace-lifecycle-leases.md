# Trace Lifecycle, Leases, and Quotas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every evaluated trace one explicit owner, prevent eviction or shutdown from disposing beneath an active analysis, and enforce deterministic entry/disk/admission quotas with observable unload and shutdown outcomes.

**Architecture:** `TraceRegistry` owns a per-trace entry whose locked state moves through `Loading -> Ready -> Retiring -> Disposed` or terminal `Faulted`. Queries receive ref-counted `TraceLease` instances only from `Ready`; retirement is the linearization point that rejects future acquisition while existing leases drain. A single admission controller coordinates entry count and disk/worker reservations, while a file-locked quota ledger makes artifact and symbol reservations atomic across server processes. Shutdown retires entries, cancels operations, drains to a deadline, and may kill only isolated Child 8 backends.

**Tech Stack:** The C# version, TFM, MCP SDK, and TraceEvent package selected by Child 11A; the current baseline is C# 12, .NET 8, ModelContextProtocol 1.2.0, and TraceEvent 3.2.2. Lifecycle code uses `TimeProvider`, `System.Threading.Channels`, Windows process identity APIs, JSON source generation, and xUnit concurrency tests.

## Accepted capability/evidence amendment (2026-08-01)

This plan continues to own trace-entry state, handle/lease lifetime, backend retirement, quotas, and exactly-once disposal. Those runtime ownership rules are separate from persistent artifact retention: retirement may close the trace handle/backend and make an ID unusable without deciding when a content-addressed ETLX or symbol artifact is deleted.

Under [ADR 0002](../../decisions/0002-capability-map-evidence-contract.md), the retention/deletion policy is a follow-up ADR gate. Therefore the older “final reference immediately deletes the object” target below is suspended as a persistent-artifact policy; until the follow-up decision, final-reference logic may release runtime leases and accounting but must not be treated as authorization for a final public retention guarantee.

## Global Constraints

- This plan starts after `2026-07-29-trace-symbol-access-policy.md`. It consumes `TraceDescriptor`, `ITraceRegistry`, `TraceLease`, `ITraceBackend`, `ITraceReferenceResolver`, `TraceArtifactKey`, `TraceArtifactStore`, and the trace-ID grammar without changing tool call sites.
- State transitions occur under one entry-local lock. The dictionary contains identity-bearing entry objects; every removal uses compare-by-key-and-entry-identity. No continuation may remove or dispose a newer replacement.
- Only `Ready` can grant a lease. The exact `Ready -> Retiring` transition is the eviction/unload/shutdown linearization point. `Retiring` never returns to `Ready`, even if the caller waiting for drain cancels or times out.
- If retirement is requested during `Loading`, set `RetireRequested=true`. A successful load performs `Loading -> Ready -> Retiring` under the same lock before any acquisition can observe `Ready`; a failed load performs `Loading -> Faulted` and removes only itself.
- The last active lease performs exactly-once backend disposal for a retiring entry. A lease release is idempotent. No timeout path, cache scan, or shutdown path force-disposes a backend with an active in-process lease.
- Entry capacity includes `Loading`, `Ready`, and `Retiring` entries until disposal completes. Disk bytes remain charged until an unreferenced artifact is actually removed. Report `TraceLog` resident bytes as an estimate and never use that estimate to claim a hard memory limit.
- Each ready entry owns exactly one `ITraceArtifactLease` and one lazily constructed `TraceIdentityIndex`. Multiple trace IDs or source generations may share one `TraceArtifactKey`; releasing one entry cannot remove the artifact while another in-process or cross-process artifact lease exists. The final lease deletes the object under its artifact lock and releases its actual disk-byte charge exactly once.
- Child 1's `TraceIdentityIndex.For(TraceLog)`/`ConditionalWeakTable` is explicitly temporary. This child moves its `Lazy<TraceIdentityIndex>` into `TraceRegistryEntry`/`InProcessTraceBackend`, passes the index explicitly to consumers, and removes the static cache so index lifetime ends with exactly-once backend disposal.
- When all eviction candidates are leased, a load reserves one of at most eight default queued slots and waits within its admission/deadline budget; queue 9 fails `budget_exceeded`. It never opens a temporary over-capacity entry.
- Cross-process accounting is fail closed: if the ledger cannot be locked, parsed, reconciled, or atomically persisted, new admission fails without conversion, symbol download, or cache growth.
- The only nested cross-process lock order is object-key lock (artifact or symbol) first, then quota-ledger lock. Publication, reference acquisition/release, eviction, scavenging, and stale-owner recovery all follow that order; multi-object reconciliation orders `(object-kind, key)` ordinally before taking the ledger lock.
- Child 8 consumes `IRuntimeQuotaManager`, `ResourceVector`, `TraceRegistry.StopAdmission`, and the optional `IIsolatedTraceBackend` termination seam. It must reserve conversion/analysis worker slots and reconcile worker usage through the same manager rather than adding semaphores beside it.
- Keep `tests/WprMcp.Tests/AssemblyInfo.cs` unchanged; Child 9 owns the parallel-test policy.

## Configuration and Stable Interfaces

Extend Child 6's staged `McpServerOptions` with one nested `TraceRegistryOptions Registry`; preserve `HostArgs`, Child 5 `ToolExecution` and all of its derived `ContractMode`/privacy/budget compatibility accessors, plus Child 6 trace/symbol access-policy semantics. The sole intentional `TracePolicy` shape change is removal of the provisional symbol total-cache quota after migrating it to Registry. Child 7 also consumes/removes Child 6's temporary nullable `CacheSize`; do not flatten or reconstruct any other earlier-child field. `Registry` is the sole final runtime owner of entry and total-cache sizes:

```csharp
internal sealed record TraceRegistryOptions(
    int MaxEntries = 2,
    TimeSpan IdleLifetime = default,
    TimeSpan AbsoluteLifetime = default,
    TimeSpan UnloadDrainTimeout = default,
    TimeSpan ShutdownDrainTimeout = default,
    long MaxArtifactStoreBytes = 64L * 1024 * 1024 * 1024,
    long MaxSymbolCacheBytes = 20L * 1024 * 1024 * 1024,
    int MaxQueuedLoads = 8)
{
    internal static TraceRegistryOptions Defaults => new(
        MaxEntries: 2,
        IdleLifetime: TimeSpan.FromMinutes(30),
        AbsoluteLifetime: TimeSpan.FromHours(4),
        UnloadDrainTimeout: TimeSpan.FromSeconds(30),
        ShutdownDrainTimeout: TimeSpan.FromSeconds(30));
}

internal sealed record McpServerOptions(
    string[] HostArgs,
    ToolExecutionOptions ToolExecution,
    TraceAndSymbolPolicyOptions TracePolicy,
    TraceRegistryOptions Registry);
```

Child 7 keeps accepting the existing `--cache-size <1..32>` CLI flag and consumes its staged nullable value exactly once as `Registry.MaxEntries = parsedCacheSize ?? 2`; after construction the final record has no `CacheSize` property and no duplicate field/flag/parser. Child 6 already removed `SymbolPath`, so Child 7 must not restore it. `McpServerOptionsTests` use reflection to require both scalar properties to be absent while their CLI flags retain the established syntax and error behavior. Child 7 also keeps Child 6's existing `--symbol-cache-bytes <1..107374182400>` syntax; it moves rather than reparses the established value. New Child 7 flags are `--trace-idle-lifetime <00:00:01..1.00:00:00>`, `--trace-absolute-lifetime <00:00:01..7.00:00:00>`, `--unload-drain-timeout <00:00:00..00:05:00>`, `--shutdown-drain-timeout <00:00:01..00:05:00>`, `--artifact-store-bytes <1..274877906944>`, and `--max-queued-trace-loads <0..32>`. Idle must not exceed absolute lifetime. The byte ceilings are 256 GiB and 100 GiB from `RuntimeHardLimits`.

Child 7 also consumes Child 6's provisional `TracePolicy.Symbols.MaxCacheBytes` into `Registry.MaxSymbolCacheBytes` while preserving its 20 GiB default and introducing the single `--symbol-cache-bytes` override. In the final source shape, remove `MaxCacheBytes` from `SymbolPolicyOptions`; symbol policy retains cache location, origins/networks, per-download `MaxDownloadBytes`, and other access decisions, while every runtime admission/eviction/status consumer reads only `Registry.MaxSymbolCacheBytes`. `RuntimeHardLimits.MaxSymbolCacheBytes` is only the immutable 100 GiB validation ceiling. There is no comparison between two configurable quota values because only the Registry value exists after construction.

Use these lifecycle and quota contracts:

```csharp
internal enum TraceEntryState { Loading, Ready, Retiring, Disposed, Faulted }

internal enum UnloadTraceDisposition { Drained, Pending, NotFound, TimedOut }

internal sealed record UnloadTraceResult(
    string TraceId,
    UnloadTraceDisposition Disposition,
    int ActiveLeases);

internal readonly record struct ResourceVector(
    long ArtifactBytes,
    long SymbolBytes,
    long WorkerCommittedBytes,
    long WorkerCpuTicks,
    int Entries,
    int ConversionWorkers,
    int AnalysisWorkers,
    int QueuedLoads)
{
    internal static ResourceVector operator +(ResourceVector left, ResourceVector right);
    internal static ResourceVector operator -(ResourceVector left, ResourceVector right);
}

internal interface IResourceReservation : IAsyncDisposable
{
    string ReservationId { get; }
    ResourceVector Reserved { get; }
    ValueTask ReconcileAsync(ResourceVector actual, CancellationToken cancellationToken);
}

internal interface IRuntimeQuotaManager
{
    ValueTask<IResourceReservation> ReserveAsync(
        ResourceVector request,
        long deadlineTimestamp,
        CancellationToken cancellationToken);
    RuntimeQuotaSnapshot Snapshot();
}

internal interface IIsolatedTraceBackend : ITraceBackend
{
    ValueTask TerminateAsync(CancellationToken cancellationToken);
}

internal interface ITraceArtifactLease : IAsyncDisposable
{
    TraceArtifact Artifact { get; }
    string ReferenceId { get; }
}
```

The quota ledger layout is:

```text
<artifact-root>/quota/ledger.lock
<artifact-root>/quota/reservations/<random-128-bit-id>.json
```

Each reservation record contains schema version `1`, reservation ID, server-instance ID, PID, process start time from the OS, creation/deadline timestamps, `ResourceVector`, and optional artifact/symbol object key. Write a new file then atomically rename it while holding `ledger.lock` with `FileShare.None`. Reclaim a record only after proving the PID no longer exists or its current process start time differs; an access-denied process check is treated as live.

---

### Task 1: Implement the entry state machine and idempotent lease release

**Files:**

- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/TraceLease.cs`
- Create: `src/WprMcp/Core/TraceRegistryEntry.cs`
- Modify: `src/WprMcp/Core/InProcessTraceBackend.cs`
- Modify: `src/WprMcp/Analyzers/TraceIdentityIndex.cs`
- Create: `src/WprMcp/Analyzers/TraceIdentityIndexFactory.cs`
- Modify: `src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/GcAnalysis.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Modify: `src/WprMcp/Tools/WaitTools.cs`
- Modify: `src/WprMcp/Tools/DiagnoseTools.cs`
- Modify: `src/WprMcp/Tools/ClrTools.cs`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Replace tests in: `tests/WprMcp.Tests/TraceRegistryTests.cs`
- Modify: `tests/WprMcp.Tests/TraceCacheTests.cs`
- Modify: `tests/WprMcp.Tests/TraceIdentityIndexTests.cs`
- Modify: `tests/WprMcp.Tests/ThreadLifetimeAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/GcAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`
- Modify: `tests/WprMcp.Tests/ThreadScopedCpuWaitTests.cs`

**Interfaces:**

- Consumes: Child 6 `ITraceBackend` factory and `TraceDescriptor`.
- Produces: `TraceRegistryEntry.State`, `ActiveLeases`, `ReadyTask`, `DrainTask`, entry-owned `Lazy<TraceIdentityIndex>`, and registry acquisitions returning an idempotent `TraceLease`.

- [ ] **Step 1: Write a failing transition and owned-index matrix.** Deterministically pause backend creation and race register/acquire/retire. Assert no lease during `Loading`, only `Loading -> Ready`, `Loading -> Faulted`, `Ready -> Retiring`, and `Retiring -> Disposed`; retirement during load cannot expose `Ready`; double lease disposal decrements once; 64 concurrent last-release calls dispose the backend once; and `AcquireAsync` on `Retiring`, `Disposed`, or `Faulted` returns `trace_not_loaded`. Inject an `ITraceIdentityIndexFactory` returning a counted `ITraceIdentityIndexOwner`; concurrently request the index from 64 analyses and assert one factory call and one immutable instance. After final backend disposal, assert an internal observable owner slot is null and the owner's release callback ran exactly once. Add an architecture assertion that `TraceIdentityIndex.cs` contains no `ConditionalWeakTable` and no public static `For(TraceLog)`. Do not use forced GC or weak-reference timing as evidence.

```csharp
[Fact]
public async Task RetireDuringLoad_SkipsAcquirableReadyState()
{
    Task<TraceDescriptor> loading = registry.RegisterAsync(source, default).AsTask();
    await backendFactory.Entered;
    registry.RequestRetirement(traceId, RetirementReason.Unload);
    backendFactory.Release();

    await loading;
    await Assert.ThrowsAsync<TraceNotLoadedException>(
        () => registry.AcquireAsync(traceId, default).AsTask());
    Assert.Equal(TraceEntryState.Disposed, registry.GetStateForTest(traceId));
    Assert.Equal(1, backendFactory.DisposeCount);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceRegistryTests|FullyQualifiedName~TraceCacheTests"` and verify the current naked-cache ownership fails the matrix.**

- [ ] **Step 3: Implement the locked state machine and entry-owned identity index.** Store an entry-local `object Gate`, `TaskCompletionSource Ready/Drained` with asynchronous continuations, `RetireRequested`, `ActiveLeases`, and `int DisposeStarted`. `AcquireAsync` awaits `ReadyTask`, locks, checks exactly `Ready`, increments, and returns a lease whose callback carries the entry identity. `ReleaseAsync` decrements under lock; when state is `Retiring` and count becomes zero, the winner of `Interlocked.CompareExchange(ref DisposeStarted, 1, 0)` disposes the backend, then transitions to `Disposed` and completes `DrainTask`. Define `ITraceIdentityIndexFactory.Create(TraceLog) -> ITraceIdentityIndexOwner`, where the owner exposes `TraceIdentityIndex Index` and idempotent `Dispose`; keep one `Lazy<ITraceIdentityIndexOwner>` with `ExecutionAndPublication` in `InProcessTraceBackend`. Remove the static weak table. Change `ThreadLifetimeAnalysis` and `GcAnalysis` to accept an index explicitly; change `CpuTools`, `WaitTools`, `DiagnoseTools`, `ClrTools`, and `MetaTools` to obtain it from the current leased backend and pass it through. Backend disposal atomically clears and disposes the owner after the last operation releases its lease.

```csharp
internal sealed class TraceLease : IAsyncDisposable
{
    private Func<ValueTask>? _release;
    internal TraceDescriptor Descriptor { get; }
    internal ITraceBackend Backend { get; }

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _release, null)?.Invoke() ?? ValueTask.CompletedTask;
}
```

- [ ] **Step 4: Run the focused command 20 times with `for ($i=0; $i -lt 20; $i++) { dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TraceRegistryTests --no-restore; if ($LASTEXITCODE) { exit $LASTEXITCODE } }`; require no disposal-count or transition failure.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/TraceLease.cs src/WprMcp/Core/TraceRegistryEntry.cs src/WprMcp/Core/InProcessTraceBackend.cs src/WprMcp/Analyzers/TraceIdentityIndex.cs src/WprMcp/Analyzers/TraceIdentityIndexFactory.cs src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs src/WprMcp/Analyzers/GcAnalysis.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/WaitTools.cs src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Tools/ClrTools.cs src/WprMcp/Tools/MetaTools.cs tests/WprMcp.Tests/TraceRegistryTests.cs tests/WprMcp.Tests/TraceCacheTests.cs tests/WprMcp.Tests/TraceIdentityIndexTests.cs tests/WprMcp.Tests/ThreadLifetimeAnalysisTests.cs tests/WprMcp.Tests/GcAnalysisTests.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs tests/WprMcp.Tests/ThreadScopedCpuWaitTests.cs
git commit -m "feat: own traces through ref-counted leases"
```

### Task 2: Linearize explicit unload and retirement

**Files:**

- Create: `src/WprMcp/Core/TraceRetirement.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/TraceRegistryEntry.cs`
- Create: `tests/WprMcp.Tests/TraceUnloadTests.cs`

**Interfaces:**

- Consumes: `ITraceRegistry.UnloadAsync(traceId, waitForDrain, cancellationToken)` and configured `UnloadDrainTimeout`.
- Produces: idempotent `UnloadTraceResult` with `Drained`, `Pending`, `NotFound`, or `TimedOut` and the active count observed at return.

- [ ] **Step 1: Write failing Acquire/Unload races.** Cover absent ID; ready/no leases; ready/one lease with `waitForDrain=false`; ready/one lease released before timeout; caller cancellation; configured timeout; repeated unload before and after drain; and 100 simultaneous Acquire/Unload operations gated so the expected winner is known. Assert cancellation/timeout leaves state `Retiring`, rejects later acquisitions, and does not dispose until the held lease releases.

```csharp
[Fact]
public async Task TimedOutUnload_DoesNotRollbackOrDisposeLiveLease()
{
    await using TraceLease lease = await registry.AcquireAsync(traceId, default);
    UnloadTraceResult result = await registry.UnloadAsync(traceId, true, default);

    Assert.Equal(UnloadTraceDisposition.TimedOut, result.Disposition);
    Assert.Equal(TraceEntryState.Retiring, registry.GetStateForTest(traceId));
    Assert.Equal(0, backend.DisposeCount);
    await lease.DisposeAsync();
    Assert.Equal(1, backend.DisposeCount);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TraceUnloadTests` and verify failures show unload is absent or unsafe.**

- [ ] **Step 3: Implement retirement as a one-way operation.** `RequestRetirement` changes `Ready` to `Retiring` under the entry lock or sets the flag during `Loading`. If no leases exist, start disposal outside the lock. `waitForDrain=false` returns `Pending` unless disposal already completed. `waitForDrain=true` waits on `DrainTask` with `TimeProvider` and `UnloadDrainTimeout`; cancellation throws only from the waiting call, and timeout returns `TimedOut`. Preserve a bounded tombstone containing final disposition so repeated unload returns `Drained`; purge tombstones on absolute lifetime.

- [ ] **Step 4: Run the focused tests 20 times using the loop from Task 1 with filter `FullyQualifiedName~TraceUnloadTests`; require stable outcomes.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceRetirement.cs src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/TraceRegistryEntry.cs tests/WprMcp.Tests/TraceUnloadTests.cs
git commit -m "feat: add linearizable trace unload"
```

### Task 3: Make fault, replacement, and source-generation races identity-safe

**Files:**

- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/TraceRegistryEntry.cs`
- Modify: `src/WprMcp/Core/TraceReferenceResolver.cs`
- Modify: `src/WprMcp/Core/TraceArtifactStore.cs`
- Create: `src/WprMcp/Core/TraceArtifactLease.cs`
- Create: `tests/WprMcp.Tests/TraceRegistryFaultTests.cs`
- Modify: `tests/WprMcp.Tests/TraceReferenceResolverTests.cs`
- Modify: `tests/WprMcp.Tests/TraceArtifactStoreTests.cs`

**Interfaces:**

- Consumes: artifact-key deduplication, registry entry identity, and the artifact store's object lock.
- Produces: retryable registration after failure/cancellation, deterministic replacement/retirement for a changed immutable source generation, and one `ITraceArtifactLease` owned by each registry entry.

- [ ] **Step 1: Write failing stale-continuation and shared-artifact tests.** Gate an old load so it faults after a newer entry for the same artifact key succeeds; assert the old cleanup cannot remove the new entry. Cancel a conversion and retry. Register path generation A and a second path/source generation with identical bytes so two trace IDs share one `TraceArtifactKey`; unload A and assert B can still execute against the ETLX and the object remains. Release B and assert the object is deleted and its actual byte charge is removed once. Immediately `load_trace` the same immutable bytes again: Child 6's in-flight-only coordinator must call the store, observe the missing ETLX while revalidating manifest/object under the key lock, rebuild it, and return a usable new trace ID—never a completed cached `TraceArtifact` referencing the deleted file. Replace the source with different bytes for generation C and assert every ID remains bound to its own artifact. Inject failure/cancellation before and after artifact-lease acquisition and assert no leaked reference and no double decrement. Inject backend dispose failure and assert state still reaches terminal `Disposed`, the failure is logged once, and owned resources release once.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceRegistryFaultTests|FullyQualifiedName~TraceReferenceResolverTests"` and verify the stale removal/retry cases fail.**

- [ ] **Step 3: Implement identity comparisons and artifact leases.** Change artifact acquisition to return `ITraceArtifactLease`; store it on the exact `TraceRegistryEntry` before `Ready`, and release it exactly once after backend disposal or any failed/cancelled load. Keep maps by trace ID and artifact key. Complete load on the entry captured by the continuation. Implement a private `TryRemoveExact<TKey,TValue>` as `((ICollection<KeyValuePair<TKey,TValue>>)map).Remove(new(key, expectedEntry))` on the current baseline (or the Child 11A-selected TFM's proven equivalent) and use it for every fault cleanup; the stale-continuation test must exercise this helper. Never key live entries by mutable source path or mtime. Identical immutable keys share the object but hold distinct reference IDs; a changed content hash is a separate key. Under `<artifact-root>/locks/<artifact-key>.lock`, remove the object only when the cross-process reference ledger has no live reference, then release actual disk bytes. A subsequent load always goes back through Child 6's artifact-store revalidation/rebuild path; the registry must not retain or resurrect the released artifact object.

- [ ] **Step 4: Run the focused command, then `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~ArtifactSingleFlightTests|FullyQualifiedName~TraceArtifactStoreTests"`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/TraceRegistryEntry.cs src/WprMcp/Core/TraceReferenceResolver.cs src/WprMcp/Core/TraceArtifactStore.cs src/WprMcp/Core/TraceArtifactLease.cs tests/WprMcp.Tests/TraceRegistryFaultTests.cs tests/WprMcp.Tests/TraceReferenceResolverTests.cs tests/WprMcp.Tests/TraceArtifactStoreTests.cs
git commit -m "fix: isolate stale trace registry completions"
```

### Task 4: Add deterministic LRU/expiry eviction and bounded admission

**Files:**

- Create: `src/WprMcp/Core/TraceAdmissionController.cs`
- Create: `src/WprMcp/Core/TraceEvictionPolicy.cs`
- Create: `src/WprMcp/Core/TraceExpiryService.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/TraceRegistryEntry.cs`
- Modify: `src/WprMcp/McpServerOptions.cs`
- Create: `tests/WprMcp.Tests/TraceAdmissionControllerTests.cs`
- Create: `tests/WprMcp.Tests/TraceEvictionPolicyTests.cs`
- Modify: `tests/WprMcp.Tests/McpServerOptionsTests.cs`

**Interfaces:**

- Consumes: `TraceRegistryOptions`, monotonic timestamps from `TimeProvider`, and registry state snapshots.
- Produces: FIFO `ReserveEntryAsync(deadlineTimestamp, CancellationToken)` admission and a deterministic eviction order.

- [ ] **Step 1: Write failing fake-time, options-ownership, acquire-expiry, sweeper, and saturation tests.** Verify `--cache-size` maps only to `Registry.MaxEntries`; `--symbol-cache-bytes` maps only to `Registry.MaxSymbolCacheBytes`; absent flags produce 2 entries and 20 GiB; exact hard ceilings pass and plus one fails before side effects. Reflection must find neither `McpServerOptions.CacheSize`/`SymbolPath` nor `SymbolPolicyOptions.MaxCacheBytes`, and a source/DI test must fail if any runtime quota consumer reads a symbol-policy cache limit instead of Registry. Verify idle age starts at last lease release, absolute age starts at successful registration, neither clock uses `DateTime.UtcNow`, and expiry retires rather than directly disposes. Advance fake time to one tick before/exactly at idle and absolute deadlines: `AcquireAsync` must grant before, but under the entry lock transition to `Retiring` and reject at the exact deadline. Without any new load or acquire, start the expiry service, wait until its timer is armed for the nearest deadline, advance fake time, and assert retirement/disposal; adding/releasing an entry reschedules an earlier nearest deadline without a lost wake-up. Absolute expiry while a lease is active retires but waits for release. With capacity 2, lease both entries and issue nine loads: eight wait FIFO and the ninth fails `budget_exceeded` before backend construction. Release one lease and assert exactly one waiter proceeds with no moment above two counted entries. Candidate order must be expired idle, expired absolute, then least-recently-released, with ordinal trace ID as tie-break; `Loading` and `Retiring` are never selectable as ready victims but still consume capacity.

```csharp
internal readonly record struct EvictionCandidate(
    string TraceId,
    bool IdleExpired,
    bool AbsoluteExpired,
    long LastReleaseTimestamp,
    long CreatedTimestamp);
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceAdmissionControllerTests|FullyQualifiedName~TraceEvictionPolicyTests|FullyQualifiedName~McpServerOptionsTests"` and verify capacity/expiry failures.**

- [ ] **Step 3: Consume staged options, implement acquire-time expiry, nearest-deadline sweep, and admission without temporary overflow.** In `McpServerOptions.Parse`, move the staged `CacheSize` to `Registry.MaxEntries`, move the provisional Child 6 symbol-cache default/`--symbol-cache-bytes` value to `Registry.MaxSymbolCacheBytes`, and remove the two staged fields from the final record/type definitions. Validate Registry values against `RuntimeHardLimits` before side effects and inject the one Registry instance into quota consumers. In `AcquireAsync`, after awaiting readiness and while holding the entry lock, compare the current monotonic timestamp to idle/absolute deadlines before incrementing; an expired entry transitions to `Retiring` in that same critical section and cannot grant a lease. `TraceExpiryService` maintains versioned nearest-deadline registrations (or an equivalent cancellable priority queue), arms a `TimeProvider` timer for the minimum ready-entry deadline, retires all due exact entry identities, and reschedules on ready/release/dispose; stale timer nodes cannot retire replacements. Use a bounded `Channel<AdmissionWaiter>` or equivalent FIFO queue whose count is charged before enqueue. Under a registry admission gate, either reserve a free entry slot, linearize retirement of one unleased ready victim, or enqueue. A retiring victim does not free capacity until its backend disposal and resource release finish. Deadlines use `TimeProvider.GetTimestamp()` and `GetElapsedTime`; cancelled/timed-out waiters remove only their own queue node and wake the next waiter.

- [ ] **Step 4: Run the focused command 20 times; add `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceRegistryTests|FullyQualifiedName~TraceUnloadTests"` to catch ownership regressions.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceAdmissionController.cs src/WprMcp/Core/TraceEvictionPolicy.cs src/WprMcp/Core/TraceExpiryService.cs src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/TraceRegistryEntry.cs src/WprMcp/McpServerOptions.cs tests/WprMcp.Tests/TraceAdmissionControllerTests.cs tests/WprMcp.Tests/TraceEvictionPolicyTests.cs tests/WprMcp.Tests/McpServerOptionsTests.cs
git commit -m "feat: enforce deterministic trace admission and expiry"
```

### Task 5: Enforce cross-process artifact, symbol, and worker reservations

**Files:**

- Create: `src/WprMcp/Core/RuntimeQuotaManager.cs`
- Create: `src/WprMcp/Core/RuntimeQuotaLedger.cs`
- Create: `src/WprMcp/Core/ResourceVector.cs`
- Create: `src/WprMcp/Core/ProcessIdentity.cs`
- Create: `src/WprMcp/Core/RuntimeQuotaJsonContext.cs`
- Create: `src/WprMcp/Core/SymbolCacheStore.cs`
- Create: `src/WprMcp/Core/SymbolCacheManifest.cs`
- Modify: `src/WprMcp/Core/TraceArtifactStore.cs`
- Modify: `src/WprMcp/Core/SymbolPolicyHttpHandler.cs`
- Modify: `src/WprMcp/Core/PolicySymbolReaderFactory.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Create: `tests/WprMcp.Tests/RuntimeQuotaManagerTests.cs`
- Create: `tests/WprMcp.Tests/RuntimeQuotaProcessHost.cs`
- Create: `tests/WprMcp.Tests/SymbolCacheQuotaTests.cs`
- Modify: `tests/WprMcp.Tests/WprMcp.Tests.csproj`

**Interfaces:**

- Consumes: `ResourceVector`, monotonic deadline, artifact and symbol object sizes, server/process identity, and the sole runtime symbol quota `TraceRegistryOptions.MaxSymbolCacheBytes`.
- Produces: `IRuntimeQuotaManager.ReserveAsync`, idempotent `IResourceReservation.DisposeAsync`, atomic `ReconcileAsync`, and `RuntimeQuotaSnapshot` with limits/reserved/actual/queued values.

- [ ] **Step 1: Write failing boundary, two-process, object-reference, and persistent-symbol tests.** For every vector field, test configured limit minus one, exact limit, and plus one. Start two helper processes at a barrier with requests whose sum exceeds artifact or worker capacity; assert exactly one reserves and the other gets `budget_exceeded`. Have two processes acquire the same artifact key: count artifact bytes once, record two reference IDs, release process A without deleting the ETLX, then release process B and assert the object is deleted and actual bytes are deducted. Kill a helper while it owns a reference and prove stale reclamation occurs only after PID/start-time verification and while holding the artifact lock. For symbols, race two processes downloading the same validated PDB identity/hash and assert one key lock, one published file, one persistent `SymbolObject` byte charge, and no PID-based reclaim after publisher exit/restart. Test reservation-to-object promotion, failed validation/cancellation without promotion, delete failure retaining the charge, successful eviction deleting before uncharge, a ledger object missing its file, an on-disk object missing its ledger record, corrupt manifests, and startup reconciliation after a killed publisher. Corrupt/truncate a reservation/reference record and assert fail-closed admission/deletion. Assert double disposal and reservation reconciliation release once. Fill disk in an injectable fake store and ensure failed publication removes its reservation, reference, and own temp directory.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~RuntimeQuotaManagerTests` and verify over-admission or missing-ledger failures.**

- [ ] **Step 3: Implement atomic ledger, shared-artifact, and persistent-symbol operations.** Acquire `ledger.lock` using `FileShare.None`, load all version-1 records with source-generated JSON and reject unknown members, validate checked arithmetic, reclaim only proven-dead owners, compare requested totals with limits from the injected `TraceRegistryOptions`—including only `Registry.MaxSymbolCacheBytes` for total symbol objects—write a unique record atomically, and release the lock. Add `RuntimeQuotaRecordKind.Reservation`, `ArtifactObject`, `ArtifactReference`, and `SymbolObject`: one artifact object owns its actual byte charge while every registry/process owner has a distinct zero-byte reference; one symbol object keyed by validated PDB identity plus content hash owns its persistent actual cache-byte charge. Creator PID/start fields on object records are audit metadata only and never make persistent objects stale. A proven-dead artifact reference is removed under object-key/ledger lock order, and an artifact with no remaining reference is deleted before uncharge. `SymbolCacheStore` takes `<symbol-cache-root>/locks/<symbol-key>.lock`, rechecks an existing manifest, reserves `MaxDownloadBytes`, downloads to a unique temp file, validates PDB identity and hash, atomically publishes, and promotes the reservation to a `SymbolObject` with actual bytes in one ledger transaction. `MaxDownloadBytes` remains a per-object policy bound and is not a total-cache quota. Symbol eviction deletes the file/manifest first and removes the object charge only after confirmed absence; delete failure remains charged. Startup reconciliation under the same locks pairs every symbol manifest/file with exactly one object record, repairs a missing charge only when within quota, deletes an orphan before declining its charge, and fails closed on corruption. Keep a process-local map for fast snapshots but treat disk as authority. Registry entry and queued-load slots use the same manager.

```csharp
internal sealed record RuntimeQuotaReservationRecord(
    int SchemaVersion,
    RuntimeQuotaRecordKind Kind,
    string ReservationId,
    string ServerInstanceId,
    int ProcessId,
    long ProcessStartUtcTicks,
    long CreatedTimestamp,
    long DeadlineTimestamp,
    ResourceVector Resources,
    string? ObjectKey,
    string? ReferencedObjectKey);
```

- [ ] **Step 4: Run the focused tests, then `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceArtifactStoreTests|FullyQualifiedName~SymbolPolicyHttpHandlerTests|FullyQualifiedName~TraceAdmissionControllerTests"`.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/RuntimeQuotaManager.cs src/WprMcp/Core/RuntimeQuotaLedger.cs src/WprMcp/Core/ResourceVector.cs src/WprMcp/Core/ProcessIdentity.cs src/WprMcp/Core/RuntimeQuotaJsonContext.cs src/WprMcp/Core/SymbolCacheStore.cs src/WprMcp/Core/SymbolCacheManifest.cs src/WprMcp/Core/TraceArtifactStore.cs src/WprMcp/Core/SymbolPolicyHttpHandler.cs src/WprMcp/Core/PolicySymbolReaderFactory.cs src/WprMcp/Core/TraceRegistry.cs tests/WprMcp.Tests/RuntimeQuotaManagerTests.cs tests/WprMcp.Tests/RuntimeQuotaProcessHost.cs tests/WprMcp.Tests/SymbolCacheQuotaTests.cs tests/WprMcp.Tests/WprMcp.Tests.csproj
git commit -m "feat: reserve runtime resources across processes"
```

### Task 6: Expose unload and cache/quota status without leaking paths

**Files:**

- Create: `src/WprMcp/Tools/TraceLifecycleTools.cs`
- Create: `src/WprMcp/Output/TraceLifecycleRecords.cs`
- Modify: `src/WprMcp/Core/TraceRegistry.cs`
- Modify: `src/WprMcp/Core/tool-contracts.v2.json`
- Modify: `src/WprMcp/Tools/MetaTools.cs`
- Modify: `tests/WprMcp.Tests/McpSdkSurfaceTests.cs`
- Create: `tests/WprMcp.Tests/TraceLifecycleToolsTests.cs`
- Modify: `tests/WprMcp.Tests/MetaToolsTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json`
- Modify: `tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json`

**Interfaces:**

- Consumes: `ITraceRegistry.UnloadAsync`, registry snapshot, and `IRuntimeQuotaManager.Snapshot`.
- Produces: MCP tools `unload_trace(traceId, waitForDrain=true, cancellationToken)` and `trace_cache_status()` plus versioned DTOs containing counts, bytes, limits, estimates, queue depth, and per-ID state without canonical paths.

- [ ] **Step 1: Write failing tool, manifest, and real-schema tests.** Assert unload validates the exact trace-ID grammar before registry access; maps all four dispositions; repeated unload is idempotent; exposes `ActiveLeases`; and never returns a source/artifact/symbol cache path. Status must report `Loading`, `Ready`, `Retiring`, estimated resident bytes explicitly named `EstimatedResidentBytes`, per-artifact `ArtifactReferenceCount` across live processes, artifact/symbol reserved and actual bytes, limits, queued loads, and timestamp ages. Load two IDs sharing one key and assert the count changes from two to one to zero across unload without exposing the key/path. Assert `unload_trace` is destructive/non-read-only and `trace_cache_status` is read-only. Extend `ToolSchemaContractTests` so real legacy and v2 `tools/list` must contain exact `load_trace`, `unload_trace`, and `trace_cache_status` schemas/annotations and validate succeeded plus every unload disposition against the v2 output schema; the untouched snapshots must fail before regeneration.

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TraceLifecycleToolsTests|FullyQualifiedName~McpSdkSurfaceTests|FullyQualifiedName~MetaToolsTests"` and `dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ToolSchemaContractTests`; verify absent manifest entries and both snapshot diffs.**

- [ ] **Step 3: Implement privacy-safe records, tools, and contract entries.** Use stable lower-case disposition/state strings, trace IDs only, checked byte counts, and monotonic ages. Map unknown IDs to `NotFound` rather than an exception so repeated unload remains idempotent. Map a caller-cancelled wait through Child 5 cancellation semantics; do not change registry retirement. Add exact v2 manifest entries for `load_trace`, `unload_trace`, and `trace_cache_status`, including data type, stable sections, lower-case enums, nullability, `additionalProperties=false`, and read-only/destructive annotations. Regenerate both normalized snapshots only through Child 5's real stdio snapshot command and review the diff; do not hand-edit unrelated tools or contract versions.

- [ ] **Step 4: Run the focused tests, `dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter FullyQualifiedName~ToolSchemaContractTests`, and `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~TelemetryTests`; inspect serialized results for absence of the test root sentinel and require no unrelated snapshot change.**

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Tools/TraceLifecycleTools.cs src/WprMcp/Output/TraceLifecycleRecords.cs src/WprMcp/Core/TraceRegistry.cs src/WprMcp/Core/tool-contracts.v2.json src/WprMcp/Tools/MetaTools.cs tests/WprMcp.Tests/McpSdkSurfaceTests.cs tests/WprMcp.Tests/TraceLifecycleToolsTests.cs tests/WprMcp.Tests/MetaToolsTests.cs tests/WprMcp.ProtocolTests/ToolSchemaContractTests.cs tests/WprMcp.ProtocolTests/Snapshots/tools-list.legacy.json tests/WprMcp.ProtocolTests/Snapshots/tools-list.v2.json
git commit -m "feat: expose trace unload and quota status"
```

### Task 7: Drain safely on shutdown and remove legacy cache ownership

**Files:**

- Create: `src/WprMcp/Core/TraceRegistryHostedService.cs`
- Create: `src/WprMcp/Core/RuntimeShutdownCoordinator.cs`
- Modify: `src/WprMcp/Program.cs`
- Modify: `src/WprMcp/Analyzers/TraceIdentityIndex.cs`
- Delete: `src/WprMcp/Core/LruCache.cs`
- Delete: `src/WprMcp/Core/TraceCache.cs`
- Delete: `src/WprMcp/Core/TraceCacheCallContext.cs`
- Delete: `tests/WprMcp.Tests/LruCacheTests.cs`
- Delete: `tests/WprMcp.Tests/TraceCacheTests.cs`
- Create: `tests/WprMcp.Tests/TraceLifecycleRegressionTests.cs`
- Modify: `tests/WprMcp.Tests/TraceIdentityIndexTests.cs`
- Create: `tests/WprMcp.Tests/RuntimeShutdownCoordinatorTests.cs`
- Modify: `.github/workflows/quality.yml`

**Interfaces:**

- Consumes: host `ApplicationStopping`, operation cancellation source, `TraceRegistry.StopAdmission`, entry drain tasks, and optional `IIsolatedTraceBackend`.
- Produces: `RuntimeShutdownResult(Drained, TimedOutTraceIds, TerminatedWorkerCount, LiveInProcessLeaseCount)` and a CI production gate that fails when an in-process lease misses the configured shutdown SLA.

- [ ] **Step 1: Write failing shutdown tests.** Assert shutdown first rejects new admission, then cancels operations, then retires all entries. A lease released within the deadline drains and disposes once. At deadline, an isolated backend is terminated then disposed; an in-process backend with a live lease is not disposed and is reported in `LiveInProcessLeaseCount`. Releasing that lease later disposes once. Two concurrent shutdown calls share one task. Assert all queued admission waiters fail cancellation and all reservations/temp owners are released by their owning completion paths.

```csharp
[Fact]
public async Task Deadline_DoesNotForceDisposeLiveInProcessLease()
{
    await using TraceLease lease = await registry.AcquireAsync(traceId, default);
    Task<RuntimeShutdownResult> stopping = coordinator.StopAsync(default);
    await coordinator.DrainWaitStartedForTest;
    fakeTime.Advance(options.ShutdownDrainTimeout + TimeSpan.FromMilliseconds(1));

    RuntimeShutdownResult result = await stopping;

    Assert.Equal(1, result.LiveInProcessLeaseCount);
    Assert.Equal(0, backend.DisposeCount);
}
```

- [ ] **Step 2: Run `dotnet test WprMcp.sln -c Release --filter FullyQualifiedName~RuntimeShutdownCoordinatorTests` and verify current host disposal violates ordering or ownership.**

- [ ] **Step 3: Implement hosted shutdown and remove old owners.** Register the coordinator before MCP transport services. `StopAsync` is idempotent, calls `StopAdmission`, cancels the operation root token, requests retirement for each exact entry, and awaits drain until the monotonic deadline. It invokes `TerminateAsync` only for `IIsolatedTraceBackend`; it logs stable redacted IDs and returns a non-success production gate when in-process leases remain. Delete `LruCache`, `TraceCache`, and call-context ownership only after `rg -n "LruCache|TraceCache|TraceCacheCallContext" src tests` shows no consumer outside the files being deleted. Remove Child 1's static identity-index seam and require `rg -n "ConditionalWeakTable|TraceIdentityIndex\.For" src/WprMcp` to return no production call or cache; all construction must originate from the current registry backend.

- [ ] **Step 4: Add a reusable-quality lifecycle gate and run verification.** Append `dotnet test WprMcp.sln -c Release --no-build --filter "FullyQualifiedName~TraceRegistry|FullyQualifiedName~TraceUnload|FullyQualifiedName~RuntimeShutdown|FullyQualifiedName~RuntimeQuota|FullyQualifiedName~TraceIdentityIndex"` to Child 11A's single implementation in `.github/workflows/quality.yml`. Leave `.github/workflows/ci.yml` as a trigger-only caller and do not edit `release.yml`. Locally run that command, `dotnet test WprMcp.sln -c Release`, `rg -n "LruCache|TraceCache|TraceCacheCallContext" src tests`, and `rg -n "ConditionalWeakTable|TraceIdentityIndex\.For" src/WprMcp`; require zero legacy production ownership matches and zero failures.

- [ ] **Step 5: Commit.**

```powershell
git add src/WprMcp/Core/TraceRegistryHostedService.cs src/WprMcp/Core/RuntimeShutdownCoordinator.cs src/WprMcp/Program.cs src/WprMcp/Analyzers/TraceIdentityIndex.cs tests/WprMcp.Tests/TraceLifecycleRegressionTests.cs tests/WprMcp.Tests/TraceIdentityIndexTests.cs tests/WprMcp.Tests/RuntimeShutdownCoordinatorTests.cs .github/workflows/quality.yml
git rm src/WprMcp/Core/LruCache.cs src/WprMcp/Core/TraceCache.cs src/WprMcp/Core/TraceCacheCallContext.cs tests/WprMcp.Tests/LruCacheTests.cs tests/WprMcp.Tests/TraceCacheTests.cs
git commit -m "feat: drain trace leases safely on shutdown"
```

## Acceptance Gate and Handoff

Run from repository root on Windows:

```powershell
dotnet restore WprMcp.sln
dotnet build WprMcp.sln -c Release --no-restore
dotnet test WprMcp.sln -c Release --no-build
rg -n "LruCache|TraceCache|TraceCacheCallContext" src tests
git status --short
```

The gate passes only when unload, expiry, eviction, source-generation replacement, and shutdown dispose each backend exactly once; no active analysis is disposed under a lease; fault cleanup cannot delete a newer success; entry and disk limits are observable and never exceeded even across two processes; saturation queues no more than configured; repeated unload is idempotent; and a missed in-process shutdown deadline fails the production gate rather than being called safe.

The files most likely to conflict with Child 6 are `TraceRegistry.cs`, `TraceLease.cs`, `McpServerOptions.cs`, `Program.cs`, and policy-store quota hooks; rebase Child 7 on the completed Child 6 branch and preserve policy validation order. Child 8 must preserve the entry state machine and implement worker backends behind `ITraceBackend`/`IIsolatedTraceBackend`; it consumes reservations for queued/conversion/analysis/worker resources. Child 8 also changes the identity factory to accept `AnalysisOperationContext`, so the first process/thread scan is cancellable and charges the initiating operation before a successful owner is published; this child's context-free factory is an ownership seam, not permission for an unbudgeted scan. Child 9 adds true child-process protocol and hostile concurrent load coverage and may then remove the test parallelism guard.

## Mandatory Spikes and Stop Conditions

- Before Task 5, prove Windows PID plus process-start-time identity is obtainable without administrator rights in the supported environment. If access is denied, records for that PID remain live and block admission; never reclaim based on age alone.
- Before deleting legacy cache classes, use the architecture search in Task 7 and the reflection surface test to prove every query releases a `TraceLease`. A remaining naked `TraceLog` consumer blocks deletion.
- Before declaring shutdown production-safe, run the representative large-trace trusted-local cancellation measurement from Child 8. If p95 drain exceeds two seconds, trusted-local must use isolation; increasing the shutdown timeout does not satisfy that requirement.
- Ledger corruption, lock starvation beyond the operation deadline, checked-arithmetic overflow, and inability to determine artifact size are fail-closed `budget_exceeded`/local diagnostic paths, not reasons to admit unaccounted work.
