using System.Diagnostics;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class CpuPreciseAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void CpuPreciseAccumulator_ExactThreadScopeBypassesProcessTopN()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var dominantThread = new ThreadInstanceKey(process.Key, Tid: 1, Generation: 1);
        var selectedLifetime = new ThreadLifetime(
            new ThreadInstanceKey(process.Key, Tid: 42, Generation: 1),
            StartUs: 0,
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 100,
            Process: process,
            Thread: selectedLifetime,
            AggregatesPidLifetimes: false,
            PidReuseObserved: true);
        var accumulator = new CpuPreciseAccumulator(
            top: 1,
            scope,
            traceEndUs: 200,
            threadStartUs: thread => thread == selectedLifetime.Key ? 7 : 3);

        accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(dominantThread, TimestampUs: 0));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: dominantThread,
            NewProcessName: "target",
            ProcessorNumber: 0,
            TimestampUs: 10));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: dominantThread,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 110));

        accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(selectedLifetime.Key, TimestampUs: 0));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: selectedLifetime.Key,
            NewProcessName: "target",
            ProcessorNumber: 1,
            TimestampUs: 20));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: selectedLifetime.Key,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 1,
            TimestampUs: 30));

        var response = accumulator.BuildResponse();

        var row = Assert.Single(response.Rows);
        Assert.Equal(42, row.Tid);
        Assert.Equal(10, row.CpuUs);
        Assert.Equal(1, row.ReadyCount);
        Assert.Equal(20, row.ReadyLatencyUs);
        Assert.Equal(10, response.TotalCpuUs);
        Assert.Equal(1, response.TotalReadyCount);
        Assert.Equal(20, response.TotalReadyLatencyUs);
        Assert.Equal(process.Key.StartUs, row.ProcessStartUs);
        Assert.Equal(selectedLifetime.Key.Generation, row.ThreadGeneration);
        Assert.Equal(7, row.ThreadStartUs);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([process.Key], response.IncludedProcesses);
        Assert.Equal(2, response.MatchedEventCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.DoesNotContain(
            response.Warnings,
            warning => warning.StartsWith("pid_aggregate:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuPreciseAccumulator_RawScopedUnresolvedSideReportsUnattributed()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 200,
            traceEndUs: 200);
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 50));
        accumulator.ReportUnresolvedCSwitchSide(pid: 100, tid: 42, timestampUs: 50);

        var response = accumulator.BuildResponse();

        Assert.Empty(response.Rows);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal(1, response.TraceIdentityUnresolvedCSwitchSideCount);
        Assert.Equal(1, response.ScopedIdentityUnresolvedCSwitchSideCount);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("source_events_unattributed:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuPreciseAccumulator_ExactThreadZeroPlaceholderStillReportsNoEvents()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var selected = new ThreadLifetime(
            new ThreadInstanceKey(process.Key, Tid: 42, Generation: 1),
            StartUs: 0,
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var other = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 300, StartUs: 0), Tid: 7, Generation: 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: process.Key.Pid,
            Process: process,
            Thread: selected,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var accumulator = new CpuPreciseAccumulator(top: 10, scope, traceEndUs: 200);
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: other,
            OldProcessName: "other",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 50));

        var response = accumulator.BuildResponse();

        var row = Assert.Single(response.Rows);
        Assert.Equal(selected.Key.Tid, row.Tid);
        Assert.Equal(0, row.ContextSwitches);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void CpuPreciseAccumulator_PidScopeKeepsReusedPidAndTidInstancesSeparate()
    {
        var firstThread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            Tid: 42,
            Generation: 1);
        var secondThread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 100, StartUs: 100),
            Tid: 42,
            Generation: 1);
        var includedProcesses = new[] { firstThread.Process, secondThread.Process };
        var includedLifetimes = new[]
        {
            new ProcessLifetime(
                firstThread.Process, EndUs: 100,
                StartObserved: true, EndObserved: true),
            new ProcessLifetime(
                secondThread.Process, EndUs: 200,
                StartObserved: true, EndObserved: true),
        };
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 100,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: true,
            PidReuseObserved: true,
            IncludedProcesses: includedProcesses,
            IncludedProcessLifetimes: includedLifetimes);
        var accumulator = new CpuPreciseAccumulator(top: 2, scope, traceEndUs: 200);

        accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(firstThread, TimestampUs: 0));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: firstThread,
            NewProcessName: "target",
            ProcessorNumber: 0,
            TimestampUs: 10));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: firstThread,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)30,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 50));

        accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(secondThread, TimestampUs: 100));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: secondThread,
            NewProcessName: "target",
            ProcessorNumber: 1,
            TimestampUs: 120));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: secondThread,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)30,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 1,
            TimestampUs: 180));

        var response = accumulator.BuildResponse();

        Assert.Equal(2, response.Rows.Count);
        var firstRow = Assert.Single(response.Rows, row => row.ProcessStartUs == 0);
        Assert.Equal(100, firstRow.Pid);
        Assert.Equal(42, firstRow.Tid);
        Assert.Equal(1, firstRow.ThreadGeneration);
        Assert.Equal(40, firstRow.CpuUs);
        Assert.Equal(2, firstRow.ContextSwitches);
        Assert.Equal(1, firstRow.ReadyCount);
        Assert.Equal(10, firstRow.ReadyLatencyUs);
        Assert.Equal(10, firstRow.MaxReadyLatencyUs);
        Assert.Single(firstRow.TopCores);

        var secondRow = Assert.Single(response.Rows, row => row.ProcessStartUs == 100);
        Assert.Equal(100, secondRow.Pid);
        Assert.Equal(42, secondRow.Tid);
        Assert.Equal(1, secondRow.ThreadGeneration);
        Assert.Equal(60, secondRow.CpuUs);
        Assert.Equal(2, secondRow.ContextSwitches);
        Assert.Equal(1, secondRow.ReadyCount);
        Assert.Equal(20, secondRow.ReadyLatencyUs);
        Assert.Equal(20, secondRow.MaxReadyLatencyUs);
        Assert.Single(secondRow.TopCores);
        Assert.Equal(100, response.TotalCpuUs);
        Assert.Equal(2, response.TotalReadyCount);
        Assert.Equal(30, response.TotalReadyLatencyUs);
        Assert.Equal("pid_aggregate", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal(
            [firstThread.Process, secondThread.Process],
            response.IncludedProcesses);
        Assert.Equal(4, response.MatchedEventCount);
        Assert.Contains(
            response.Warnings,
            warning => warning.StartsWith("pid_aggregate:", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuPreciseAccumulator_ProcessScopeKeepsReusedTidGenerationsSeparate()
    {
        var process = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 100, StartUs: 10),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var firstThread = new ThreadInstanceKey(process.Key, Tid: 42, Generation: 1);
        var secondThread = new ThreadInstanceKey(process.Key, Tid: 42, Generation: 2);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 100,
            Process: process,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var accumulator = new CpuPreciseAccumulator(top: 10, scope, traceEndUs: 200);

        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: firstThread,
            NewProcessName: "target",
            ProcessorNumber: 0,
            TimestampUs: 20));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: firstThread,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 50));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: secondThread,
            NewProcessName: "target",
            ProcessorNumber: 0,
            TimestampUs: 80));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: secondThread,
            OldProcessName: "target",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 120));

        var response = accumulator.BuildResponse();

        Assert.Equal(2, response.Rows.Count);
        Assert.All(response.Rows, row => Assert.Equal(process.Key.StartUs, row.ProcessStartUs));
        Assert.Equal([1L, 2L], response.Rows.OrderBy(row => row.ThreadGeneration)
            .Select(row => row.ThreadGeneration));
        Assert.Equal("single_process", response.ScopeMode);
        Assert.False(response.PidReuseObserved);
        Assert.Equal([process.Key], response.IncludedProcesses);
    }

    [Fact]
    public void CpuPreciseAccumulator_ExactNewInstanceIgnoresOldEndpointSwitchOut()
    {
        var oldThread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            Tid: 42,
            Generation: 1);
        var selectedProcess = new ProcessLifetime(
            new ProcessInstanceKey(Pid: 100, StartUs: 100),
            EndUs: 200,
            StartObserved: true,
            EndObserved: true);
        var selectedThread = new ThreadLifetime(
            new ThreadInstanceKey(selectedProcess.Key, Tid: 42, Generation: 1),
            StartUs: 100,
            EndUs: 180,
            StartObserved: true,
            EndObserved: true);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: 100,
            Process: selectedProcess,
            Thread: selectedThread,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var accumulator = new CpuPreciseAccumulator(top: 1, scope, traceEndUs: 200);

        accumulator.ProcessReady(new CpuPreciseResolvedReadyEvent(
            oldThread,
            TimestampUs: 100));
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: oldThread,
            OldProcessName: "old-target",
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: null,
            NewProcessName: string.Empty,
            ProcessorNumber: 0,
            TimestampUs: 100));

        var response = accumulator.BuildResponse();

        var row = Assert.Single(response.Rows);
        Assert.Equal(0, row.ContextSwitches);
        Assert.Equal(0, response.TotalContextSwitches);
        Assert.False(response.HasContextSwitches);
        Assert.True(response.TraceHasContextSwitches);
        Assert.Contains(
            response.Warnings,
            warning =>
                warning.Contains("ReadyThread", StringComparison.Ordinal) &&
                warning.Contains("none matched", StringComparison.Ordinal));
    }

    [Fact]
    public void CpuPreciseAccumulator_TraceEndCompletionStopsAtThreadLifetime()
    {
        var thread = new ThreadInstanceKey(
            new ProcessInstanceKey(Pid: 100, StartUs: 0),
            Tid: 42,
            Generation: 1);
        var scope = new ThreadAnalysisScope(
            new TimeWindow(0, 200),
            Pid: null,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var accumulator = new CpuPreciseAccumulator(
            top: 1,
            scope,
            traceEndUs: 200,
            threadEndUs: candidate => candidate == thread ? 100 : null);
        accumulator.ProcessCSwitch(new CpuPreciseResolvedSwitchEvent(
            OldThread: null,
            OldProcessName: string.Empty,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewThread: thread,
            NewProcessName: "target",
            ProcessorNumber: 0,
            TimestampUs: 90));

        var response = accumulator.BuildResponse();

        Assert.Equal(10, response.TotalCpuUs);
        Assert.Equal(10, Assert.Single(response.Rows).CpuUs);
    }

    [Fact]
    public void CpuPreciseAnalysis_ReturnsRowsOrKeywordWarning()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        var resp = tools.CpuPreciseAnalysis(FixturePath, top: 20);

        if (resp.Rows.Count == 0)
            Assert.Contains(resp.Warnings, w => w.Contains("CSwitch", StringComparison.OrdinalIgnoreCase));
        else
            Assert.True(resp.TotalCpuUs > 0 || resp.TotalContextSwitches > 0);
    }

    [Fact]
    public void CpuPreciseAnalysis_RejectsBadTop()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuPreciseAnalysis("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.CpuPreciseAnalysis("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void CpuPreciseAnalysis_MissingExactProcessReturnsStructuredEmptyResponse()
    {
        var tools = new CpuTools(new TraceCache(capacity: 2));

        var response = tools.CpuPreciseAnalysis(
            FixturePath,
            pid: 999_999,
            processStartUs: 123);

        Assert.Empty(response.Rows);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void CpuPreciseAccumulator_ClipsCpuToHalfOpenWindowAndAttributesCore()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 3,
            TimeStampRelativeMSec: 90));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)30,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 3,
            TimeStampRelativeMSec: 120));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(100, row.Pid);
        Assert.Equal("target", row.ProcessName);
        Assert.Equal(42, row.Tid);
        Assert.Equal(20_000, row.CpuUs);
        Assert.Equal(3, row.PrimaryCore);
        var core = Assert.Single(row.TopCores);
        Assert.Equal(3, core.Core);
        Assert.Equal(20_000, core.CpuUs);
        Assert.Equal(1, row.QuantumEndSwitches);
    }

    [Fact]
    public void CpuPreciseAccumulator_SeedsFirstObservedRunningInterval()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 120));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(20_000, row.CpuUs);
        Assert.Equal(2, row.PrimaryCore);
        Assert.Equal(20_000, resp.TotalCpuUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_SeedsOnlyFirstObservedSwitchOutPerCore()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 50));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 43,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 8,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        Assert.Equal(50_000, resp.TotalCpuUs);
        Assert.Contains(resp.Rows, row => row.Tid == 42 && row.CpuUs == 50_000);
        Assert.Contains(resp.Rows, row => row.Tid == 43 && row.CpuUs == 0);
        Assert.Contains(resp.Warnings, warning => warning.Contains("stale per-core", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("unmatched CSwitch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuPreciseAccumulator_DoesNotSeedPreviouslyObservedThread()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 50));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 8,
            ProcessorNumber: 3,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows.Where(row => row.Tid == 42));
        Assert.Equal(50_000, row.CpuUs);
        Assert.Equal(50_000, resp.TotalCpuUs);
        Assert.Contains(resp.Warnings, warning => warning.Contains("unmatched CSwitch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuPreciseAccumulator_WarnsForUnmatchedPreviouslyReadiedSwitchOut()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 200_000);

        accumulator.ProcessReady(new CpuPreciseReadyEvent(
            AwakenedProcessId: 100,
            AwakenedThreadId: 42,
            TimeStampRelativeMSec: 10));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 50));

        var resp = accumulator.BuildResponse();

        Assert.Contains(resp.Rows, row => row.Tid == 42 && row.CpuUs == 0);
        Assert.Contains(resp.Warnings, warning => warning.Contains("unmatched CSwitch", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("stale per-core", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuPreciseAccumulator_ClipsFullTraceIntervalsToTraceEnd()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: null,
            endUs: null,
            traceEndUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 2,
            TimeStampRelativeMSec: 1_000));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(200_000, row.CpuUs);
        Assert.Equal(200_000, resp.TotalCpuUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_FlushesRunningThreadAtWindowEnd()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 4,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(50_000, row.CpuUs);
        Assert.Equal(4, row.PrimaryCore);
        Assert.Equal(50_000, resp.TotalCpuUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_FlushesOnlyOneRunningThreadPerCore()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 100_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 0,
            OldProcessName: "Idle",
            OldThreadId: 0,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 4,
            TimeStampRelativeMSec: 10));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 0,
            OldProcessName: "Idle",
            OldThreadId: 0,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 43,
            ProcessorNumber: 4,
            TimeStampRelativeMSec: 50));

        var resp = accumulator.BuildResponse();

        Assert.Equal(50_000, resp.TotalCpuUs);
        Assert.Contains(resp.Rows, row => row.Tid == 42 && row.CpuUs == 0);
        Assert.Contains(resp.Rows, row => row.Tid == 43 && row.CpuUs == 50_000);
        Assert.Contains(resp.Warnings, warning => warning.Contains("stale per-core", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("unmatched CSwitch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuPreciseAccumulator_DropsStaleThreadWhenCoreOldThreadDiffers()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 0,
            endUs: 100_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 0,
            OldProcessName: "Idle",
            OldThreadId: 0,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 4,
            TimeStampRelativeMSec: 10));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 43,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 44,
            ProcessorNumber: 4,
            TimeStampRelativeMSec: 50));

        var resp = accumulator.BuildResponse();

        Assert.Equal(50_000, resp.TotalCpuUs);
        Assert.Contains(resp.Rows, row => row.Tid == 42 && row.CpuUs == 0);
        Assert.Contains(resp.Rows, row => row.Tid == 43 && row.CpuUs == 0);
        Assert.Contains(resp.Rows, row => row.Tid == 44 && row.CpuUs == 50_000);
        Assert.Contains(resp.Warnings, warning => warning.Contains("stale per-core", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("unmatched CSwitch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CpuPreciseAccumulator_FlushesRunningThreadAtTraceEnd()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: null,
            endUs: null,
            traceEndUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 5,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(50_000, row.CpuUs);
        Assert.Equal(5, row.PrimaryCore);
        Assert.Equal(50_000, resp.TotalCpuUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_ComputesReadyLatencyAtSwitchIn()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessReady(new CpuPreciseReadyEvent(
            AwakenedProcessId: 100,
            AwakenedThreadId: 42,
            TimeStampRelativeMSec: 110));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 1,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(1, row.ReadyCount);
        Assert.Equal(40_000, row.ReadyLatencyUs);
        Assert.Equal(40_000, row.AvgReadyLatencyUs);
        Assert.Equal(40_000, row.MaxReadyLatencyUs);
        Assert.Equal(40_000, resp.TotalReadyLatencyUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_ClipsReadyLatencyToWindowStart()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessReady(new CpuPreciseReadyEvent(
            AwakenedProcessId: 100,
            AwakenedThreadId: 42,
            TimeStampRelativeMSec: 90));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 1,
            TimeStampRelativeMSec: 150));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(1, row.ReadyCount);
        Assert.Equal(50_000, row.ReadyLatencyUs);
        Assert.Equal(50_000, row.AvgReadyLatencyUs);
        Assert.Equal(50_000, row.MaxReadyLatencyUs);
        Assert.Equal(50_000, resp.TotalReadyLatencyUs);
    }

    [Fact]
    public void CpuPreciseAccumulator_StraddlingRunningIntervalDoesNotEmitEmptyWindowWarning()
    {
        var accumulator = new CpuPreciseAccumulator(
            top: 10,
            pid: 100,
            startUs: 100_000,
            endUs: 200_000);

        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 300,
            OldProcessName: "runner",
            OldThreadId: 7,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 100,
            NewProcessName: "target",
            NewThreadId: 42,
            ProcessorNumber: 1,
            TimeStampRelativeMSec: 90));
        accumulator.ProcessCSwitch(new CpuPreciseSwitchEvent(
            OldProcessId: 100,
            OldProcessName: "target",
            OldThreadId: 42,
            OldThreadWaitReason: (ThreadWaitReason)13,
            NewProcessId: 300,
            NewProcessName: "runner",
            NewThreadId: 7,
            ProcessorNumber: 1,
            TimeStampRelativeMSec: 250));

        var resp = accumulator.BuildResponse();

        var row = Assert.Single(resp.Rows);
        Assert.Equal(100_000, row.CpuUs);
        Assert.Equal(0, resp.TotalContextSwitches);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.Null(resp.NoDataReason);
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("none matched", StringComparison.OrdinalIgnoreCase));
    }
}
