using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class PointStackProcessScopeContractTests
{
    private const string CpuFixture = "fixtures/small_cpu.etl";
    private static readonly TraceCache Cache = new(capacity: 2);

    [Fact]
    public void FileIoTopAndCaller_MissingProcessReturnSameStructuredEmptyScope()
    {
        var tools = new IoTools(Cache);

        var top = tools.FileIoTopStacks(
            CpuFixture, pid: 999_999, processStartUs: 123_456, top: 5);
        var caller = tools.FileIoCallerCallee(
            CpuFixture, "missing!frame", pid: 999_999,
            processStartUs: 123_456, top: 5);

        Assert.Empty(top.Rows);
        Assert.Equal("scope_not_found", top.ScopeStatus);
        Assert.Equal("scope_not_found", top.NoDataReason);
        Assert.Equal(0, top.MatchedEventCount);
        Assert.Equal(top.ScopeStatus, caller.ScopeStatus);
        Assert.Equal(top.ScopeMode, caller.ScopeMode);
        Assert.Equal(top.NoDataReason, caller.NoDataReason);
        Assert.Equal(top.MatchedEventCount, caller.MatchedEventCount);
        Assert.Equal(top.IncludedProcesses, caller.IncludedProcesses);
        Assert.DoesNotContain(caller.Warnings, warning =>
            warning.StartsWith("focus_not_found:", StringComparison.Ordinal) ||
            warning.StartsWith("Focus function", StringComparison.Ordinal));
    }

    [Fact]
    public void HardFaultByFile_MissingProcessReturnsStructuredEmptyScope()
    {
        var response = new HardFaultTools(Cache).HardFaultByFile(
            CpuFixture, pid: 999_999, processStartUs: 123_456, top: 5);

        Assert.Empty(response.Rows);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Equal("unresolved", response.ScopeMode);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.NotNull(response.Warnings);
    }

    [Fact]
    public void ReadyThreadTopAndCaller_UseAwakenedProcessInstanceSelector()
    {
        var trace = Cache.Get(CpuFixture);
        var process = trace.Processes
            .Where(item => item.ProcessID > 0)
            .OrderBy(item => item.StartTimeRelativeMsec)
            .First();
        var processStartUs = TraceTime.FromMilliseconds(process.StartTimeRelativeMsec);
        var tools = new ReadyThreadTools(Cache);

        var top = tools.ReadyThreadTopStacks(
            CpuFixture, awakenedPid: process.ProcessID,
            awakenedProcessStartUs: processStartUs, top: 5);
        var caller = tools.ReadyThreadCallerCallee(
            CpuFixture, "missing!frame", awakenedPid: process.ProcessID,
            awakenedProcessStartUs: processStartUs, top: 5);

        var expected = new ProcessInstanceKey(process.ProcessID, processStartUs);
        Assert.Equal(expected, top.SelectedProcess);
        Assert.Equal(expected, caller.SelectedProcess);
        Assert.Equal("single_process", top.ScopeMode);
        Assert.Equal(top.ScopeMode, caller.ScopeMode);
        Assert.Equal(top.StackCoverage, caller.StackCoverage);
        Assert.Equal(top.MatchedEventCount, caller.MatchedEventCount);
    }

    [Fact]
    public void StackResultContract_ClassifiesNoDataByStablePriority()
    {
        var noEvents = new DomainStackCoverageAccumulator("file_io", "bytes").Snapshot();
        ProcessLifetime[] lifetimes =
        [
            new(new ProcessInstanceKey(42, 100), 200, true, true),
        ];
        var missingScope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 500), pid: 42, processStartUs: 999, lifetimes);
        var missing = StackResultContract.From(
            missingScope, filterSpecified: true, noEvents);
        var unfiltered = StackResultContract.From(
            processScope: null, filterSpecified: false, noEvents);
        var filtered = StackResultContract.From(
            processScope: null, filterSpecified: true, noEvents);

        Assert.Equal("scope_not_found", missing.NoDataReason);
        Assert.Equal("unknown", missing.CapabilityStatus);
        Assert.Equal("event_class_not_observed", unfiltered.NoDataReason);
        Assert.Equal("not_observed", unfiltered.CapabilityStatus);
        Assert.Equal("no_events_in_scope", filtered.NoDataReason);
        Assert.Equal("unknown", filtered.CapabilityStatus);

        var noStacksAccumulator = new DomainStackCoverageAccumulator("file_io", "bytes");
        noStacksAccumulator.Observe(hasStack: false, metric: 4_096);
        var noStacks = StackResultContract.From(
            processScope: null, filterSpecified: false, noStacksAccumulator.Snapshot());
        Assert.Equal("stacks_unavailable", noStacks.NoDataReason);
        Assert.Equal("observed", noStacks.CapabilityStatus);

        var focusMissing = StackResultContract.From(
            processScope: null,
            filterSpecified: false,
            noStacksAccumulator.Snapshot() with
            {
                StackedEventCount = 1,
                CoverageState = "full",
            },
            focusRequested: true,
            focusFound: false);
        Assert.Equal("focus_not_found", focusMissing.NoDataReason);
    }

    [Fact]
    public void StackResultContract_UsesTraceWideEventCountForFilteredEmptyScopes()
    {
        var noScopedEvents = new DomainStackCoverageAccumulator("file_io", "bytes").Snapshot();

        var classPresentOutsideScope = StackResultContract.From(
            processScope: null,
            filterSpecified: true,
            noScopedEvents,
            traceEventCount: 7);
        Assert.Equal("unknown", classPresentOutsideScope.CapabilityStatus);
        Assert.Equal("no_events_in_scope", classPresentOutsideScope.NoDataReason);

        var classAbsentInAnalyzableWindow = StackResultContract.From(
            processScope: null,
            filterSpecified: true,
            noScopedEvents,
            traceEventCount: 0);
        Assert.Equal("not_observed", classAbsentInAnalyzableWindow.CapabilityStatus);
        Assert.Equal("event_class_not_observed", classAbsentInAnalyzableWindow.NoDataReason);

        var threadScope = new ThreadAnalysisScope(
            new TimeWindow(0, 100),
            Pid: null,
            Process: null,
            Thread: null,
            AggregatesPidLifetimes: false,
            PidReuseObserved: false);
        var threadClassPresentOutsideScope = StackResultContract.FromThreadScope(
            threadScope,
            filterSpecified: true,
            noScopedEvents,
            traceEventCount: 7);
        Assert.Equal("unknown", threadClassPresentOutsideScope.CapabilityStatus);
        Assert.Equal("no_events_in_scope", threadClassPresentOutsideScope.NoDataReason);
    }

    [Fact]
    public void EveryPointStackResponse_ExposesScopeAndNoDataFields()
    {
        Type[] responseTypes =
        [
            typeof(CpuTopFunctionsResponse), typeof(WaitTopStacksResponse),
            typeof(FileIoStacksResponse), typeof(DiskIoStacksResponse),
            typeof(HardFaultStacksResponse), typeof(ImageLoadStacksResponse),
            typeof(VirtualAllocStacksResponse), typeof(NetIoStacksResponse),
            typeof(RegistryStacksResponse), typeof(ReadyThreadStacksResponse),
            typeof(AlpcStacksResponse), typeof(ClrAllocStacksResponse),
            typeof(ClrExceptionStacksResponse), typeof(ClrContentionStacksResponse),
            typeof(HeapAllocStacksResponse), typeof(GenericEventStacksResponse),
            typeof(InterruptStacksResponse), typeof(CallerCalleeResponse),
        ];

        string[] fields =
        [
            "SelectedProcess", "ScopeMode", "PidReuseObserved", "IncludedProcesses",
            "ScopeStatus", "CapabilityStatus", "MatchedEventCount", "NoDataReason",
        ];

        foreach (var responseType in responseTypes)
        foreach (var field in fields)
            Assert.NotNull(responseType.GetProperty(field));
    }
}
