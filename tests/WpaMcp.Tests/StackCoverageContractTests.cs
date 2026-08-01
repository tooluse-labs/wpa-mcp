using WpaMcp.Output;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class StackCoverageContractTests
{
    private const string WaitFixture = "fixtures/small_wait_bound.etl";

    [Fact]
    public void InspectTrace_DoesNotRecommendUnstackedImageDomainFromOtherDomainStacks()
    {
        var cache = new TraceCache(capacity: 2);
        var inspect = new MetaTools(cache).InspectTrace(WaitFixture);

        Assert.True(inspect.Capabilities.HasStackWalks);
        Assert.True(inspect.Capabilities.HasAttachedEventStacks);
        Assert.False(inspect.Capabilities.HasExplicitStackWalkEvents);
        Assert.Equal(0, inspect.Capabilities.ExplicitStackWalkEventCount);
        var coverage = Assert.IsType<DomainStackCoverage>(
            inspect.Capabilities.StackCoverageByDomain!["image_load"]);
        Assert.Equal("no_stacks", coverage.CoverageState);
        Assert.True(coverage.TotalEventCount > 0);
        Assert.Equal(0, coverage.StackedEventCount);
        Assert.DoesNotContain(
            inspect.CapabilitySupportedTools,
            row => row.ToolName == "image_load_top_stacks");

        var response = new ImageLoadTools(cache).ImageLoadTopStacks(WaitFixture, top: 5);
        var responseCoverage = Assert.IsType<DomainStackCoverage>(response.StackCoverage);
        Assert.Equal(coverage.TotalEventCount, responseCoverage.TotalEventCount);
        Assert.Equal(coverage.StackedEventCount, responseCoverage.StackedEventCount);
        Assert.Equal("no_stacks", responseCoverage.CoverageState);
        Assert.Equal("all_processes", response.ScopeMode);
        Assert.Empty(response.IncludedProcesses ?? []);
        Assert.Contains(response.Rows, row => row.Function == "?!?");
        Assert.Contains(response.Warnings, warning => warning.Contains(
            "stack_coverage_state=no_stacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Recommendations_DoNotUseUnrelatedGlobalStacksForFileIo()
    {
        var capabilities = Capabilities() with
        {
            HasFileIo = true,
            HasStackWalks = true,
            HasAttachedEventStacks = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>
            {
                ["file_io"] = Coverage("file_io", total: 20, stacked: 0),
            },
        };

        var recommendations = MetaTools.BuildCapabilitySupportedTools(capabilities);

        Assert.Contains(recommendations, row => row.ToolName == "file_io_top_files");
        Assert.DoesNotContain(recommendations, row => row.ToolName == "file_io_top_stacks");
        var flow = Assert.Single(
            MetaTools.BuildRecommendedDiagnosticFlows(capabilities),
            row => row.FlowName == "io_contention");
        Assert.Contains("file_io_top_files", flow.ToolSequence);
        Assert.DoesNotContain("file_io_top_stacks", flow.ToolSequence);
        Assert.Contains(flow.Caveats, caveat => caveat.Contains("no_stacks", StringComparison.Ordinal));
    }

    [Fact]
    public void Recommendations_MarkPartialDomainCoverage()
    {
        var capabilities = Capabilities() with
        {
            HasDiskIo = true,
            HasStackWalks = true,
            HasAttachedEventStacks = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>
            {
                ["disk_io"] = Coverage("disk_io", total: 4, stacked: 2),
            },
        };

        var recommendation = Assert.Single(
            MetaTools.BuildCapabilitySupportedTools(capabilities),
            row => row.ToolName == "disk_io_top_stacks");

        Assert.Contains("stack_coverage_state=partial", recommendation.Reason);
        Assert.Contains("stack_coverage_pct=50", recommendation.Reason);
        var flow = Assert.Single(
            MetaTools.BuildRecommendedDiagnosticFlows(capabilities),
            row => row.FlowName == "io_contention");
        Assert.Contains("disk_io_top_stacks", flow.ToolSequence);
    }

    [Fact]
    public void CpuHotspotFlow_OmitsCpuStackToolWhenCpuEventsHaveNoStacks()
    {
        var capabilities = Capabilities() with
        {
            HasCpuSamples = true,
            HasCSwitch = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>
            {
                ["cpu"] = Coverage("cpu", total: 20, stacked: 0),
            },
        };

        var flow = Assert.Single(
            MetaTools.BuildRecommendedDiagnosticFlows(capabilities),
            row => row.FlowName == "cpu_hotspot");

        Assert.DoesNotContain("cpu_top_functions", flow.ToolSequence);
        Assert.Contains("cpu_precise_analysis", flow.ToolSequence);
        Assert.Contains(flow.Caveats, caveat => caveat.Contains(
            "cpu_top_functions omitted", StringComparison.Ordinal));
    }

    [Fact]
    public void CallerCallee_NoStacksDoesNotClaimFocusFrameAbsent()
    {
        const string fixture = "fixtures/small_wait_bound.etl";
        var response = new ImageLoadTools(new TraceCache(capacity: 2))
            .ImageLoadCallerCallee(fixture, function: "definitely-not-a-frame", top: 5);

        Assert.Equal("stacks_unavailable", response.NoDataReason);
        Assert.DoesNotContain(response.Warnings, warning => warning.StartsWith(
            "Focus function", StringComparison.Ordinal));
        Assert.DoesNotContain(response.Warnings, warning => warning.StartsWith(
            "focus_not_found:", StringComparison.Ordinal));
    }

    private static DomainStackCoverage Coverage(string domain, long total, long stacked) =>
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
            MetricName: "count",
            ContainsSyntheticUnknown: stacked < total,
            SyntheticUnknownFrame: stacked < total ? "?!?" : null);

    private static TraceCapabilities Capabilities() =>
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
