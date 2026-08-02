using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class ThreadCompareWindowsTests
{
    private const string FixturePath = "fixtures/small_wait_bound.etl";

    [Fact]
    public void InvalidWindowSetsFailBeforeTraceAccess()
    {
        var tools = new ThreadComparisonTools(new TraceCache(capacity: 1));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ThreadCompareWindows(
            "missing.etl", 1, 2, [new("only", 0, 1)]));
        Assert.Throws<ArgumentException>(() => tools.ThreadCompareWindows(
            "missing.etl", 1, 2, [new("same", 0, 1), new("same", 1, 2)]));
        Assert.Throws<ArgumentException>(() => tools.ThreadCompareWindows(
            "missing.etl", 1, 2, [new("a", 2, 1), new("b", 1, 2)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ThreadCompareWindows(
            "missing.etl", 1, 2, [new("a", 0, 1), new("b", 1, 2)], top: 51));
    }

    [Fact]
    public void ExactThreadWindowsMatchTheUnderlyingCpuAndWaitTools()
    {
        var cache = new TraceCache(capacity: 2);
        var lifetime = FindBlockedThread(cache, FixturePath)
            ?? throw new InvalidOperationException("The wait fixture has no blocked thread.");
        var windows = Split(lifetime);
        var comparison = new ThreadComparisonTools(cache).ThreadCompareWindows(
            FixturePath,
            lifetime.Key.Process.Pid,
            lifetime.Key.Tid,
            windows,
            top: 5,
            processStartUs: lifetime.Key.Process.StartUs,
            threadStartUs: lifetime.StartUs,
            threadGeneration: lifetime.Key.Generation);

        Assert.Equal(lifetime.Key, comparison.SelectedThread);
        Assert.Equal("ok", comparison.ScopeStatus);
        Assert.Equal(windows.Select(window => window.Name),
            comparison.Rows.Select(row => row.Name));
        var cpu = new CpuTools(cache);
        var wait = new WaitTools(cache);
        foreach (var actual in comparison.Rows)
        {
            var sampled = cpu.CpuTopFunctions(
                FixturePath, 5, lifetime.Key.Process.Pid, actual.StartUs, actual.EndUs,
                resolveSymbols: false, tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            var precise = cpu.CpuPreciseAnalysis(
                FixturePath, 1, lifetime.Key.Process.Pid, actual.StartUs, actual.EndUs,
                lifetime.Key.Tid, lifetime.Key.Process.StartUs, lifetime.StartUs,
                lifetime.Key.Generation);
            var blocked = wait.WaitAnalysis(
                FixturePath, 1, lifetime.Key.Process.Pid, actual.StartUs, actual.EndUs,
                lifetime.Key.Tid, lifetime.Key.Process.StartUs, lifetime.StartUs,
                lifetime.Key.Generation);
            var stacks = wait.WaitTopStacks(
                FixturePath, 5, lifetime.Key.Process.Pid, actual.StartUs, actual.EndUs,
                resolveSymbols: false, tid: lifetime.Key.Tid,
                processStartUs: lifetime.Key.Process.StartUs,
                threadStartUs: lifetime.StartUs,
                threadGeneration: lifetime.Key.Generation);
            IReadOnlyList<WaitReasonBucket> reasons =
                blocked.Rows.FirstOrDefault()?.TopWaitReasons ?? [];

            Assert.Equal(sampled.TotalSamples, actual.SampledCpuSamples);
            Assert.Equal(precise.TotalCpuUs, actual.RunningUs);
            Assert.Equal(precise.TotalContextSwitches, actual.ContextSwitches);
            Assert.Equal(precise.TotalReadyCount, actual.ReadyCount);
            Assert.Equal(precise.TotalReadyLatencyUs, actual.ReadyLatencyUs);
            Assert.Equal(blocked.TotalBlockedUs, actual.BlockedUs);
            Assert.Equal(blocked.ScopedCSwitches, actual.BlockedSwitchOutCount);
            Assert.Equal(blocked.MatchedIntervalCount, actual.BlockedIntervalCount);
            Assert.Equal(sampled.Rows.ToArray(), actual.TopCpuFunctions.ToArray());
            Assert.Equal(reasons.ToArray(), actual.TopWaitReasons.ToArray());
            Assert.Equal(stacks.Rows.ToArray(), actual.TopWaitFunctions.ToArray());
            Assert.Equal(sampled.StackCoverage, actual.CpuStackCoverage);
            Assert.Equal(stacks.StackCoverage, actual.WaitStackCoverage);
        }
    }

    [Fact]
    public void SnapshotPagesAreStableRetryableAndQueryBound()
    {
        var coordinator = new QueryResultCursorCoordinator(
            "principal-a", "off", new QueryResultCursorRegistry());
        var runtime = new ThreadComparisonPaginationRuntime(coordinator);
        var context = new TimelineQueryContext(
            "trace-a", "generation-a", TimelinePagination.ThreadCompareWindowsTool,
            ToolContractVersions.V2, null, new string('a', 64),
            TimelinePagination.ThreadCompareWindowsOrdering);
        var rows = Enumerable.Range(0, 5).Select(Row).ToArray();
        var complete = new ThreadCompareWindowsResponse(
            rows, [], null, null, "single_process", false, [], [], "ok",
            "observed", 5, null, [], "window-0",
            TotalWindowCount: rows.Length, ReturnedCount: rows.Length);

        var first = runtime.Start(context, complete, pageSize: 2);
        Assert.Equal(["window-0", "window-1"], first.Rows.Select(row => row.Name));
        Assert.StartsWith("twr_", first.ResultSetId, StringComparison.Ordinal);
        var cursor = Assert.IsType<string>(coordinator.FinalizeTimeline(
            context, null, 0, first.ReturnedCount, rows.Length, first.ResultSetId!));
        var second = runtime.Resume(context, cursor, pageSize: 2);
        var retry = runtime.Resume(context, cursor, pageSize: 2);
        Assert.Equal(["window-2", "window-3"], second.Rows.Select(row => row.Name));
        Assert.Equal(second.Rows, retry.Rows);
        Assert.Equal(first.ResultSetId, second.ResultSetId);
        Assert.Equal(1, runtime.AnalysisSnapshotCount);

        var mismatch = context with { QueryHash = new string('b', 64) };
        var error = Assert.Throws<QueryResultCursorException>(() =>
            runtime.Resume(mismatch, cursor, pageSize: 2));
        Assert.Equal(QueryResultCursorFailureKind.Invalid, error.Kind);
    }

    [Fact]
    public void RealEtlComparesOneResolvedThreadWhenConfigured()
    {
        var source = Environment.GetEnvironmentVariable("WPAMCP_REAL_ETL");
        if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

        var cache = new TraceCache(capacity: 1);
        var lifetime = FindBlockedThread(cache, source);
        if (lifetime is null || lifetime.EndUs - lifetime.StartUs < 2) return;
        var response = new ThreadComparisonTools(cache).ThreadCompareWindows(
            source,
            lifetime.Key.Process.Pid,
            lifetime.Key.Tid,
            Split(lifetime),
            top: 3,
            processStartUs: lifetime.Key.Process.StartUs,
            threadStartUs: lifetime.StartUs,
            threadGeneration: lifetime.Key.Generation);

        Assert.Equal(lifetime.Key, response.SelectedThread);
        Assert.Equal(2, response.Rows.Count);
        Assert.Equal("ok", response.ScopeStatus);
    }

    private static ThreadComparisonWindowRow Row(int index) => new(
        $"window-{index}", index * 10, index * 10 + 10, 10,
        1, 2, 1, 1, 1, 3, 1, 1, [], [], [], null, null,
        "observed", "observed", "observed", "unavailable",
        "skipped", "no_stacks", null, null, null, "stacks_unavailable", []);

    private static ThreadComparisonWindowInput[] Split(ThreadLifetime lifetime)
    {
        var split = lifetime.StartUs + (lifetime.EndUs - lifetime.StartUs) / 2;
        if (split <= lifetime.StartUs || split >= lifetime.EndUs)
            throw new InvalidOperationException("Selected thread lifetime is too short to split.");
        return
        [
            new("fast", lifetime.StartUs, split),
            new("slow", split, lifetime.EndUs),
        ];
    }

    private static ThreadLifetime? FindBlockedThread(TraceCache cache, string path)
    {
        var trace = cache.Get(path);
        var identities = TraceIdentityIndex.For(trace);
        var aggregate = WpaMcp.Analyzers.WaitAnalysis.Analyze(
            trace, top: int.MaxValue, pid: null, startUs: null, endUs: null);
        foreach (var row in aggregate.Rows.Where(row => row.BlockedUs > 0))
        {
            var lifetime = identities.Threads.Lifetimes.FirstOrDefault(candidate =>
                candidate.Key.Process.Pid == row.Pid &&
                candidate.Key.Process.StartUs == row.ProcessStartUs &&
                candidate.Key.Tid == row.Tid &&
                candidate.Key.Generation == row.ThreadGeneration &&
                candidate.StartUs == row.ThreadStartUs);
            if (lifetime is not null && lifetime.EndUs - lifetime.StartUs >= 2)
                return lifetime;
        }

        return null;
    }
}
