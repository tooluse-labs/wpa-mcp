using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class NetConnectionAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void NetConnections_NoNetworkTrace_EmptyAndWarns()
    {
        // small_cpu.etl doesn't enable the NetworkTrace keyword.
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        var resp = tools.NetConnections(FixturePath);
        Assert.Empty(resp.Connections);
        Assert.Equal(0, resp.TotalConnections);
        Assert.Contains(resp.Warnings, w => w.Contains("NetworkTrace", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("not_observed", resp.CapabilityStatus);
        Assert.Equal("event_class_not_observed", resp.NoDataReason);
        Assert.Equal(0, resp.MatchedEventCount);
    }

    [Fact]
    public void NetConnections_PidFilterReflectedInResponse()
    {
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        var resp = tools.NetConnections(FixturePath, pid: 999_999);
        Assert.Equal(999_999, resp.Pid);
        Assert.Empty(resp.Connections);
        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal("scope_not_found", resp.NoDataReason);
    }

    [Fact]
    public void NetConnections_RejectsBadTop()
    {
        var tools = new NetIoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.NetConnections("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.NetConnections("nonexistent.etl", top: 1001));
        Assert.Throws<ArgumentException>(() =>
            tools.NetConnections("nonexistent.etl", processStartUs: 10));
    }

    [Fact]
    public void AnalyzeEvents_SameConnIdAcrossPidsPairsWithItsEmitterProcess()
    {
        var first = new ProcessInstanceKey(10, 0);
        var second = new ProcessInstanceKey(20, 0);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(first, 200, true, false),
                new ProcessLifetime(second, 200, true, false),
            ],
            events:
            [
                Open(10, connId: 7, timeUs: 10),
                Open(20, connId: 7, timeUs: 20),
                Close(10, connId: 7, timeUs: 60),
                Close(20, connId: 7, timeUs: 90),
            ],
            pid: null,
            top: 10,
            window: new TimeWindow(0, 200),
            processStartUs: null);

        Assert.Equal(2, response.TotalConnections);
        Assert.Equal(4, response.MatchedEventCount);
        Assert.Equal(50, Assert.Single(response.Connections, row => row.Pid == 10).DurationUs);
        Assert.Equal(70, Assert.Single(response.Connections, row => row.Pid == 20).DurationUs);
        Assert.All(response.Connections, row => Assert.Equal(0, row.ProcessStartUs));
    }

    [Fact]
    public void AnalyzeEvents_ReusedPidAndConnIdDoesNotClosePriorProcessSlot()
    {
        var oldProcess = new ProcessInstanceKey(10, 0);
        var newProcess = new ProcessInstanceKey(10, 100);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(oldProcess, 100, true, true),
                new ProcessLifetime(newProcess, 200, true, false),
            ],
            events:
            [
                Open(10, connId: 7, timeUs: 10),
                Open(10, connId: 7, timeUs: 120),
                Close(10, connId: 7, timeUs: 150),
            ],
            pid: 10,
            top: 10,
            window: new TimeWindow(0, 200),
            processStartUs: null);

        Assert.Equal("pid_aggregate", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], response.IncludedProcesses);
        Assert.Equal(2, response.TotalConnections);
        Assert.Equal(3, response.MatchedEventCount);
        var oldRow = Assert.Single(response.Connections, row => row.ProcessStartUs == 0);
        Assert.NotEqual(120, oldRow.CloseTimeUs);
        Assert.Null(oldRow.CloseTimeUs);
        Assert.Null(oldRow.DurationUs);
        Assert.False(oldRow.TraceResidentEnd);
        Assert.Equal("process_end_unobserved", oldRow.EndState);
        var newRow = Assert.Single(response.Connections, row => row.ProcessStartUs == 100);
        Assert.Equal(30, newRow.DurationUs);
    }

    [Fact]
    public void AnalyzeEvents_OpenBeforeWindowAndCloseInsideWindowIsIncluded()
    {
        var process = new ProcessInstanceKey(10, 0);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes: [new ProcessLifetime(process, 200, true, false)],
            events:
            [
                Open(10, connId: 7, timeUs: 10),
                Close(10, connId: 7, timeUs: 60),
            ],
            pid: 10,
            top: 10,
            window: new TimeWindow(50, 100),
            processStartUs: null);

        var row = Assert.Single(response.Connections);
        Assert.Equal(10, row.OpenTimeUs);
        Assert.Equal(60, row.CloseTimeUs);
        Assert.Equal(50, row.DurationUs);
        Assert.Equal(1, response.MatchedEventCount);
    }

    [Fact]
    public void AnalyzeEvents_ExactSelectorReturnsOnlySelectedProcessLifetime()
    {
        var oldProcess = new ProcessInstanceKey(10, 0);
        var selected = new ProcessInstanceKey(10, 100);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(oldProcess, 100, true, true),
                new ProcessLifetime(selected, 200, true, false),
            ],
            events:
            [
                Open(10, connId: 7, timeUs: 10),
                Close(10, connId: 7, timeUs: 60),
                Open(10, connId: 7, timeUs: 120),
                Close(10, connId: 7, timeUs: 180),
            ],
            pid: 10,
            top: 10,
            window: new TimeWindow(0, 200),
            processStartUs: selected.StartUs);

        var row = Assert.Single(response.Connections);
        Assert.Equal(selected.StartUs, row.ProcessStartUs);
        Assert.Equal(selected, response.SelectedProcess);
        Assert.Equal("single_process", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([selected], response.IncludedProcesses);
        Assert.Equal(2, response.MatchedEventCount);
        Assert.DoesNotContain(
            response.Warnings,
            warning => warning.Contains("identity_unresolved", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_TraceResidentEndHasUnknownCloseAndDuration()
    {
        var process = new ProcessInstanceKey(10, 0);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 200,
            processLifetimes:
            [
                new ProcessLifetime(process, 200, true, false),
            ],
            events: [Open(10, connId: 7, timeUs: 10)],
            pid: 10,
            top: 10,
            window: new TimeWindow(0, 200),
            processStartUs: null);

        var row = Assert.Single(response.Connections);
        Assert.True(row.TraceResidentEnd);
        Assert.Null(row.CloseTimeUs);
        Assert.Null(row.DurationUs);
        Assert.Equal("trace_end_unobserved", row.EndState);
        Assert.Equal(1, response.MatchedEventCount);
    }

    [Fact]
    public void AnalyzeEvents_MissingExactSelectorReturnsStructuredEmptyResponse()
    {
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(
                    new ProcessInstanceKey(10, 0), 100, true, false),
            ],
            events: [Open(10, connId: 7, timeUs: 10)],
            pid: 10,
            top: 10,
            window: new TimeWindow(0, 100),
            processStartUs: 50);

        Assert.Empty(response.Connections);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void AnalyzeEvents_OrphanCloseReportsUnpairedEndpointAndNoCompletedLifecycle()
    {
        var process = new ProcessInstanceKey(10, 0);
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes:
            [
                new ProcessLifetime(process, 100, true, false),
            ],
            events: [Close(10, connId: 7, timeUs: 50)],
            pid: 10,
            top: 10,
            window: new TimeWindow(0, 100),
            processStartUs: null);

        Assert.Empty(response.Connections);
        Assert.Equal(0, response.TotalConnections);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(1, response.UnpairedCloseCount);
        Assert.Equal("observed", response.CapabilityStatus);
        Assert.Equal("unpaired_endpoints_in_scope", response.NoDataReason);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("unpaired_network_close:", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzeEvents_UnresolvedInWindowEndpointIsNotMisreportedAsNoEvents()
    {
        var response = NetConnectionAnalysis.AnalyzeEvents(
            traceEndUs: 100,
            processLifetimes: [],
            events: [Open(10, connId: 7, timeUs: 50)],
            pid: null,
            top: 10,
            window: new TimeWindow(0, 100),
            processStartUs: null);

        Assert.Empty(response.Connections);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal(1, response.TraceIdentityUnresolvedEndpointCount);
        Assert.Equal(1, response.ScopedIdentityUnresolvedEndpointCount);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("source_events_unattributed:", StringComparison.Ordinal));
    }

    private static NetConnectionEvent Open(int pid, ulong connId, long timeUs) =>
        new(
            Pid: pid,
            ConnId: connId,
            Kind: NetConnectionEventKind.Connect,
            TimeUs: timeUs,
            RemoteAddress: "10.0.0.2",
            RemotePort: 443,
            LocalAddress: "10.0.0.1",
            LocalPort: 50_000,
            IsIPv6: false);

    private static NetConnectionEvent Close(int pid, ulong connId, long timeUs) =>
        new(
            Pid: pid,
            ConnId: connId,
            Kind: NetConnectionEventKind.Disconnect,
            TimeUs: timeUs);
}
