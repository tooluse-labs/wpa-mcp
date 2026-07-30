# Slow-Startup Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `diagnose_slow_startup` select, rank, and explain only processes with an observed ProcessStart, using one process-instance-specific startup window for all primary metrics and evidence.

**Architecture:** Build a startup catalog from Child 1's process lifetimes, derive one checked half-open `StartupWindow` per observed start, and project Child 2's shared scheduler intervals plus instance-resolved image loads into that window. Rank from startup-only wall/CPU metrics, then execute wait, image, CPU, and optional first-image-gap evidence with process-instance provenance and auditable parent/child windows.

**Tech Stack:** The C# language version, TFM, TraceEvent version, and xUnit version selected by Child 11A (repository baseline at plan time: C# 12, .NET 8, TraceEvent 3.2.2, xUnit 2.5.3)

## Global Constraints

- Complete Child 11A, Child 1, and Child 2 before this plan; consume `ProcessLifetime`, `ProcessInstanceKey`, `TraceIdentityIndex`, `ThreadAnalysisScope`, `RunningInterval`, `BlockedInterval`, and `SchedulerIntervalAccumulator` directly.
- Do not change target frameworks, NuGet packages, MCP SDK versions, WPR profiles, or assembly-level test parallelization.
- A startup window is exactly `[ProcessStartUs,min(checked(ProcessStartUs+startupWindowUs),observed ProcessEndUs,TraceDurationUs))`.
- `ProcessStartObserved` means Child 1 observed a real ProcessStart event. `StartUs==0`, rundown, inferred metadata, or presence in `trace.Processes` does not establish startup provenance.
- Candidate selection, scheduler wait, scheduler CPU, image loads, CPU stacks, evidence, and executed-call metadata must use the same `ProcessInstanceKey` and parent startup window.
- The only permitted primary child window is first-image gap `[ProcessStartUs,FirstImageLoadUs)`; it must be contained by and record the parent startup window.
- `StartupWaitRatio = ObservedStartupWallUs / StartupCpuUs`; zero CPU yields null. Order by ratio descending, observed startup wall descending, process start ascending, then PID ascending.
- Lifetime wall, CPU, wait-ratio, and image-load values cannot influence selection or ordering; retain lifetime metrics only under names prefixed with `Lifetime`.
- Bound discovery inputs and provenance samples by Child 1's `Validation.MaxCollectionItems`; expose total counts and `HasMore` rather than retaining or returning an unbounded process collection.
- Keep the existing MCP method parameters. Child 5 owns final `ToolEnvelope` serialization; this child emits `Status="Partial"` plus `Code="startup_window_truncated"` in startup provenance for Child 5 to lift into the envelope.
- Do not identify startup evidence by PID alone. Stable IDs and provenance include both PID and process start.

---

## File Structure

| Path | Action | Responsibility |
|---|---|---|
| `src/WprMcp/Analyzers/StartupWindow.cs` | Create | Checked startup-window construction and completeness |
| `src/WprMcp/Analyzers/StartupProcessCatalog.cs` | Create | Join TraceEvent process metadata to Child 1 observed lifetimes |
| `src/WprMcp/Analyzers/SchedulerIntervalTraceReader.cs` | Create | Stream Child 2 closed intervals to bounded sinks in one trace pass |
| `src/WprMcp/Analyzers/StartupMetricsAccumulator.cs` | Create | Clip scheduler intervals to each process startup window |
| `src/WprMcp/Analyzers/StartupImageLoadAnalysis.cs` | Create | Stream image loads into bounded process-instance startup buckets |
| `src/WprMcp/Analyzers/SlowStartupProjection.cs` | Create | Startup-only candidate ranking and evidence-window planning |
| `src/WprMcp/Analyzers/ProcessInstanceResolver.cs` | Modify | Expose immutable `Lifetimes` already owned by the resolver |
| `src/WprMcp/Analyzers/WaitAnalysis.cs` | Modify | Reuse the shared scheduler interval trace reader |
| `src/WprMcp/Tools/DiagnoseTools.cs` | Modify | Replace lifetime/PID orchestration with startup projections |
| `src/WprMcp/Output/StartupRecords.cs` | Create | Startup candidate, window, and gap-evidence records |
| `src/WprMcp/Output/Records.cs` | Modify | Move startup records and extend composite provenance fields |
| `tests/WprMcp.Tests/StartupWindowTests.cs` | Create | Observed start/end, trace truncation, and overflow tests |
| `tests/WprMcp.Tests/StartupMetricsAccumulatorTests.cs` | Create | Child 2 scheduler interval projection and PID-reuse tests |
| `tests/WprMcp.Tests/StartupImageLoadAnalysisTests.cs` | Create | Instance/window image-load isolation tests |
| `tests/WprMcp.Tests/SlowStartupProjectionTests.cs` | Create | Ratio, ranking, lifetime-exclusion, and child-window tests |
| `tests/WprMcp.Tests/DiagnoseToolsTests.cs` | Modify | Composite output, calls, not-concluded, and fixture regressions |
| `tests/WprMcp.Tests/SlowStartupWindowAcceptanceTests.cs` | Create | Cross-component acceptance matrix |

### Task 1: Derive startup windows only from observed process starts

**Files:**
- Create: `src/WprMcp/Analyzers/StartupWindow.cs`
- Create: `src/WprMcp/Analyzers/StartupProcessCatalog.cs`
- Modify: `src/WprMcp/Analyzers/ProcessInstanceResolver.cs`
- Create: `tests/WprMcp.Tests/StartupWindowTests.cs`

**Interfaces:**
- Consumes: Child 1 `ProcessLifetime`, `ProcessInstanceKey`, `TraceIdentityIndex.Processes`, `TraceTime`, and `TimeWindow`.
- Produces: `StartupWindow.Create`, `StartupProcessCatalog.Build`, `StartupProcessObservation`, and one exclusion per ineligible process instance.

- [ ] **Step 1: Write the failing provenance and bound tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class StartupWindowTests
{
    [Fact]
    public void Create_ObservedEarlyExit_IsCompleteShortLifetime()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, 100), EndUs: 350,
            StartObserved: true, EndObserved: true);

        var window = StartupWindow.Create(lifetime, startupWindowUs: 1_000, traceDurationUs: 5_000);

        Assert.Equal(new TimeWindow(100, 350), window.Bounds);
        Assert.Equal(1_100, window.RequestedEndUs);
        Assert.Equal("Complete", window.Status);
        Assert.Null(window.Code);
    }

    [Fact]
    public void Create_TraceEndCutsRequestedWindow_IsPartial()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, 900), EndUs: 1_000,
            StartObserved: true, EndObserved: false);

        var window = StartupWindow.Create(lifetime, startupWindowUs: 500, traceDurationUs: 1_000);

        Assert.Equal(new TimeWindow(900, 1_000), window.Bounds);
        Assert.Equal("Partial", window.Status);
        Assert.Equal("startup_window_truncated", window.Code);
    }

    [Fact]
    public void Build_PreExistingProcessIsExcludedExactlyOnce()
    {
        var metadata = new StartupProcessMetadata(
            new ProcessLifetime(new ProcessInstanceKey(9, 0), 2_000,
                StartObserved: false, EndObserved: false),
            ParentPid: 1, Name: "resident.exe", LifetimeCpuUs: 50,
            LifetimeImageLoadCount: 4);

        var result = StartupProcessCatalog.Build(
            [metadata], startupWindowUs: 500, traceDurationUs: 2_000,
            nameSubstring: null, maxCollectionItems: Validation.MaxCollectionItems);

        Assert.Empty(result.Eligible);
        var exclusion = Assert.Single(result.Excluded);
        Assert.Equal("startup_start_not_observed", exclusion.Code);
        Assert.Equal(metadata.Lifetime.Key, exclusion.Process);
    }

    [Fact]
    public void Build_OrdinaryDiscoveryBoundsEligibleAndExcludedSamples()
    {
        var metadata = Enumerable.Range(1, Validation.MaxCollectionItems + 10)
            .Select(index => Process(
                pid: index, startUs: index * 10L,
                startObserved: index % 2 == 0))
            .ToList();

        var result = StartupProcessCatalog.Build(
            metadata, startupWindowUs: 5, traceDurationUs: 10_000,
            nameSubstring: null, maxCollectionItems: 8);

        Assert.Equal(metadata.Count(item => item.Lifetime.StartObserved), result.TotalEligibleCount);
        Assert.Equal(metadata.Count(item => !item.Lifetime.StartObserved), result.TotalUnobservedStartCount);
        Assert.Equal(0, result.TotalOtherExcludedCount);
        Assert.Equal(8, result.Eligible.Count);
        Assert.Equal(8, result.Excluded.Count);
        Assert.True(result.EligibleHasMore);
        Assert.True(result.ExcludedHasMore);
    }

    [Fact]
    public void Create_CheckedAdditionRejectsOverflow()
    {
        var lifetime = new ProcessLifetime(
            new ProcessInstanceKey(7, long.MaxValue - 4), long.MaxValue,
            StartObserved: true, EndObserved: false);

        Assert.Throws<OverflowException>(() =>
            StartupWindow.Create(lifetime, startupWindowUs: 10, traceDurationUs: long.MaxValue));
    }

    private static StartupProcessMetadata Process(int pid, long startUs, bool startObserved) =>
        new(
            new ProcessLifetime(
                new ProcessInstanceKey(pid, startUs),
                EndUs: startUs + 100,
                StartObserved: startObserved,
                EndObserved: true),
            ParentPid: 1,
            Name: $"process-{pid}.exe",
            LifetimeCpuUs: 10,
            LifetimeImageLoadCount: 1);
}
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupWindowTests"`

Expected: compilation fails because the startup window/catalog types and resolver lifetime list are absent.

- [ ] **Step 3: Implement checked window construction and catalog contracts**

Expose the resolver's already-sorted immutable list without adding another identity cache:

```csharp
internal sealed class ProcessInstanceResolver
{
    public IReadOnlyList<ProcessLifetime> Lifetimes { get; }
    public InstanceResolution<ProcessInstanceKey> Resolve(int pid, long timestampUs, long? processStartUs);
}
```

Create these exact startup contracts:

```csharp
namespace WprMcp.Analyzers;

internal sealed record StartupWindow(
    ProcessInstanceKey Process,
    TimeWindow Bounds,
    long RequestedEndUs,
    long TraceDurationUs,
    bool ProcessStartObserved,
    bool ProcessEndObserved,
    string Status,
    string? Code)
{
    public static StartupWindow Create(
        ProcessLifetime lifetime, long startupWindowUs, long traceDurationUs)
    {
        if (!lifetime.StartObserved)
            throw new InvalidOperationException("startup_start_not_observed");
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));
        if (traceDurationUs <= lifetime.Key.StartUs)
            throw new InvalidOperationException("startup_window_empty");

        var requestedEndUs = checked(lifetime.Key.StartUs + startupWindowUs);
        var observedEndUs = lifetime.EndObserved ? lifetime.EndUs : long.MaxValue;
        var endUs = Math.Min(requestedEndUs, Math.Min(observedEndUs, traceDurationUs));
        if (endUs <= lifetime.Key.StartUs)
            throw new InvalidOperationException("startup_window_empty");

        var observedExitAtOrBeforeTraceEnd =
            lifetime.EndObserved && lifetime.EndUs <= traceDurationUs;
        var truncatedByTraceEnd =
            traceDurationUs < requestedEndUs && !observedExitAtOrBeforeTraceEnd;

        return new StartupWindow(
            lifetime.Key,
            new TimeWindow(lifetime.Key.StartUs, endUs),
            requestedEndUs,
            traceDurationUs,
            ProcessStartObserved: true,
            ProcessEndObserved: lifetime.EndObserved,
            Status: truncatedByTraceEnd ? "Partial" : "Complete",
            Code: truncatedByTraceEnd ? "startup_window_truncated" : null);
    }
}

internal sealed record StartupProcessMetadata(
    ProcessLifetime Lifetime,
    int ParentPid,
    string Name,
    long LifetimeCpuUs,
    int LifetimeImageLoadCount);

internal sealed record StartupProcessObservation(
    StartupProcessMetadata Metadata,
    StartupWindow Window)
{
    public ProcessInstanceKey Process => Metadata.Lifetime.Key;
    public long LifetimeWallUs => checked(Metadata.Lifetime.EndUs - Metadata.Lifetime.Key.StartUs);
    public double? LifetimeWaitRatio => Metadata.LifetimeCpuUs == 0
        ? null
        : LifetimeWallUs / (double)Metadata.LifetimeCpuUs;
}

internal sealed record StartupProcessExclusion(
    ProcessInstanceKey Process, string ProcessName, string Code, string Reason);

internal sealed record StartupProcessCatalogResult(
    IReadOnlyList<StartupProcessObservation> Eligible,
    int TotalEligibleCount,
    bool EligibleHasMore,
    IReadOnlyList<StartupProcessExclusion> Excluded,
    int TotalUnobservedStartCount,
    int TotalOtherExcludedCount,
    bool ExcludedHasMore,
    bool ExplicitNameTarget);

internal static class StartupProcessCatalog
{
    public static StartupProcessCatalogResult Build(
        IEnumerable<StartupProcessMetadata> processes,
        long startupWindowUs,
        long traceDurationUs,
        string? nameSubstring,
        int maxCollectionItems);

    public static StartupProcessCatalogResult FromTrace(
        TraceLog trace,
        TraceIdentityIndex identities,
        long startupWindowUs,
        string? nameSubstring,
        int maxCollectionItems = Validation.MaxCollectionItems);
}
```

`Build` validates `maxCollectionItems` with `Validation.RequireCollectionCount`, applies a non-empty `nameSubstring` before counting or bounding, and processes metadata in `(process start,PID)` order. It counts all matching eligible/excluded instances but retains at most `maxCollectionItems` of each. It emits `startup_start_not_observed` for `StartObserved=false`, increments `TotalUnobservedStartCount` for that code, emits `startup_window_empty` and increments `TotalOtherExcludedCount` when no positive interval remains, and creates a window otherwise. `EligibleHasMore`/`ExcludedHasMore` compare total counts with retained counts. Candidate metric collection consumes only the bounded `Eligible` list and reports `EligibleHasMore`; it never silently implies a global exhaustive ranking when more inputs existed.

`FromTrace` normalizes each `TraceProcess` start with `TraceTime.FromMilliseconds`, joins it to `identities.Processes.Lifetimes` by exact `ProcessInstanceKey`, and carries `ParentID`, `Name`, `CPUMSec`, and loaded-module count only as lifetime metadata. Rundown flags and `StartUs==0` never override `ProcessLifetime.StartObserved`.

- [ ] **Step 4: Run focused and Child 1 identity tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupWindowTests|FullyQualifiedName~ProcessInstanceResolverTests|FullyQualifiedName~TraceIdentityIndexTests"`

Expected: requested-window, observed-exit, trace-truncation, equality boundary, pre-existing, empty-window, overflow, and duplicate-PID lifetime cases pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/StartupWindow.cs src/WprMcp/Analyzers/StartupProcessCatalog.cs src/WprMcp/Analyzers/ProcessInstanceResolver.cs tests/WprMcp.Tests/StartupWindowTests.cs
git commit -m "feat(startup): derive observed process windows"
```

### Task 2: Stream Child 2 scheduler intervals into each startup window

**Files:**
- Create: `src/WprMcp/Analyzers/SchedulerIntervalTraceReader.cs`
- Create: `src/WprMcp/Analyzers/StartupMetricsAccumulator.cs`
- Modify: `src/WprMcp/Analyzers/WaitAnalysis.cs`
- Create: `tests/WprMcp.Tests/StartupMetricsAccumulatorTests.cs`

**Interfaces:**
- Consumes: Child 2 `SchedulerIntervalAccumulator.ProcessSwitch`, `.Stop`, `.Complete`, `RunningInterval`, `BlockedInterval`, and `SchedulerIntervalResult`.
- Produces: `ISchedulerIntervalSink`, streaming `SchedulerIntervalTraceReader.Read`, bounded `SchedulerStreamSummary`, and `StartupMetricsAccumulator` used by candidate selection and wait evidence.

- [ ] **Step 1: Write the failing scheduler projection tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class StartupMetricsAccumulatorTests
{
    [Fact]
    public void Project_ClipsRunningAndBlockedIntervalsToOneStartupWindow()
    {
        var process = new ProcessInstanceKey(10, 100);
        var thread = new ThreadInstanceKey(process, 5, 1);
        var observation = Observation(process, new TimeWindow(100, 200));
        var running = new[]
        {
            new RunningInterval(thread, 90, 110, 0),
            new RunningInterval(thread, 140, 160, 0),
            new RunningInterval(thread, 210, 250, 0),
        };
        var blocked = new[]
        {
            new BlockedInterval(thread, 80, 120, "Executive"),
            new BlockedInterval(thread, 180, 220, "UserRequest"),
            new BlockedInterval(thread, 230, 260, "UserRequest"),
        };

        var metrics = StartupMetricsAccumulator.Project([observation], running, blocked)[process];

        Assert.Equal(30, metrics.StartupCpuUs);
        Assert.Equal(40, metrics.StartupBlockedUs);
        Assert.Equal(20, metrics.BlockedUsByReason["Executive"]);
        Assert.Equal(20, metrics.BlockedUsByReason["UserRequest"]);
    }

    [Fact]
    public void Project_ReusedPidLaterLifetimeCannotEnterEarlierMetrics()
    {
        var early = new ProcessInstanceKey(10, 100);
        var late = new ProcessInstanceKey(10, 300);
        var metrics = StartupMetricsAccumulator.Project(
            [Observation(early, new TimeWindow(100, 200))],
            [new RunningInterval(new ThreadInstanceKey(late, 5, 1), 310, 390, 0)],
            [new BlockedInterval(new ThreadInstanceKey(late, 5, 1), 320, 380, "Executive")])[early];

        Assert.Equal(0, metrics.StartupCpuUs);
        Assert.Equal(0, metrics.StartupBlockedUs);
    }

    private static StartupProcessObservation Observation(
        ProcessInstanceKey process,
        TimeWindow bounds)
    {
        var lifetime = new ProcessLifetime(
            process, EndUs: bounds.EndUs, StartObserved: true, EndObserved: true);
        return new StartupProcessObservation(
            new StartupProcessMetadata(
                lifetime, ParentPid: 1, Name: "app.exe",
                LifetimeCpuUs: 10, LifetimeImageLoadCount: 1),
            new StartupWindow(
                process, bounds, RequestedEndUs: bounds.EndUs,
                TraceDurationUs: bounds.EndUs, ProcessStartObserved: true,
                ProcessEndObserved: true, Status: "Complete", Code: null));
    }
}
```

The pure `Project` seam accepts interval enumerables, so these tests never require an ETL reader or an all-trace interval list.

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupMetricsAccumulatorTests"`

Expected: compilation fails because the trace reader and startup metrics projector do not exist.

- [ ] **Step 3: Extract one streaming scheduler trace reader and keep WaitAnalysis on it**

Use these exact contracts:

```csharp
internal interface ISchedulerIntervalSink
{
    void OnRunning(in RunningInterval interval);
    void OnBlocked(in BlockedInterval interval);
}

internal sealed record SchedulerStreamSummary(
    SchedulerIntervalResult Completion,
    int IdentityDiagnosticCount,
    IReadOnlyList<IdentityDiagnostic> DiagnosticSample);

internal static class SchedulerIntervalTraceReader
{
    public const int MaxDiagnosticSample = 32;

    public static SchedulerStreamSummary Read(
        TraceLog trace,
        TraceIdentityIndex identities,
        IReadOnlyList<ISchedulerIntervalSink> sinks);
}
```

Move the post-Child-2 kernel CSwitch/thread-stop adapter out of `WaitAnalysis` into `Read`. For each event, resolve both sides to `ThreadInstanceKey` through the shared identity index and call the Child 2 accumulator even when neither side belongs to a future query. Immediately fan every non-null `ClosedSchedulerIntervals.Running`/`.Blocked` and every interval returned by `.Stop` to each sink; after `.Complete(identities.TraceEndUs)`, fan out `Completion.ClosedAtTraceEnd`. Keep only Child 2's active scheduler dictionaries, sink aggregates, a total diagnostic count, and the first 32 identity diagnostics. Never retain an all-trace interval list and never create a `ThreadInstanceKey` from PID/TID alone.

Change `WaitAnalysis.Analyze` to instantiate its existing aggregation as an `ISchedulerIntervalSink`, call `Read`, and build the response from that sink. Its pure `Project` test seam remains enumerable-based. This makes public wait analysis and startup analysis consume the same interval producer without allocating one object list per closed scheduler interval.

- [ ] **Step 4: Implement startup metric projection**

```csharp
internal sealed record StartupSchedulerMetrics(
    long StartupCpuUs,
    long StartupBlockedUs,
    IReadOnlyDictionary<string, long> BlockedUsByReason,
    int RunningIntervalCount,
    int BlockedIntervalCount);

internal sealed class StartupMetricsAccumulator : ISchedulerIntervalSink
{
    public StartupMetricsAccumulator(IReadOnlyList<StartupProcessObservation> processes);
    public void OnRunning(in RunningInterval interval);
    public void OnBlocked(in BlockedInterval interval);
    public IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Complete();

    public static IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Project(
        IReadOnlyList<StartupProcessObservation> processes,
        IEnumerable<RunningInterval> running,
        IEnumerable<BlockedInterval> blocked);
}
```

The constructor pre-indexes observations by `ProcessInstanceKey` and allocates one bounded aggregate per eligible process; it does not index timestamps or retain intervals. For a running or blocked callback, select only the observation whose `Process == interval.Thread.Process`, charge `observation.Window.Bounds.IntersectDurationUs(interval.StartUs,interval.EndUs)`, and skip zero overlap. Group blocked overlap by `BlockedInterval.WaitReason` using ordinal string comparison. `Complete` publishes immutable aggregates. The static `Project` method creates the same sink, feeds the supplied enumerables, and completes it for unit tests. Do not read `TraceProcess.CPUMSec`, `ProcessRow.WallUs`, `ProcessRow.CpuUs`, or `ProcessRow.WaitRatio` here.

- [ ] **Step 5: Run focused and Child 2 regression tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupMetricsAccumulatorTests|FullyQualifiedName~SchedulerIntervalAccumulatorTests|FullyQualifiedName~WaitAnalysisTests"
```

Expected: overlap clipping, PID/TID reuse, unmatched interval metadata, and existing wait totals pass through the one shared scheduler stream.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Analyzers/SchedulerIntervalTraceReader.cs src/WprMcp/Analyzers/StartupMetricsAccumulator.cs src/WprMcp/Analyzers/WaitAnalysis.cs tests/WprMcp.Tests/StartupMetricsAccumulatorTests.cs
git commit -m "feat(startup): project shared scheduler intervals"
```

### Task 3: Stream image loads into bounded process-instance startup buckets

**Files:**
- Create: `src/WprMcp/Analyzers/StartupImageLoadAnalysis.cs`
- Create: `tests/WprMcp.Tests/StartupImageLoadAnalysisTests.cs`

**Interfaces:**
- Consumes: Child 1 `TraceIdentityIndex.Processes.Resolve`, Task 1 `StartupProcessObservation`, and validated `topImageLoads`.
- Produces: streaming `StartupImageLoadAccumulator`, `StartupImageLoadAnalysis.Collect`, and bounded `StartupImageLoadBucket` values keyed by `ProcessInstanceKey`.

- [ ] **Step 1: Write the failing instance/window isolation tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class StartupImageLoadAnalysisTests
{
    [Fact]
    public void Project_UsesOnlySameInstanceAndHalfOpenStartupWindow()
    {
        var early = new ProcessInstanceKey(20, 100);
        var late = new ProcessInstanceKey(20, 300);
        var observation = Observation(early, new TimeWindow(100, 200));
        var events = new[]
        {
            new StartupImageLoadEvent(early, 120, "first.dll", 10),
            new StartupImageLoadEvent(early, 199, "last.dll", 20),
            new StartupImageLoadEvent(early, 200, "at-end.dll", 30),
            new StartupImageLoadEvent(late, 150, "wrong-generation.dll", 40),
        };

        var bucket = StartupImageLoadAnalysis.Project(events, [observation], maxRowsPerProcess: 10)[early];
        var rows = bucket.FirstLoads;

        Assert.Equal(["first.dll", "last.dll"], rows.Select(row => row.FileName));
        Assert.Equal([20L, 99L], rows.Select(row => row.TimeFromProcessStartUs));
        Assert.Null(rows[0].GapFromPrevUs);
        Assert.Equal(79, rows[1].GapFromPrevUs);
        Assert.Equal(2L, bucket.TotalAvailable);
        Assert.False(bucket.HasMore);
    }

    [Fact]
    public void Project_NoMatchingLoadReturnsAnEmptyInstanceBucket()
    {
        var process = new ProcessInstanceKey(20, 100);
        var bucket = StartupImageLoadAnalysis.Project(
            Array.Empty<StartupImageLoadEvent>(),
            [Observation(process, new TimeWindow(100, 200))],
            maxRowsPerProcess: 10)[process];

        Assert.Equal(0L, bucket.TotalAvailable);
        Assert.Empty(bucket.FirstLoads);
        Assert.False(bucket.HasMore);
    }

    [Fact]
    public void Project_CountsAllButRetainsOnlyBoundedEarliestRows()
    {
        var process = new ProcessInstanceKey(20, 100);
        var observation = Observation(process, new TimeWindow(100, 200));
        var events = Enumerable.Range(0, 5)
            .Select(index => new StartupImageLoadEvent(
                process, 110 + index, $"{index}.dll", ImageSize: 1));

        var bucket = StartupImageLoadAnalysis.Project(
            events, [observation], maxRowsPerProcess: 2)[process];

        Assert.Equal(5L, bucket.TotalAvailable);
        Assert.Equal(["0.dll", "1.dll"], bucket.FirstLoads.Select(row => row.FileName));
        Assert.True(bucket.HasMore);
    }

    private static StartupProcessObservation Observation(
        ProcessInstanceKey process,
        TimeWindow bounds)
    {
        var lifetime = new ProcessLifetime(
            process, EndUs: bounds.EndUs, StartObserved: true, EndObserved: true);
        return new StartupProcessObservation(
            new StartupProcessMetadata(
                lifetime, ParentPid: 1, Name: "app.exe",
                LifetimeCpuUs: 10, LifetimeImageLoadCount: 1),
            new StartupWindow(
                process, bounds, RequestedEndUs: bounds.EndUs,
                TraceDurationUs: bounds.EndUs, ProcessStartObserved: true,
                ProcessEndObserved: true, Status: "Complete", Code: null));
    }
}
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupImageLoadAnalysisTests"`

Expected: compilation fails because the instance-resolved image-load analyzer is absent.

- [ ] **Step 3: Implement instance resolution and projection**

```csharp
internal readonly record struct StartupImageLoadEvent(
    ProcessInstanceKey Process,
    long TimeUs,
    string FileName,
    long ImageSize);

internal sealed record StartupImageLoadBucket(
    long TotalAvailable,
    IReadOnlyList<ImageLoadRow> FirstLoads,
    bool HasMore);

internal sealed record StartupImageLoadResult(
    IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> ByProcess,
    long UnresolvedProcessInstanceCount,
    long AmbiguousProcessInstanceCount);

internal sealed class StartupImageLoadAccumulator
{
    public StartupImageLoadAccumulator(
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess);
    public void OnImageLoad(in StartupImageLoadEvent imageLoad);
    public IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> Complete();
}

internal static class StartupImageLoadAnalysis
{
    public static StartupImageLoadResult Collect(
        TraceLog trace,
        TraceIdentityIndex identities,
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess);

    internal static IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> Project(
        IEnumerable<StartupImageLoadEvent> events,
        IReadOnlyList<StartupProcessObservation> processes,
        int maxRowsPerProcess);
}
```

Validate `maxRowsPerProcess` with `Validation.RequireTop`. The accumulator pre-creates one bucket per bounded eligible process. `OnImageLoad` requires exact process-key equality and `Window.Bounds.ContainsPoint`, increments `TotalAvailable`, and retains only the earliest `maxRowsPerProcess` items ordered by `(TimeUs,FileName ordinal)`; insert into the bounded list and discard its largest item when necessary. `Complete` computes `TimeFromProcessStartUs` and `GapFromPrevUs` over retained chronological rows and sets `HasMore=TotalAvailable>FirstLoads.Count`.

`Collect` walks ImageLoad events once, normalizes each timestamp, resolves `identities.Processes.Resolve(data.ProcessID,timestampUs,processStartUs:null)`, and immediately feeds only `Resolved` events to the accumulator. It retains no all-event staging list. `Project` feeds its enumerable through the same accumulator for unit tests. Do not call `trace.Processes.FirstOrDefault`, bucket by PID, or reuse `ImageLoadAnalysis.ForPids` for startup evidence.

- [ ] **Step 4: Run focused and existing image-load tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~StartupImageLoadAnalysisTests|FullyQualifiedName~ImageLoadAnalysisTests"`

Expected: same-instance selection, half-open boundaries, chronological gaps, missing loads, duplicate PID lifetimes, and the existing non-startup image tools pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/StartupImageLoadAnalysis.cs tests/WprMcp.Tests/StartupImageLoadAnalysisTests.cs
git commit -m "feat(startup): scope image loads by process instance"
```

### Task 4: Rank startup candidates and plan bounded evidence windows

**Files:**
- Create: `src/WprMcp/Analyzers/SlowStartupProjection.cs`
- Create: `tests/WprMcp.Tests/SlowStartupProjectionTests.cs`

**Interfaces:**
- Consumes: `StartupProcessObservation`, `StartupSchedulerMetrics`, and process-instance image-load buckets.
- Produces: `SlowStartupProjection.Rank`, `SlowStartupCandidateData`, and `StartupEvidenceWindowPlan` used by `DiagnoseTools`.

- [ ] **Step 1: Write the failing ratio and deterministic-order tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class SlowStartupProjectionTests
{
    [Fact]
    public void Rank_UsesStartupMetricsAndRequiredTieBreakers()
    {
        var p1 = Observation(pid: 8, startUs: 200, endUs: 400, lifetimeCpuUs: 1);
        var p2 = Observation(pid: 9, startUs: 100, endUs: 300, lifetimeCpuUs: 9_999);
        var p3 = Observation(pid: 7, startUs: 100, endUs: 300, lifetimeCpuUs: 2);
        var metrics = Metrics(
            (p1.Process, cpuUs: 20L),
            (p2.Process, cpuUs: 20L),
            (p3.Process, cpuUs: 20L));

        var ranked = SlowStartupProjection.Rank(
            [p1, p2, p3], metrics, EmptyImages(p1, p2, p3),
            nameSubstring: null, minWaitRatio: 0, maxCandidates: 3);

        Assert.Equal([7, 9, 8], ranked.Select(row => row.Process.Pid));
        Assert.All(ranked, row => Assert.Equal(10.0, row.StartupWaitRatio));
    }

    [Fact]
    public void Rank_ZeroStartupCpuHasNullRatioAndLifetimeValuesCannotRescueIt()
    {
        var process = Observation(pid: 8, startUs: 100, endUs: 200, lifetimeCpuUs: 1_000_000);
        var ranked = SlowStartupProjection.Rank(
            [process], Metrics((process.Process, cpuUs: 0L)), EmptyImages(process),
            nameSubstring: null, minWaitRatio: 0, maxCandidates: 5);

        Assert.Empty(ranked);
        Assert.Null(SlowStartupProjection.StartupWaitRatio(100, 0));
    }

    [Fact]
    public void EvidencePlan_FirstImageChildIsHalfOpenAndInsideParent()
    {
        var candidate = Candidate(pid: 5, processStartUs: 100, startupEndUs: 500,
            firstImageLoadUs: 350);

        var plan = SlowStartupProjection.PlanEvidence(candidate, slowFirstImageLoadThresholdUs: 200);

        Assert.Equal(new TimeWindow(100, 500), plan.ParentWindow);
        Assert.Equal(new TimeWindow(100, 350), plan.FirstImageChildWindow);
        Assert.True(plan.ParentWindow.ContainsPoint(plan.FirstImageChildWindow!.StartUs));
        Assert.True(plan.FirstImageChildWindow.EndUs <= plan.ParentWindow.EndUs);
    }

    [Fact]
    public void Rank_PostWindowSchedulerAndImageActivityCannotChangeResult()
    {
        var process = Observation(pid: 8, startUs: 100, endUs: 200, lifetimeCpuUs: 1);
        var thread = new ThreadInstanceKey(process.Process, 4, 1);
        var baselineMetrics = StartupMetricsAccumulator.Project(
            [process], [new RunningInterval(thread, 110, 130, 0)], []);
        var noisyMetrics = StartupMetricsAccumulator.Project(
            [process],
            [new RunningInterval(thread, 110, 130, 0), new RunningInterval(thread, 300, 900, 0)],
            [new BlockedInterval(thread, 400, 800, "Executive")]);
        var baselineImages = StartupImageLoadAnalysis.Project(
            [new StartupImageLoadEvent(process.Process, 120, "first.dll", 1)],
            [process], maxRowsPerProcess: 10);
        var noisyImages = StartupImageLoadAnalysis.Project(
            [
                new StartupImageLoadEvent(process.Process, 120, "first.dll", 1),
                new StartupImageLoadEvent(process.Process, 700, "late.dll", 1),
            ],
            [process], maxRowsPerProcess: 10);

        var baseline = SlowStartupProjection.Rank(
            [process], baselineMetrics, baselineImages, null, 0, 1);
        var noisy = SlowStartupProjection.Rank(
            [process], noisyMetrics, noisyImages, null, 0, 1);

        Assert.Equal(baseline, noisy);
    }

    private static StartupProcessObservation Observation(
        int pid, long startUs, long endUs, long lifetimeCpuUs)
    {
        var process = new ProcessInstanceKey(pid, startUs);
        var lifetime = new ProcessLifetime(
            process, EndUs: endUs, StartObserved: true, EndObserved: true);
        return new StartupProcessObservation(
            new StartupProcessMetadata(
                lifetime, ParentPid: 1, Name: $"p{pid}.exe",
                LifetimeCpuUs: lifetimeCpuUs, LifetimeImageLoadCount: 99),
            new StartupWindow(
                process, new TimeWindow(startUs, endUs), RequestedEndUs: endUs,
                TraceDurationUs: endUs, ProcessStartObserved: true,
                ProcessEndObserved: true, Status: "Complete", Code: null));
    }

    private static IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> Metrics(
        params (ProcessInstanceKey Process, long cpuUs)[] values) =>
        values.ToDictionary(
            item => item.Process,
            item => new StartupSchedulerMetrics(
                item.cpuUs, StartupBlockedUs: 0,
                new Dictionary<string, long>(StringComparer.Ordinal),
                RunningIntervalCount: 0, BlockedIntervalCount: 0));

    private static IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> EmptyImages(
        params StartupProcessObservation[] values) =>
        values.ToDictionary(
            item => item.Process,
            _ => new StartupImageLoadBucket(
                TotalAvailable: 0,
                FirstLoads: Array.Empty<ImageLoadRow>(),
                HasMore: false));

    private static SlowStartupCandidateData Candidate(
        int pid, long processStartUs, long startupEndUs, long firstImageLoadUs)
    {
        var observation = Observation(pid, processStartUs, startupEndUs, lifetimeCpuUs: 1);
        return new SlowStartupCandidateData(
            observation.Process,
            observation.Metadata.ParentPid,
            observation.Metadata.Name,
            observation.Window,
            ObservedStartupWallUs: observation.Window.Bounds.DurationUs,
            StartupCpuUs: 20,
            StartupBlockedUs: 0,
            StartupWaitRatio: observation.Window.Bounds.DurationUs / 20.0,
            new Dictionary<string, long>(StringComparer.Ordinal),
            StartupImageLoadCount: 1,
            StartupImageLoadsHasMore: false,
            [new ImageLoadRow(firstImageLoadUs, firstImageLoadUs - processStartUs,
                "first.dll", ImageSize: 1, GapFromPrevUs: null)],
            observation.LifetimeWallUs,
            observation.Metadata.LifetimeCpuUs,
            observation.LifetimeWaitRatio,
            observation.Metadata.LifetimeImageLoadCount);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~SlowStartupProjectionTests"`

Expected: compilation fails because the startup-only ranking and evidence plan do not exist.

- [ ] **Step 3: Implement exact ranking and evidence-plan contracts**

```csharp
internal sealed record SlowStartupCandidateData(
    ProcessInstanceKey Process,
    int ParentPid,
    string Name,
    StartupWindow StartupWindow,
    long ObservedStartupWallUs,
    long StartupCpuUs,
    long StartupBlockedUs,
    double? StartupWaitRatio,
    IReadOnlyDictionary<string, long> StartupBlockedUsByReason,
    long StartupImageLoadCount,
    bool StartupImageLoadsHasMore,
    IReadOnlyList<ImageLoadRow> StartupImageLoads,
    long LifetimeWallUs,
    long LifetimeCpuUs,
    double? LifetimeWaitRatio,
    int LifetimeImageLoadCount);

internal sealed record StartupEvidenceWindowPlan(
    string EvidenceIdPrefix,
    ProcessInstanceKey Process,
    TimeWindow ParentWindow,
    TimeWindow? FirstImageChildWindow,
    string? NotConcludedCode);

internal static class SlowStartupProjection
{
    public static double? StartupWaitRatio(long observedStartupWallUs, long startupCpuUs) =>
        startupCpuUs == 0 ? null : observedStartupWallUs / (double)startupCpuUs;

    public static IReadOnlyList<SlowStartupCandidateData> Rank(
        IReadOnlyList<StartupProcessObservation> processes,
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler,
        IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> imageLoads,
        string? nameSubstring,
        double minWaitRatio,
        int maxCandidates);

    public static StartupEvidenceWindowPlan PlanEvidence(
        SlowStartupCandidateData candidate,
        long slowFirstImageLoadThresholdUs);
}
```

`Rank` sets `ObservedStartupWallUs=StartupWindow.Bounds.DurationUs`, derives the ratio only from that wall value and `StartupSchedulerMetrics.StartupCpuUs`, copies `StartupImageLoadBucket.TotalAvailable`/`HasMore` and its bounded `FirstLoads`, applies the case-insensitive name filter, rejects null or below-threshold ratios, and orders by `StartupWaitRatio` descending, `ObservedStartupWallUs` descending, `Process.StartUs` ascending, and `Process.Pid` ascending before taking `maxCandidates`. `LifetimeWallUs`, `LifetimeCpuUs`, `LifetimeWaitRatio`, and `LifetimeImageLoadCount` are copied only after ranking keys are computed.

`PlanEvidence` uses `EvidenceIdPrefix=$"slow-startup.pid-{Pid}.start-{StartUs}"`. With no image load, return `NotConcludedCode="first_image_load_not_observed"`. With a first load whose positive offset meets the threshold, create exactly `new TimeWindow(Process.StartUs,firstLoad.TimeUs)`; because Task 3 already clips image loads, its end cannot exceed the parent end. Otherwise leave the child null without inventing a one-microsecond interval.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~SlowStartupProjectionTests|FullyQualifiedName~StartupMetricsAccumulatorTests|FullyQualifiedName~StartupImageLoadAnalysisTests"`

Expected: ratio nullability, threshold, all four tie-breakers, lifetime-value exclusion, post-window exclusion, duplicate PID identifiers, and contained child windows pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/SlowStartupProjection.cs tests/WprMcp.Tests/SlowStartupProjectionTests.cs
git commit -m "feat(startup): rank startup-scoped candidates"
```

### Task 5: Rebuild `diagnose_slow_startup` on one process-instance window

**Files:**
- Modify: `src/WprMcp/Tools/DiagnoseTools.cs`
- Create: `src/WprMcp/Output/StartupRecords.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4, Child 2 `ThreadAnalysisScope`, `WaitAnalysis.Project`, and the Child 2 scope overload of `CpuAnalysis.TopFunctions`.
- Produces: startup-scoped `DiagnoseSlowStartupResponse`, process-instance evidence IDs, and parent/child call provenance ready for Child 5 envelope mapping.

- [ ] **Step 1: Replace lifetime-based composite tests with startup-window assertions**

```csharp
[Fact]
public void DiagnoseSlowStartup_PrimaryCallsShareCandidateProcessAndWindow()
{
    var tools = new DiagnoseTools(new TraceCache(capacity: 2));
    var response = tools.DiagnoseSlowStartup(
        "fixtures/small_cpu.etl", minWaitRatio: 0, maxCandidates: 3,
        startupWindowUs: 123_456);

    foreach (var candidate in response.Candidates)
    {
        var calls = response.ExecutedToolCalls!
            .Where(call => call.ProcessStartUs == candidate.ProcessStartUs &&
                           call.Pid == candidate.Pid &&
                           call.ParentStartUs is null)
            .ToList();

        Assert.Contains(calls, call => call.ToolName == "startup_candidate_projection");
        Assert.Contains(calls, call => call.ToolName == "wait_analysis");
        Assert.Contains(calls, call => call.ToolName == "image_load_timing");
        Assert.Contains(calls, call => call.ToolName == "cpu_top_functions");
        Assert.All(calls, call =>
        {
            Assert.Equal(candidate.ProcessStartUs, call.StartUs);
            Assert.Equal(candidate.StartupEndUs, call.EndUs);
        });
    }
}

[Fact]
public void DiagnoseSlowStartup_FirstImageChildRecordsParentAndDoesNotAddOneMicrosecond()
{
    var tools = new DiagnoseTools(new TraceCache(capacity: 2));
    var response = tools.DiagnoseSlowStartup(
        "fixtures/small_cpu.etl", nameSubstring: "taskhostw",
        minWaitRatio: 0, maxCandidates: 1,
        slowFirstImageLoadThresholdUs: 0, topWindowEvidence: 3);

    foreach (var gap in response.FirstImageLoadGapEvidence ?? Array.Empty<StartupGapEvidenceRow>())
    {
        Assert.Equal(gap.ProcessStartUs, gap.ChildStartUs);
        Assert.Equal(gap.FirstImageLoadTimeUs, gap.ChildEndUs);
        Assert.True(gap.ChildStartUs >= gap.ParentWindow.StartUs);
        Assert.True(gap.ChildEndUs <= gap.ParentWindow.EndUs);
        var call = Assert.Single(response.ExecutedToolCalls!, item => item.CallId == gap.CallId);
        Assert.Equal(gap.ParentWindow.StartUs, call.ParentStartUs);
        Assert.Equal(gap.ParentWindow.EndUs, call.ParentEndUs);
    }
}
```

Replace the old assertions that expect one full-trace `wait_analysis`, lifetime-ranked `WaitRatio`, a trace-resident candidate, PID-only call IDs, or `FirstImageLoadTimeUs + 1`.

- [ ] **Step 2: Run the composite tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~DiagnoseToolsTests&Name~DiagnoseSlowStartup"`

Expected: failures show full-trace wait provenance, PID-only IDs, lifetime candidate fields, and the one-microsecond child-window extension.

- [ ] **Step 3: Define startup output and provenance records**

Move `SlowStartupCandidate`, `StartupGapEvidenceRow`, and `DiagnoseSlowStartupResponse` from `Records.cs` to `StartupRecords.cs` and use these exact records:

```csharp
namespace WprMcp.Output;

public sealed record StartupWindowProvenance(
    int Pid,
    long ProcessStartUs,
    long StartUs,
    long EndUs,
    long RequestedEndUs,
    long TraceDurationUs,
    bool ProcessStartObserved,
    bool ProcessEndObserved,
    string Status,
    string? Code);

public sealed record SlowStartupCandidate(
    string EvidenceId,
    int Pid,
    long ProcessStartUs,
    int ParentPid,
    string Name,
    long StartupEndUs,
    long ObservedStartupWallUs,
    long StartupCpuUs,
    long StartupBlockedUs,
    double? StartupWaitRatio,
    long StartupImageLoadCount,
    bool StartupImageLoadsHasMore,
    IReadOnlyList<WaitReasonBucket> TopStartupWaitReasons,
    IReadOnlyList<ImageLoadRow> FirstStartupImageLoads,
    IReadOnlyList<CpuFunctionRow>? TopStartupCpuFunctions,
    StartupWindowProvenance Window,
    long LifetimeWallUs,
    long LifetimeCpuUs,
    double? LifetimeWaitRatio,
    int LifetimeImageLoadCount);

public sealed record StartupGapEvidenceRow(
    string EvidenceId,
    string CallId,
    int Pid,
    long ProcessStartUs,
    string ProcessName,
    long FirstImageLoadTimeUs,
    long FirstImageLoadOffsetUs,
    StartupWindowProvenance ParentWindow,
    long ChildStartUs,
    long ChildEndUs,
    DiagnoseWindowResponse Window);

public sealed record StartupProcessExclusionRow(
    string EvidenceId,
    int Pid,
    long ProcessStartUs,
    string ProcessName,
    string Code);

public sealed record StartupDiscoverySummary(
    int EligibleStartupInstanceCount,
    int ConsideredStartupInstanceCount,
    bool CandidateInputHasMore,
    int ExcludedUnobservedStartCount,
    int OtherExcludedStartupInstanceCount,
    IReadOnlyList<StartupProcessExclusionRow> ExcludedSamples,
    bool ExcludedSamplesHasMore);

public sealed record DiagnoseSlowStartupResponse(
    IReadOnlyList<SlowStartupCandidate> Candidates,
    [property: Obsolete("Use structured Evidence, NotConcluded, ExecutedToolCalls, and NextTools instead.")]
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompositeEvidence>? Evidence = null,
    IReadOnlyList<CompositeNotConcluded>? NotConcluded = null,
    IReadOnlyList<CompositeToolCall>? ExecutedToolCalls = null,
    IReadOnlyList<CompositeNextTool>? NextTools = null,
    IReadOnlyList<StartupGapEvidenceRow>? FirstImageLoadGapEvidence = null,
    StartupDiscoverySummary? Discovery = null);
```

Append these optional properties to the existing composite records so other call sites remain source-compatible:

```csharp
// CompositeToolCall, after OrderBy
long? ProcessStartUs = null, long? ParentStartUs = null, long? ParentEndUs = null

// CompositeEvidence, after Frames
long? ProcessStartUs = null

// CompositeNotConcluded, after ThresholdPct
long? ProcessStartUs = null, string? EvidenceId = null
```

- [ ] **Step 4: Replace `DiagnoseSlowStartup` orchestration**

Before loading the trace, call `Validation.RequireTop(topImageLoads)` and `Validation.RequireTop(topCpu)` in addition to the existing `maxCandidates`, ratio, startup-window, first-image-threshold, window-evidence, and maximum-window checks.

After argument validation, execute this order exactly:

1. Load the trace, get `TraceIdentityIndex.For(trace)`, and call `StartupProcessCatalog.FromTrace(trace,identities,startupWindowUs,nameSubstring,Validation.MaxCollectionItems)`. Candidate metrics and ranking use only its bounded `Eligible` collection. Copy total/retained counts, `TotalUnobservedStartCount`, `TotalOtherExcludedCount`, and both `HasMore` flags into `StartupDiscoverySummary`; candidate-input truncation is section metadata, not `Partial`.
2. In ordinary discovery (`nameSubstring` null/empty), add one aggregate `CompositeNotConcluded(Code="startup_starts_not_observed",Pid=null,MetricName="excludedUnobservedStartCount",MetricValue=TotalUnobservedStartCount,Unit="process_instances")` when the count is positive; return at most `Validation.MaxCollectionItems` `ExcludedSamples`. For an explicit name target, add instance-level `startup_start_not_observed` rows only for the bounded matching exclusion samples, each with PID, process start, and instance-specific evidence ID. In both modes, `ExcludedSamplesHasMore` discloses omitted samples and no unbounded per-instance list is built.
3. Create one `StartupMetricsAccumulator` for all eligible startup windows, stream the trace once with `SchedulerIntervalTraceReader.Read(trace,identities,[startupMetrics])`, complete its bounded aggregates, stream startup image loads once with `StartupImageLoadAnalysis.Collect(trace,identities,catalog.Eligible,maxRowsPerProcess:topImageLoads)`, and pass that result's `.ByProcess` map to `SlowStartupProjection.Rank`. Do not materialize scheduler intervals or image-load events.
4. For each returned candidate, resolve `ThreadAnalysisScope` with `(pid,processStartUs)` and the exact parent window. Build wait reasons from the already-collected blocked intervals and call the Child 2 CPU scope overload; no primary evidence path may use the old PID-only/full-trace overloads.
5. Add four primary `CompositeToolCall` rows named `startup_candidate_projection`, `wait_analysis`, `image_load_timing`, and `cpu_top_functions`, all with the candidate PID, process start, and identical `StartUs`/`EndUs`. Mark `startup_candidate_projection` non-replayable because it is internal. Mark `image_load_timing` non-replayable with `InternalNote="Instance-scoped startup projection; the public image_load_timing surface has no processStartUs/window selector."`. The Child 2 process-instance overloads make `wait_analysis` and `cpu_top_functions` replayable.
6. Use `$"slow-startup.pid-{Pid}.start-{ProcessStartUs}"` as the prefix for candidate, evidence, call, and not-concluded IDs.
7. If no instance-resolved image load exists, append `first_image_load_not_observed`. If a positive first-load offset meets the threshold, call `BuildDiagnoseWindow` with the exact child `[ProcessStartUs,FirstImageLoadUs)`, copy process-start and parent bounds onto its parent call record, and emit `StartupGapEvidenceRow`. Never use `+1`.
8. Copy `StartupWindow.Status` and `.Code` to `StartupWindowProvenance`. A trace-end truncation remains a candidate with `Partial/startup_window_truncated`; an observed early exit remains `Complete`.

Convert blocked reasons to `WaitReasonBucket` only after summing all complete startup-window intervals, sort by `BlockedUs` descending then reason ordinal, and take five. Pass the exact `ThreadAnalysisScope` to CPU stacks so later reuse of the PID cannot enter. Prefix any warning with the instance evidence ID, not PID alone.

- [ ] **Step 5: Run composite, CPU, wait, and image tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~DiagnoseToolsTests|FullyQualifiedName~Startup|FullyQualifiedName~WaitAnalysisTests|FullyQualifiedName~CpuAnalysisTests|FullyQualifiedName~ImageLoadAnalysisTests"
```

Expected: all primary calls share one process/window, resident processes are omitted, instance IDs are distinct, child calls record both windows, and existing non-startup composites remain compatible.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Tools/DiagnoseTools.cs src/WprMcp/Output/StartupRecords.cs src/WprMcp/Output/Records.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs
git commit -m "fix(diagnose): scope slow startup evidence"
```

### Task 6: Add the complete startup-window acceptance gate

**Files:**
- Create: `tests/WprMcp.Tests/SlowStartupWindowAcceptanceTests.cs`
- Modify: `tests/WprMcp.Tests/DiagnoseToolsTests.cs`
- Modify: `tests/WprMcp.Tests/TimeWindowSemanticsTests.cs`

**Interfaces:**
- Consumes: all startup contracts and the rebuilt composite from Tasks 1–5.
- Produces: one executable gate covering candidate selection, provenance, truncation, image evidence, and PID reuse.

- [ ] **Step 1: Write the cross-component acceptance tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tests;

public sealed class SlowStartupWindowAcceptanceTests
{
    [Fact]
    public void LaterLifetimeAndPostWindowSignalsCannotChangeEarlierCandidate()
    {
        var early = Metadata(33, 100, 250, startObserved: true, endObserved: true, "early.exe");
        var reused = Metadata(33, 300, 500, startObserved: true, endObserved: true, "later.exe");
        var catalog = StartupProcessCatalog.Build(
            [early, reused], startupWindowUs: 100, traceDurationUs: 500,
            nameSubstring: null, maxCollectionItems: Validation.MaxCollectionItems);
        var earlyThread = new ThreadInstanceKey(early.Lifetime.Key, 4, 1);
        var laterThread = new ThreadInstanceKey(reused.Lifetime.Key, 4, 1);
        var baselineMetrics = StartupMetricsAccumulator.Project(
            catalog.Eligible,
            [new RunningInterval(earlyThread, 110, 130, 0)],
            Array.Empty<BlockedInterval>());
        var noisyMetrics = StartupMetricsAccumulator.Project(
            catalog.Eligible,
            [
                new RunningInterval(earlyThread, 110, 130, 0),
                new RunningInterval(earlyThread, 220, 290, 0),
                new RunningInterval(laterThread, 320, 340, 0),
            ],
            [new BlockedInterval(laterThread, 350, 390, "Executive")]);
        var baselineImages = StartupImageLoadAnalysis.Project(
            [new StartupImageLoadEvent(early.Lifetime.Key, 120, "first.dll", 1)],
            catalog.Eligible, maxRowsPerProcess: 10);
        var noisyImages = StartupImageLoadAnalysis.Project(
            [
                new StartupImageLoadEvent(early.Lifetime.Key, 120, "first.dll", 1),
                new StartupImageLoadEvent(early.Lifetime.Key, 240, "post-window.dll", 1),
                new StartupImageLoadEvent(reused.Lifetime.Key, 330, "later-life.dll", 1),
            ],
            catalog.Eligible, maxRowsPerProcess: 10);

        var baseline = SlowStartupProjection.Rank(
            catalog.Eligible, baselineMetrics, baselineImages, null, 0, 2)
            .Single(row => row.Process == early.Lifetime.Key);
        var noisy = SlowStartupProjection.Rank(
            catalog.Eligible, noisyMetrics, noisyImages, null, 0, 2)
            .Single(row => row.Process == early.Lifetime.Key);

        Assert.Equal(baseline, noisy);
        Assert.DoesNotContain(noisy.StartupImageLoads, load => load.TimeUs >= noisy.StartupWindow.Bounds.EndUs);
    }

    [Fact]
    public void DuplicatePidLifetimesHaveDistinctEvidenceIds()
    {
        var first = Observation(Metadata(44, 100, 200, true, true, "one.exe"), 100, 200);
        var second = Observation(Metadata(44, 300, 400, true, true, "two.exe"), 300, 400);
        var scheduler = new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
        {
            [first.Process] = Scheduler(cpuUs: 10),
            [second.Process] = Scheduler(cpuUs: 10),
        };
        var images = new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
        {
            [first.Process] = EmptyImages(),
            [second.Process] = EmptyImages(),
        };
        var candidates = SlowStartupProjection.Rank(
            [first, second], scheduler, images, null, 0, 2);
        var ids = candidates
            .Select(candidate => SlowStartupProjection.PlanEvidence(candidate, 1).EvidenceIdPrefix)
            .ToList();

        Assert.Equal(2, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("start-100", ids[0] + ids[1]);
        Assert.Contains("start-300", ids[0] + ids[1]);
    }

    [Fact]
    public void PreExistingProcessAndMissingImageEachYieldOneStructuredResult()
    {
        var resident = Metadata(70, 0, 500, false, false, "target-app-resident.exe");
        var observed = Metadata(71, 100, 300, true, true, "target-app-no-image.exe");
        var catalog = StartupProcessCatalog.Build(
            [resident, observed], startupWindowUs: 100, traceDurationUs: 500,
            nameSubstring: "target-app", maxCollectionItems: Validation.MaxCollectionItems);

        Assert.DoesNotContain(catalog.Eligible, row => row.Process == resident.Lifetime.Key);
        var exclusion = Assert.Single(catalog.Excluded);
        Assert.Equal("startup_start_not_observed", exclusion.Code);
        var candidate = Assert.Single(SlowStartupProjection.Rank(
            catalog.Eligible,
            new Dictionary<ProcessInstanceKey, StartupSchedulerMetrics>
            {
                [observed.Lifetime.Key] = Scheduler(cpuUs: 10),
            },
            new Dictionary<ProcessInstanceKey, StartupImageLoadBucket>
            {
                [observed.Lifetime.Key] = EmptyImages(),
            },
            nameSubstring: "target-app", minWaitRatio: 0, maxCandidates: 1));
        Assert.Equal("first_image_load_not_observed",
            SlowStartupProjection.PlanEvidence(candidate, 1).NotConcludedCode);
    }

    [Fact]
    public void OrdinaryDiscoveryBoundsUnobservedStartSamples()
    {
        var count = Validation.MaxCollectionItems + 25;
        var metadata = Enumerable.Range(1, count)
            .Select(pid => Metadata(pid, pid * 10L, pid * 10L + 5,
                startObserved: false, endObserved: false, $"resident-{pid}.exe"));
        var catalog = StartupProcessCatalog.Build(
            metadata, startupWindowUs: 5, traceDurationUs: 100_000,
            nameSubstring: null, maxCollectionItems: Validation.MaxCollectionItems);

        Assert.Equal(count, catalog.TotalUnobservedStartCount);
        Assert.Equal(Validation.MaxCollectionItems, catalog.Excluded.Count);
        Assert.True(catalog.ExcludedHasMore);
    }

    [Fact]
    public void TraceEndOnlyIsPartialButObservedExitIsComplete()
    {
        var truncated = StartupWindow.Create(
            new ProcessLifetime(new ProcessInstanceKey(80, 900), 1_000, true, false),
            startupWindowUs: 500, traceDurationUs: 1_000);
        var exited = StartupWindow.Create(
            new ProcessLifetime(new ProcessInstanceKey(81, 800), 950, true, true),
            startupWindowUs: 500, traceDurationUs: 1_000);

        Assert.Equal("Partial", truncated.Status);
        Assert.Equal("startup_window_truncated", truncated.Code);
        Assert.Equal("Complete", exited.Status);
        Assert.Null(exited.Code);
    }

    private static StartupProcessMetadata Metadata(
        int pid, long startUs, long endUs, bool startObserved, bool endObserved, string name) =>
        new(
            new ProcessLifetime(new ProcessInstanceKey(pid, startUs), endUs,
                startObserved, endObserved),
            ParentPid: 1, Name: name, LifetimeCpuUs: 1, LifetimeImageLoadCount: 0);

    private static StartupProcessObservation Observation(
        StartupProcessMetadata metadata, long startUs, long endUs) =>
        new(
            metadata,
            new StartupWindow(
                metadata.Lifetime.Key, new TimeWindow(startUs, endUs),
                RequestedEndUs: endUs, TraceDurationUs: endUs,
                ProcessStartObserved: true, ProcessEndObserved: true,
                Status: "Complete", Code: null));

    private static StartupSchedulerMetrics Scheduler(long cpuUs) =>
        new(
            cpuUs,
            StartupBlockedUs: 0,
            new Dictionary<string, long>(StringComparer.Ordinal),
            RunningIntervalCount: 0,
            BlockedIntervalCount: 0);

    private static StartupImageLoadBucket EmptyImages() =>
        new(TotalAvailable: 0, FirstLoads: Array.Empty<ImageLoadRow>(), HasMore: false);
}
```

- [ ] **Step 2: Run the acceptance tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~SlowStartupWindowAcceptanceTests"`

Expected: any remaining PID-only bucket, lifetime ranking key, missing provenance field, or trace-end/observed-exit conflation fails a named acceptance case.

- [ ] **Step 3: Add fixture-level assertions and remove stale semantics**

In `DiagnoseToolsTests`, assert every returned candidate has `ProcessStartObserved=true`, every primary call has its process start and exact candidate bounds, every child call is contained by its parent, and every evidence ID is unique. Change the former trace-resident test so the resident process is absent from `Candidates` and has one `startup_start_not_observed` result. Change the former first-image test to expect child end equal to `FirstImageLoadTimeUs`.

In `TimeWindowSemanticsTests`, replace any `diagnose_slow_startup` exemption or lifetime-window assertion with reflection assertions for `ProcessStartUs`, `StartupEndUs`, `ObservedStartupWallUs`, `StartupCpuUs`, `StartupWaitRatio`, and `StartupWindowProvenance`. Assert the ambiguous fields `WallUs`, `CpuUs`, `WaitRatio`, and `ImageLoadCount` are absent from `SlowStartupCandidate`; only the `Lifetime*` auxiliary names remain.

- [ ] **Step 4: Run the Child 4 gate and full suite**

```powershell
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Startup|FullyQualifiedName~DiagnoseToolsTests|FullyQualifiedName~TimeWindowSemantics|FullyQualifiedName~SchedulerInterval|FullyQualifiedName~WaitAnalysis|FullyQualifiedName~CpuAnalysis"
dotnet test WprMcp.sln -c Release
```

Expected: both commands pass; post-window and later-lifetime signals cannot affect selection/evidence, all provenance windows agree, and other diagnose tools retain their behavior.

- [ ] **Step 5: Run a stale-pattern scan**

Run: `rg -n "ForPids\(trace, candidatePids\)|WaitRatio: c\.WaitRatio|WallUs: c\.WallUs|StartUs \+ startupWindowUs|FirstImageLoadTimeUs \+ 1|slow-startup\.pid-\{c\.Pid\}" src/WprMcp/Tools/DiagnoseTools.cs`

Expected: exit code 1 with no matches.

- [ ] **Step 6: Commit**

```powershell
git add tests/WprMcp.Tests/SlowStartupWindowAcceptanceTests.cs tests/WprMcp.Tests/DiagnoseToolsTests.cs tests/WprMcp.Tests/TimeWindowSemanticsTests.cs
git commit -m "test(startup): enforce one observed startup window"
```

## Child 4 Completion Gate

- Every candidate has a real observed ProcessStart and one exact checked startup window.
- Candidate wall, CPU, ratio, wait, and image-load counts come only from that window and process instance.
- Ranking is ratio descending, observed startup wall descending, process start ascending, then PID ascending; zero startup CPU produces null and lifetime values never participate.
- Pre-existing/rundown-only processes are omitted. Explicit name/instance targets receive bounded instance-level `startup_start_not_observed` rows; ordinary discovery receives one aggregate `startup_starts_not_observed` result plus bounded samples, counts, and `HasMore`.
- Trace-end-only truncation is `Partial/startup_window_truncated`; an observed early exit is a complete short lifetime.
- First image load is from the same process instance and inside the startup window; absence produces `first_image_load_not_observed`. Image-load totals count all matching events while returned rows are bounded and expose `HasMore`.
- First-image child evidence uses `[ProcessStartUs,FirstImageLoadUs)`, records parent and child bounds, and never extends beyond the parent.
- Duplicate PID lifetimes have distinct IDs containing process start, and later-lifetime signals cannot enter earlier evidence.
- Child 5 remains responsible for lifting startup provenance status/code into the final versioned `ToolEnvelope`.
