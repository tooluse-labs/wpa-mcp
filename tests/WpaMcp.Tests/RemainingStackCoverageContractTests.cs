using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class RemainingStackCoverageContractTests
{
    private const string CpuFixture = "fixtures/small_cpu.etl";
    private const string WaitFixture = "fixtures/small_wait_bound.etl";
    private static readonly TraceCache Cache = new(capacity: 3);

    [Fact]
    public void AlpcTopAndCallerCallee_ReportCountCoverage()
    {
        var tools = new AlpcTools(Cache);
        var top = tools.AlpcTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "alpc", "count", top.TotalEvents);
        var callerCallee = tools.AlpcCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void ClrAllocTopAndCallerCallee_ReportByteCoverage()
    {
        var tools = new ClrTools(Cache);
        var top = tools.ClrAllocTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "clr_alloc", "bytes", top.TotalBytes);
        var callerCallee = tools.ClrAllocCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void ClrContentionTopAndCallerCallee_ReportDurationCoverage()
    {
        var tools = new ClrTools(Cache);
        var top = tools.ClrContentionTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "clr_contention", "us", top.TotalAccountedBlockedUs);
        var callerCallee = tools.ClrContentionCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void ClrExceptionTopAndCallerCallee_ReportCountCoverage()
    {
        var tools = new ClrTools(Cache);
        var top = tools.ClrExceptionTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "clr_exception", "count", top.TotalEventCount);
        var callerCallee = tools.ClrExceptionCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void GenericEventTopAndCallerCallee_ReportQuerySpecificCountCoverage()
    {
        const string provider = "MSNT_SystemTrace";
        var tools = new GenericProviderTools(Cache);
        var top = tools.GenericEventTopStacks(CpuFixture, provider, top: 5);
        AssertCoverage(top.StackCoverage, "generic_event", "count", top.TotalEventCount);
        var callerCallee = tools.GenericEventCallerCallee(
            CpuFixture, provider, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void HeapAllocTopAndCallerCallee_ReportByteCoverage()
    {
        var tools = new HeapTools(Cache);
        var top = tools.HeapAllocTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "heap_alloc", "bytes", top.TotalBytes);
        var callerCallee = tools.HeapAllocCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void InterruptTopAndCallerCallee_ReportDurationCoverage()
    {
        var tools = new InterruptTools(Cache);
        var top = tools.InterruptTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "interrupt", "us", top.TotalUs);
        var callerCallee = tools.InterruptCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void NetIoTopAndCallerCallee_ReportByteCoverage()
    {
        var tools = new NetIoTools(Cache);
        var top = tools.NetTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "net_io", "bytes", top.TotalBytes);
        var callerCallee = tools.NetCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void ReadyThreadTopAndCallerCallee_ReportCountCoverage()
    {
        var tools = new ReadyThreadTools(Cache);
        var top = tools.ReadyThreadTopStacks(WaitFixture, top: 5);
        AssertCoverage(top.StackCoverage, "ready_thread", "count", top.TotalReadyCount);
        var callerCallee = tools.ReadyThreadCallerCallee(
            WaitFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void RegistryTopAndCallerCallee_ReportCountCoverage()
    {
        var tools = new RegistryTools(Cache);
        var top = tools.RegistryTopStacks(CpuFixture, top: 5);
        AssertCoverage(top.StackCoverage, "registry", "count", top.TotalOps);
        var callerCallee = tools.RegistryCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
    }

    [Fact]
    public void VirtualAllocTopAndCallerCallee_ReportByteCoverage()
    {
        var tools = new VirtualMemoryTools(Cache);
        var top = tools.VirtualAllocTopStacks(CpuFixture, top: 5);
        AssertCoverage(
            top.StackCoverage,
            "virtual_alloc",
            "virtualMemoryOperationBytes",
            top.TotalOperationBytes);
        Assert.Equal(top.AllocatedBytes + top.FreedBytes, top.TotalOperationBytes);
        Assert.Equal(top.AllocatedCount + top.FreedCount, top.TotalOperationCount);
        Assert.Equal(top.AllocatedBytes - top.FreedBytes, top.NetObservedOperationBytes);
        Assert.Equal(top.TotalOperationBytes, top.TotalBytes);
        Assert.Equal(top.TotalOperationCount, top.TotalOpCount);
        Assert.Equal("float32_per_sample_approximate", top.MetricPrecision);
        Assert.Equal("float32_per_sample_approximate", top.RowMetricAccounting);
        Assert.Equal("exact_long", top.ExactTotalAccounting);
        var callerCallee = tools.VirtualAllocCallerCallee(CpuFixture, Pick(top.Rows, row => row.Function), top: 5);
        AssertSameCoverage(top.StackCoverage, callerCallee.StackCoverage);
        Assert.Equal("virtualMemoryOperationBytes", callerCallee.MetricName);
        Assert.Equal("float32_per_sample_approximate", callerCallee.MetricPrecision);
        Assert.Equal("float32_per_sample_approximate", callerCallee.RowMetricAccounting);
        Assert.Equal("exact_long", callerCallee.ExactTotalAccounting);
    }

    [Fact]
    public void Recommendations_DoNotUseUnrelatedGlobalStacksForRemainingDomains()
    {
        var domains = new[]
        {
            "ready_thread", "clr_alloc", "clr_exception", "clr_contention", "net_io",
            "alpc", "interrupt", "virtual_alloc", "registry", "heap_alloc",
        };
        var capabilities = EmptyCapabilities() with
        {
            HasStackWalks = true,
            HasAttachedEventStacks = true,
            HasReadyThread = true,
            HasClrAlloc = true,
            HasClrException = true,
            HasClrContention = true,
            HasNetIo = true,
            HasAlpc = true,
            HasInterrupt = true,
            HasVirtualAlloc = true,
            HasRegistry = true,
            HasNtHeap = true,
            StackCoverageByDomain = domains.ToDictionary(
                domain => domain,
                domain => Coverage(domain, total: 10, stacked: 0,
                    metricName: domain == "virtual_alloc" ? "virtualMemoryOperationBytes" :
                                domain is "clr_alloc" or "net_io" or "heap_alloc" ? "bytes" :
                                domain is "clr_contention" or "interrupt" ? "us" : "count")),
        };

        var recommendations = MetaTools.BuildCapabilitySupportedTools(capabilities);
        var stackTools = new[]
        {
            "ready_thread_top_stacks", "clr_alloc_top_stacks", "clr_exception_top_stacks",
            "clr_contention_top_stacks", "net_top_stacks", "alpc_top_stacks",
            "interrupt_top_stacks", "virtual_alloc_top_stacks", "registry_top_stacks",
            "heap_alloc_top_stacks",
        };
        Assert.DoesNotContain(recommendations, row => stackTools.Contains(row.ToolName));

        var flows = MetaTools.BuildRecommendedDiagnosticFlows(capabilities);
        var dotnet = Assert.Single(flows, row => row.FlowName == "dotnet_runtime");
        Assert.DoesNotContain(dotnet.ToolSequence, tool => tool.EndsWith("_top_stacks", StringComparison.Ordinal));
        var network = Assert.Single(flows, row => row.FlowName == "network_activity");
        Assert.DoesNotContain("net_top_stacks", network.ToolSequence);
    }

    [Fact]
    public void PartialDomainCoverage_AddsMachineReadableRecommendationAndFlowCaveat()
    {
        var capabilities = EmptyCapabilities() with
        {
            HasNetIo = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>
            {
                ["net_io"] = Coverage("net_io", total: 4, stacked: 2, metricName: "bytes"),
            },
        };

        var recommendation = Assert.Single(
            MetaTools.BuildCapabilitySupportedTools(capabilities),
            row => row.ToolName == "net_top_stacks");
        Assert.Contains("stack_coverage_state=partial", recommendation.Reason, StringComparison.Ordinal);

        var flow = Assert.Single(
            MetaTools.BuildRecommendedDiagnosticFlows(capabilities),
            row => row.FlowName == "network_activity");
        Assert.Contains("net_top_stacks", flow.ToolSequence);
        Assert.Contains(flow.Caveats, caveat => caveat.Contains(
            "stack_coverage_state=partial;domain=net_io", StringComparison.Ordinal));
    }

    [Fact]
    public void TraceCapabilities_ExposeEveryRemainingDomainWithCanonicalMetricName()
    {
        var coverage = Assert.IsAssignableFrom<IReadOnlyDictionary<string, DomainStackCoverage>>(
            Cache.GetCapabilities(CpuFixture).StackCoverageByDomain);
        var expected = new Dictionary<string, string>
        {
            ["alpc"] = "count",
            ["clr_alloc"] = "bytes",
            ["clr_contention"] = "us",
            ["clr_exception"] = "count",
            ["generic_event"] = "count",
            ["heap_alloc"] = "bytes",
            ["interrupt"] = "us",
            ["net_io"] = "bytes",
            ["ready_thread"] = "count",
            ["registry"] = "count",
            ["virtual_alloc"] = "virtualMemoryOperationBytes",
        };

        foreach (var (domain, metricName) in expected)
        {
            var domainCoverage = Assert.IsType<DomainStackCoverage>(coverage[domain]);
            Assert.Equal(domain, domainCoverage.Domain);
            Assert.Equal(metricName, domainCoverage.MetricName);
        }
    }

    private static string Pick<TRow>(IReadOnlyList<TRow> rows, Func<TRow, string> selector) =>
        rows.Count == 0 ? "?!?" : selector(rows[0]);

    private static void AssertCoverage(
        DomainStackCoverage? coverage,
        string domain,
        string metricName,
        long totalMetric)
    {
        var value = Assert.IsType<DomainStackCoverage>(coverage);
        Assert.Equal(domain, value.Domain);
        Assert.Equal(metricName, value.MetricName);
        Assert.Equal(totalMetric, value.TotalMetric);
        Assert.True(value.StackedEventCount <= value.TotalEventCount);
        Assert.True(value.StackedMetric <= value.TotalMetric);
        Assert.Equal(value.StackedEventCount < value.TotalEventCount, value.ContainsSyntheticUnknown);
        Assert.Equal(value.ContainsSyntheticUnknown ? "?!?" : null, value.SyntheticUnknownFrame);
    }

    private static void AssertSameCoverage(
        DomainStackCoverage? expected,
        DomainStackCoverage? actual) =>
        Assert.Equal(Assert.IsType<DomainStackCoverage>(expected), Assert.IsType<DomainStackCoverage>(actual));

    private static DomainStackCoverage Coverage(
        string domain,
        long total,
        long stacked,
        string metricName) =>
        new(
            Domain: domain,
            TotalEventCount: total,
            StackedEventCount: stacked,
            StackCoveragePct: total == 0 ? null : 100.0 * stacked / total,
            CoverageState: total == 0 ? "no_events" : stacked == 0 ? "no_stacks" : stacked == total ? "full" : "partial",
            TotalMetric: total,
            StackedMetric: stacked,
            MetricStackCoveragePct: total == 0 ? null : 100.0 * stacked / total,
            UnknownStackFrameIsSynthetic: true,
            MetricName: metricName,
            ContainsSyntheticUnknown: stacked < total,
            SyntheticUnknownFrame: stacked < total ? "?!?" : null);

    private static TraceCapabilities EmptyCapabilities() =>
        new(
            HasCpuSamples: false,
            HasCSwitch: false,
            HasFileIo: false,
            HasDiskIo: false,
            HasImageLoad: false,
            HasHardFaults: false,
            HasStackWalks: false,
            HasVirtualAlloc: false,
            HasNetIo: false,
            HasNetConnections: false,
            HasRegistry: false,
            HasReadyThread: false,
            HasInterrupt: false,
            HasAlpc: false,
            HasThreadEvents: false,
            HasClrGc: false,
            HasClrJit: false,
            HasClrAlloc: false,
            HasClrException: false,
            HasClrContention: false,
            HasNtHeap: false,
            HasMemoryProcessInfo: false,
            HasHandleEvents: false,
            HasPoolEvents: false);
}
