using System.Diagnostics;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class CpuPreciseAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

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
}
