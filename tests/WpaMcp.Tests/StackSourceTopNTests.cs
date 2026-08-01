using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using Microsoft.Diagnostics.Symbols;
using Xunit;

namespace WpaMcp.Tests;

public class StackSourceTopNTests
{
    [Fact]
    public void AddInterval_SplitsMetricAcrossBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(0, 100), bucketCount: 2);

        histogram.AddDurationInterval(intervalStartUs: 25, intervalEndUs: 75);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build());
        Assert.Equal(100, result.EndUs);
        Assert.Equal(new long[] { 25, 25 }, result.Buckets);
    }

    [Fact]
    public void AddInterval_UsesHalfOpenBoundsAndPreservesUntouchedBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(10, 95), bucketCount: 10);

        histogram.AddDurationInterval(intervalStartUs: 28, intervalEndUs: 29);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build());
        Assert.Equal(9, result.BucketWidthUs);
        Assert.Equal(
            new long[] { 0, 0, 1, 0, 0, 0, 0, 0, 0, 0 },
            result.Buckets);
    }

    [Fact]
    public void Pct_ClampsFloatingPointOvershoot()
    {
        Assert.Equal(100.0, StackSourceTopN.Pct(total: 100, n: 100.01));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 100, n: -1));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 0, n: 1));
    }

    [Fact]
    public void PctOfTrace_OnlyEmitsForFilteredViews()
    {
        Assert.Null(StackSourceTopN.PctOfTrace(hasFilter: false, traceTotal: 100, n: 50));
        Assert.Equal(50.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 50));
        Assert.Equal(100.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 100.01));
    }

    [Fact]
    public void DomainCoverageAccumulator_ReportsAllFourStates()
    {
        var noEvents = new DomainStackCoverageAccumulator("cpu").Snapshot();
        Assert.Equal("no_events", noEvents.CoverageState);
        Assert.True(noEvents.UnknownStackFrameIsSynthetic);
        Assert.False(noEvents.ContainsSyntheticUnknown);

        var noStacks = new DomainStackCoverageAccumulator("file_io");
        noStacks.Observe(hasStack: false, metric: 10);
        Assert.Equal("no_stacks", noStacks.Snapshot().CoverageState);
        Assert.True(noStacks.Snapshot().ContainsSyntheticUnknown);
        Assert.Equal("?!?", noStacks.Snapshot().SyntheticUnknownFrame);

        var partial = new DomainStackCoverageAccumulator("disk_io");
        partial.Observe(hasStack: false, metric: 10);
        partial.Observe(hasStack: true, metric: 30);
        Assert.Equal("partial", partial.Snapshot().CoverageState);
        Assert.True(partial.Snapshot().ContainsSyntheticUnknown);
        Assert.Equal(50, partial.Snapshot().StackCoveragePct);
        Assert.Equal(75, partial.Snapshot().MetricStackCoveragePct);

        var full = new DomainStackCoverageAccumulator("image_load");
        full.Observe(hasStack: true, metric: 1);
        Assert.Equal("full", full.Snapshot().CoverageState);
        Assert.True(full.Snapshot().UnknownStackFrameIsSynthetic);
        Assert.False(full.Snapshot().ContainsSyntheticUnknown);
        Assert.Null(full.Snapshot().SyntheticUnknownFrame);
    }

    [Fact]
    public void DomainCoverageAccumulator_PreservesExactLongMetricsAboveFloatPrecision()
    {
        const long metric = 16_777_217;
        var accumulator = new DomainStackCoverageAccumulator("hard_fault", "bytes");

        accumulator.Observe(hasStack: true, metric);

        DomainStackCoverage result = accumulator.Snapshot();
        Assert.Equal(metric, result.TotalMetric);
        Assert.Equal(metric, result.StackedMetric);
        Assert.Equal("bytes", result.MetricName);
        Assert.True(result.UnknownStackFrameIsSynthetic);
    }

    [Fact]
    public void SymbolFrameMetrics_ReportDifferentUniqueAndWeightedRates()
    {
        var accumulator = new SymbolFrameMetricAccumulator("bytes");
        accumulator.ObserveCodeFrame(frameIdentity: 1, module: "a", resolved: true, metric: 2);
        accumulator.ObserveCodeFrame(frameIdentity: 2, module: "b", resolved: false, metric: 8);

        var stats = accumulator.Snapshot(SymbolLookupAttempt.Executed());

        Assert.Equal(1, stats.Resolved);
        Assert.Equal(1, stats.Unresolved);
        Assert.Equal(0.5, stats.ResolutionRate);
        Assert.Equal(0.5, stats.ObservedUniqueCodeFrameNameResolutionRate);
        Assert.Equal(0.2, stats.ObservedMetricWeightedCodeFrameNameResolutionRate);
        Assert.Equal(10, stats.TotalCodeFrameMetric);
        Assert.Equal("bytes", stats.MetricName);
    }

    [Fact]
    public void ComputeSymbolStats_ExcludesSyntheticSampleAndPreservesExactLongMetric()
    {
        const long metric = 16_777_217;
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        raw.AddSample(raw.NoStackCallStack, hasStack: false, timeRelativeMSec: 0, metric);
        raw.Source.DoneAddingSamples();

        var stats = StackSourceTopN.ComputeSymbolStats(raw, SymbolLookupAttempt.Skipped());

        Assert.Equal(0, stats.UniqueCodeFrameCount);
        Assert.Null(stats.ResolutionRate);
        Assert.Null(stats.ObservedMetricWeightedCodeFrameNameResolutionRate);
        Assert.Equal(1, stats.ExcludedSyntheticOrPseudoUniqueFrames);
        Assert.Equal(metric, stats.ExcludedSyntheticOrPseudoFrameMetric);
        Assert.Equal("exact_long", stats.MetricAccounting);
    }

    [Fact]
    public void TryLookupWarmSymbols_ReportsSkippedExecutedAndFailed()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test");
        raw.Source.DoneAddingSamples();
        using var reader = StackSourceTopN.OpenSymbolReader(trace, TextWriter.Null);
        var calls = 0;

        var skipped = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, resolveSymbols: false, reader,
            (_, _, _) => calls++);
        var executed = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, resolveSymbols: true, reader,
            (_, _, _) => calls++);
        var failed = StackSourceTopN.TryLookupWarmSymbols(
            raw.Source, resolveSymbols: true, reader,
            (_, _, _) => throw new InvalidOperationException("lookup exploded"));

        Assert.Equal("skipped", skipped.State);
        Assert.Equal("executed", executed.State);
        Assert.Equal("failed", failed.State);
        Assert.Contains("lookup exploded", failed.Failure);
        Assert.Equal(1, calls);

        var warnings = new List<string>();
        var failedStats = new SymbolFrameMetricAccumulator("count").Snapshot(failed);
        StackSourceTopN.AddSymbolLookupWarning(warnings, failedStats);
        Assert.Contains(warnings, warning =>
            warning.Contains("symbol_lookup_state=failed", StringComparison.Ordinal));
    }
}
