using WprMcp.Analyzers;
using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public class ThreadLifetimeAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ThreadLifetime_System_ReturnsCleanShape()
    {
        // PID 4 (System) is always running and has many kernel threads. The CPU profile
        // captures Thread events. Even if we don't get rich data, the shape must be valid.
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 4, top: 50);
        Assert.Equal(4, resp.Pid);
        // Total >= 0 (may be 0 if no Thread keyword captured for this process).
        Assert.True(resp.TotalThreads >= 0);
        // Top is bounded.
        Assert.True(resp.Threads.Count <= 50);
        Assert.True(resp.PeakConcurrentThreads >= 0);
    }

    [Fact]
    public void ThreadLifetime_NonexistentPid_EmptyAndWarns()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 999_999, top: 10);
        Assert.Equal(0, resp.TotalThreads);
        Assert.Empty(resp.Threads);
        Assert.NotEmpty(resp.Warnings);
    }

    [Fact]
    public void ThreadLifetime_LifetimeUsConsistentWithEndMinusStart()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var resp = ThreadLifetimeAnalysis.Analyze(trace, pid: 4, top: 200);
        foreach (var t in resp.Threads)
            Assert.Equal(t.EndTimeUs - t.StartTimeUs, t.LifetimeUs);
    }
}
