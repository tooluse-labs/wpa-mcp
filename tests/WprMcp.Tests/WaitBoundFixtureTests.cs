using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class WaitBoundFixtureTests
{
    private const string FixturePath = "fixtures/small_wait_bound.etl";

    [Fact]
    public void WaitBoundFixture_ExposesContextSwitchReadyThreadAndStacks()
    {
        var cache = new TraceCache(capacity: 2);

        var capabilities = cache.GetCapabilities(FixturePath);
        var metadata = cache.GetMetadata(FixturePath);
        var probe = StackProbeAnalysis.Analyze(cache.Get(FixturePath), FixturePath);

        Assert.True(capabilities.HasCSwitch);
        Assert.True(capabilities.HasReadyThread);
        Assert.True(capabilities.HasStackWalks);
        Assert.True(capabilities.HasCSwitchStacks);
        Assert.True(capabilities.HasReadyThreadStacks);
        Assert.True(metadata.Stackwalks.EventsWithCallStacks > 0);
        Assert.False(probe.HasExplicitStackWalkEvents);
        Assert.True(probe.HasUsableEventStacks);
        Assert.True(probe.CSwitchEvents > 0);
        Assert.True(probe.CSwitchEventsWithCallStacks > 0);
        Assert.True(probe.ReadyThreadEvents > 0);
        Assert.True(probe.ReadyThreadEventsWithCallStacks > 0);
        Assert.Contains(probe.Notes, note =>
            note.Contains("event_attached_stacks_without_explicit_stackwalk", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WaitTopStacks_ReturnsRealStackRowsForCapturedPowerShellWaits()
    {
        WithSymbolPathUnset(() =>
        {
            var cache = new TraceCache(capacity: 2);
            var pid = CapturedPowerShellPid(cache);
            var tools = new WaitTools(cache);

            var resp = tools.WaitTopStacks(FixturePath, top: 10, pid: pid);

            Assert.NotEmpty(resp.Rows);
            Assert.True(resp.TotalBlockedUs > 0);
            Assert.True(resp.SampleCount > 0);
            Assert.DoesNotContain(resp.Warnings,
                warning => warning.Contains("No CSwitch", StringComparison.OrdinalIgnoreCase) ||
                           warning.Contains("no blocked-time samples", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void ReadyThreadTopStacks_ReturnsRealRowsForCapturedPowerShellWaits()
    {
        WithSymbolPathUnset(() =>
        {
            var cache = new TraceCache(capacity: 2);
            var pid = CapturedPowerShellPid(cache);
            var tools = new ReadyThreadTools(cache);

            var resp = tools.ReadyThreadTopStacks(FixturePath, top: 10, awakenedPid: pid);

            Assert.NotEmpty(resp.Rows);
            Assert.True(resp.TotalReadyCount > 0);
            Assert.DoesNotContain(resp.Warnings,
                warning => warning.Contains("No DispatcherReadyThread", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void DiagnoseHighWait_UsesStackEvidenceOnWaitBoundFixture()
    {
        WithSymbolPathUnset(() =>
        {
            var cache = new TraceCache(capacity: 2);
            var pid = CapturedPowerShellPid(cache);
            var tools = new DiagnoseTools(cache);

            var resp = tools.DiagnoseHighWait(FixturePath, pid: pid, maxCandidates: 1, topStacks: 5);

            var candidate = Assert.Single(resp.Candidates);
            Assert.Equal(pid, candidate.Pid);
            Assert.NotNull(candidate.WaitStacksCallId);
            Assert.Contains(resp.ExecutedToolCalls, call => call.ToolName == "wait_top_stacks");
            Assert.Contains(resp.Evidence, item => item.EvidenceType == "wait_stack_summary");
            Assert.DoesNotContain(resp.NotConcluded, item => item.Code == "missing_stackwalks");
        });
    }

    private static int CapturedPowerShellPid(TraceCache cache)
    {
        var trace = cache.Get(FixturePath);
        var waitRows = WaitAnalysis.Analyze(trace, top: int.MaxValue, pid: null, startUs: null, endUs: null).Rows;
        return waitRows
            .Where(row => row.ProcessName.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            .GroupBy(row => row.Pid)
            .Select(group => new
            {
                Pid = group.Key,
                BlockedUs = group.Sum(row => row.BlockedUs),
            })
            .Where(row => row.BlockedUs > 0)
            .OrderByDescending(row => row.BlockedUs)
            .First()
            .Pid;
    }

    private static void WithSymbolPathUnset(Action action)
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }
}
