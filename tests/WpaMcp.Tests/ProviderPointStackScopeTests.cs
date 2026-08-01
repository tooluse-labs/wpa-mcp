using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class ProviderPointStackScopeTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";
    private const int MissingPid = 999_999;
    private const long MissingStartUs = 123_456;
    private static readonly TraceCache Cache = new(capacity: 2);

    [Fact]
    public void ProviderTopStacks_MissingExactProcess_ReturnStructuredScopeNotFound()
    {
        AssertMissing(new NetIoTools(Cache).NetTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new RegistryTools(Cache).RegistryTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new AlpcTools(Cache).AlpcTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new HeapTools(Cache).HeapAllocTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new GenericProviderTools(Cache).GenericEventTopStacks(
            FixturePath, "NoSuchProvider-DoesNotExist", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
    }

    [Fact]
    public void ProviderCallerCallee_MissingExactProcess_MatchesTopContract()
    {
        AssertMissing(new NetIoTools(Cache).NetCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new RegistryTools(Cache).RegistryCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new AlpcTools(Cache).AlpcCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new HeapTools(Cache).HeapAllocCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(new GenericProviderTools(Cache).GenericEventCallerCallee(
            FixturePath, "NoSuchProvider-DoesNotExist", "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
    }

    [Fact]
    public void ClrStackTools_MissingExactProcess_ReturnConsistentTopAndCallerContracts()
    {
        var tools = new ClrTools(Cache);

        AssertMissing(tools.ClrAllocTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(tools.ClrAllocCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(tools.ClrExceptionTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(tools.ClrExceptionCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(tools.ClrContentionTopStacks(
            FixturePath, top: 5, pid: MissingPid, processStartUs: MissingStartUs));
        AssertMissing(tools.ClrContentionCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: MissingPid, processStartUs: MissingStartUs));
    }

    [Fact]
    public void ProcessStartSelector_RequiresPidAcrossProviderAndClrStackTools()
    {
        Assert.Throws<ArgumentException>(() =>
            new NetIoTools(Cache).NetTopStacks(FixturePath, processStartUs: 1));
        Assert.Throws<ArgumentException>(() =>
            new RegistryTools(Cache).RegistryTopStacks(FixturePath, processStartUs: 1));
        Assert.Throws<ArgumentException>(() =>
            new AlpcTools(Cache).AlpcTopStacks(FixturePath, processStartUs: 1));
        Assert.Throws<ArgumentException>(() =>
            new HeapTools(Cache).HeapAllocTopStacks(FixturePath, processStartUs: 1));
        Assert.Throws<ArgumentException>(() =>
            new GenericProviderTools(Cache).GenericEventTopStacks(
                FixturePath, "provider", processStartUs: 1));
        Assert.Throws<ArgumentException>(() =>
            new ClrTools(Cache).ClrContentionTopStacks(FixturePath, processStartUs: 1));
    }

    [Fact]
    public void ExactExistingProcess_IsReportedConsistentlyByPointAndIntervalStacks()
    {
        using var lease = Cache.Acquire(FixturePath);
        var lifetime = TraceIdentityIndex.For(lease.Trace).Processes.Lifetimes
            .First(item => item.Key.Pid > 0 && item.Key.StartUs >= 0);
        var expected = lifetime.Key;

        var netTools = new NetIoTools(Cache);
        var net = netTools.NetTopStacks(
            FixturePath, top: 5, pid: expected.Pid, processStartUs: expected.StartUs);
        var netCaller = netTools.NetCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: expected.Pid, processStartUs: expected.StartUs);
        var clrTools = new ClrTools(Cache);
        var contentionTop = clrTools.ClrContentionTopStacks(
            FixturePath, top: 5, pid: expected.Pid, processStartUs: expected.StartUs);
        var contention = clrTools.ClrContentionCallerCallee(
            FixturePath, "missing!frame", top: 5,
            pid: expected.Pid, processStartUs: expected.StartUs);

        Assert.Equal(expected, net.SelectedProcess);
        Assert.Equal(expected, netCaller.SelectedProcess);
        Assert.Equal(expected, contentionTop.SelectedProcess);
        Assert.Equal(expected, contention.SelectedProcess);
        Assert.Equal("single_process", net.ScopeMode);
        Assert.Equal(net.ScopeMode, netCaller.ScopeMode);
        Assert.Equal(net.ScopeMode, contentionTop.ScopeMode);
        Assert.Equal(net.ScopeMode, contention.ScopeMode);
        Assert.Equal("ok", net.ScopeStatus);
        Assert.Equal(net.ScopeStatus, netCaller.ScopeStatus);
        Assert.Equal(net.ScopeStatus, contentionTop.ScopeStatus);
        Assert.Equal(net.ScopeStatus, contention.ScopeStatus);
        Assert.Equal([expected], net.IncludedProcesses);
        Assert.Equal(net.IncludedProcesses, netCaller.IncludedProcesses);
        Assert.Equal(net.IncludedProcesses, contentionTop.IncludedProcesses);
        Assert.Equal(net.IncludedProcesses, contention.IncludedProcesses);
        Assert.Equal(net.StackCoverage, netCaller.StackCoverage);
        Assert.Equal(net.MatchedEventCount, netCaller.MatchedEventCount);
        Assert.Equal(contentionTop.StackCoverage, contention.StackCoverage);
        Assert.Equal(contentionTop.MatchedEventCount, contention.MatchedEventCount);
    }

    private static void AssertMissing(NetIoStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(RegistryStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(AlpcStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(HeapAllocStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(GenericEventStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(ClrAllocStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(ClrExceptionStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(ClrContentionStacksResponse response)
    {
        Assert.Empty(response.Rows);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(CallerCalleeResponse response)
    {
        Assert.Empty(response.Callers);
        Assert.Empty(response.Callees);
        AssertMissing(response.ScopeStatus, response.ScopeMode, response.NoDataReason,
            response.MatchedEventCount, response.SelectedProcess, response.IncludedProcesses);
    }

    private static void AssertMissing(
        string scopeStatus,
        string scopeMode,
        string? noDataReason,
        long matchedEventCount,
        ProcessInstanceKey? selectedProcess,
        IReadOnlyList<ProcessInstanceKey>? includedProcesses)
    {
        Assert.Equal("scope_not_found", scopeStatus);
        Assert.Equal("unresolved", scopeMode);
        Assert.Equal("scope_not_found", noDataReason);
        Assert.Equal(0, matchedEventCount);
        Assert.Null(selectedProcess);
        Assert.Empty(includedProcesses ?? []);
    }
}
