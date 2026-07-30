# GC and Duration Accounting Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Correct GC/pause association and give every paired-duration analyzer explicit full-interval and query-window-accounted durations without losing totals to TopN.

**Architecture:** Build every start/stop interval over the full trace with a typed pairing primitive, then project completed intervals through Child 1's half-open `TimeWindow`. GC wall intervals and suspend/restart pauses are paired independently by process/CLR identity and associated after the walk; SecurityScan, JIT, Finalizer, and CLR contention reuse the same accounting model while preserving domain-specific aggregation and stacks.

**Tech Stack:** The C# language version, TFM, TraceEvent version, and xUnit version selected by Child 11A (repository baseline at plan time: C# 12, .NET 8, TraceEvent 3.2.2, xUnit 2.5.3)

## Global Constraints

- Complete Child 11A and Child 1 before this plan; consume `TraceTime`, `TimeWindow`, `TimeWindowInput`, `ProcessInstanceKey`, `ThreadInstanceKey`, and `TraceIdentityIndex.For(TraceLog)` rather than recreating time or identity logic.
- Do not change target frameworks, NuGet packages, MCP SDK versions, WPR profiles, or assembly-level test parallelization.
- Treat every window as half-open `[StartUs,EndUs)` and every duration as integer microseconds.
- Pair over the full trace before applying PID, process-instance, or time-window projection.
- Never pair by raw PID or TID when a process or thread instance can be resolved; unresolved or ambiguous identity is counted as incomplete evidence.
- Keep existing MCP tool method signatures. Until Child 5 introduces `ToolEnvelope`, retain legacy duration fields as aliases of accounted duration and add the warning whose stable prefix is `time_semantics_v2`.
- Use `AccountingMode = "clipped_overlap_v2"` for every new full/accounted duration field.
- Compute complete totals before sorting or `.Take(top)`; TopN affects rows and `HasMore`, never totals or response completeness.
- Do not add the Child 5 wire envelope, response-version switch, or final legacy/v2 serializer in this child.

---

## File Structure

| Path | Action | Responsibility |
|---|---|---|
| `src/WprMcp/Core/DurationAccounting.cs` | Create | Project a completed interval into one half-open query window |
| `src/WprMcp/Analyzers/IntervalPairAccumulator.cs` | Create | Typed FIFO start/stop pairing over the full trace |
| `src/WprMcp/Analyzers/GcIntervalAccumulator.cs` | Create | Pair GC walls and pauses, then associate each pause once |
| `src/WprMcp/Output/DurationRecords.cs` | Create | Full/accounted DTOs for GC, JIT, SecurityScan, Finalizer, and contention |
| `src/WprMcp/Output/Records.cs` | Modify | Remove the duration DTO declarations moved to `DurationRecords.cs` |
| `src/WprMcp/Output/Warnings.cs` | Modify | Add the stable legacy time-semantics warning |
| `src/WprMcp/Analyzers/GcAnalysis.cs` | Modify | Adapt CLR events to process/CLR/GC identities and project GC results |
| `src/WprMcp/Analyzers/JitAnalysis.cs` | Modify | Pair all JIT intervals before clipping and TopN |
| `src/WprMcp/Analyzers/SecurityScanAnalysis.cs` | Modify | Use typed pairing and clipped scan duration totals |
| `src/WprMcp/Analyzers/FinalizerAnalysis.cs` | Modify | Pair finalizer batches before clipping |
| `src/WprMcp/Analyzers/ClrContentionStackAnalysis.cs` | Modify | Key contention by thread instance and charge clipped overlap to stacks |
| `src/WprMcp/Analyzers/EventPairAggregator.cs` | Delete | Superseded string-keyed, unaccounted pairing primitive |
| `tests/WprMcp.Tests/DurationAccountingTests.cs` | Create | Half-open interval and typed-pairing unit tests |
| `tests/WprMcp.Tests/GcIntervalAccumulatorTests.cs` | Create | GC/pause association acceptance sequences |
| `tests/WprMcp.Tests/DurationAnalyzerInvariantTests.cs` | Create | Cross-analyzer total, TopN, alias, and warning invariants |
| `tests/WprMcp.Tests/GcAnalysisTests.cs` | Modify | Trace adapter, no-event, and real GC fixture coverage |
| `tests/WprMcp.Tests/JitAnalysisTests.cs` | Modify | Window clipping and TopN coverage |
| `tests/WprMcp.Tests/SecurityScanAnalysisTests.cs` | Modify | Full/accounted scan projection coverage |
| `tests/WprMcp.Tests/FinalizerAnalysisTests.cs` | Modify | Finalizer overlap coverage |
| `tests/WprMcp.Tests/ClrContentionStackAnalysisTests.cs` | Modify | Thread-instance and clipped metric coverage |
| `tests/WprMcp.Tests/EventPairAggregatorTests.cs` | Delete | Superseded by typed pairing tests |

### Task 1: Add typed full-trace pairing and window accounting

**Files:**
- Create: `src/WprMcp/Core/DurationAccounting.cs`
- Create: `src/WprMcp/Analyzers/IntervalPairAccumulator.cs`
- Create: `tests/WprMcp.Tests/DurationAccountingTests.cs`

**Interfaces:**
- Consumes: `TimeWindow.IntersectDurationUs(long intervalStartUs, long intervalEndUs)` from Child 1.
- Produces: `IntervalPairAccumulator<TKey,TStart,TStop>`, `IntervalPairResult<TKey,TStart,TStop>`, `PairedInterval<TKey,TStart,TStop>`, and `DurationAccounting.Project`.

- [ ] **Step 1: Write the failing pairing and clipping tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class DurationAccountingTests
{
    [Fact]
    public void PairBeforeClip_LeftAndRightOverlapRetainFullDuration()
    {
        var pairs = new IntervalPairAccumulator<string, string, string>();
        pairs.AddStart("scan-1", 90, "start");
        pairs.AddStop("scan-1", 210, "stop");

        var pair = Assert.Single(pairs.Complete().Pairs);
        var projected = Assert.IsType<AccountedPairedInterval<string, string, string>>(
            DurationAccounting.Project(pair, new TimeWindow(100, 200)));

        Assert.Equal(120, projected.FullDurationUs);
        Assert.Equal(100, projected.AccountedDurationUs);
        Assert.Equal("clipped_overlap_v2", projected.AccountingMode);
    }

    [Fact]
    public void Project_TouchingOrDisjointIntervalReturnsNull()
    {
        var pair = new PairedInterval<int, string, string>(1, 20, 30, "s", "e");
        Assert.Null(DurationAccounting.Project(pair, new TimeWindow(0, 20)));
        Assert.Null(DurationAccounting.Project(pair, new TimeWindow(30, 40)));
    }

    [Fact]
    public void Complete_PairsRepeatedKeyFifoAndReportsInvalidAndUnmatched()
    {
        var pairs = new IntervalPairAccumulator<int, string, string>();
        pairs.AddStop(7, 5, "orphan-stop");
        pairs.AddStart(7, 10, "first");
        pairs.AddStart(7, 20, "second");
        pairs.AddStop(7, 15, "first-stop");
        pairs.AddStop(7, 19, "backward-stop");
        pairs.AddStart(8, 30, "orphan-start");

        var result = pairs.Complete();

        var pair = Assert.Single(result.Pairs);
        Assert.Equal(10, pair.StartUs);
        Assert.Equal(15, pair.EndUs);
        Assert.Single(result.InvalidIntervals);
        Assert.Single(result.UnmatchedStarts);
        Assert.Single(result.UnmatchedStops);
    }
}
```

- [ ] **Step 2: Run the focused test and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~DurationAccountingTests"`

Expected: compilation fails because the typed pair accumulator and duration accounting types do not exist.

- [ ] **Step 3: Implement the exact contracts**

Create these types; `Complete()` is idempotent, drains pending starts exactly once, and rejects subsequent `AddStart`/`AddStop` calls with `InvalidOperationException`.

```csharp
namespace WprMcp.Analyzers;

internal readonly record struct PendingIntervalStart<TKey, TStart>(
    TKey Key, long TimeUs, TStart Data) where TKey : notnull;

internal readonly record struct UnmatchedIntervalStop<TKey, TStop>(
    TKey Key, long TimeUs, TStop Data) where TKey : notnull;

internal readonly record struct InvalidPairedInterval<TKey, TStart, TStop>(
    TKey Key, long StartUs, long EndUs, TStart StartData, TStop StopData) where TKey : notnull;

internal readonly record struct PairedInterval<TKey, TStart, TStop>(
    TKey Key, long StartUs, long EndUs, TStart StartData, TStop StopData) where TKey : notnull
{
    public long FullDurationUs => checked(EndUs - StartUs);
}

internal sealed record IntervalPairResult<TKey, TStart, TStop>(
    IReadOnlyList<PairedInterval<TKey, TStart, TStop>> Pairs,
    IReadOnlyList<PendingIntervalStart<TKey, TStart>> UnmatchedStarts,
    IReadOnlyList<UnmatchedIntervalStop<TKey, TStop>> UnmatchedStops,
    IReadOnlyList<InvalidPairedInterval<TKey, TStart, TStop>> InvalidIntervals)
    where TKey : notnull;

internal sealed class IntervalPairAccumulator<TKey, TStart, TStop> where TKey : notnull
{
    public void AddStart(TKey key, long timeUs, TStart data);
    public void AddStop(TKey key, long timeUs, TStop data);
    public IntervalPairResult<TKey, TStart, TStop> Complete();
}
```

Use a `Dictionary<TKey,Queue<PendingIntervalStart<TKey,TStart>>>`. A stop consumes only the oldest start with the identical typed key. If `stopUs <= startUs`, append `InvalidPairedInterval` and do not append a completed pair. A stop with no queued start is unmatched; every queued start remaining at completion is unmatched.

Create the accounting projection in the core namespace:

```csharp
using WprMcp.Analyzers;

namespace WprMcp.Core;

internal readonly record struct AccountedPairedInterval<TKey, TStart, TStop>(
    TKey Key,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode,
    TStart StartData,
    TStop StopData) where TKey : notnull;

internal static class DurationAccounting
{
    public const string ClippedOverlapMode = "clipped_overlap_v2";

    public static AccountedPairedInterval<TKey, TStart, TStop>? Project<TKey, TStart, TStop>(
        PairedInterval<TKey, TStart, TStop> pair,
        TimeWindow window) where TKey : notnull
    {
        var accountedUs = window.IntersectDurationUs(pair.StartUs, pair.EndUs);
        return accountedUs == 0
            ? null
            : new AccountedPairedInterval<TKey, TStart, TStop>(
                pair.Key,
                pair.StartUs,
                pair.EndUs,
                pair.FullDurationUs,
                accountedUs,
                ClippedOverlapMode,
                pair.StartData,
                pair.StopData);
    }
}
```

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~DurationAccountingTests|FullyQualifiedName~TimeWindowTests"`

Expected: all FIFO, invalid, unmatched, left/right/enclosing overlap, and half-open boundary cases pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Core/DurationAccounting.cs src/WprMcp/Analyzers/IntervalPairAccumulator.cs tests/WprMcp.Tests/DurationAccountingTests.cs
git commit -m "feat(core): add typed duration accounting"
```

### Task 2: Pair and associate GC walls and pauses independently

**Files:**
- Create: `src/WprMcp/Analyzers/GcIntervalAccumulator.cs`
- Create: `tests/WprMcp.Tests/GcIntervalAccumulatorTests.cs`

**Interfaces:**
- Consumes: `ProcessInstanceKey` from Child 1 and `IntervalPairAccumulator<TKey,TStart,TStop>` from Task 1.
- Produces: `GcIntervalAccumulator.Complete()`, `GcIntervalSet`, `GcWallWithPauses`, `GcPauseInterval`, and explicit `GcIncompleteEvidence` rows.

- [ ] **Step 1: Write the failing association acceptance tests**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;

namespace WprMcp.Tests;

public sealed class GcIntervalAccumulatorTests
{
    private static readonly ProcessInstanceKey Process = new(42, 10);

    [Fact]
    public void SuspendBeforeGc_RestartAfterGc_AssociatesSeventyMicroseconds()
    {
        var a = new GcIntervalAccumulator();
        a.AddSuspendStart(Process, clrInstanceId: 1, timestampUs: 90);
        a.AddGcStart(Process, 1, gcCount: 7, timestampUs: 100, generation: 2, reason: "AllocLarge");
        a.AddGcStop(Process, 1, gcCount: 7, timestampUs: 150);
        a.AddRestartStop(Process, 1, timestampUs: 160);

        var result = a.Complete();
        var gc = Assert.Single(result.Gcs);

        Assert.Equal(70, Assert.Single(gc.Pauses).FullDurationUs);
        Assert.Empty(result.OrphanPauses);
    }

    [Fact]
    public void BackgroundAndForegroundGc_EachPauseIsAssociatedOnce()
    {
        var a = new GcIntervalAccumulator();
        a.AddSuspendStart(Process, 1, 90);
        a.AddGcStart(Process, 1, 1, 100, 2, "Background");
        a.AddRestartStop(Process, 1, 120);
        a.AddSuspendStart(Process, 1, 130);
        a.AddGcStart(Process, 1, 2, 140, 0, "AllocSmall");
        a.AddGcStop(Process, 1, 2, 160);
        a.AddRestartStop(Process, 1, 170);
        a.AddGcStop(Process, 1, 1, 200);

        var result = a.Complete();
        var byCount = result.Gcs.ToDictionary(gc => gc.Key.GcCount);

        Assert.Equal(30, Assert.Single(byCount[1].Pauses).FullDurationUs);
        Assert.Equal(40, Assert.Single(byCount[2].Pauses).FullDurationUs);
        Assert.Equal(2, result.Gcs.Sum(gc => gc.Pauses.Count));
        Assert.Empty(result.OrphanPauses);
    }

    [Fact]
    public void NoGcStartsInsidePause_UsesGreatestOverlapThenLatestStart()
    {
        var a = new GcIntervalAccumulator();
        a.AddGcStart(Process, 1, 1, 100, 2, "Background");
        a.AddGcStart(Process, 1, 2, 130, 1, "Induced");
        a.AddSuspendStart(Process, 1, 150);
        a.AddRestartStop(Process, 1, 180);
        a.AddGcStop(Process, 1, 1, 180);
        a.AddGcStop(Process, 1, 2, 180);

        var result = a.Complete();
        Assert.Empty(result.Gcs.Single(gc => gc.Key.GcCount == 1).Pauses);
        Assert.Single(result.Gcs.Single(gc => gc.Key.GcCount == 2).Pauses);
    }

    [Fact]
    public void MissingClrIdentity_IsIncompleteAndNeverFallsBackToPid()
    {
        var a = new GcIntervalAccumulator();
        a.AddGcStart(Process, null, 1, 100, 0, "AllocSmall");
        a.AddGcStop(Process, 1, 1, 130);
        a.AddSuspendStart(Process, null, 90);
        a.AddRestartStop(Process, 1, 140);

        var result = a.Complete();

        Assert.Empty(result.Gcs);
        Assert.Empty(result.OrphanPauses);
        Assert.Equal(2, result.IncompleteEvidence.Count(row => row.Code == "missing_clr_instance"));
        Assert.True(result.UnmatchedGcStopCount > 0);
        Assert.True(result.UnmatchedRestartStopCount > 0);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~GcIntervalAccumulatorTests"`

Expected: compilation fails because `GcIntervalAccumulator` and its result types are absent.

- [ ] **Step 3: Implement the GC state and deterministic association rule**

Use these exact contracts:

```csharp
namespace WprMcp.Analyzers;

internal readonly record struct ClrGcKey(
    ProcessInstanceKey Process, ushort ClrInstanceId, int GcCount);
internal readonly record struct ClrPauseKey(
    ProcessInstanceKey Process, ushort ClrInstanceId);
internal readonly record struct GcStartData(int Generation, string Reason);
internal readonly record struct GcStopData;
internal readonly record struct SuspendStartData;
internal readonly record struct RestartStopData;

internal sealed record GcPauseInterval(
    ClrPauseKey Key, long StartUs, long EndUs, long FullDurationUs);
internal sealed record GcWallWithPauses(
    ClrGcKey Key,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    int Generation,
    string Reason,
    IReadOnlyList<GcPauseInterval> Pauses);
internal sealed record GcIncompleteEvidence(
    string Code, ProcessInstanceKey Process, long TimestampUs, string EventKind);
internal sealed record GcIntervalSet(
    IReadOnlyList<GcWallWithPauses> Gcs,
    IReadOnlyList<GcPauseInterval> OrphanPauses,
    IReadOnlyList<GcIncompleteEvidence> IncompleteEvidence,
    int UnmatchedGcStartCount,
    int UnmatchedGcStopCount,
    int UnmatchedSuspendStartCount,
    int UnmatchedRestartStopCount,
    int InvalidIntervalCount);

internal sealed class GcIntervalAccumulator
{
    public void AddGcStart(ProcessInstanceKey process, ushort? clrInstanceId, int gcCount,
        long timestampUs, int generation, string reason);
    public void AddGcStop(ProcessInstanceKey process, ushort? clrInstanceId, int gcCount,
        long timestampUs);
    public void AddSuspendStart(ProcessInstanceKey process, ushort? clrInstanceId, long timestampUs);
    public void AddRestartStop(ProcessInstanceKey process, ushort? clrInstanceId, long timestampUs);
    public GcIntervalSet Complete();
}
```

Internally use one `IntervalPairAccumulator<ClrGcKey,GcStartData,GcStopData>` and one `IntervalPairAccumulator<ClrPauseKey,SuspendStartData,RestartStopData>`. A null CLR ID records `missing_clr_instance` and does not enter either pairer. Complete both pairers before association, sort GC and pause intervals by `(Process.Pid,Process.StartUs,ClrInstanceId,StartUs,EndUs)`, and associate each completed pause with one GC chosen by this function:

```csharp
private static GcWallWithPauses? SelectOwner(
    GcPauseInterval pause,
    IReadOnlyList<GcWallWithPauses> gcs)
{
    var compatible = gcs
        .Where(gc => gc.Key.Process == pause.Key.Process &&
                     gc.Key.ClrInstanceId == pause.Key.ClrInstanceId)
        .Select(gc => new
        {
            Gc = gc,
            OverlapUs = Math.Max(0,
                Math.Min(gc.EndUs, pause.EndUs) - Math.Max(gc.StartUs, pause.StartUs)),
        })
        .Where(item => item.OverlapUs > 0)
        .ToList();

    var startInside = compatible
        .Where(item => pause.StartUs <= item.Gc.StartUs && item.Gc.StartUs < pause.EndUs)
        .OrderByDescending(item => item.Gc.StartUs)
        .ThenByDescending(item => item.Gc.Key.GcCount)
        .FirstOrDefault();
    if (startInside is not null) return startInside.Gc;

    return compatible
        .OrderByDescending(item => item.OverlapUs)
        .ThenByDescending(item => item.Gc.StartUs)
        .ThenByDescending(item => item.Gc.Key.GcCount)
        .Select(item => item.Gc)
        .FirstOrDefault();
}
```

Build the mutable pause lists only inside `Complete`, then publish new immutable `GcWallWithPauses` values. A completed pause becomes an orphan only when `SelectOwner` returns null. Incomplete or unmatched pause evidence is never emitted as an orphan interval.

- [ ] **Step 4: Run the focused tests**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~GcIntervalAccumulatorTests|FullyQualifiedName~DurationAccountingTests"`

Expected: both required sequences, greatest-overlap tie-breaking, missing CLR identity, PID reuse, invalid endpoints, unmatched endpoints, and idempotent completion pass.

- [ ] **Step 5: Commit**

```powershell
git add src/WprMcp/Analyzers/GcIntervalAccumulator.cs tests/WprMcp.Tests/GcIntervalAccumulatorTests.cs
git commit -m "fix(gc): associate pauses with typed gc intervals"
```

### Task 3: Integrate the GC trace adapter and dual-duration output

**Files:**
- Modify: `src/WprMcp/Analyzers/GcAnalysis.cs`
- Create: `src/WprMcp/Output/DurationRecords.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `src/WprMcp/Output/Warnings.cs`
- Modify: `tests/WprMcp.Tests/GcAnalysisTests.cs`

**Interfaces:**
- Consumes: `TraceIdentityIndex.For(trace)`, `TraceTime.FromMilliseconds`, `TimeWindowInput.Resolve`, `GcIntervalAccumulator`, and `DurationAccounting.Project`.
- Produces: GC response fields in which legacy totals alias accounted totals and explicit `Full*`/`Accounted*` fields cannot be confused.

- [ ] **Step 1: Add failing projection, alias, warning, and fixture tests**

```csharp
[Fact]
public void Project_ClipsGcAndAssociatedPauseIndependently()
{
    var process = new ProcessInstanceKey(42, 10);
    var pause = new GcPauseInterval(
        new ClrPauseKey(process, 1), StartUs: 95, EndUs: 130, FullDurationUs: 35);
    var set = new GcIntervalSet(
        Gcs:
        [
            new GcWallWithPauses(
                new ClrGcKey(process, 1, 4), StartUs: 90, EndUs: 210,
                FullDurationUs: 120, Generation: 2, Reason: "AllocLarge", Pauses: [pause]),
        ],
        OrphanPauses: [],
        IncompleteEvidence: [],
        UnmatchedGcStartCount: 0,
        UnmatchedGcStopCount: 0,
        UnmatchedSuspendStartCount: 0,
        UnmatchedRestartStopCount: 0,
        InvalidIntervalCount: 0);

    var response = GcAnalysis.Project(set, new TimeWindow(100, 200), pid: 42);
    var row = Assert.Single(response.Events);

    Assert.Equal(120, row.FullDurationUs);
    Assert.Equal(100, row.AccountedDurationUs);
    Assert.Equal(35, row.FullPauseUs);
    Assert.Equal(30, row.AccountedPauseUs);
    Assert.Equal(row.AccountedDurationUs, row.DurationUs);
    Assert.Equal(row.AccountedPauseUs, row.PauseUs);
    Assert.Equal(response.TotalAccountedGcUs, response.TotalGcUs);
    Assert.Equal(response.TotalAccountedPauseUs, response.TotalPauseUs);
    Assert.Contains(response.Warnings, warning => warning.StartsWith("time_semantics_v2:"));
}

[Fact]
public void ClrGcAnalysis_PerfViewGcFixture_PreservesGenerationCountsAndDualTotals()
{
    var tools = new ClrTools(new TraceCache(capacity: 2));
    var response = tools.ClrGcAnalysis("fixtures/perfview_gcevents.etl");

    Assert.NotEmpty(response.Events);
    Assert.Equal(response.TotalGcCount, response.Gen0Count + response.Gen1Count + response.Gen2Count);
    Assert.Equal(response.Events.Where(row => !row.IsOrphanPause).Sum(row => row.AccountedDurationUs),
        response.TotalAccountedGcUs);
    Assert.Equal(response.Events.Sum(row => row.AccountedPauseUs ?? 0),
        response.TotalAccountedPauseUs);
    Assert.All(response.Events, row => Assert.Equal("clipped_overlap_v2", row.AccountingMode));
}
```

The pure projection test constructs Task 2 records directly; do not synthesize TraceEvent objects for it.

- [ ] **Step 2: Run the GC tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~GcAnalysisTests"`

Expected: compilation fails for `GcAnalysis.Project` and the new full/accounted response members.

- [ ] **Step 3: Define the GC DTO and stable warning**

Move `GcEventRow` and `GcAnalysisResponse` from `Records.cs` into `DurationRecords.cs` with these exact declarations:

```csharp
namespace WprMcp.Output;

public sealed record GcEventRow(
    long StartUs,
    long DurationUs,
    int Generation,
    string Reason,
    int Pid,
    long? PauseUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    long? FullPauseUs,
    long? AccountedPauseUs,
    string AccountingMode,
    long ProcessStartUs,
    int? ClrInstanceId,
    int? GcCount,
    bool IsOrphanPause);

public sealed record GcAnalysisResponse(
    int? Pid,
    int TotalGcCount,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    long TotalGcUs,
    long TotalPauseUs,
    IReadOnlyList<GcEventRow> Events,
    IReadOnlyList<string> Warnings,
    long TotalFullGcUs,
    long TotalAccountedGcUs,
    long TotalFullPauseUs,
    long TotalAccountedPauseUs,
    string AccountingMode,
    int IncompleteClrIdentityCount,
    int UnmatchedGcIntervalCount,
    int UnmatchedPauseIntervalCount,
    int InvalidIntervalCount);
```

Add this warning builder member:

```csharp
public const string LegacyAccountedDurationWarning =
    "time_semantics_v2: legacy DurationUs/PauseUs and duration totals are accounted overlap within the requested half-open window; use FullDurationUs/FullPauseUs for complete paired wall time.";
```

- [ ] **Step 4: Replace the GC event-order state with pair-then-project**

`GcAnalysis.Analyze` must resolve the `TimeWindow` and identity index once, walk every GC and suspend/restart event without time filtering, resolve the process instance at each event timestamp, extract nullable `ClrInstanceID`, and feed Task 2. Use this exact helper for payload identity so an event version without the field is explicit:

```csharp
internal static ushort? TryReadClrInstanceId(Microsoft.Diagnostics.Tracing.TraceEvent data)
{
    for (var index = 0; index < data.PayloadNames.Length; index++)
    {
        if (!string.Equals(data.PayloadNames[index], "ClrInstanceID", StringComparison.OrdinalIgnoreCase))
            continue;
        var value = data.PayloadValue(index);
        return value is null ? null : Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    return null;
}
```

Add the pure seam `internal static GcAnalysisResponse Project(GcIntervalSet intervals, TimeWindow window, int? pid)`. For each GC, project its wall and each associated pause independently. Include a GC row when its wall or at least one associated pause has positive window overlap. For orphan pauses, emit `Generation=-1`, `Reason="(pause without compatible GC interval)"`, `GcCount=null`, and set both duration families from that pause. Sum complete projected rows before ordering by `StartUs`; assign `DurationUs=AccountedDurationUs`, `PauseUs=AccountedPauseUs`, `TotalGcUs=TotalAccountedGcUs`, and `TotalPauseUs=TotalAccountedPauseUs`. Append the stable warning while the current unversioned response is the legacy surface; Child 5 will make the warning conditional on response mode.

- [ ] **Step 5: Run focused and affected tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~GcAnalysisTests|FullyQualifiedName~GcIntervalAccumulatorTests|FullyQualifiedName~TimeWindowSemanticsTests"
```

Expected: real-fixture generation counts, the two required pause sequences, clipped totals, no-event shape, aliases, and warning prefix pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Analyzers/GcAnalysis.cs src/WprMcp/Output/DurationRecords.cs src/WprMcp/Output/Records.cs src/WprMcp/Output/Warnings.cs tests/WprMcp.Tests/GcAnalysisTests.cs
git commit -m "fix(gc): project full and accounted durations"
```

### Task 4: Migrate SecurityScan and JIT to the common accounting path

**Files:**
- Modify: `src/WprMcp/Analyzers/SecurityScanAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/JitAnalysis.cs`
- Modify: `src/WprMcp/Output/DurationRecords.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `tests/WprMcp.Tests/SecurityScanAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/JitAnalysisTests.cs`

**Interfaces:**
- Consumes: Task 1 pairing/accounting and Child 1 process identities/windows.
- Produces: `SecurityScanAnalysis.ProjectPairs`, `JitAnalysis.ProjectPairs`, dual duration rows/totals, and section `HasMore` flags.

- [ ] **Step 1: Add failing pair-before-clip and TopN tests**

```csharp
[Fact]
public void JitProjection_AccountsAllCompletedRowsBeforeTop()
{
    var process = new ProcessInstanceKey(8, 0);
    var pairs = new[]
    {
        new PairedInterval<JitPairKey, JitStartData, JitStopData>(
            new JitPairKey(process, 1, 1), 90, 130,
            new JitStartData("A", 10), new JitStopData()),
        new PairedInterval<JitPairKey, JitStartData, JitStopData>(
            new JitPairKey(process, 1, 2), 120, 180,
            new JitStartData("B", 20), new JitStopData()),
    };

    var response = JitAnalysis.ProjectPairs(pairs, new TimeWindow(100, 150), pid: 8, top: 1);

    Assert.Equal(60, response.TotalAccountedJitUs);
    Assert.Equal(response.TotalAccountedJitUs, response.TotalJitUs);
    Assert.Single(response.TopMethods);
    Assert.True(response.HasMore);
    Assert.True(response.TopMethods.Sum(row => row.AccountedDurationUs) <= response.TotalAccountedJitUs);
}

[Fact]
public void SecurityProjection_UsesClippedDurationInTargetAndRequestTotals()
{
    var emitter = new ProcessInstanceKey(4, 0);
    var fields = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["__Source"] = "Microsoft Defender",
        ["__ProviderName"] = "Microsoft-Antimalware-Engine",
        ["__Id"] = "scan-1",
        ["Path"] = "c:\\sample.dll",
        ["Process"] = "app.exe",
        ["PID"] = "8",
    };
    var pair = new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
        new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", "scan-1"),
        90, 210, new SecurityScanStartData(fields), new SecurityScanStopData(fields));
    var response = SecurityScanAnalysis.ProjectPairs(
        [pair], new TimeWindow(100, 200), top: 10,
        pid: null, processSubstring: null, pathSubstring: null, providerSubstring: null);

    Assert.Equal(120, Assert.Single(response.SlowScans).FullDurationUs);
    Assert.Equal(100, response.SlowScans[0].AccountedDurationUs);
    Assert.Equal(100, response.SlowScans[0].DurationUs);
    Assert.Equal(100, Assert.Single(response.Rows).TotalAccountedDurationUs);
    Assert.Equal(100, response.TotalDurationUs);
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~JitAnalysisTests|FullyQualifiedName~SecurityScanAnalysisTests"`

Expected: compilation fails for the pure projection seams and dual-duration fields.

- [ ] **Step 3: Add typed keys/payloads and output fields**

Use these analyzer contracts:

```csharp
internal readonly record struct JitPairKey(
    ProcessInstanceKey Process, ushort ClrInstanceId, long MethodId);
internal readonly record struct JitStartData(string Method, int MethodIlSize);
internal readonly record struct JitStopData;
internal readonly record struct SecurityScanPairKey(
    ProcessInstanceKey EmitterProcess, string ProviderName, string Id);
internal sealed record SecurityScanStartData(IReadOnlyDictionary<string, string> Fields);
internal sealed record SecurityScanStopData(IReadOnlyDictionary<string, string> Fields);
```

Move the JIT and SecurityScan duration records from `Records.cs` to `DurationRecords.cs`. Preserve their existing constructor fields first, then append:

```csharp
// JitMethodRow
long StartUs, long EndUs, long FullDurationUs, long AccountedDurationUs,
string AccountingMode, long ProcessStartUs

// JitAnalysisResponse
long TotalFullJitUs, long TotalAccountedJitUs, bool HasMore,
int UnmatchedIntervalCount, int InvalidIntervalCount, string AccountingMode

// SecurityScanRequestRow
long FullDurationUs, long AccountedDurationUs, string AccountingMode

// SecurityScanTargetRow
long TotalFullDurationUs, long TotalAccountedDurationUs,
double? AvgAccountedDurationUs, long? MaxAccountedDurationUs, string AccountingMode

// SecurityScanAnalysisResponse
long TotalFullDurationUs, long TotalAccountedDurationUs,
bool RowsHasMore, bool SlowScansHasMore, bool ProvidersHasMore,
int InvalidIntervalCount, string AccountingMode
```

The existing `JitDurationUs`, `TotalJitUs`, `DurationUs`, `TotalDurationUs`, `AvgDurationUs`, and `MaxDurationUs` fields are accounted aliases. Full aggregate fields sum the full duration of every pair with positive overlap in the requested window; accounted fields sum only overlap.

- [ ] **Step 4: Replace early window filters and string-key pairing**

In both analyzers, resolve the emitting process identity at the start and stop timestamps and feed all compatible events to typed pair accumulators before projection. A Defender pair requires the same `EmitterProcess`, provider, and request ID at both endpoints; an engine-process restart or PID reuse cannot consume the prior generation's start. `JitAnalysis.ProjectPairs` filters by selected process instance/PID and positive overlap, computes totals from every projected row, then orders by `AccountedDurationUs` descending, `StartUs` ascending, and takes `top`. `SecurityScanAnalysis.ProjectPairs` preserves current source/provider/path filters, but aggregates each `RowStats` with both full and accounted values before any section TopN. Set each `HasMore` to `completeRowCount > returnedRowCount`.

Do not time-filter `MethodJittingStarted`, `MethodLoadVerbose`, Defender stream start, or Defender stream stop callbacks. Keep unpaired third-party security result events point-filtered with `TimeWindow.ContainsPoint`; they have counts but no invented duration.

- [ ] **Step 5: Run focused and regression tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~JitAnalysisTests|FullyQualifiedName~SecurityScanAnalysisTests|FullyQualifiedName~DurationAccountingTests"
```

Expected: left/right/enclosing overlaps, PID reuse isolation, unmatched counts, stable filters, totals-before-TopN, `HasMore`, and legacy aliases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Analyzers/SecurityScanAnalysis.cs src/WprMcp/Analyzers/JitAnalysis.cs src/WprMcp/Output/DurationRecords.cs src/WprMcp/Output/Records.cs tests/WprMcp.Tests/SecurityScanAnalysisTests.cs tests/WprMcp.Tests/JitAnalysisTests.cs
git commit -m "fix(analyzers): clip security and jit durations"
```

### Task 5: Migrate Finalizer and CLR contention without cross-instance pairing

**Files:**
- Modify: `src/WprMcp/Analyzers/FinalizerAnalysis.cs`
- Modify: `src/WprMcp/Analyzers/ClrContentionStackAnalysis.cs`
- Modify: `src/WprMcp/Output/DurationRecords.cs`
- Modify: `src/WprMcp/Output/Records.cs`
- Modify: `tests/WprMcp.Tests/FinalizerAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/ClrContentionStackAnalysisTests.cs`

**Interfaces:**
- Consumes: `ProcessInstanceKey`, `ThreadInstanceKey`, `ThreadAnalysisScope` from Child 2, and Task 1 accounting.
- Produces: process-instance-safe finalizer pairs, `ClrContentionStackAnalysis.ProjectIntervals`, and thread-instance-safe contention stack metrics with explicit full/accounted response totals.

- [ ] **Step 1: Add failing process/thread reuse and overlap tests**

```csharp
[Fact]
public void FinalizerProjection_LeftOverlapChargesOnlyWindowContribution()
{
    var process = new ProcessInstanceKey(12, 10);
    var pair = new PairedInterval<FinalizerPairKey, FinalizerStartData, FinalizerStopData>(
        new FinalizerPairKey(process, 1), 90, 130,
        new FinalizerStartData(), new FinalizerStopData(FinalizersRun: 4));

    var response = FinalizerAnalysis.ProjectBatches([pair], new TimeWindow(100, 120), pid: 12);
    var row = Assert.Single(response.Batches);

    Assert.Equal(40, row.FullDurationUs);
    Assert.Equal(20, row.AccountedDurationUs);
    Assert.Equal(20, row.DurationUs);
    Assert.Equal(20, response.TotalBatchUs);
}

[Fact]
public void ContentionProjection_ReusedTidCannotConsumeOldGenerationStart()
{
    var process = new ProcessInstanceKey(12, 10);
    var oldThread = new ThreadInstanceKey(process, 77, 1);
    var newThread = new ThreadInstanceKey(process, 77, 2);
    var accumulator = new IntervalPairAccumulator<ThreadInstanceKey, ContentionStartData, ContentionStopData>();
    accumulator.AddStart(oldThread, 90, new ContentionStartData(default));
    accumulator.AddStop(newThread, 130, new ContentionStopData());

    var result = accumulator.Complete();
    Assert.Empty(result.Pairs);
    Assert.Single(result.UnmatchedStarts);
    Assert.Single(result.UnmatchedStops);
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~FinalizerAnalysisTests|FullyQualifiedName~ClrContentionStackAnalysisTests"`

Expected: compilation fails for the projection seam, typed keys, and full/accounted fields.

- [ ] **Step 3: Define typed pair and output contracts**

```csharp
internal readonly record struct FinalizerPairKey(ProcessInstanceKey Process, ushort ClrInstanceId);
internal readonly record struct FinalizerStartData;
internal readonly record struct FinalizerStopData(int FinalizersRun);
internal readonly record struct ContentionStartData(Microsoft.Diagnostics.Tracing.Etlx.CallStackIndex Stack);
internal readonly record struct ContentionStopData;

internal sealed record ContentionDurationSample(
    ThreadInstanceKey Thread,
    Microsoft.Diagnostics.Tracing.Etlx.CallStackIndex Stack,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode);

internal sealed record ContentionDurationProjection(
    IReadOnlyList<ContentionDurationSample> Samples,
    long TotalFullDurationUs,
    long TotalAccountedDurationUs,
    int UnmatchedIntervalCount,
    int InvalidIntervalCount);
```

Move finalizer and contention records from `Records.cs` to `DurationRecords.cs`. Append `EndUs`, `FullDurationUs`, `AccountedDurationUs`, `AccountingMode`, and `ProcessStartUs` to `FinalizerBatchRow`; append `TotalFullBatchUs`, `TotalAccountedBatchUs`, `UnmatchedIntervalCount`, `InvalidIntervalCount`, and `AccountingMode` to `FinalizerAnalysisResponse`. Keep `DurationUs` and `TotalBatchUs` as accounted aliases.

Append `ExclusiveAccountedBlockedUs`, `InclusiveAccountedBlockedUs`, `AccountingMode` to `ClrContentionStackRow`. Append `TotalFullBlockedUs`, `TotalAccountedBlockedUs`, `UnmatchedIntervalCount`, `InvalidIntervalCount`, `HasMore`, and `AccountingMode` to `ClrContentionStacksResponse`. Keep `ExclusiveBlockedUs`, `InclusiveBlockedUs`, and `TotalBlockedUs` as accounted aliases. Full duration is exposed at response level; stack rows deliberately describe window contribution because a full interval can span outside the queried stack view.

- [ ] **Step 4: Pair full trace, then charge only compatible overlap**

For finalizers, resolve process and nullable CLR identity for both endpoints, pair by `FinalizerPairKey`, and call `ProjectBatches` only after the walk. Continue point-filtering `GCFinalizeObject` counts with `TimeWindow.ContainsPoint`, because those are events rather than durations.

For contention, resolve `ThreadInstanceKey` through `TraceIdentityIndex.Threads` at both endpoints and pair by that key. Store the start stack in `ContentionStartData`; ignore `ContentionStop.DurationNs` for accounting and derive full duration from normalized start/stop timestamps so full and accounted share the same interval boundaries. Add `internal static ContentionDurationProjection ProjectIntervals(IReadOnlyList<PairedInterval<ThreadInstanceKey,ContentionStartData,ContentionStopData>> pairs,ThreadAnalysisScope scope,int unmatchedIntervalCount,int invalidIntervalCount)`. It returns one `ContentionDurationSample` for every positive `scope.AccountInterval`, with full duration from the pair and accounted duration from the scope.

Add one stack sample with `metric=ContentionDurationSample.AccountedDurationUs` only after projection. Accumulate `TotalFullBlockedUs` and `TotalAccountedBlockedUs` from the complete sample list before constructing the call tree or taking `top`; feed the original `[sample.StartUs,sample.EndUs)` to Child 2's `WhenHistogram.AddDurationInterval` so the exact clipped contribution is distributed across every overlapped bucket. Do not collapse a duration into one point bucket.

- [ ] **Step 5: Run focused and affected tests**

```powershell
dotnet test WprMcp.sln --filter "FullyQualifiedName~FinalizerAnalysisTests|FullyQualifiedName~ClrContentionStackAnalysisTests|FullyQualifiedName~ThreadInstanceCatalogTests|FullyQualifiedName~StackSourceTopNTests"
```

Expected: process/TID reuse, left/right/enclosing overlap, zero overlap, missing CLR identity, unmatched endpoints, stack totals, and legacy aliases pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Analyzers/FinalizerAnalysis.cs src/WprMcp/Analyzers/ClrContentionStackAnalysis.cs src/WprMcp/Output/DurationRecords.cs src/WprMcp/Output/Records.cs tests/WprMcp.Tests/FinalizerAnalysisTests.cs tests/WprMcp.Tests/ClrContentionStackAnalysisTests.cs
git commit -m "fix(clr): account finalizer and contention overlap"
```

### Task 6: Lock cross-analyzer totals and remove the obsolete pairer

**Files:**
- Create: `tests/WprMcp.Tests/DurationAnalyzerInvariantTests.cs`
- Modify: `tests/WprMcp.Tests/GcAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/JitAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/SecurityScanAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/FinalizerAnalysisTests.cs`
- Modify: `tests/WprMcp.Tests/ClrContentionStackAnalysisTests.cs`
- Delete: `src/WprMcp/Analyzers/EventPairAggregator.cs`
- Delete: `tests/WprMcp.Tests/EventPairAggregatorTests.cs`

**Interfaces:**
- Consumes: every complete projection and DTO added in Tasks 1–5.
- Produces: one executable invariant gate for accounted totals, TopN, accounting labels, aliases, and explicit incomplete evidence.

- [ ] **Step 1: Write the failing invariant theory**

```csharp
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tests;

public sealed class DurationAnalyzerInvariantTests
{
    public static IEnumerable<object[]> Sections()
    {
        yield return [DurationInvariantFixtures.Gc()];
        yield return [DurationInvariantFixtures.Jit()];
        yield return [DurationInvariantFixtures.SecurityScan()];
        yield return [DurationInvariantFixtures.Finalizer()];
        yield return [DurationInvariantFixtures.Contention()];
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void CompleteRowsAndTopRowsRespectAccountedTotal(DurationSectionProbe section)
    {
        Assert.Equal(section.CompleteRows.Sum(row => row.AccountedDurationUs), section.TotalAccountedDurationUs);
        Assert.True(section.ReturnedRows.Sum(row => row.AccountedDurationUs) <= section.TotalAccountedDurationUs);
        if (!section.HasMore)
            Assert.Equal(section.TotalAccountedDurationUs,
                section.ReturnedRows.Sum(row => row.AccountedDurationUs));
        Assert.All(section.CompleteRows, row =>
        {
            Assert.Equal("clipped_overlap_v2", row.AccountingMode);
            Assert.True(row.FullDurationUs >= row.AccountedDurationUs);
        });
    }

    [Theory]
    [MemberData(nameof(Sections))]
    public void LegacyAliasesAreAccountedAndWarn(DurationSectionProbe section)
    {
        Assert.Equal(section.TotalAccountedDurationUs, section.LegacyTotalUs);
        Assert.All(section.ReturnedRows, row => Assert.Equal(row.AccountedDurationUs, row.LegacyDurationUs));
        Assert.Contains(section.Warnings, warning => warning.StartsWith("time_semantics_v2:"));
    }
}
```

Add these concrete probe and fixture definitions to the same test file. The contention response warning/stack aliases remain covered by `ClrContentionStackAnalysisTests`; this fixture audits its pre-call-tree interval projection.

```csharp
internal sealed record DurationRowProbe(
    long FullDurationUs,
    long AccountedDurationUs,
    long LegacyDurationUs,
    string AccountingMode);

internal sealed record DurationSectionProbe(
    IReadOnlyList<DurationRowProbe> CompleteRows,
    IReadOnlyList<DurationRowProbe> ReturnedRows,
    long TotalAccountedDurationUs,
    long LegacyTotalUs,
    bool HasMore,
    IReadOnlyList<string> Warnings);

internal static class DurationInvariantFixtures
{
    private static readonly TimeWindow Window = new(100, 200);
    private static readonly (long StartUs, long EndUs)[] Spans =
    [
        (90, 130),
        (120, 180),
        (170, 210),
    ];

    public static DurationSectionProbe Gc()
    {
        var process = new ProcessInstanceKey(10, 0);
        var gcs = Spans.Select((span, index) =>
            new GcWallWithPauses(
                new ClrGcKey(process, 1, index + 1),
                span.StartUs, span.EndUs, span.EndUs - span.StartUs,
                Generation: index, Reason: "test", Pauses: Array.Empty<GcPauseInterval>()))
            .ToList();
        var response = GcAnalysis.Project(
            new GcIntervalSet(gcs, [], [], 0, 0, 0, 0, 0), Window, pid: 10);
        var rows = response.Events.Select(row => Probe(
            row.FullDurationUs, row.AccountedDurationUs, row.DurationUs, row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows, rows, response.TotalAccountedGcUs, response.TotalGcUs,
            HasMore: false, response.Warnings);
    }

    public static DurationSectionProbe Jit()
    {
        var process = new ProcessInstanceKey(10, 0);
        var pairs = Spans.Select((span, index) =>
            new PairedInterval<JitPairKey, JitStartData, JitStopData>(
                new JitPairKey(process, 1, index + 1), span.StartUs, span.EndUs,
                new JitStartData($"Method{index}", index + 1), new JitStopData()))
            .ToList();
        var complete = JitAnalysis.ProjectPairs(pairs, Window, pid: 10, top: int.MaxValue);
        var returned = JitAnalysis.ProjectPairs(pairs, Window, pid: 10, top: 2);
        return new DurationSectionProbe(
            complete.TopMethods.Select(row => Probe(
                row.FullDurationUs, row.AccountedDurationUs, row.JitDurationUs, row.AccountingMode)).ToList(),
            returned.TopMethods.Select(row => Probe(
                row.FullDurationUs, row.AccountedDurationUs, row.JitDurationUs, row.AccountingMode)).ToList(),
            returned.TotalAccountedJitUs,
            returned.TotalJitUs,
            returned.HasMore,
            returned.Warnings);
    }

    public static DurationSectionProbe SecurityScan()
    {
        var pairs = Spans.Select((span, index) => ScanPair(index, span.StartUs, span.EndUs)).ToList();
        var complete = SecurityScanAnalysis.ProjectPairs(
            pairs, Window, int.MaxValue, null, null, null, null);
        var returned = SecurityScanAnalysis.ProjectPairs(
            pairs, Window, 2, null, null, null, null);
        return new DurationSectionProbe(
            complete.SlowScans.Select(row => Probe(
                row.FullDurationUs, row.AccountedDurationUs, row.DurationUs, row.AccountingMode)).ToList(),
            returned.SlowScans.Select(row => Probe(
                row.FullDurationUs, row.AccountedDurationUs, row.DurationUs, row.AccountingMode)).ToList(),
            returned.TotalAccountedDurationUs,
            returned.TotalDurationUs,
            returned.SlowScansHasMore,
            returned.Warnings);
    }

    public static DurationSectionProbe Finalizer()
    {
        var process = new ProcessInstanceKey(10, 0);
        var pairs = Spans.Select((span, index) =>
            new PairedInterval<FinalizerPairKey, FinalizerStartData, FinalizerStopData>(
                new FinalizerPairKey(process, 1), span.StartUs, span.EndUs,
                new FinalizerStartData(), new FinalizerStopData(index + 1)))
            .ToList();
        var response = FinalizerAnalysis.ProjectBatches(pairs, Window, pid: 10);
        var rows = response.Batches.Select(row => Probe(
            row.FullDurationUs, row.AccountedDurationUs, row.DurationUs, row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows, rows, response.TotalAccountedBatchUs, response.TotalBatchUs,
            HasMore: false, response.Warnings);
    }

    public static DurationSectionProbe Contention()
    {
        var process = new ProcessInstanceKey(10, 0);
        var thread = new ThreadInstanceKey(process, 7, 1);
        var pairs = Spans.Select(span =>
            new PairedInterval<ThreadInstanceKey, ContentionStartData, ContentionStopData>(
                thread, span.StartUs, span.EndUs,
                new ContentionStartData(default), new ContentionStopData()))
            .ToList();
        var scope = new ThreadAnalysisScope(
            Window,
            new ProcessLifetime(process, EndUs: 300, StartObserved: true, EndObserved: true),
            Thread: null);
        var projection = ClrContentionStackAnalysis.ProjectIntervals(
            pairs, scope, unmatchedIntervalCount: 0, invalidIntervalCount: 0);
        var rows = projection.Samples.Select(row => Probe(
            row.FullDurationUs, row.AccountedDurationUs,
            row.AccountedDurationUs, row.AccountingMode)).ToList();
        return new DurationSectionProbe(
            rows, rows, projection.TotalAccountedDurationUs,
            projection.TotalAccountedDurationUs, HasMore: false,
            [WarningBuilder.LegacyAccountedDurationWarning]);
    }

    private static PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>
        ScanPair(int index, long startUs, long endUs)
    {
        var emitter = new ProcessInstanceKey(4, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = $"scan-{index}",
            ["Path"] = $"c:\\file-{index}.dll",
            ["Process"] = "app.exe",
            ["PID"] = "10",
        };
        return new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
            new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", $"scan-{index}"),
            startUs, endUs, new SecurityScanStartData(fields), new SecurityScanStopData(fields));
    }

    private static DurationRowProbe Probe(
        long fullDurationUs,
        long accountedDurationUs,
        long legacyDurationUs,
        string accountingMode) =>
        new(fullDurationUs, accountedDurationUs, legacyDurationUs, accountingMode);
}
```

- [ ] **Step 2: Run the invariant gate and verify failure**

Run: `dotnet test WprMcp.sln --filter "FullyQualifiedName~DurationAnalyzerInvariantTests"`

Expected: at least one analyzer fails total-before-TopN, alias, `HasMore`, or warning normalization until all Task 1–5 paths use the common projection.

- [ ] **Step 3: Apply the invariant fixes and remove the old implementation**

For every response, materialize the complete projected row list, compute full/accounted totals, derive `HasMore`, then sort/take. Never recompute totals from returned rows. Append `WarningBuilder.LegacyAccountedDurationWarning` exactly once per legacy response that exposes a duration, including an empty response. Delete the old string-keyed `EventPairAggregator` and its tests after `rg -n "EventPairAggregator|EventPair(Start|Stop|Result)?" src tests` returns only the two files being deleted.

- [ ] **Step 4: Run the Child 3 gate and full suite**

```powershell
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Duration|FullyQualifiedName~Gc|FullyQualifiedName~Jit|FullyQualifiedName~SecurityScan|FullyQualifiedName~Finalizer|FullyQualifiedName~ClrContention"
dotnet test WprMcp.sln -c Release
```

Expected: both commands pass; each complete accounted-row sum equals its total, TopN sums never exceed totals, non-truncated sections equal totals, missing CLR identity remains explicit, and no legacy field contains full duration.

- [ ] **Step 5: Confirm the obsolete pairer is gone**

Run: `rg -n "EventPairAggregator|EventPair(Start|Stop|Result)?" src tests`

Expected: exit code 1 with no matches.

- [ ] **Step 6: Commit**

```powershell
git add src/WprMcp/Analyzers/EventPairAggregator.cs tests/WprMcp.Tests/EventPairAggregatorTests.cs tests/WprMcp.Tests/DurationAnalyzerInvariantTests.cs tests/WprMcp.Tests/GcAnalysisTests.cs tests/WprMcp.Tests/JitAnalysisTests.cs tests/WprMcp.Tests/SecurityScanAnalysisTests.cs tests/WprMcp.Tests/FinalizerAnalysisTests.cs tests/WprMcp.Tests/ClrContentionStackAnalysisTests.cs
git commit -m "test(duration): enforce accounting invariants"
```

## Child 3 Completion Gate

- `SuspendStart(90) -> GCStart(100) -> GCStop(150) -> RestartStop(160)` yields one 70 us associated pause and no orphan.
- Background GC #1 `[100,200)` with pause `[90,120)` and foreground GC #2 `[140,160)` with pause `[130,170)` yield 30 us and 40 us respectively, without reuse or double counting.
- Every paired analyzer walks and pairs the full trace before window projection.
- Every legacy duration and legacy duration total equals accounted overlap and carries the `time_semantics_v2` warning.
- Every dual-duration row labels full and accounted values with `clipped_overlap_v2`; full duration is never described as query-window contribution.
- Complete totals are independent of TopN, and each `HasMore=false` section's returned accounted sum equals its total.
- Missing CLR, process, or thread identity is counted as incomplete/unmatched evidence and never falls back to PID/TID pairing.
- Child 5 remains responsible for the final `ToolEnvelope`, response-version selection, and conditional legacy serializer.
