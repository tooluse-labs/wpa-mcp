using WpaMcp.Analyzers;
using WpaMcp.Core;
using Xunit;

namespace WpaMcp.Tests;

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

        var metrics = StartupMetricsAccumulator.Project(
            [observation], running, blocked)[process];

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
            [new BlockedInterval(
                new ThreadInstanceKey(late, 5, 1), 320, 380, "Executive")])[early];

        Assert.Equal(0, metrics.StartupCpuUs);
        Assert.Equal(0, metrics.StartupBlockedUs);
    }

    private static StartupProcessObservation Observation(
        ProcessInstanceKey process,
        TimeWindow bounds)
    {
        var lifetime = new ProcessLifetime(
            process,
            EndUs: bounds.EndUs,
            StartObserved: true,
            EndObserved: true);
        return new StartupProcessObservation(
            new StartupProcessMetadata(
                lifetime,
                ParentPid: 1,
                Name: "app.exe",
                LifetimeCpuUs: 10,
                LifetimeImageLoadCount: 1),
            new StartupWindow(
                process,
                bounds,
                RequestedEndUs: bounds.EndUs,
                TraceDurationUs: bounds.EndUs,
                ProcessStartObserved: true,
                ProcessEndObserved: true,
                Status: "Complete",
                Code: null));
    }
}
