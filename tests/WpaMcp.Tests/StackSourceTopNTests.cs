using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Stacks;
using System.ComponentModel;
using System.Reflection;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class StackSourceTopNTests
{
    [Fact]
    public void TopByValue_UsesOrdinalKeyAsStableTieBreaker()
    {
        var source = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["zeta"] = 7,
            ["Alpha"] = 7,
            ["alpha"] = 7,
            ["larger"] = 8,
        };

        var rows = StackSourceTopN.TopByValue(
            source,
            4,
            static (key, value) => (key, value));

        Assert.Equal(
            ["larger", "Alpha", "alpha", "zeta"],
            rows.Select(row => row.key));
    }

    [Fact]
    public void AddInterval_SplitsMetricAcrossBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(0, 100), bucketCount: 2);

        histogram.AddDurationInterval(intervalStartUs: 25, intervalEndUs: 75);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build("event_count"));
        Assert.Equal("event_count", result.Unit);
        Assert.Equal(100, result.EndUs);
        Assert.Equal(new long[] { 25, 25 }, result.Buckets);
    }

    [Fact]
    public void AddInterval_UsesHalfOpenBoundsAndPreservesUntouchedBuckets()
    {
        var histogram = StackSourceTopN.WhenHistogram.ForWindow(
            new TimeWindow(10, 95), bucketCount: 10);

        histogram.AddDurationInterval(intervalStartUs: 28, intervalEndUs: 29);

        var result = Assert.IsType<WpaMcp.Output.TimeHistogram>(histogram.Build("microseconds"));
        Assert.Equal("microseconds", result.Unit);
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
    public void StackContract_PreservesAmbiguousProcessStatusAsNoDataReason()
    {
        var key = new ProcessInstanceKey(42, 0);
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 50),
            pid: 42,
            processStartUs: 0,
            lifetimes:
            [
                new ProcessLifetime(key, 75, true, true),
                new ProcessLifetime(key, 90, true, true),
            ]);

        var contract = StackResultContract.From(
            scope,
            filterSpecified: true,
            new DomainStackCoverageAccumulator("file_io").Snapshot(),
            traceEventCount: 1);

        Assert.Equal("ambiguous_process_instance", contract.ScopeStatus);
        Assert.Equal("ambiguous_process_instance", contract.NoDataReason);
        Assert.Equal("unknown", contract.CapabilityStatus);
        Assert.Equal(0, contract.MatchedEventCount);
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

    [Theory]
    [InlineData((1L << 24) - 1)]
    [InlineData(1L << 24)]
    [InlineData((1L << 24) + 1)]
    [InlineData(1L << 53)]
    [InlineData(long.MaxValue)]
    public void ExactFrameAndCallerCalleeMetrics_DoNotRoundTripThroughFloat(long metric)
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        var callerFrame = raw.Source.Interner.FrameIntern("test!Caller");
        var callerStack = raw.Source.Interner.CallStackIntern(
            callerFrame,
            StackSourceCallStackIndex.Invalid);
        var leafFrame = raw.Source.Interner.FrameIntern("test!Leaf");
        var leafStack = raw.Source.Interner.CallStackIntern(leafFrame, callerStack);
        raw.AddSample(leafStack, hasStack: true, timeRelativeMSec: 0, metric);
        raw.Source.DoneAddingSamples();
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);

        var projection = StackSourceTopN.ComputeExactFrameMetrics(normalized);
        var caller = Assert.Single(projection.Frames, frame => frame.Function == "test!Caller");
        var leaf = Assert.Single(projection.Frames, frame => frame.Function == "test!Leaf");
        Assert.Equal(metric, projection.TotalMetric);
        Assert.Equal(metric, caller.InclusiveMetric);
        Assert.Equal(0, caller.ExclusiveMetric);
        Assert.Equal(metric, leaf.InclusiveMetric);
        Assert.Equal(metric, leaf.ExclusiveMetric);

        var response = StackSourceTopN.ComputeCallerCallee(
            normalized,
            focusFunction: "test!Leaf",
            top: 5,
            metricName: "bytes",
            stats: new SymbolFrameMetricAccumulator("bytes").Snapshot(
                SymbolLookupAttempt.Skipped()),
            baseWarnings: [],
            resultContract: ObservedContract(1));
        Assert.Equal(metric, response.SourceTotalMetric);
        Assert.Equal(metric, response.FocusExclusiveMetric);
        Assert.Equal(metric, response.FocusInclusiveMetric);
        Assert.Equal(metric, Assert.Single(response.Callers).InclusiveMetric);
        Assert.Equal(metric, Assert.Single(response.Callees).InclusiveMetric);
        Assert.Equal("exact_long", response.MetricPrecision);
        Assert.Equal("exact_long", response.RowMetricAccounting);
    }

    [Fact]
    public void ExactFrameMetrics_ThrowsOnCheckedLongOverflow()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");

        // Bypass the independently checked coverage accumulator so this test reaches the
        // per-frame projection's own overflow boundary.
        AddRawSampleWithoutCoverage(raw, long.MaxValue, timeRelativeMSec: 0);
        AddRawSampleWithoutCoverage(raw, 1, timeRelativeMSec: 1);
        raw.Source.DoneAddingSamples();
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);

        Assert.Throws<OverflowException>(() =>
            StackSourceTopN.ComputeExactFrameMetrics(normalized));
        Assert.Throws<OverflowException>(() =>
            StackSourceTopN.ComputeCallerCallee(
                normalized,
                focusFunction: "?!?",
                top: 5,
                metricName: "bytes",
                stats: new SymbolFrameMetricAccumulator("bytes").Snapshot(
                    SymbolLookupAttempt.Skipped()),
                baseWarnings: []));
    }

    [Fact]
    public void ExactFrameMetrics_RecursionCountsInclusiveFrameOncePerSample()
    {
        const long metric = (1L << 24) + 1;
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        var recursiveFrame = raw.Source.Interner.FrameIntern("test!Recursive");
        var rootOccurrence = raw.Source.Interner.CallStackIntern(
            recursiveFrame,
            StackSourceCallStackIndex.Invalid);
        var middleFrame = raw.Source.Interner.FrameIntern("test!Middle");
        var middleStack = raw.Source.Interner.CallStackIntern(middleFrame, rootOccurrence);
        var leafOccurrence = raw.Source.Interner.CallStackIntern(recursiveFrame, middleStack);
        raw.AddSample(leafOccurrence, hasStack: true, timeRelativeMSec: 0, metric);
        raw.Source.DoneAddingSamples();
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);

        var projection = StackSourceTopN.ComputeExactFrameMetrics(normalized);
        var recursive = Assert.Single(
            projection.Frames,
            frame => frame.Function == "test!Recursive");
        Assert.Equal(metric, recursive.ExclusiveMetric);
        Assert.Equal(metric, recursive.InclusiveMetric);
        Assert.Equal(1, recursive.ExclusiveCount);
        Assert.Equal(1, recursive.InclusiveCount);

        var response = StackSourceTopN.ComputeCallerCallee(
            normalized,
            focusFunction: "test!Recursive",
            top: 5,
            metricName: "bytes",
            stats: new SymbolFrameMetricAccumulator("bytes").Snapshot(
                SymbolLookupAttempt.Skipped()),
            baseWarnings: [],
            resultContract: ObservedContract(1));
        Assert.Equal(metric, response.FocusExclusiveMetric);
        Assert.Equal(metric, response.FocusInclusiveMetric);
        Assert.Equal("test!Middle", Assert.Single(response.Callers).Function);
        Assert.Equal("<self>", Assert.Single(response.Callees).Function);
    }

    [Fact]
    public void ExactMetricTokens_SurviveRawAndNormalizedTimeSorting()
    {
        const long lateMetric = (1L << 24) + 1;
        const long earlyMetric = 1L << 53;
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        var lateStack = Stack(raw.Source, "test!Late");
        var earlyStack = Stack(raw.Source, "test!Early");

        raw.AddSample(lateStack, hasStack: true, timeRelativeMSec: 20, lateMetric);
        raw.AddSample(earlyStack, hasStack: true, timeRelativeMSec: 10, earlyMetric);
        raw.Source.DoneAddingSamples();

        Assert.Equal(10, raw.Source.GetSampleByIndex((StackSourceSampleIndex)0).TimeRelativeMSec);
        AssertExactLeafMetric(raw.Source, "test!Early", earlyMetric);
        AssertExactLeafMetric(raw.Source, "test!Late", lateMetric);

        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);
        Assert.Equal(10, normalized.GetSampleByIndex((StackSourceSampleIndex)0).TimeRelativeMSec);
        AssertExactLeafMetric(normalized, "test!Early", earlyMetric);
        AssertExactLeafMetric(normalized, "test!Late", lateMetric);
    }

    [Fact]
    public void ExactMetricTokens_SurviveEqualTimeRawAndNormalizedSorting()
    {
        const long alphaMetric = (1L << 24) + 1;
        const long zuluMetric = 1L << 53;
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        var zuluStack = Stack(raw.Source, "test!Zulu");
        var alphaStack = Stack(raw.Source, "test!Alpha");

        raw.AddSample(zuluStack, hasStack: true, timeRelativeMSec: 10, zuluMetric);
        raw.AddSample(alphaStack, hasStack: true, timeRelativeMSec: 10, alphaMetric);
        raw.Source.DoneAddingSamples();

        AssertExactLeafMetric(raw.Source, "test!Alpha", alphaMetric);
        AssertExactLeafMetric(raw.Source, "test!Zulu", zuluMetric);
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);
        AssertExactLeafMetric(normalized, "test!Alpha", alphaMetric);
        AssertExactLeafMetric(normalized, "test!Zulu", zuluMetric);
    }

    [Fact]
    public void ExactFrameAndCallerCalleeTies_UseOrdinalFunctionOrder()
    {
        var projection = new ExactStackMetricProjection(
            Frames:
            [
                new ExactStackFrameMetric("test!Zulu", 5, 5, 1, 1),
                new ExactStackFrameMetric("test!Alpha", 5, 5, 1, 1),
            ],
            TotalMetric: 10,
            TotalCount: 2);
        Assert.Equal(
            ["test!Alpha", "test!Zulu"],
            StackSourceTopN.RankExactFrames(projection)
                .Select(frame => frame.Function));
        Assert.Equal(
            "test!Alpha",
            Assert.Single(StackSourceTopN.RankExactFrames(projection).Take(1)).Function);

        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        var alphaFrame = raw.Source.Interner.FrameIntern("test!Alpha");
        var alphaRoot = raw.Source.Interner.CallStackIntern(
            alphaFrame,
            StackSourceCallStackIndex.Invalid);
        var zuluFrame = raw.Source.Interner.FrameIntern("test!Zulu");
        var zuluRoot = raw.Source.Interner.CallStackIntern(
            zuluFrame,
            StackSourceCallStackIndex.Invalid);
        var focusFrame = raw.Source.Interner.FrameIntern("test!Focus");
        var focusWithAlphaCaller = raw.Source.Interner.CallStackIntern(focusFrame, alphaRoot);
        var focusWithZuluCaller = raw.Source.Interner.CallStackIntern(focusFrame, zuluRoot);
        var focusRoot = raw.Source.Interner.CallStackIntern(
            focusFrame,
            StackSourceCallStackIndex.Invalid);
        var alphaWithFocusCaller = raw.Source.Interner.CallStackIntern(alphaFrame, focusRoot);
        var zuluWithFocusCaller = raw.Source.Interner.CallStackIntern(zuluFrame, focusRoot);
        raw.AddSample(focusWithZuluCaller, true, 10, 5);
        raw.AddSample(focusWithAlphaCaller, true, 10, 5);
        raw.AddSample(zuluWithFocusCaller, true, 10, 5);
        raw.AddSample(alphaWithFocusCaller, true, 10, 5);
        raw.Source.DoneAddingSamples();
        var normalized = StackSourceTopN.BuildNormalized(
            raw.Source,
            trace,
            excludeEtwSelfOverhead: false);

        var response = StackSourceTopN.ComputeCallerCallee(
            normalized,
            focusFunction: "test!Focus",
            top: 2,
            metricName: "bytes",
            stats: new SymbolFrameMetricAccumulator("bytes").Snapshot(
                SymbolLookupAttempt.Skipped()),
            baseWarnings: [],
            resultContract: ObservedContract(4));
        Assert.Equal(["<root>", "test!Alpha"], response.Callers.Select(node => node.Function));
        Assert.Equal(["<self>", "test!Alpha"], response.Callees.Select(node => node.Function));
    }

    [Fact]
    public void ExactFrameMetrics_RejectsSourceWithoutExactParallelSeries()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var source = new MutableTraceEventStackSource(trace);
        var frame = source.Interner.FrameIntern("test!Leaf");
        var stack = source.Interner.CallStackIntern(
            frame,
            StackSourceCallStackIndex.Invalid);
        var sample = new StackSourceSample(source)
        {
            StackIndex = stack,
            Metric = (float)((1L << 24) + 1),
        };
        source.AddSample(sample);
        source.DoneAddingSamples();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StackSourceTopN.ComputeExactFrameMetrics(source));
        Assert.Contains("exact_stack_metrics_unavailable", exception.Message);
    }

    [Fact]
    public void ExactFrameMetrics_RejectsDuplicateExactTokens()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        AddRawSampleWithToken(raw, metric: 1, timeRelativeMSec: 0, token: 0);
        AddRawSampleWithToken(raw, metric: 2, timeRelativeMSec: 1, token: 0);
        raw.Source.DoneAddingSamples();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StackSourceTopN.ComputeExactFrameMetrics(raw.Source));
        Assert.Contains("exact_stack_metric_token_duplicate", exception.Message);
    }

    [Fact]
    public void ExactFrameMetrics_RejectsOutOfRangeExactToken()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        AddRawSampleWithToken(raw, metric: 1, timeRelativeMSec: 0, token: 1);
        raw.Source.DoneAddingSamples();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StackSourceTopN.ComputeExactFrameMetrics(raw.Source));
        Assert.Contains("exact_stack_metric_token_out_of_range", exception.Message);
    }

    [Fact]
    public void ExactFrameMetrics_RejectsIncompleteExactSeries()
    {
        var trace = new TraceCache(capacity: 2).Get("fixtures/small_cpu.etl");
        var raw = StackSourceTopN.CreateRawSource(trace, "test", "bytes");
        raw.AddSample(raw.NoStackCallStack, false, 0, 1);
        raw.ExactSampleMetrics.Clear();
        raw.Source.DoneAddingSamples();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            StackSourceTopN.ComputeExactFrameMetrics(raw.Source));
        Assert.Contains("exact_stack_metrics_unavailable", exception.Message);
    }

    private static void AddRawSampleWithoutCoverage(
        StackSourceTopN.RawStackSource raw,
        long metric,
        double timeRelativeMSec)
        => AddRawSampleWithToken(
            raw,
            metric,
            timeRelativeMSec,
            raw.ExactSampleMetrics.Count);

    private static void AddRawSampleWithToken(
        StackSourceTopN.RawStackSource raw,
        long metric,
        double timeRelativeMSec,
        int token)
    {
        raw.Sample.StackIndex = raw.NoStackCallStack;
        raw.Sample.TimeRelativeMSec = timeRelativeMSec;
        raw.Sample.Metric = (float)metric;
        raw.Sample.Scenario = token;
        raw.Source.AddSample(raw.Sample);
        raw.ExactSampleMetrics.Add(metric);
    }

    private static StackSourceCallStackIndex Stack(
        MutableTraceEventStackSource source,
        string function)
    {
        var frame = source.Interner.FrameIntern(function);
        return source.Interner.CallStackIntern(
            frame,
            StackSourceCallStackIndex.Invalid);
    }

    private static StackResultContract ObservedContract(long matchedEventCount) => new(
        SelectedProcess: null,
        ScopeMode: "all_processes",
        PidReuseObserved: false,
        IncludedProcesses: Array.Empty<ProcessInstanceKey>(),
        ScopeStatus: "ok",
        CapabilityStatus: "observed",
        MatchedEventCount: matchedEventCount,
        NoDataReason: null);

    private static void AssertExactLeafMetric(
        MutableTraceEventStackSource source,
        string function,
        long expected)
    {
        var projection = StackSourceTopN.ComputeExactFrameMetrics(source);
        var frame = Assert.Single(
            projection.Frames,
            candidate => candidate.Function == function);
        Assert.Equal(expected, frame.ExclusiveMetric);
        Assert.Equal(expected, frame.InclusiveMetric);
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
        Assert.ThrowsAny<OperationCanceledException>(() =>
            StackSourceTopN.TryLookupWarmSymbols(
                raw.Source,
                resolveSymbols: true,
                reader,
                (_, _, _) => throw new OperationCanceledException("lookup cancelled")));

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

    [Theory]
    [InlineData(typeof(ClrTools), nameof(ClrTools.ClrAllocCallerCallee))]
    [InlineData(typeof(ClrTools), nameof(ClrTools.ClrExceptionCallerCallee))]
    [InlineData(typeof(ClrTools), nameof(ClrTools.ClrContentionCallerCallee))]
    [InlineData(typeof(GenericProviderTools), nameof(GenericProviderTools.GenericEventCallerCallee))]
    [InlineData(typeof(HeapTools), nameof(HeapTools.HeapAllocCallerCallee))]
    public void CallerCalleeFocusDescriptionsDeclareExactCaseSensitiveMatching(
        Type toolType,
        string methodName)
    {
        var parameter = Assert.Single(
            Assert.IsAssignableFrom<MethodInfo>(toolType.GetMethod(methodName)!)
                .GetParameters(),
            value => value.Name == "focusFunction");
        var description = Assert.IsType<DescriptionAttribute>(
            parameter.GetCustomAttribute<DescriptionAttribute>()).Description;

        Assert.Contains("exact", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("case-sensitive", description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("substring", description, StringComparison.OrdinalIgnoreCase);
    }
}
