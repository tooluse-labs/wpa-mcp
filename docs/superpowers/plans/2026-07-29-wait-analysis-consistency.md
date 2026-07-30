# Thread-Scoped CPU and Wait Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make one resolved thread selectable across CPU/wait summaries and stacks while guaranteeing identical blocked-time semantics for wait summary, stacks, caller/callee, and histograms.

**Architecture:** A `ThreadAnalysisScope` validates selectors and resolves only selectors that request a concrete process/thread instance against Child 1's one-per-trace `TraceIdentityIndex`. Legacy PID-only scope deliberately aggregates every matching PID generation and reports reuse as a warning. One scheduler interval accumulator, keyed by `ThreadInstanceKey`, closes running/blocked intervals over the full trace, retains the old-thread blocking stack captured at switch-out, and clips intervals only when projecting into the requested window. CPU and wait stack builders filter events before TopN and symbolization.

**Tech Stack:** C#; TraceEvent kernel CSwitch/ReadyThread/SampledProfile events; TraceEvent stack sources; xUnit synthetic accumulators plus `small_wait_bound.etl`.

## Global Constraints

- Requires Child 1's `TraceTime`, `TimeWindow`, identity records, process/thread catalogs, and `TraceIdentityIndex.For(trace)`.
- `tid` is optional but requires `pid`; append new optional MCP parameters to preserve existing calls.
- The minimum `(pid,tid,startUs,endUs)` selector must resolve one thread generation or return explicit unresolved/ambiguous evidence.
- State machines traverse all relevant events; an out-of-window switch-out may anchor an in-window or post-window resume.
- TopN never changes totals or `Status`; it changes only section `HasMore` in Child 5.
- Missing symbols cannot change thread selection, samples, CPU time, blocked time, or wait reasons.
- A wait stack means the old thread's switch-out/blocking stack obtained from `TraceLog.GetCallStackIndexForCSwitchBlockingEventIndex`; the ordinary CSwitch call stack belongs to the thread that got CPU and must never be substituted.
- Remove Child 1 architecture-allowlist entries owned by `C2` as each analyzer migrates.

---

## File structure

| File | Action | Responsibility |
|---|---|---|
| `src/WprMcp/Analyzers/ThreadAnalysisScope.cs` | Create | Resolve and apply PID/process/thread lifetime selectors from `TraceIdentityIndex` |
| `src/WprMcp/Analyzers/SchedulerIntervalAccumulator.cs` | Create | Running/blocked interval state keyed by thread generation |
| `src/WprMcp/Analyzers/WaitAnalysis.cs` | Modify | Project scheduler intervals into per-thread summary |
| `src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs` | Modify | Stack projection from the same closed blocked intervals |
| `src/WprMcp/Analyzers/StackSourceTopN.cs` | Modify | Scope-aware request and duration-distributed `when` buckets |
| `src/WprMcp/Analyzers/CpuAnalysis.cs` | Modify | Thread-filtered sampled CPU top/caller-callee |
| `src/WprMcp/Analyzers/CpuPreciseAnalysis.cs` | Modify | Thread-filtered exact scheduler CPU/ready latency |
| `src/WprMcp/Tools/WaitTools.cs` | Modify | Add shared thread selector to three wait tools |
| `src/WprMcp/Tools/CpuTools.cs` | Modify | Add shared thread selector to three CPU tools |
| `src/WprMcp/Output/Records.cs` | Modify | Add source totals, unmatched counts, and instance identity to relevant responses |
| `tests/WprMcp.Tests/SchedulerIntervalAccumulatorTests.cs` | Create | Exact interval and reuse tests |
| `tests/WprMcp.Tests/ThreadAnalysisScopeTests.cs` | Create | Selector resolution and ambiguity tests |
| `tests/WprMcp.Tests/ThreadScopedCpuWaitTests.cs` | Create | Six-tool thread-selection invariants |
| Existing CPU/wait tests | Modify | Preserve process-wide behavior and add histogram/TopN assertions |

### Task 1: Resolve one analysis scope before traversing events

**Files:**
- Create: `src/WprMcp/Analyzers/ThreadAnalysisScope.cs`
- Create: `tests/WprMcp.Tests/ThreadAnalysisScopeTests.cs`

**Interfaces:**
- Consumes: `TimeWindow`, `ProcessInstanceResolver`, `ThreadInstanceCatalog`, and `ThreadSelector`.
- Produces: `ThreadAnalysisScope.Resolve` and `Matches`.

- [ ] **Step 1: Write failing selector tests**

```csharp
[Fact]
public void Resolve_UniqueThread_ProducesLifetimeBoundScope()
{
    var scope = ThreadAnalysisScope.Resolve(
        window: new TimeWindow(100, 200),
        pid: 50,
        tid: 7,
        processStartUs: 20,
        threadStartUs: 80,
        identities: IdentityIndex());

    Assert.Equal(InstanceResolutionStatus.Resolved, scope.Status);
    Assert.True(scope.Value.HasValue);
    var resolved = scope.Value.Value;
    Assert.True(resolved.MatchesPoint(pid: 50, tid: 7, timestampUs: 150));
    Assert.False(resolved.MatchesPoint(pid: 50, tid: 8, timestampUs: 150));
}

[Fact]
public void Resolve_ReusedTidAcrossWindow_IsAmbiguous()
{
    var result = ThreadAnalysisScope.Resolve(
        new TimeWindow(0, 300), 50, 7, null, null, ReusedIdentityIndex());
    Assert.Equal(InstanceResolutionStatus.Ambiguous, result.Status);
}

[Fact]
public void Resolve_LegacyPidOnly_AggregatesReusedProcessInstancesAndWarns()
{
    var result = ThreadAnalysisScope.Resolve(
        new TimeWindow(0, 300), 50, null, null, null, ReusedProcessIdentityIndex());

    Assert.Equal(InstanceResolutionStatus.Resolved, result.Status);
    Assert.Null(result.Value!.Value.Process);
    Assert.True(result.Value.Value.AggregatesPidLifetimes);
    Assert.True(result.Value.Value.PidReuseObserved);
    Assert.True(result.Value.Value.MatchesPoint(50, 7, 25));
    Assert.True(result.Value.Value.MatchesPoint(50, 9, 225));
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadAnalysisScopeTests"`

Expected: compilation fails because `ThreadAnalysisScope` is absent.

- [ ] **Step 3: Implement the exact scope contract**

```csharp
internal readonly record struct ThreadAnalysisScope(
    TimeWindow Window,
    int? Pid,
    ProcessLifetime? Process,
    ThreadLifetime? Thread,
    bool AggregatesPidLifetimes,
    bool PidReuseObserved)
{
    public bool MatchesPoint(int pid, int tid, long timestampUs)
    {
        if (!Window.ContainsPoint(timestampUs)) return false;
        if (Thread is not null)
            return Thread.Key.Process.Pid == pid && Thread.Key.Tid == tid &&
                   Thread.StartUs <= timestampUs && timestampUs < Thread.EndUs;
        if (Process is not null)
            return Process.Key.Pid == pid && Process.Key.StartUs <= timestampUs && timestampUs < Process.EndUs;
        return Pid is null || Pid.Value == pid;
    }

    public bool MatchesThread(ThreadInstanceKey thread) =>
        Thread is not null ? Thread.Key == thread :
        Process is not null ? Process.Key == thread.Process :
        Pid is null || Pid.Value == thread.Process.Pid;

    public long AccountInterval(ThreadInstanceKey thread, long startUs, long endUs) =>
        MatchesThread(thread)
            ? Window.IntersectDurationUs(
                Math.Max(startUs, Thread?.StartUs ?? Process?.Key.StartUs ?? 0),
                Math.Min(endUs, Thread?.EndUs ?? Process?.EndUs ?? long.MaxValue))
            : 0;

    public static InstanceResolution<ThreadAnalysisScope> Resolve(
        TimeWindow window,
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        TraceIdentityIndex identities);
}
```

`Resolve` first calls Child 1's `Validation.RequireThreadSelector`. With `pid == null`, it returns an all-process scope. With `pid` alone and no `processStartUs`, it returns a PID-aggregate scope even when multiple process lifetimes intersect the window; `PidReuseObserved` is true when the identity index finds more than one and becomes the stable `ambiguous_process_instance` warning, not a failed result. With `pid + processStartUs`, it resolves exactly one intersecting process lifetime or returns the process-instance error. With `tid`, it searches matching thread lifetimes across the PID aggregate or selected process, applies optional `threadStartUs`, and requires exactly one matching generation; zero/multiple matches return the thread-instance errors. A resolved thread also binds `Process` to its exact owning lifetime. It does not call an analyzer, rebuild the identity index, or open a stack source.

- [ ] **Step 4: Run selector and Child 1 identity tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadAnalysisScopeTests|FullyQualifiedName~ProcessInstanceResolverTests|FullyQualifiedName~ThreadInstanceCatalogTests"`

Expected: unique, absent, process-reuse, and thread-generation ambiguity cases pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/ThreadAnalysisScope.cs tests/WprMcp.Tests/ThreadAnalysisScopeTests.cs
git commit -m "feat(analyzers): resolve thread analysis scope"
```

### Task 2: Build one scheduler running/blocked interval state machine

**Files:**
- Create: `src/WprMcp/Analyzers/SchedulerIntervalAccumulator.cs`
- Create: `tests/WprMcp.Tests/SchedulerIntervalAccumulatorTests.cs`
- Modify: `src/WprMcp/Analyzers/WaitAnalysis.cs` (move `WaitReasonName` only if necessary; preserve its public helper)

**Interfaces:**
- Consumes: resolved `ThreadInstanceKey` values and normalized integer timestamps.
- Produces: `RunningInterval`, `BlockedInterval`, `SchedulerIntervalResult`, and `ProcessSwitch`.

- [ ] **Step 1: Write failing exact-interval and reuse tests**

```csharp
[Fact]
public void SwitchOutBeforeWindow_ResumeAfterWindow_ProducesOneClippableInterval()
{
    var thread = Thread(10, processStartUs: 0, tid: 5, generation: 1);
    var accumulator = new SchedulerIntervalAccumulator();
    accumulator.ProcessSwitch(
        oldThread: thread, newThread: null, timestampUs: 90,
        waitReason: "UserRequest", core: 0,
        oldThreadBlockingStack: (CallStackIndex)11);
    var closed = accumulator.ProcessSwitch(
        oldThread: null, newThread: thread, timestampUs: 210,
        waitReason: "Unknown", core: 0);

    Assert.Equal(
        new BlockedInterval(thread, 90, 210, "UserRequest", (CallStackIndex)11),
        closed.Blocked);
    Assert.Equal(100, new TimeWindow(100, 200).IntersectDurationUs(closed.Blocked!.StartUs, closed.Blocked.EndUs));
}

[Fact]
public void ReusedTidWithDifferentGeneration_DoesNotCloseOldWait()
{
    var oldThread = Thread(10, 0, 5, 1);
    var newThread = Thread(10, 0, 5, 2);
    var accumulator = new SchedulerIntervalAccumulator();
    accumulator.ProcessSwitch(oldThread, null, 10, "Executive", core: 0);
    Assert.Null(accumulator.ProcessSwitch(null, newThread, 20, "Unknown", core: 0).Blocked);
    Assert.Equal(1, accumulator.Complete(100).UnmatchedBlockedIntervalCount);
}
```

- [ ] **Step 2: Run the focused test**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~SchedulerIntervalAccumulatorTests"`

Expected: compilation fails because the shared accumulator does not exist.

- [ ] **Step 3: Implement the pure state machine**

Add `using Microsoft.Diagnostics.Tracing.Etlx;` to the accumulator and tests for `CallStackIndex`.

```csharp
internal readonly record struct RunningInterval(ThreadInstanceKey Thread, long StartUs, long EndUs, int Core);
internal readonly record struct BlockedInterval(
    ThreadInstanceKey Thread,
    long StartUs,
    long EndUs,
    string WaitReason,
    CallStackIndex BlockingStack = CallStackIndex.Invalid);
internal readonly record struct ClosedSchedulerIntervals(RunningInterval? Running, BlockedInterval? Blocked);
internal sealed record SchedulerIntervalResult(
    IReadOnlyList<RunningInterval> ClosedAtTraceEnd,
    int UnmatchedRunningIntervalCount,
    int UnmatchedBlockedIntervalCount,
    int IdentityMismatchCount);

internal sealed class SchedulerIntervalAccumulator
{
    private readonly Dictionary<ThreadInstanceKey,
        (long StartUs, string Reason, CallStackIndex BlockingStack)> _blocked = new();
    private readonly Dictionary<int, (ThreadInstanceKey Thread, long StartUs)> _runningByCore = new();

    public ClosedSchedulerIntervals ProcessSwitch(
        ThreadInstanceKey? oldThread,
        ThreadInstanceKey? newThread,
        long timestampUs,
        string waitReason,
        int core,
        CallStackIndex oldThreadBlockingStack = CallStackIndex.Invalid);
    public ClosedSchedulerIntervals Stop(ThreadInstanceKey thread, long timestampUs);
    public SchedulerIntervalResult Complete(long traceEndUs);
}
```

For each CSwitch, close the old running interval on that core when it has a known start, open the old thread's blocked interval, close the new thread's blocked interval when present, and open the new running interval. The TraceEvent adapter obtains the old-thread stack at switch-out with `trace.GetCallStackIndexForCSwitchBlockingEventIndex(data.EventIndex)` and passes that value as `oldThreadBlockingStack`; it never stores `data.CallStackIndex()` as blocking provenance because the ordinary CSwitch stack is for the thread that got CPU. Resolve both event sides through `TraceIdentityIndex` at the event timestamp before calling the accumulator. Reject negative/backward intervals, close known running state on thread stop, and count rather than inventing an end for unmatched blocked intervals. Running intervals may close at a known thread-stop or trace end; blocked intervals may not. Identity mismatches are counted and never paired by raw TID.

- [ ] **Step 4: Run the scheduler tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~SchedulerIntervalAccumulatorTests"`

Expected: exact running, blocked, capture-boundary, cross-process reuse, same-process generation reuse, and stop-cleanup tests pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/SchedulerIntervalAccumulator.cs tests/WprMcp.Tests/SchedulerIntervalAccumulatorTests.cs
git commit -m "feat(analyzers): unify scheduler interval accounting"
```

### Task 3: Rebuild `wait_analysis` on the shared interval stream

**Files:**
- Modify: `src/WprMcp/Analyzers/WaitAnalysis.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `tests/WprMcp.Tests/WaitAnalysisTests.cs`

**Interfaces:**
- Consumes: `ThreadAnalysisScope`, `SchedulerIntervalAccumulator`, and `TimeWindow`.
- Produces: `WaitAnalysis.Analyze(TraceLog,int,ThreadAnalysisScope)` and response totals independent of TopN.

- [ ] **Step 1: Add failing projection tests**

```csharp
[Fact]
public void WaitAccumulator_TargetTidBelowTop_IsStillReturned()
{
    var response = WaitAnalysis.Project(
        intervals: ManyThreadsWithTargetLast(targetTid: 900),
        scope: ScopeFor(pid: 10, tid: 900, startUs: 0, endUs: 100),
        top: 1);
    var row = Assert.Single(response.Rows);
    Assert.Equal(900, row.Tid);
}

[Fact]
public void WaitAccumulator_TotalIsComputedBeforeTop()
{
    var intervals = ThreeThreads();
    var scope = ProcessScope();
    var response = WaitAnalysis.Project(intervals, scope, top: 1);
    Assert.Equal(
        intervals.Sum(x => scope.AccountInterval(x.Thread, x.StartUs, x.EndUs)),
        response.TotalBlockedUs);
}
```

- [ ] **Step 2: Run wait tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~WaitAnalysisTests"`

Expected: compilation fails for `Project`/scope overload and the current response has no process-wide total/unmatched count.

- [ ] **Step 3: Refactor wait aggregation**

Add `TotalBlockedUs`, `UnmatchedBlockedIntervalCount`, selected `ProcessInstanceKey?`, and `ThreadInstanceKey?` to `WaitAnalysisResponse`. Aggregate every closed blocked interval using `scope.AccountInterval`; aggregate running intervals for `CpuUs`; only then sort/take for process-wide results. When `scope.Thread` is set, return its row directly and use `top` only for its wait-reason list. For legacy PID-only scope, aggregate rows from every matching process generation; if `scope.PidReuseObserved`, append one stable `ambiguous_process_instance` warning while retaining the successful aggregate. Set the selected process key only for an exact process/thread scope, never to an arbitrary generation of a PID aggregate.

Keep `WaitReasonName(ThreadWaitReason)` unchanged as the canonical reason mapping. Remove the private `(Pid,Tid)` dictionaries and direct timestamp clipping from `WaitAnalysisAccumulator`.

- [ ] **Step 4: Run wait summary tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~WaitAnalysisTests|FullyQualifiedName~SchedulerIntervalAccumulatorTests"
```

Expected: exact boundary totals, target-below-TopN, unmatched partial metadata input, and existing fixture shape pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/WaitAnalysis.cs src/WprMcp/Output/Records.cs tests/WprMcp.Tests/WaitAnalysisTests.cs
git commit -m "refactor(wait): project summary from shared intervals"
```

### Task 4: Make blocked stacks, caller/callee, and `when` use clipped intervals

**Files:**
- Modify: `src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/StackSourceTopN.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `tests/WprMcp.Tests/BlockedTimeStackAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/StackSourceTopNTests.cs`

**Interfaces:**
- Consumes: a closed `BlockedInterval` at the resume event plus `ThreadAnalysisScope`.
- Produces: `WhenHistogram.ForWindow(TimeWindow,int)`, `AddDurationInterval(long,long)`, and scope-aware blocked stack responses.

- [ ] **Step 1: Write failing stack and bucket tests**

```csharp
[Fact]
public void AddInterval_SplitsMetricAcrossBuckets()
{
    var histogram = StackSourceTopN.WhenHistogram.ForWindow(new TimeWindow(0, 100), bucketCount: 2);
    histogram.AddDurationInterval(intervalStartUs: 25, intervalEndUs: 75);
    Assert.Equal(new long[] { 25, 25 }, histogram.Build()!.Buckets);
}

[Fact]
public void BlockedProjection_ResumeOutsideWindow_UsesSwitchOutBlockingStackAndClippedMetric()
{
    var result = BlockedTimeStackAnalysis.ProjectSynthetic(
        switchOutUs: 90,
        resumeUs: 210,
        window: new TimeWindow(100, 200),
        blockingStack: (CallStackIndex)11,
        ordinaryResumeStack: (CallStackIndex)22);
    Assert.Equal(100, result.TotalBlockedUs);
    Assert.Equal(100, Assert.Single(result.Samples).MetricUs);
    Assert.Equal((CallStackIndex)11, result.Samples[0].SourceStack);
}
```

- [ ] **Step 2: Run stack tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~BlockedTimeStackAnalysisTests|FullyQualifiedName~StackSourceTopNTests"`

Expected: failures show resume-point full-duration accounting and resume-bucket behavior.

- [ ] **Step 3: Replace TID-only stack state and distribute duration**

Preserve the positional `StackAnalysisRequest(Pid,StartUs,EndUs,SymbolLog,When)` constructor used by non-CPU/wait analyzers and append `public ThreadAnalysisScope? ThreadScope { get; init; }`. Add a `PassesFilter(int pid,int tid,long nowUs)` overload that delegates to `ThreadScope.Value.MatchesPoint` when present; keep existing overloads until their owning children migrate. During the full CSwitch traversal, resolve old/new `ThreadInstanceKey` from `TraceIdentityIndex`, feed `SchedulerIntervalAccumulator`, and when switch-in closes a blocked interval:

```csharp
var scope = request.ThreadScope ?? throw new InvalidOperationException("wait stacks require a resolved scope");
var accountedUs = scope.AccountInterval(interval.Thread, interval.StartUs, interval.EndUs);
if (accountedUs > 0)
{
    raw.AddSample(interval.BlockingStack, data, accountedUs);
    request.When.AddDurationInterval(interval.StartUs, interval.EndUs);
}
```

The `data` argument above is used only to attach the resolved blocked thread/process root when converting the stored call-stack index; its ordinary `data.CallStackIndex()` is deliberately ignored. If the special blocking stack is invalid, keep the full clipped duration under `?!?`; do not substitute the resume stack. `ProjectSynthetic` exposes both stack tokens so the regression proves their inequality. Do not apply `StackAnalysisRequest.PassesFilter` to the resume timestamp before closing the interval: a resume after `window.EndUs` can close a wait that overlaps the requested window.

Implement `WhenHistogram.ForWindow(TimeWindow,int)` with ceiling bucket width `checked((window.DurationUs + bucketCount - 1) / bucketCount)` and cap each bucket's effective end at `window.EndUs`. `AddDurationInterval` adds each bucket's exact half-open overlap; `TimeHistogram` gains an `EndUs` field so the shorter final bucket is explicit. Retain `AddPoint` for count/sample histograms and remove the blocked-time resume-bucket call. Keep the `?!?` fallback in `RawStackSource.AddSample`. Add source total and unmatched count to top-stack and caller/callee responses. Caller/callee uses the same normalized source; it does not rescan with a different predicate. Do not stop the CSwitch traversal at `window.EndUs`, because a later resume may close a wait that intersects the window.

- [ ] **Step 4: Run wait stack and real wait-bound fixture tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~BlockedTimeStackAnalysisTests|FullyQualifiedName~StackSourceTopNTests|FullyQualifiedName~WaitBoundFixtureTests"
```

Expected: histogram sum equals `TotalBlockedUs`, filtered totals equal wait summary for the same scope, and real stacks remain non-empty.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs src/WprMcp/Analyzers/StackSourceTopN.cs src/WprMcp/Output/Records.cs tests/WprMcp.Tests/BlockedTimeStackAnalysisTests.cs tests/WprMcp.Tests/StackSourceTopNTests.cs
git commit -m "fix(wait): align stack and histogram blocked time"
```

### Task 5: Add thread selection to sampled CPU stacks

**Files:**
- Modify: `src/WprMcp/Analyzers/CpuAnalysis.cs`
- Modify: `tests/WprMcp.Tests/CpuAnalysisTests.cs`

**Interfaces:**
- Consumes: `ThreadAnalysisScope`.
- Produces: scope overloads for `TopFunctions` and `CallerCallee`.

- [ ] **Step 1: Write failing sampled-event selection tests**

Add a pure predicate seam and assert two events with the same PID but different TIDs. Also run once with `resolveSymbols=false` and once with a fake resolved frame name; sample counts and selected TID must be identical.

```csharp
Assert.True(CpuAnalysis.PassesScope(scope, pid: 10, tid: 7, timestampUs: 150));
Assert.False(CpuAnalysis.PassesScope(scope, pid: 10, tid: 8, timestampUs: 150));
```

- [ ] **Step 2: Run CPU tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~CpuAnalysisTests"`

Expected: compilation fails for the scope predicate/overloads.

- [ ] **Step 3: Filter samples before adding them to raw sources**

Add `ThreadAnalysisScope scope` to the internal `BuildNormalized` path and replace separate PID/window predicates with:

```csharp
var timestampUs = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
if (!scope.MatchesPoint(ev.ProcessID, ev.ThreadID, timestampUs)) continue;
raw.AddSample(ev.CallStackIndex(), ev, metric: 1);
```

Apply the same scope to both top-functions and caller/callee. Do not warm or resolve frames excluded by the thread scope.

- [ ] **Step 4: Run CPU and stack normalization tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~CpuAnalysisTests|FullyQualifiedName~StackSourceTopNTests"`

Expected: process-wide tests remain compatible and thread selection is independent of symbol mode.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/CpuAnalysis.cs tests/WprMcp.Tests/CpuAnalysisTests.cs
git commit -m "feat(cpu): filter sampled stacks by thread instance"
```

### Task 6: Add thread selection to CPU Precise

**Files:**
- Modify: `src/WprMcp/Analyzers/CpuPreciseAnalysis.cs`
- Modify: `tests/WprMcp.Tests/CpuPreciseAnalysisTests.cs`

**Interfaces:**
- Consumes: scope and scheduler identity resolution.
- Produces: `CpuPreciseAnalysis.Analyze(TraceLog,int,ThreadAnalysisScope)`.

- [ ] **Step 1: Add a failing target-below-TopN test**

Construct two exact run intervals in one process where TID 1 has 100 ms and TID 42 has 10 ms. With `top=1` and a TID 42 scope, assert TID 42 is the only row and totals are 10 ms. Add ready-latency events for both TIDs and assert only TID 42 contributes.

- [ ] **Step 2: Run CPU Precise tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~CpuPreciseAnalysisTests"`

Expected: the current PID-only accumulator returns the higher-ranked thread or both threads.

- [ ] **Step 3: Apply scope at projection, not state maintenance**

Keep per-core running and ready state for all events so outside-scope events can close scheduler state. Charge completed CPU and ready-latency intervals with `scope.AccountInterval`; count point-in-time switch/ready events with `scope.MatchesPoint`; and retain response rows with `scope.MatchesThread`. Do not apply the requested window while maintaining open per-core/ready state. When a thread selector is present, bypass process-wide `.Take(top)` and return the resolved thread row directly.

- [ ] **Step 4: Run CPU Precise and scheduler tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~CpuPreciseAnalysisTests|FullyQualifiedName~SchedulerIntervalAccumulatorTests"`

Expected: existing per-core/capture-boundary behavior passes and target TID is never TopN-truncated.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/CpuPreciseAnalysis.cs tests/WprMcp.Tests/CpuPreciseAnalysisTests.cs
git commit -m "feat(cpu): scope precise scheduler metrics to one thread"
```

### Task 7: Expose one selector on all six CPU/wait tools and lock cross-tool invariants

**Files:**
- Modify: `src/WprMcp/Tools/WaitTools.cs`
- Modify: `src/WprMcp/Tools/CpuTools.cs`
- Create: `tests/WprMcp.Tests/ThreadScopedCpuWaitTests.cs`
- Modify: `tests/WprMcp.Tests/McpSurfaceConformanceTests.cs`

**Interfaces:**
- Consumes: scope resolver and all scope-aware analyzer overloads.
- Produces: optional `tid`, `processStartUs`, and `threadStartUs` on six public MCP methods.

- [ ] **Step 1: Write failing surface and cross-tool tests**

Reflection must find the same selector parameter names/types on `WaitAnalysis`, `WaitTopStacks`, `WaitCallerCallee`, `CpuPrecise`, `CpuTopFunctions`, and `CpuCallerCallee`. Behavioral tests must cover invalid selector relationships/negative selector timestamps before trace access, legacy PID-only aggregation across reused process generations with one warning, exact process selection, unique thread selection, same-PID TID reuse, target below TopN, switch-out before window/resume after window, switch-out blocking-stack provenance, and unchanged totals with/without symbol resolution. Add a three-row degradation matrix: no CSwitch means no exact CPU/off-CPU durations; CSwitch without a blocking stackwalk means exact duration charged to `?!?`; blocking stackwalk without symbols means the same samples and durations with unresolved frame names.

- [ ] **Step 2: Run the new tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~ThreadScopedCpuWaitTests|FullyQualifiedName~McpSurfaceConformanceTests"
```

Expected: schema/reflection tests fail because the parameters are absent.

- [ ] **Step 3: Append the selector parameters and resolve before analysis**

Append this exact parameter group to each of the six tool methods, after all existing optional parameters:

```csharp
[Description("Optional thread ID; requires pid and is resolved within the requested half-open window.")]
int? tid = null,
[Description("Optional exact process start in trace-relative microseconds; requires pid. Without it, pid-only queries retain aggregate behavior across process lifetimes.")]
long? processStartUs = null,
[Description("Optional exact thread start in trace-relative microseconds; requires pid and tid.")]
long? threadStartUs = null
```

Call `Validation.RequireThreadSelector(pid,tid,processStartUs,threadStartUs)` and validate the window shape before `_cache.Get`, resolve `TimeWindow`, obtain `TraceIdentityIndex.For(trace)`, resolve `ThreadAnalysisScope`, and only then call the corresponding analyzer. Exact process/thread resolution returns typed internal failures: `process_instance_not_found`, `ambiguous_process_instance`, `thread_instance_not_found`, or `ambiguous_thread_instance`; Child 5 maps them to the wire envelope without leaking candidate details. A PID-only call without instance selectors is not an exact-resolution failure path: it preserves process-wide aggregation across reused lifetimes and emits the stable ambiguity warning. Legacy calls with all three selector fields null preserve all-process/process-wide behavior. A requested thread bypasses process-wide TopN in all six paths; selection occurs before stack construction, symbol resolution, sorting, and truncation.

Expose distinct availability metadata on relevant response DTOs: `HasContextSwitches`, `HasContextSwitchBlockingStacks`, `HasSampledProfileStacks`, and `SymbolResolutionState`. Update the wait-tool descriptions to define stack attribution as the old thread's switch-out/blocking stack. Durations never become unavailable merely because names are unresolved, and an unknown stack remains charged to `?!?` instead of disappearing.

- [ ] **Step 4: Run all CPU/wait and conformance tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~Wait|FullyQualifiedName~Cpu|FullyQualifiedName~ThreadScopedCpuWait|FullyQualifiedName~McpSurfaceConformance"
```

Expected: all six tools share the same selector, same-scope totals agree, and missing symbols degrade names only.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Tools/WaitTools.cs src/WprMcp/Tools/CpuTools.cs tests/WprMcp.Tests/ThreadScopedCpuWaitTests.cs tests/WprMcp.Tests/McpSurfaceConformanceTests.cs
git commit -m "feat(tools): expose unified thread CPU and wait filters"
```

## Final verification

- [ ] Run `dotnet build WprMcp.sln -c Release -warnaserror`.
- [ ] Run `dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Wait|FullyQualifiedName~Cpu|FullyQualifiedName~ThreadAnalysisScope|FullyQualifiedName~SchedulerInterval|FullyQualifiedName~ThreadScopedCpuWait"`.
- [ ] Run the `small_wait_bound.etl` tests twice, once with symbol resolution disabled and once enabled; selected TID/sample totals must match.
- [ ] Confirm `rg -n "Dictionary<int, double>|record struct ThreadKey\(int Pid, int Tid\)" src/WprMcp/Analyzers/WaitAnalysis.cs src/WprMcp/Analyzers/BlockedTimeStackAnalysis.cs` returns no legacy TID-only state.
- [ ] Remove every `C2` entry from `tests/WprMcp.Tests/Architecture/window-primitive-allowlist.txt` and run the architecture test.
- [ ] Run `git diff --check`.
