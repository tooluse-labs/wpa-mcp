using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class CpuAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured by fixtures/capture_all.ps1

    [Fact]
    public void PassesScope_SelectsOneThreadGeneration()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 10, StartUs: 20),
            EndUs: 250,
            StartObserved: true,
            EndObserved: true);
        var thread = new ThreadLifetime(
            new ThreadInstanceKey(process.Key, Tid: 7, Generation: 1),
            StartUs: 80,
            EndUs: 220,
            StartObserved: true,
            EndObserved: true);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(100, 200),
            Pid: 10,
            Process: process,
            Thread: thread,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);

        Assert.True(CpuAnalysis.PassesScope(scope, pid: 10, tid: 7, timestampUs: 150));
        Assert.False(CpuAnalysis.PassesScope(scope, pid: 10, tid: 8, timestampUs: 150));
        Assert.False(CpuAnalysis.PassesScope(scope, pid: 11, tid: 7, timestampUs: 150));
        Assert.False(CpuAnalysis.PassesScope(scope, pid: 10, tid: 7, timestampUs: 200));
    }

    [Fact]
    public void ScopedTopFunctions_SampleSelectionDoesNotDependOnSymbolResolution()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = new TimeWindow(0, traceEndUs);
        var samples = new List<(int Pid, int Tid, long TimestampUs)>();
        foreach (var traceEvent in trace.Events)
        {
            if (traceEvent is SampledProfileTraceData sample)
            {
                samples.Add((
                    sample.ProcessID,
                    sample.ThreadID,
                    TraceTime.FromMilliseconds(sample.TimeStampRelativeMSec)));
            }
        }
        var selected = samples.First(sample => sample.Pid > 0 && sample.Tid > 0);
        var process = new ProcessLifetime(
            new ProcessInstanceKey(selected.Pid, StartUs: 0),
            traceEndUs,
            StartObserved: false,
            EndObserved: false);
        var thread = new ThreadLifetime(
            new ThreadInstanceKey(process.Key, selected.Tid, Generation: 1),
            StartUs: 0,
            EndUs: traceEndUs,
            StartObserved: false,
            EndObserved: false);
        var scope = new ThreadAnalysisScope(
            window,
            selected.Pid,
            process,
            thread,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var expectedSamples = samples
            .Count(sample => CpuAnalysis.PassesScope(
                scope,
                sample.Pid,
                sample.Tid,
                sample.TimestampUs));

        var unresolved = CpuAnalysis.TopFunctions(
            trace, top: 1000, scope, TextWriter.Null, resolveSymbols: false);
        var resolved = CpuAnalysis.TopFunctions(
            trace, top: 1000, scope, TextWriter.Null, resolveSymbols: true);

        Assert.True(expectedSamples > 0);
        Assert.Equal(expectedSamples, unresolved.Rows.Sum(row => row.ExclusiveSamples));
        Assert.Equal(expectedSamples, resolved.Rows.Sum(row => row.ExclusiveSamples));
    }

    [Fact]
    public void CpuTopFunctions_ReturnsAtMostTopRows()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10);
        Assert.True(resp.Rows.Count <= 10);
        var coverage = Assert.IsType<DomainStackCoverage>(resp.StackCoverage);
        Assert.Equal("cpu", coverage.Domain);
        Assert.Equal("count", coverage.MetricName);
        Assert.Equal(resp.TotalSamples, coverage.TotalEventCount);
        Assert.Equal(resp.TotalSamples, coverage.TotalMetric);
    }

    [Fact]
    public void CpuTopFunctions_RowsOrderedByExclusiveDescending()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].ExclusiveSamples >= resp.Rows[i].ExclusiveSamples);
    }

    [Fact]
    public void CpuTopFunctions_ResolutionStatsAreNullableWhenNoCodeFramesAreObserved()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10);

        if (resp.Stats.UniqueCodeFrameCount == 0)
        {
            Assert.Null(resp.Stats.ResolutionRate);
            Assert.Null(resp.Stats.ObservedUniqueCodeFrameNameResolutionRate);
            Assert.Null(resp.Stats.ObservedMetricWeightedCodeFrameNameResolutionRate);
        }
        else
        {
            Assert.NotNull(resp.Stats.ResolutionRate);
            Assert.InRange(resp.Stats.ResolutionRate.Value, 0.0, 1.0);
        }
    }

    [Fact]
    public void CpuTopFunctions_FilteredDefaultOmitsTracePctForSpeed()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10, startUs: 0);

        Assert.NotEmpty(resp.Rows);
        Assert.All(resp.Rows, r =>
        {
            Assert.Null(r.ExclusivePctOfTrace);
            Assert.Null(r.InclusivePctOfTrace);
        });
        Assert.Contains(resp.Warnings, w => w.Contains("PctOfTrace", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuTopFunctions_EndUsIsExclusive()
    {
        var sampleTimes = CpuSampleTimesUs();
        var distinctTimes = sampleTimes.Distinct().ToList();
        Assert.True(distinctTimes.Count > 1, "fixture must have CPU samples at multiple timestamps");

        var endUs = distinctTimes[(distinctTimes.Count - 1) / 2];
        var expectedSamples = sampleTimes.Count(t => t < endUs);
        Assert.InRange(expectedSamples, 1, sampleTimes.Count - 1);

        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10, endUs: endUs);

        var noStackRow = resp.Rows.First(r => r.Function == "?!?");
        Assert.Equal(expectedSamples, noStackRow.ExclusiveSamples);
        Assert.Null(noStackRow.ExclusivePctOfTrace);
        Assert.Null(noStackRow.InclusivePctOfTrace);
    }

    [Fact]
    public void CpuTopFunctions_FilteredCanOptIntoTracePct()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuTopFunctions(FixturePath, top: 10, startUs: 0, includeTracePct: true);

        Assert.NotEmpty(resp.Rows);
        Assert.All(resp.Rows, r =>
        {
            Assert.NotNull(r.ExclusivePctOfTrace);
            Assert.NotNull(r.InclusivePctOfTrace);
        });
        Assert.DoesNotContain(resp.Warnings, w => w.Contains("PctOfTrace", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuTopFunctions_RejectsBadTop()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuTopFunctions("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuTopFunctions("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void CpuTopFunctionsBatch_MatchesSinglePidResponses()
    {
        var pids = CpuSamplePids().Take(3).ToArray();
        Assert.NotEmpty(pids);

        var tools = new CpuTools(new TraceCache(capacity: 2));
        var batch = tools.CpuTopFunctionsBatch(FixturePath, pids, top: 5, startUs: 0, resolveSymbols: true);

        Assert.Empty(batch.Warnings);
        foreach (var pid in pids)
        {
            var single = tools.CpuTopFunctions(FixturePath, top: 5, pid: pid, startUs: 0, resolveSymbols: true);
            Assert.True(batch.PerPid.ContainsKey(pid), $"batch missing pid {pid}");
            var batched = batch.PerPid[pid];

            Assert.Equal(single.Stats.Resolved, batched.Stats.Resolved);
            Assert.Equal(single.Stats.Unresolved, batched.Stats.Unresolved);
            Assert.Equal(single.Rows.Select(r => r.Function), batched.Rows.Select(r => r.Function));
            Assert.Equal(single.Rows.Select(r => r.ExclusiveSamples), batched.Rows.Select(r => r.ExclusiveSamples));
            Assert.Equal(single.Warnings, batched.Warnings);
            Assert.Equal(single.HasSampledProfileStacks, batched.HasSampledProfileStacks);
            Assert.Equal(single.SymbolResolutionState, batched.SymbolResolutionState);
            Assert.Equal(single.StackCoverage, batched.StackCoverage);
        }
    }

    [Fact]
    public void CpuTopFunctionsBatch_PreservesPerPidStackAvailabilityMetadata()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var raws = new Dictionary<int, StackSourceTopN.RawStackSource>
        {
            [101] = StackSourceTopN.CreateRawSource(trace),
            [202] = StackSourceTopN.CreateRawSource(trace),
        };

        using var symbolReader = StackSourceTopN.OpenSymbolReader(TextWriter.Null);
        var result = CpuAnalysis.BuildTopFunctionsResponsesForRawSources(
            trace,
            raws,
            symbolReader,
            traceTotalSamples: 0,
            top: 5,
            excludeEtwSelfOverhead: false,
            hasFilter: true,
            includeTracePct: false,
            resolveSymbols: false,
            pidsWithSampledProfileStacks: new HashSet<int> { 202 });

        Assert.False(result[101].HasSampledProfileStacks);
        Assert.Equal("no_stacks", result[101].SymbolResolutionState);
        Assert.True(result[202].HasSampledProfileStacks);
        Assert.Equal("skipped", result[202].SymbolResolutionState);
    }

    [Fact]
    public void CpuTopFunctionsBatch_DefaultsToFastSymbolSkippedMode()
    {
        var pids = CpuSamplePids().Take(1).ToArray();
        Assert.NotEmpty(pids);

        var tools = new CpuTools(new TraceCache(capacity: 2));
        var batch = tools.CpuTopFunctionsBatch(FixturePath, pids, top: 5);

        Assert.Contains(batch.Warnings, w => w.Contains("Symbol resolution skipped", StringComparison.Ordinal));
        Assert.False(batch.Partial);
        Assert.Equal(pids.Length, batch.RequestedPidCount);
        Assert.Equal(batch.PerPid.Count, batch.CompletedPidCount);
        Assert.Equal(pids, batch.CompletedPids);
        Assert.Empty(batch.PidsNotFound ?? []);
        Assert.Empty(batch.PidsWithNoSamples ?? []);
    }

    [Fact]
    public void CpuTopFunctionsBatch_RejectsSelectorLengthMismatchBeforeTraceLoad()
    {
        var tools = new CpuTools(new TraceCache(capacity: 1));

        var error = Assert.Throws<ArgumentException>(() => tools.CpuTopFunctionsBatch(
            "missing.etl",
            pids: [10, 20],
            processStartUs: [100]));

        Assert.Contains("process_start_selector_count_mismatch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuTopFunctionsBatch_RejectsConflictingDuplicatePidSelectorsBeforeTraceLoad()
    {
        var tools = new CpuTools(new TraceCache(capacity: 1));

        var error = Assert.Throws<ArgumentException>(() => tools.CpuTopFunctionsBatch(
            "missing.etl",
            pids: [10, 10],
            processStartUs: [null, 100]));

        Assert.Contains("duplicate_pid_selector_conflict", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CpuTopFunctionsBatch_MissingPidIsScopeNotFound_NotCompletedEmptyResult()
    {
        var missingPid = int.MaxValue;
        var tools = new CpuTools(new TraceCache(capacity: 1));

        var response = tools.CpuTopFunctionsBatch(FixturePath, [missingPid], top: 5);

        Assert.Empty(response.PerPid);
        Assert.Empty(response.CompletedPids ?? []);
        Assert.Equal([missingPid], response.PidsNotFound);
        Assert.Empty(response.PidsWithNoSamples ?? []);
        Assert.Empty(response.SkippedPids ?? []);
        Assert.Equal(0, response.CompletedPidCount);
        Assert.False(response.Partial);
        var scope = Assert.Single(response.ScopeResults ?? []);
        Assert.Equal("scope_not_found", scope.ScopeStatus);
        Assert.Equal("scope_not_found", scope.ResultStatus);
        Assert.Equal("scope_not_found", scope.NoDataReason);
        Assert.Equal("unknown", scope.CapabilityStatus);
    }

    [Fact]
    public void CpuTopFunctions_MissingExactProcessReturnsStructuredScopeNotFound()
    {
        var tools = new CpuTools(new TraceCache(capacity: 1));

        var response = tools.CpuTopFunctions(
            FixturePath,
            pid: int.MaxValue,
            processStartUs: 42,
            top: 5);

        Assert.Empty(response.Rows);
        Assert.Equal("unresolved", response.ScopeMode);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Null(response.SelectedProcess);
        Assert.Empty(response.IncludedProcesses ?? []);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("scope_not_found:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuTopFunctionsBatch_ExistingInstanceWithoutSamplesIsCompletedAndClassified()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var identities = TraceIdentityIndex.For(trace);
        var samplePoints = trace.Events
            .OfType<SampledProfileTraceData>()
            .Select(sample => (sample.ProcessID, TimeUs: TraceTime.FromMilliseconds(sample.TimeStampRelativeMSec)))
            .ToHashSet();
        var candidate = identities.Processes.Lifetimes
            .Where(lifetime =>
                lifetime.Key.Pid > 0 &&
                lifetime.Key.StartUs >= 0 &&
                lifetime.EndUs > lifetime.Key.StartUs)
            .Select(lifetime =>
            {
                var end = Math.Min(lifetime.EndUs, identities.TraceEndUs);
                var time = lifetime.Key.StartUs;
                while (time < end && samplePoints.Contains((lifetime.Key.Pid, time)))
                    time++;
                return (Lifetime: lifetime, TimeUs: time, EndUs: end);
            })
            .First(item => item.TimeUs < item.EndUs);
        var tools = new CpuTools(new TraceCache(capacity: 1));

        var response = tools.CpuTopFunctionsBatch(
            FixturePath,
            [candidate.Lifetime.Key.Pid],
            top: 5,
            startUs: candidate.TimeUs,
            endUs: candidate.TimeUs + 1,
            processStartUs: [candidate.Lifetime.Key.StartUs]);

        var perPid = Assert.Single(response.PerPid);
        Assert.Equal(candidate.Lifetime.Key.Pid, perPid.Key);
        Assert.Empty(perPid.Value.Rows);
        Assert.Equal(0, perPid.Value.TotalSamples);
        Assert.Equal("single_process", perPid.Value.ScopeMode);
        Assert.Equal(candidate.Lifetime.Key, perPid.Value.SelectedProcess);
        Assert.Equal("no_events_in_scope", perPid.Value.NoDataReason);
        Assert.Equal([candidate.Lifetime.Key.Pid], response.CompletedPids);
        Assert.Empty(response.PidsNotFound ?? []);
        Assert.Equal([candidate.Lifetime.Key.Pid], response.PidsWithNoSamples);
        Assert.Equal(1, response.CompletedPidCount);
        Assert.False(response.Partial);
        var scope = Assert.Single(response.ScopeResults ?? []);
        Assert.Equal("completed_no_samples", scope.ResultStatus);
        Assert.Equal("no_events_in_scope", scope.NoDataReason);
        Assert.Equal(0, scope.MatchedSampleCount);
        Assert.Equal("unknown", scope.CapabilityStatus);
        Assert.Contains(perPid.Value.Warnings, warning =>
            warning.StartsWith("no_events_in_scope:", StringComparison.Ordinal));
        Assert.DoesNotContain(perPid.Value.Warnings, warning =>
            warning.Contains("keyword disabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuSampleCapabilityContract_TraceAbsentClassifiesTopCallerCalleeAndBatch()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var identities = TraceIdentityIndex.For(trace);
        var samplePoints = trace.Events
            .OfType<SampledProfileTraceData>()
            .Select(sample => (
                sample.ProcessID,
                TimeUs: TraceTime.FromMilliseconds(sample.TimeStampRelativeMSec)))
            .ToHashSet();
        var candidate = identities.Processes.Lifetimes
            .Where(lifetime =>
                lifetime.Key.Pid > 0 &&
                lifetime.Key.StartUs >= 0 &&
                lifetime.EndUs > lifetime.Key.StartUs)
            .Select(lifetime =>
            {
                var end = Math.Min(lifetime.EndUs, identities.TraceEndUs);
                var time = lifetime.Key.StartUs;
                while (time < end && samplePoints.Contains((lifetime.Key.Pid, time)))
                    time++;
                return (Lifetime: lifetime, TimeUs: time, EndUs: end);
            })
            .First(item => item.TimeUs < item.EndUs);
        var window = new TimeWindow(candidate.TimeUs, candidate.TimeUs + 1);
        var processScope = ProcessAnalysisScope.Resolve(
            window,
            candidate.Lifetime.Key.Pid,
            candidate.Lifetime.Key.StartUs,
            identities);
        var threadScope = CpuTools.ResolveStackScope(
            window,
            candidate.Lifetime.Key.Pid,
            tid: null,
            candidate.Lifetime.Key.StartUs,
            threadStartUs: null,
            identities,
            processScope);

        var top = CpuAnalysis.TopFunctions(
            trace,
            top: 5,
            scope: threadScope,
            symbolLog: TextWriter.Null,
            resolveSymbols: false,
            hasFilter: true,
            processScope: processScope,
            traceHasCpuSamples: false);
        var callerCallee = CpuAnalysis.CallerCallee(
            trace,
            focusFunction: "missing::focus",
            top: 5,
            scope: threadScope,
            symbolLog: TextWriter.Null,
            resolveSymbols: false,
            processScope: processScope,
            traceHasCpuSamples: false);
        var batch = CpuAnalysis.ExecuteTopFunctionsBatch(
            trace,
            top: 5,
            selectors:
            [
                new CpuAnalysis.BatchSelector(
                    candidate.Lifetime.Key.Pid,
                    candidate.Lifetime.Key.StartUs),
            ],
            window,
            TextWriter.Null,
            excludeEtwSelfOverhead: false,
            includeTracePct: false,
            resolveSymbols: false,
            timeBudgetMs: 100_000,
            traceHasCpuSamples: false);

        Assert.Equal("not_observed", top.CapabilityStatus);
        Assert.Equal("event_class_not_observed", top.NoDataReason);
        Assert.Equal(0, top.MatchedEventCount);
        Assert.Equal("not_observed", callerCallee.CapabilityStatus);
        Assert.Equal("event_class_not_observed", callerCallee.NoDataReason);
        Assert.Equal(0, callerCallee.MatchedEventCount);
        var perPid = Assert.Single(batch.PerPid).Value;
        Assert.Equal("not_observed", perPid.CapabilityStatus);
        Assert.Equal("event_class_not_observed", perPid.NoDataReason);
        var scope = Assert.Single(batch.ScopeResults);
        Assert.Equal("not_observed", scope.CapabilityStatus);
        Assert.Equal("event_class_not_observed", scope.NoDataReason);
    }

    [Fact]
    public void CpuTopFunctionsBatch_BudgetStopClassifiesResolvedPidAsSkipped()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var pid = CpuSamplePids().First();
        var window = new TimeWindow(
            0,
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds));
        var execution = CpuAnalysis.ExecuteTopFunctionsBatch(
            trace,
            top: 5,
            selectors: [new CpuAnalysis.BatchSelector(pid, ProcessStartUs: null)],
            window,
            TextWriter.Null,
            excludeEtwSelfOverhead: false,
            includeTracePct: false,
            resolveSymbols: false,
            timeBudgetMs: 100_000,
            stopRequested: () => true);

        Assert.Empty(execution.PerPid);
        Assert.Empty(execution.CompletedPids);
        Assert.Equal([pid], execution.SkippedPids);
        Assert.True(execution.Partial);
        var scope = Assert.Single(execution.ScopeResults);
        Assert.Equal("budget_skipped", scope.ResultStatus);
        Assert.Equal("budget_exhausted", scope.NoDataReason);
        Assert.Contains(execution.Warnings, warning =>
            warning.StartsWith("time_budget_exhausted:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuTopFunctionsBatch_ReuseScopeMetadataDistinguishesAggregateAndExactInstance()
    {
        ProcessLifetime[] lifetimes =
        [
            new(new ProcessInstanceKey(42, 100), 200, true, true),
            new(new ProcessInstanceKey(42, 300), 400, true, true),
        ];
        var window = new TimeWindow(0, 500);

        var aggregate = Assert.Single(CpuAnalysis.ResolveBatchScopes(
            window,
            [new CpuAnalysis.BatchSelector(42, ProcessStartUs: null)],
            lifetimes));
        var exact = Assert.Single(CpuAnalysis.ResolveBatchScopes(
            window,
            [new CpuAnalysis.BatchSelector(42, ProcessStartUs: 300)],
            lifetimes));

        Assert.Equal("pid_aggregate", aggregate.Scope.ScopeMode);
        Assert.True(aggregate.Scope.PidReuseObserved);
        Assert.Null(aggregate.Scope.SelectedProcess);
        Assert.Equal(
            [new ProcessInstanceKey(42, 100), new ProcessInstanceKey(42, 300)],
            aggregate.Scope.IncludedProcesses);
        Assert.Equal("single_process", exact.Scope.ScopeMode);
        Assert.True(exact.Scope.PidReuseObserved);
        Assert.Equal(new ProcessInstanceKey(42, 300), exact.Scope.SelectedProcess);
        Assert.Equal([new ProcessInstanceKey(42, 300)], exact.Scope.IncludedProcesses);
    }

    [Fact]
    public void CpuTopFunctionsBatch_PidCountEnforcesSharedBoundary()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var accepted = Enumerable.Range(1, Validation.MaxCollectionItems).ToArray();
        var rejected = Enumerable.Range(1, Validation.MaxCollectionItems + 1).ToArray();

        Assert.Throws<FileNotFoundException>(() => tools.CpuTopFunctionsBatch(
            "missing-before-validation.etl",
            accepted));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuTopFunctionsBatch(
            "missing-before-validation.etl",
            rejected));
    }

    [Fact]
    public void CpuTopFunctionsBatch_IsolatesPerPidProjectionFailures()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var raws = new Dictionary<int, StackSourceTopN.RawStackSource>
        {
            [101] = StackSourceTopN.CreateRawSource(trace),
            [202] = StackSourceTopN.CreateRawSource(trace),
        };
        var warnings = new List<string>();
        var okResponse = new CpuTopFunctionsResponse(
            Array.Empty<CpuFunctionRow>(),
            new SymbolStats(Resolved: 0, Unresolved: 0, ResolutionRate: 1.0, TopUnresolvedModules: Array.Empty<UnresolvedModule>()),
            Array.Empty<string>());

        using var symbolReader = StackSourceTopN.OpenSymbolReader(TextWriter.Null);
        var result = CpuAnalysis.BuildTopFunctionsResponsesForRawSources(
            trace,
            raws,
            symbolReader,
            traceTotalSamples: 0,
            top: 5,
            excludeEtwSelfOverhead: false,
            hasFilter: true,
            includeTracePct: false,
            warnings,
            project: (pid, _) =>
            {
                if (pid == 101) throw new InvalidOperationException("boom");
                return okResponse;
            });

        Assert.False(result.ContainsKey(101));
        Assert.Same(okResponse, result[202]);
        Assert.Contains("pid 101: boom", warnings);
    }

    [Fact]
    public void CpuCallerCallee_OnNoStackRootReturnsExpectedShape()
    {
        // small_cpu.etl was captured without Sample-stackwalks enabled, so 100% of CPU
        // samples land on the synthetic "?!?" root. Test against that — it's the only
        // frame guaranteed to be present, and exercising it validates the caller/callee
        // mechanics on a sample with no real stack:
        //   focusInclusive == focusExclusive == totalSamples (every sample IS the ?!? leaf)
        //   Callers should contain a single "<root>" entry (?!? was interned with Invalid caller)
        //   Callees should contain a single "<self>" entry (?!? is always the leaf)
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var topResp = tools.CpuTopFunctions(FixturePath, top: 5);
        Assert.Contains(topResp.Rows, r => r.Function == "?!?");
        var noStackRow = topResp.Rows.First(r => r.Function == "?!?");

        var ccResp = tools.CpuCallerCallee(FixturePath, function: "?!?", top: 10);
        Assert.Equal("?!?", ccResp.FocusFunction);
        Assert.Equal("samples", ccResp.MetricName);
        Assert.True(ccResp.FocusInclusiveMetric > 0,
            $"?!? should have inclusive samples > 0; got {ccResp.FocusInclusiveMetric}");
        Assert.Equal(topResp.StackCoverage, ccResp.StackCoverage);
        Assert.Equal(noStackRow.InclusiveSamples, ccResp.FocusInclusiveMetric);
        // ?!? is the leaf of every no-stack sample, so exclusive == inclusive.
        Assert.Equal(ccResp.FocusInclusiveMetric, ccResp.FocusExclusiveMetric);
        Assert.Contains(ccResp.Callers, c => c.Function == "<root>");
        Assert.Contains(ccResp.Callees, c => c.Function == "<self>");
    }

    [Fact]
    public void CpuCallerCallee_UnknownFunctionWithoutStacksReportsStacksUnavailable()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuCallerCallee(FixturePath, function: "this::is::not::a::real::frame", top: 10);
        Assert.Equal(0, resp.FocusInclusiveMetric);
        Assert.Empty(resp.Callers);
        Assert.Empty(resp.Callees);
        Assert.Equal("stacks_unavailable", resp.NoDataReason);
        Assert.Equal(0, resp.StackCoverage?.StackedEventCount);
        Assert.Contains(resp.Warnings, w =>
            w.StartsWith("stacks_unavailable:", StringComparison.Ordinal));
        Assert.DoesNotContain(resp.Warnings, w =>
            w.StartsWith("focus_not_found:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuCallerCallee_RejectsBadInput()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "x", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "x", top: 1001));
        Assert.Throws<ArgumentException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "", top: 10));
        Assert.Throws<ArgumentException>(() =>
            tools.CpuCallerCallee("nonexistent.etl", function: "  ", top: 10));
    }

    [Fact]
    public void CpuCallerCallee_CallersAndCalleesOrderedByInclusiveDesc()
    {
        // small_cpu.etl has only ?!? in the data (no Sample-stackwalks), so this collapses
        // to single-row checks where ordering is trivially descending. The assertion still
        // guards against a regression that produces UNORDERED output on richer traces.
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuCallerCallee(FixturePath, function: "?!?", top: 50);
        for (var i = 1; i < resp.Callers.Count; i++)
            Assert.True(resp.Callers[i - 1].InclusiveMetric >= resp.Callers[i].InclusiveMetric);
        for (var i = 1; i < resp.Callees.Count; i++)
            Assert.True(resp.Callees[i - 1].InclusiveMetric >= resp.Callees[i].InclusiveMetric);
    }

    private static List<long> CpuSampleTimesUs()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var times = new List<long>();
        foreach (var ev in trace.Events)
        {
            if (ev is SampledProfileTraceData)
                times.Add((long)(ev.TimeStampRelativeMSec * 1000));
        }
        return times;
    }

    private static List<int> CpuSamplePids()
    {
        var trace = new TraceCache(capacity: 1).Get(FixturePath);
        var pids = new List<int>();
        foreach (var ev in trace.Events)
        {
            if (ev is SampledProfileTraceData && ev.ProcessID > 0 && !pids.Contains(ev.ProcessID))
                pids.Add(ev.ProcessID);
        }
        return pids;
    }
}
