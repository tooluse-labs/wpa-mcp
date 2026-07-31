using WprMcp.Analyzers;
using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public sealed class GcIntervalAccumulatorTests
{
    private static readonly ProcessInstanceKey Process = new(42, 10);

    [Fact]
    public void SuspendBeforeGc_RestartAfterGc_AssociatesSeventyMicroseconds()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddSuspendStart(Process, clrInstanceId: 1, timestampUs: 90);
        accumulator.AddGcStart(
            Process, clrInstanceId: 1, gcCount: 7, timestampUs: 100,
            generation: 2, reason: "AllocLarge");
        accumulator.AddGcStop(Process, clrInstanceId: 1, gcCount: 7, timestampUs: 150);
        accumulator.AddRestartStop(Process, clrInstanceId: 1, timestampUs: 160);

        var result = accumulator.Complete();
        var gc = Assert.Single(result.Gcs);

        Assert.Equal(70, Assert.Single(gc.Pauses).FullDurationUs);
        Assert.Empty(result.OrphanPauses);
    }

    [Fact]
    public void BackgroundAndForegroundGc_EachPauseIsAssociatedOnce()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddSuspendStart(Process, 1, 90);
        accumulator.AddGcStart(Process, 1, 1, 100, 2, "Background");
        accumulator.AddRestartStop(Process, 1, 120);
        accumulator.AddSuspendStart(Process, 1, 130);
        accumulator.AddGcStart(Process, 1, 2, 140, 0, "AllocSmall");
        accumulator.AddGcStop(Process, 1, 2, 160);
        accumulator.AddRestartStop(Process, 1, 170);
        accumulator.AddGcStop(Process, 1, 1, 200);

        var result = accumulator.Complete();
        var byCount = result.Gcs.ToDictionary(gc => gc.Key.GcCount);

        Assert.Equal(30, Assert.Single(byCount[1].Pauses).FullDurationUs);
        Assert.Equal(40, Assert.Single(byCount[2].Pauses).FullDurationUs);
        Assert.Equal(2, result.Gcs.Sum(gc => gc.Pauses.Count));
        Assert.Empty(result.OrphanPauses);
    }

    [Fact]
    public void NoGcStartsInsidePause_UsesGreatestOverlapThenLatestStart()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(Process, 1, 1, 100, 2, "Background");
        accumulator.AddGcStart(Process, 1, 2, 130, 1, "Induced");
        accumulator.AddSuspendStart(Process, 1, 150);
        accumulator.AddRestartStop(Process, 1, 180);
        accumulator.AddGcStop(Process, 1, 1, 180);
        accumulator.AddGcStop(Process, 1, 2, 180);

        var result = accumulator.Complete();

        Assert.Empty(result.Gcs.Single(gc => gc.Key.GcCount == 1).Pauses);
        Assert.Single(result.Gcs.Single(gc => gc.Key.GcCount == 2).Pauses);
    }

    [Fact]
    public void MissingClrIdentity_IsIncompleteAndNeverFallsBackToPid()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(Process, null, 1, 100, 0, "AllocSmall");
        accumulator.AddGcStop(Process, 1, 1, 130);
        accumulator.AddSuspendStart(Process, null, 90);
        accumulator.AddRestartStop(Process, 1, 140);

        var result = accumulator.Complete();

        Assert.Empty(result.Gcs);
        Assert.Empty(result.OrphanPauses);
        Assert.Equal(2, result.IncompleteEvidence.Count(
            row => row.Code == "missing_clr_instance"));
        Assert.True(result.UnmatchedGcStopCount > 0);
        Assert.True(result.UnmatchedRestartStopCount > 0);
    }

    [Fact]
    public void ReusedPid_CannotPairAcrossProcessInstances()
    {
        var oldProcess = new ProcessInstanceKey(42, 10);
        var newProcess = new ProcessInstanceKey(42, 200);
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(oldProcess, 1, 7, 100, 2, "Background");
        accumulator.AddGcStop(newProcess, 1, 7, 250);

        var result = accumulator.Complete();

        Assert.Empty(result.Gcs);
        Assert.Equal(1, result.UnmatchedGcStartCount);
        Assert.Equal(1, result.UnmatchedGcStopCount);
    }

    [Fact]
    public void CompletedPauseWithoutCompatibleGc_IsOrphanedOnce()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddSuspendStart(Process, 1, 20);
        accumulator.AddRestartStop(Process, 1, 40);

        var result = accumulator.Complete();

        Assert.Equal(20, Assert.Single(result.OrphanPauses).FullDurationUs);
        Assert.Empty(result.Gcs);
        Assert.Equal(0, result.UnmatchedSuspendStartCount);
        Assert.Equal(0, result.UnmatchedRestartStopCount);
    }

    [Fact]
    public void InvalidAndUnmatchedEndpoints_AreCountedExplicitly()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(Process, 1, 1, 100, 0, "AllocSmall");
        accumulator.AddGcStop(Process, 1, 1, 100);
        accumulator.AddGcStart(Process, 1, 2, 200, 1, "Induced");
        accumulator.AddRestartStop(Process, 1, 300);

        var result = accumulator.Complete();

        Assert.Empty(result.Gcs);
        Assert.Equal(1, result.InvalidIntervalCount);
        Assert.Equal(1, result.UnmatchedGcStartCount);
        Assert.Equal(1, result.UnmatchedRestartStopCount);
    }

    [Fact]
    public void Complete_IsIdempotentAndRejectsFurtherEvents()
    {
        var accumulator = new GcIntervalAccumulator();
        accumulator.AddGcStart(Process, 1, 1, 100, 0, "AllocSmall");

        var first = accumulator.Complete();
        var second = accumulator.Complete();

        Assert.Same(first, second);
        Assert.Throws<InvalidOperationException>(new Action(() =>
            accumulator.AddGcStop(Process, 1, 1, 120)));
    }
}
