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

        var bucket = StartupImageLoadAnalysis.Project(
            events, [observation], maxRowsPerProcess: 10)[early];
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
