# Input, Time-Window, and Instance-Identity Foundations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every analyzer and MCP tool one exact input-validation, time-accounting, process-instance, and thread-instance foundation.

**Architecture:** Pure value types in `Core/` own timestamp normalization and half-open window arithmetic. Pure catalogs in `Analyzers/` resolve process and thread lifetimes without silently selecting reused IDs. Tool facades validate syntax before trace access and resolve trace-relative bounds after a trace descriptor is available.

**Tech Stack:** C#; xUnit; TraceEvent `TraceLog`; reflection/source-conformance tests; Windows PowerShell; the TFM selected by Child 11A.

## Global Constraints

- Approved specification commit: `7ef8ff5`.
- Time windows are half-open `[StartUs,EndUs)` and must resolve to `0 <= StartUs < EndUs <= TraceDurationUs`.
- Convert non-negative millisecond/nanosecond timestamps by checked floor to integer microseconds before subtraction or clipping.
- `tid` is optional at the MCP surface but requires `pid`; `processStartUs` requires `pid`, and `threadStartUs` requires both `pid` and `tid`. Every supplied selector timestamp is non-negative and is validated before trace access. Instance reuse is an explicit unresolved/ambiguous result whenever an instance-level selector requires uniqueness.
- Shared limits are: 16 KiB serialized arguments, 4,096 characters per string, 128 collection items, and TopN/bucket maximum 1,000.
- This child introduces internal primitives only; Child 5 maps resolution failures to stable MCP wire errors.
- Execute in an isolated worktree and do not combine tasks into one commit.

---

## File structure

| File | Action | Responsibility |
|---|---|---|
| `src/WprMcp/Core/TraceTime.cs` | Create | Checked conversion from TraceEvent time units to integer microseconds |
| `src/WprMcp/Core/TimeWindow.cs` | Create | Pre-trace input shape and resolved half-open interval operations |
| `src/WprMcp/Core/Validation.cs` | Modify | Shared argument, string, collection, PID/TID, and window validation |
| `src/WprMcp/Core/TraceIdentity.cs` | Create | Process/thread key and selector records |
| `src/WprMcp/Analyzers/ProcessInstanceResolver.cs` | Create | Deterministic process-lifetime resolution |
| `src/WprMcp/Analyzers/ThreadInstanceCatalog.cs` | Create | Thread generation tracking and selector resolution |
| `src/WprMcp/Analyzers/TraceIdentityIndex.cs` | Create | Immutable, lazily cached process/thread catalog per `TraceLog` |
| `src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs` | Modify | Consume the shared catalog instead of a TID-only dictionary |
| `src/WprMcp/Tools/*.cs` | Modify | Call shared syntactic window validation before `_cache.Get` |
| `tests/WprMcp.Tests/TimeWindowTests.cs` | Create | Exact conversion/window boundary matrix |
| `tests/WprMcp.Tests/ValidationTests.cs` | Create | Shared limit and PID/TID validation |
| `tests/WprMcp.Tests/ProcessInstanceResolverTests.cs` | Create | Resolved/unresolved/ambiguous process cases |
| `tests/WprMcp.Tests/ThreadInstanceCatalogTests.cs` | Create | Generation, stop, and reuse cases |
| `tests/WprMcp.Tests/TraceIdentityIndexTests.cs` | Create | TraceEvent adapter, reuse, and one-build concurrency cases |
| `tests/WprMcp.Tests/McpSurfaceConformanceTests.cs` | Create | MCP parameter inventory and validation-before-trace rules |
| `tests/WprMcp.Tests/Architecture/window-primitive-allowlist.txt` | Create | Reviewed files allowed to touch raw TraceEvent time values |

### Task 1: Add canonical time conversion and half-open windows

**Files:**
- Create: `src/WprMcp/Core/TraceTime.cs`
- Create: `src/WprMcp/Core/TimeWindow.cs`
- Create: `tests/WprMcp.Tests/TimeWindowTests.cs`

**Interfaces:**
- Produces: `TraceTime.FromMilliseconds(double)`, `TraceTime.FromNanoseconds(long)`, `TimeWindowInput.Validate(long?,long?,long?)`, `TimeWindowInput.Resolve(long,long?)`, `TimeWindow.ContainsPoint(long)`, and `TimeWindow.IntersectDurationUs(long,long)`.

- [ ] **Step 1: Write the failing boundary and conversion tests**

Create `tests/WprMcp.Tests/TimeWindowTests.cs` with tests using this matrix:

```csharp
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class TimeWindowTests
{
    [Theory]
    [InlineData(0.0000, 0)]
    [InlineData(1.2349, 1234)]
    [InlineData(1.9999, 1999)]
    public void FromMilliseconds_FloorsOnce(double milliseconds, long expectedUs) =>
        Assert.Equal(expectedUs, TraceTime.FromMilliseconds(milliseconds));

    [Theory]
    [InlineData(-5, 0, 0)]
    [InlineData(0, 10, 10)]
    [InlineData(5, 15, 10)]
    [InlineData(15, 25, 5)]
    [InlineData(0, 30, 20)]
    [InlineData(20, 30, 0)]
    public void IntersectDurationUs_UsesHalfOpenOverlap(long intervalStart, long intervalEnd, long expected) =>
        Assert.Equal(expected, new TimeWindow(0, 20).IntersectDurationUs(intervalStart, intervalEnd));

    [Fact]
    public void Resolve_RejectsEmptyOneSidedWindowAfterTraceDurationIsKnown()
    {
        var input = TimeWindowInput.Validate(startUs: 100, endUs: null, maxDurationUs: 1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => input.Resolve(traceDurationUs: 100, maxDurationUs: 1_000));
    }
}
```

- [ ] **Step 2: Run the test and record the expected failure**

Run:

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~TimeWindowTests"
```

Expected: compilation fails because `TraceTime`, `TimeWindow`, and `TimeWindowInput` do not exist.

- [ ] **Step 3: Implement the exact primitive API**

Create the types with these signatures and guards:

```csharp
namespace WprMcp.Core;

internal static class TraceTime
{
    public static long FromMilliseconds(double value)
    {
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        var microseconds = Math.Floor(value * 1_000d);
        if (microseconds > long.MaxValue) throw new OverflowException("timestamp exceeds Int64 microseconds");
        return checked((long)microseconds);
    }

    public static long FromNanoseconds(long value)
    {
        if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
        return value / 1_000;
    }
}

internal readonly record struct TimeWindow
{
    public TimeWindow(long startUs, long endUs)
    {
        if (startUs < 0 || endUs <= startUs) throw new ArgumentOutOfRangeException(nameof(endUs));
        StartUs = startUs;
        EndUs = endUs;
    }

    public long StartUs { get; }
    public long EndUs { get; }
    public long DurationUs => checked(EndUs - StartUs);
    public bool ContainsPoint(long timestampUs) => StartUs <= timestampUs && timestampUs < EndUs;
    public long IntersectDurationUs(long intervalStartUs, long intervalEndUs)
    {
        if (intervalEndUs <= intervalStartUs) return 0;
        return Math.Max(0, Math.Min(intervalEndUs, EndUs) - Math.Max(intervalStartUs, StartUs));
    }
}

internal readonly record struct TimeWindowInput(long? StartUs, long? EndUs)
{
    public static TimeWindowInput Validate(long? startUs, long? endUs, long? maxDurationUs)
    {
        if (startUs is < 0) throw new ArgumentOutOfRangeException(nameof(startUs));
        if (endUs is < 0) throw new ArgumentOutOfRangeException(nameof(endUs));
        if (maxDurationUs is <= 0) throw new ArgumentOutOfRangeException(nameof(maxDurationUs));
        if (startUs.HasValue && endUs.HasValue)
        {
            if (endUs.Value <= startUs.Value) throw new ArgumentOutOfRangeException(nameof(endUs));
            if (maxDurationUs.HasValue && endUs.Value - startUs.Value > maxDurationUs.Value)
                throw new ArgumentOutOfRangeException(nameof(endUs));
        }
        return new TimeWindowInput(startUs, endUs);
    }

    public TimeWindow Resolve(long traceDurationUs, long? maxDurationUs)
    {
        if (traceDurationUs <= 0) throw new ArgumentOutOfRangeException(nameof(traceDurationUs));
        var resolved = new TimeWindow(StartUs ?? 0, EndUs ?? traceDurationUs);
        if (resolved.EndUs > traceDurationUs) throw new ArgumentOutOfRangeException(nameof(EndUs));
        if (maxDurationUs.HasValue && resolved.DurationUs > maxDurationUs.Value)
            throw new ArgumentOutOfRangeException(nameof(EndUs));
        return resolved;
    }
}
```

`Validate` rejects negative explicit bounds, non-positive maximum duration, reversed/equal double-sided bounds, and an already-known double-sided width over the maximum. `Resolve` substitutes `0`/`traceDurationUs`, then enforces the complete resolved invariant and maximum.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~TimeWindowTests"`

Expected: all `TimeWindowTests` pass with exact integer values.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/TraceTime.cs src/WprMcp/Core/TimeWindow.cs tests/WprMcp.Tests/TimeWindowTests.cs
git commit -m "feat(core): add canonical trace time and half-open windows"
```

### Task 2: Centralize shared MCP input validation and enforce surface conformance

**Files:**
- Modify: `src/WprMcp/Core/Validation.cs`
- Create: `tests/WprMcp.Tests/ValidationTests.cs`
- Create: `tests/WprMcp.Tests/McpSurfaceConformanceTests.cs`
- Create: `tests/WprMcp.Tests/Architecture/window-primitive-allowlist.txt`

**Interfaces:**
- Consumes: `TimeWindowInput.Validate` from Task 1.
- Produces: shared constants and `Validation.RequireWindowInput`, `RequireThreadSelector`, `RequireText`, and `RequireCollectionCount`.

- [ ] **Step 1: Add failing shared-limit tests**

```csharp
[Fact]
public void RequirePidTid_RejectsTidWithoutPid()
{
    Assert.Throws<ArgumentException>(() => Validation.RequirePidTid(pid: null, tid: 42));
    Validation.RequirePidTid(pid: 123, tid: 42);
}

[Theory]
[InlineData(null, null, 10, null)]
[InlineData(7, null, null, 10)]
[InlineData(7, 8, -1, null)]
[InlineData(7, 8, null, -1)]
public void RequireThreadSelector_RejectsInvalidShapeBeforeTraceAccess(
    int? pid, int? tid, long? processStartUs, long? threadStartUs) =>
    Assert.ThrowsAny<ArgumentException>(() =>
        Validation.RequireThreadSelector(pid, tid, processStartUs, threadStartUs));

[Theory]
[InlineData(4096, true)]
[InlineData(4097, false)]
public void RequireText_EnforcesCharacterCeiling(int length, bool accepted)
{
    var value = new string('x', length);
    var action = () => Validation.RequireText(value, allowEmpty: true);
    if (accepted) action(); else Assert.Throws<ArgumentOutOfRangeException>(action);
}

[Fact]
public void RequireCollectionCount_RejectsMoreThan128()
{
    Assert.Throws<ArgumentOutOfRangeException>(() => Validation.RequireCollectionCount(129));
}
```

In `McpSurfaceConformanceTests`, enumerate every `[McpServerTool]` method and assert that any `tid` parameter is paired with `pid`, any `endUs` description says `exclusive`, and the checked-in windowed-method inventory exactly equals reflection output.

- [ ] **Step 2: Run focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ValidationTests|FullyQualifiedName~McpSurfaceConformanceTests"`

Expected: compilation fails for the new validation methods; the surface inventory also fails until its checked-in list is populated from the current reflection result.

- [ ] **Step 3: Implement validation and the architecture allowlist**

Add these exact constants and entry points to `Validation`:

```csharp
public const int MaxSerializedArgumentsBytes = 16 * 1024;
public const int MaxStringChars = 4_096;
public const int MaxCollectionItems = 128;

public static TimeWindowInput RequireWindowInput(long? startUs, long? endUs, long? maxDurationUs = null) =>
    TimeWindowInput.Validate(startUs, endUs, maxDurationUs);

public static void RequirePidTid(int? pid, int? tid)
{
    if (pid is <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
    if (tid is <= 0) throw new ArgumentOutOfRangeException(nameof(tid));
    if (tid.HasValue && !pid.HasValue) throw new ArgumentException("tid requires pid", nameof(tid));
}

public static void RequireThreadSelector(
    int? pid,
    int? tid,
    long? processStartUs,
    long? threadStartUs)
{
    RequirePidTid(pid, tid);
    if (processStartUs is < 0) throw new ArgumentOutOfRangeException(nameof(processStartUs));
    if (threadStartUs is < 0) throw new ArgumentOutOfRangeException(nameof(threadStartUs));
    if (processStartUs.HasValue && !pid.HasValue)
        throw new ArgumentException("processStartUs requires pid", nameof(processStartUs));
    if (threadStartUs.HasValue && (!pid.HasValue || !tid.HasValue))
        throw new ArgumentException("threadStartUs requires pid and tid", nameof(threadStartUs));
}

public static string RequireText(string value, bool allowEmpty = false)
{
    ArgumentNullException.ThrowIfNull(value);
    if (!allowEmpty && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("text is required", nameof(value));
    if (value.Length > MaxStringChars) throw new ArgumentOutOfRangeException(nameof(value));
    return value;
}

public static int RequireCollectionCount(int count)
{
    if (count < 0 || count > MaxCollectionItems) throw new ArgumentOutOfRangeException(nameof(count));
    return count;
}
```

The architecture test scans `src/WprMcp` for direct TraceEvent `* 1000` timestamp casts and manual `Math.Min/Math.Max` window clipping. The UTF-8 allowlist has one non-comment entry per finding using the exact grammar `relative/path.cs|OWNER|reason`, for example `src/WprMcp/Analyzers/WaitAnalysis.cs|C2|migrate scheduler intervals to TraceTime and TimeWindow`. Sort entries ordinally, reject duplicate paths, reject unknown owners, and require owners to be one of `PERMANENT`, `C2`, `C3`, `C4`, or `C8`. Seed it from current hits; `src/WprMcp/Core/TraceTime.cs` and `src/WprMcp/Core/TimeWindow.cs` are the only `PERMANENT` entries. The test rejects every new unlisted occurrence, and later children delete their owned entries as they migrate analyzers.

- [ ] **Step 4: Run focused and existing surface tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~ValidationTests|FullyQualifiedName~McpSurfaceConformanceTests|FullyQualifiedName~TimeWindowSemanticsTests"
```

Expected: all tests pass and the reflection inventory is deterministic.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/Validation.cs tests/WprMcp.Tests/ValidationTests.cs tests/WprMcp.Tests/McpSurfaceConformanceTests.cs tests/WprMcp.Tests/Architecture/window-primitive-allowlist.txt
git commit -m "feat(core): centralize MCP input validation"
```

### Task 3: Resolve process lifetimes without PID-only guesses

**Files:**
- Create: `src/WprMcp/Core/TraceIdentity.cs`
- Create: `src/WprMcp/Analyzers/ProcessInstanceResolver.cs`
- Create: `tests/WprMcp.Tests/ProcessInstanceResolverTests.cs`

**Interfaces:**
- Produces: `ProcessInstanceKey`, `ProcessLifetime`, `InstanceResolution<T>`, and `ProcessInstanceResolver.Resolve`.

- [ ] **Step 1: Write failing resolved/unresolved/ambiguous tests**

```csharp
[Fact]
public void Resolve_OverlappingPidLifetimes_IsAmbiguous()
{
    var resolver = new ProcessInstanceResolver(new[]
    {
        new ProcessLifetime(new ProcessInstanceKey(7, 10), 100, StartObserved: true, EndObserved: true),
        new ProcessLifetime(new ProcessInstanceKey(7, 80), 150, StartObserved: true, EndObserved: true),
    });

    var result = resolver.Resolve(pid: 7, timestampUs: 90, processStartUs: null);
    Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
    Assert.Equal(2, result.Candidates.Count);
}

[Fact]
public void Resolve_ProcessStartSelector_DisambiguatesExactLifetime()
{
    var resolver = ResolverWithReusedPid();
    var result = resolver.Resolve(7, timestampUs: 120, processStartUs: 100);
    Assert.Equal(new ProcessInstanceKey(7, 100), result.Value);
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ProcessInstanceResolverTests"`

Expected: compilation fails because the identity records and resolver are absent.

- [ ] **Step 3: Implement the pure resolver**

Use these exact contracts:

```csharp
internal readonly record struct ProcessInstanceKey(int Pid, long StartUs);
internal sealed record ProcessLifetime(
    ProcessInstanceKey Key,
    long EndUs,
    bool StartObserved,
    bool EndObserved)
{
    public bool Contains(long timestampUs) => Key.StartUs <= timestampUs && timestampUs < EndUs;
}

internal enum InstanceResolutionStatus { Resolved, Unresolved, Ambiguous }

internal sealed record InstanceResolution<T>(
    InstanceResolutionStatus Status,
    T? Value,
    IReadOnlyList<T> Candidates) where T : struct;

internal sealed class ProcessInstanceResolver
{
    public ProcessInstanceResolver(IEnumerable<ProcessLifetime> lifetimes);
    public IReadOnlyList<ProcessLifetime> Lifetimes { get; }
    public InstanceResolution<ProcessInstanceKey> Resolve(int pid, long timestampUs, long? processStartUs);
}
```

Sort lifetimes by PID/start, reject invalid overlapping metadata only through an `Ambiguous` result, and never use `LastProcessWithID` or collection order as a tie-break.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ProcessInstanceResolverTests"`

Expected: resolved, unresolved, exact-selector, endpoint, reuse, and overlap cases pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/TraceIdentity.cs src/WprMcp/Analyzers/ProcessInstanceResolver.cs tests/WprMcp.Tests/ProcessInstanceResolverTests.cs
git commit -m "feat(analyzers): add process instance resolver"
```

### Task 4: Track thread generations and resolve one thread instance

**Files:**
- Modify: `src/WprMcp/Core/TraceIdentity.cs`
- Create: `src/WprMcp/Analyzers/ThreadInstanceCatalog.cs`
- Create: `tests/WprMcp.Tests/ThreadInstanceCatalogTests.cs`

**Interfaces:**
- Consumes: `ProcessInstanceKey`, `TimeWindow`, and process resolution.
- Produces: `ThreadInstanceKey`, `ThreadSelector`, `ThreadLifetime`, and `ThreadInstanceCatalog.Resolve`.

- [ ] **Step 1: Write failing reuse tests**

```csharp
[Fact]
public void StopThenStartSameTid_CreatesNewGeneration()
{
    var process = new ProcessInstanceKey(10, 0);
    var catalog = new ThreadInstanceCatalog();
    catalog.Start(process, tid: 44, startUs: 10, startObserved: true);
    catalog.Stop(process, tid: 44, endUs: 20);
    catalog.Start(process, tid: 44, startUs: 30, startObserved: true);
    catalog.Complete(traceEndUs: 100);

    Assert.Equal(new long[] { 1, 2 }, catalog.Lifetimes.Select(x => x.Key.Generation));
}

[Fact]
public void Resolve_TwoGenerationsInWindow_IsAmbiguous()
{
    var catalog = CatalogWithTidReuse();
    var result = catalog.Resolve(new ThreadSelector(10, 44, null, null), new TimeWindow(0, 100));
    Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadInstanceCatalogTests"`

Expected: compilation fails because the thread identity contracts are absent.

- [ ] **Step 3: Implement the catalog and selector**

```csharp
internal readonly record struct ThreadInstanceKey(
    ProcessInstanceKey Process,
    int Tid,
    long Generation);

internal readonly record struct ThreadSelector(
    int Pid,
    int Tid,
    long? ProcessStartUs,
    long? ThreadStartUs);

internal sealed record ThreadLifetime(
    ThreadInstanceKey Key,
    long StartUs,
    long EndUs,
    bool StartObserved,
    bool EndObserved)
{
    public bool Intersects(TimeWindow window) => StartUs < window.EndUs && EndUs > window.StartUs;
}

internal sealed class ThreadInstanceCatalog
{
    public IReadOnlyList<ThreadLifetime> Lifetimes { get; }
    public void Start(ProcessInstanceKey process, int tid, long startUs, bool startObserved);
    public void Stop(ProcessInstanceKey process, int tid, long endUs);
    public void Complete(long traceEndUs);
    public InstanceResolution<ThreadInstanceKey> Resolve(ThreadSelector selector, TimeWindow window);
}
```

`ThreadInstanceCatalog` increments generation per `(ProcessInstanceKey,Tid)`, closes an active generation on stop or detected reuse, keeps rundown/inferred provenance flags, and returns unresolved/ambiguous rather than joining generations.

- [ ] **Step 4: Run focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadInstanceCatalogTests|FullyQualifiedName~ProcessInstanceResolverTests"`

Expected: cross-process reuse, same-PID process reuse, stop/start reuse, missing start, and exact `threadStartUs` selection pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/TraceIdentity.cs src/WprMcp/Analyzers/ThreadInstanceCatalog.cs tests/WprMcp.Tests/ThreadInstanceCatalogTests.cs
git commit -m "feat(analyzers): track thread instance generations"
```

### Task 5: Build one reusable identity index for each loaded trace

**Files:**
- Create: `src/WprMcp/Analyzers/TraceIdentityIndex.cs`
- Create: `tests/WprMcp.Tests/TraceIdentityIndexTests.cs`

**Interfaces:**
- Consumes: `TraceLog`, `TraceTime`, `ProcessInstanceResolver`, and `ThreadInstanceCatalog`.
- Produces: `TraceIdentityIndex.For(TraceLog)` and a pure `BuildFromEvents` seam for Child 2 and Child 4.

- [ ] **Step 1: Write failing construction, caching, and reuse tests**

```csharp
[Fact]
public void BuildFromEvents_MapsReusedPidAndTidToDistinctInstances()
{
    var index = TraceIdentityIndex.BuildFromEvents(
        traceEndUs: 500,
        processes: ReusedProcessLifetimes(),
        threads: ReusedThreadLifecycleEvents());

    Assert.Equal(2, index.Processes.Lifetimes.Count(x => x.Key.Pid == 20));
    Assert.Equal(2, index.Threads.Lifetimes.Count(x => x.Key.Tid == 7));
    Assert.NotEqual(index.Threads.Lifetimes[0].Key, index.Threads.Lifetimes[1].Key);
}

[Fact]
public void For_SameTraceLog_ReturnsSameImmutableIndex()
{
    using var trace = OpenFixture();
    Assert.Same(TraceIdentityIndex.For(trace), TraceIdentityIndex.For(trace));
}
```

Also test a thread-start event exactly at a process boundary, rundown/inferred starts, a stop without a start, and concurrent `For(trace)` calls. The concurrency test injects an internal builder and asserts one invocation; it must not rely on timing.

Add two process-provenance regressions: a real `ProcessStart` at trace-relative `0` yields `StartObserved=true`, while a `ProcessDCStart`/`trace.Processes` backfill at the same timestamp yields `StartObserved=false`. Timestamp value alone must never decide provenance.

- [ ] **Step 2: Run the focused test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~TraceIdentityIndexTests"`

Expected: compilation fails because the index is absent.

- [ ] **Step 3: Implement the immutable index and TraceEvent adapter**

Use this contract:

```csharp
internal sealed class TraceIdentityIndex
{
    private static readonly ConditionalWeakTable<TraceLog, Lazy<TraceIdentityIndex>> Cache = new();

    public ProcessInstanceResolver Processes { get; }
    public ThreadInstanceCatalog Threads { get; }
    public long TraceEndUs { get; }
    public IReadOnlyList<IdentityDiagnostic> Diagnostics { get; }

    public static TraceIdentityIndex For(TraceLog trace);
    internal static IReadOnlyList<ProcessLifetime> BuildProcessLifetimes(
        long traceEndUs,
        IReadOnlyList<ProcessLifecycleEvent> events,
        IReadOnlyList<ProcessLifetimeBackfill> backfill);
    internal static TraceIdentityIndex BuildFromEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processes,
        IReadOnlyList<ThreadLifecycleEvent> threads);
}

internal enum ThreadLifecycleEventKind { Start, Stop, RundownStart, RundownStop }
internal enum ProcessLifecycleEventKind { Start, Stop, RundownStart, RundownStop }
internal readonly record struct ProcessLifecycleEvent(
    int Pid,
    long TimestampUs,
    ProcessLifecycleEventKind Kind);
internal readonly record struct ProcessLifetimeBackfill(
    int Pid,
    long StartUs,
    long EndUs);
internal readonly record struct ThreadLifecycleEvent(
    int Pid,
    int Tid,
    long TimestampUs,
    ThreadLifecycleEventKind Kind,
    bool Observed);
```

`For` first walks ProcessStart/ProcessStop/ProcessDC events and builds process generations by PID. Only a real ProcessStart sets `StartObserved=true`, and only a real ProcessStop sets `EndObserved=true`; rundown and `trace.Processes` backfill create inferred bounds with the corresponding flag false. Reconcile `trace.Processes` only to fill missing lifetime/name metadata, never to overwrite event provenance, and never infer observation from `StartUs==0` or a session-end epsilon. Then walk ThreadStart/ThreadStop/ThreadDC events once, normalize every timestamp with `TraceTime`, resolve the owning process instance at that timestamp, and feed `ThreadInstanceCatalog`. An unresolved or ambiguous event adds a typed diagnostic; it is never assigned by collection order. Publish only completed immutable catalogs from `Lazy<TraceIdentityIndex>` using `LazyThreadSafetyMode.ExecutionAndPublication`.

The weak-table cache is an interim seam. Child 7 moves index ownership into the registry entry while preserving call semantics during migration. Do not add a second identity cache to `TraceCache` in this child.

- [ ] **Step 4: Run focused and concurrency tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~TraceIdentityIndexTests|FullyQualifiedName~ProcessInstanceResolverTests|FullyQualifiedName~ThreadInstanceCatalogTests"`

Expected: one immutable index is published per `TraceLog`, reuse stays separated, and unresolved events are reported instead of guessed.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/TraceIdentityIndex.cs tests/WprMcp.Tests/TraceIdentityIndexTests.cs
git commit -m "feat(analyzers): index process and thread instances per trace"
```

### Task 6: Route existing thread-lifetime analysis through the catalogs

**Files:**
- Modify: `src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs`
- Modify: `tests/WprMcp.Tests/ThreadLifetimeAnalysisTests.cs`

**Interfaces:**
- Consumes: `TraceTime`, `ProcessInstanceResolver`, and `ThreadInstanceCatalog`.
- Produces: existing `ThreadLifetimeResponse` with no public-schema change in this task.

- [ ] **Step 1: Add a failing regression that forbids `LastProcessWithID` behavior**

Add a pure accumulator seam `ThreadLifetimeAnalysis.AnalyzeEvents` and test two process lifetimes reusing PID/TID. Assert rows carry only the requested process start and have separate generations; also assert `TraceResidentStart` derives from rundown provenance, not `StartTimeUs == 0`.

```csharp
var rows = ThreadLifetimeAnalysis.AnalyzeEvents(
    traceEndUs: 200,
    processLifetimes: ReusedPidLifetimes(),
    events: ReusedPidThreadEvents(),
    selector: new ProcessInstanceKey(20, 100));
Assert.All(rows, row => Assert.True(row.StartTimeUs >= 100));
```

- [ ] **Step 2: Run the test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadLifetimeAnalysisTests"`

Expected: compilation fails because `AnalyzeEvents` does not exist; the current implementation also uses `LastProcessWithID` and a TID-only map.

- [ ] **Step 3: Refactor to a pure event seam plus TraceEvent adapter**

Consume Task 5's `ThreadLifecycleEvent` seam. The TraceEvent adapter normalizes timestamps with `TraceTime`, resolves the process for each event through `TraceIdentityIndex.For(trace)`, then maps indexed lifetimes to existing output rows. Keep the existing public `Analyze(TraceLog,int,int)` signature until Child 2/5 add selectors to the MCP surface.

- [ ] **Step 4: Run thread and smoke suites**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadLifetimeAnalysisTests|FullyQualifiedName~TraceEventSmokeTests"
```

Expected: all existing fixture tests and new reuse tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/ThreadLifetimeAnalysis.cs tests/WprMcp.Tests/ThreadLifetimeAnalysisTests.cs
git commit -m "refactor(analyzers): use shared thread lifetime catalog"
```

### Task 7: Make every windowed MCP facade validate before trace access

**Files:**
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
- Modify: `src/WprMcp/Tools/VirtualMemoryTools.cs`
- Modify: `src/WprMcp/Tools/WaitTools.cs`
- Modify: `src/WprMcp/Analyzers/MarkerSearch.cs`
- Modify: `tests/WprMcp.Tests/McpSurfaceConformanceTests.cs`
- Modify: `tests/WprMcp.Tests/ValidationTests.cs`
- Modify: `tests/WprMcp.Tests/MarkerSearchTests.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`
- Modify: `tests/WprMcp.Tests/CpuAnalysisTests.cs`

**Interfaces:**
- Consumes: `Validation.RequireWindowInput` and `TimeWindowInput.Resolve`.
- Produces: one entry pattern for every windowed MCP method.

- [ ] **Step 1: Extend conformance tests to prove validation precedes `_cache.Get`**

For every reflected windowed method, add a test case using `path="missing-before-validation.etl"` and an invalid negative/reversed window. The expected exception is `ArgumentOutOfRangeException`, never `FileNotFoundException`. Add explicit boundary tests for `MarkerSearch.fieldMaxChars`, CPU batch PID count, and startup `topCpu`/`topImageLoads` at 1,000/1,001.

- [ ] **Step 2: Run the conformance suite**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~McpSurfaceConformanceTests|FullyQualifiedName~MarkerSearchTests|FullyQualifiedName~DiagnoseToolsTests|FullyQualifiedName~CpuAnalysisTests"`

Expected: failures identify every facade still touching the trace before shared validation and every unbounded parameter.

- [ ] **Step 3: Apply the same entry pattern to every listed tool**

```csharp
var requestedWindow = Validation.RequireWindowInput(startUs, endUs, maxDurationUs);
Validation.RequirePidTid(pid, tid: null);
var trace = _cache.Get(path);
var window = requestedWindow.Resolve(TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs);
```

Pass `window.StartUs`/`window.EndUs` to legacy analyzers until their child plan changes them to `TimeWindow`. Use `Validation.RequireText`, `RequireCollectionCount`, `RequireTop`, and `RequireWhenBuckets` for the other shared limits. `fieldMaxChars` is bounded by `MaxStringChars`; batch PIDs by `MaxCollectionItems`; `topCpu`/`topImageLoads` by `MaxTop`.

- [ ] **Step 4: Run surface and full unit tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~McpSurfaceConformanceTests|FullyQualifiedName~TimeWindowSemanticsTests|FullyQualifiedName~ValidationTests"
dotnet test WprMcp.sln
```

Expected: invalid input fails before trace access, all legal existing calls remain compatible, and the full suite passes.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/Validation.cs src/WprMcp/Analyzers/MarkerSearch.cs src/WprMcp/Tools/AlpcTools.cs src/WprMcp/Tools/ClrTools.cs src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Tools/GenericProviderTools.cs src/WprMcp/Tools/HardFaultTools.cs src/WprMcp/Tools/HeapTools.cs src/WprMcp/Tools/ImageLoadTools.cs src/WprMcp/Tools/InterruptTools.cs src/WprMcp/Tools/IoTools.cs src/WprMcp/Tools/MarkerTools.cs src/WprMcp/Tools/MetaTools.cs src/WprMcp/Tools/NetIoTools.cs src/WprMcp/Tools/ReadyThreadTools.cs src/WprMcp/Tools/RegistryTools.cs src/WprMcp/Tools/SecurityTools.cs src/WprMcp/Tools/SymbolTools.cs src/WprMcp/Tools/VirtualMemoryTools.cs src/WprMcp/Tools/WaitTools.cs tests/WprMcp.Tests/McpSurfaceConformanceTests.cs tests/WprMcp.Tests/ValidationTests.cs tests/WprMcp.Tests/MarkerSearchTests.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs tests/WprMcp.Tests/CpuAnalysisTests.cs
git commit -m "refactor(tools): enforce shared input and window validation"
```

## Final verification

- [ ] Run `dotnet build WprMcp.sln -c Release -warnaserror` and require zero warnings/errors.
- [ ] Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~TimeWindow|FullyQualifiedName~Validation|FullyQualifiedName~ProcessInstance|FullyQualifiedName~ThreadInstance|FullyQualifiedName~McpSurfaceConformance"`.
- [ ] Run `rg -n "LastProcessWithID|\(long\)\([^\n]*TimeStampRelativeMSec[^\n]*1000" src/WprMcp` and reconcile every hit with the architecture allowlist.
- [ ] Run `git diff --check` and confirm only planned files changed.
