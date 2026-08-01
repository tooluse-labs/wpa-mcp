using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class InspectTraceTests
{
    private const string CpuFixture = "fixtures/small_cpu.etl";
    private const string FileIoFixture = "fixtures/small_fileio.etl";
    private const string MmapFixture = "fixtures/small_mmap.etl";
    private const string MemoryFixture = "fixtures/small_memory.etl";
    private const string PerfViewGcFixture = "fixtures/perfview_gcevents.etl";

    [Fact]
    public void FileIoCapabilityAgreesWithAnalyzerOnPresentAndMissingFixtures()
    {
        var cache = new TraceCache(capacity: 4);
        var meta = new MetaTools(cache);
        var io = new IoTools(cache);

        var fileIoInspect = meta.InspectTrace(FileIoFixture);
        var fileIoRows = io.FileIoTopFiles(FileIoFixture).Rows;
        Assert.True(fileIoInspect.Capabilities.HasFileIo);
        Assert.Contains(fileIoInspect.CapabilitySupportedTools, r => r.ToolName == "file_io_top_files");
        Assert.NotEmpty(fileIoRows);
        Assert.Contains(fileIoRows, row => row.WriteBytes > 0);

        var cpuInspect = meta.InspectTrace(CpuFixture);
        Assert.False(cpuInspect.Capabilities.HasFileIo);
        Assert.Contains(cpuInspect.Warnings, w => w.Code == "missing_file_io");
        Assert.Empty(io.FileIoTopFiles(CpuFixture).Rows);
    }

    [Fact]
    public void HardFaultCapabilityAgreesWithAnalyzerOnMmapFixture()
    {
        var cache = new TraceCache(capacity: 4);
        var meta = new MetaTools(cache);
        var hardFaults = new HardFaultTools(cache);

        var inspect = meta.InspectTrace(MmapFixture);

        Assert.True(inspect.Capabilities.HasHardFaults);
        Assert.Contains(inspect.CapabilitySupportedTools, r => r.ToolName == "hard_fault_by_file");
        Assert.NotEmpty(hardFaults.HardFaultByFile(MmapFixture).Rows);
    }

    [Fact]
    public void ImageLoadCapabilityAgreesWithAnalyzerOnMmapFixture()
    {
        var cache = new TraceCache(capacity: 4);
        var meta = new MetaTools(cache);
        var imageLoad = new ImageLoadTools(cache);

        var inspect = meta.InspectTrace(MmapFixture);
        var stacks = imageLoad.ImageLoadTopStacks(MmapFixture);

        Assert.True(inspect.Capabilities.HasImageLoad);
        Assert.Contains(inspect.CapabilitySupportedTools, r => r.ToolName == "image_load_top_gaps");
        Assert.True(stacks.TotalLoads > 0);
    }

    [Fact]
    public void ClrGcCapabilityAgreesWithAnalyzerOnPerfViewFixture()
    {
        var cache = new TraceCache(capacity: 4);
        var meta = new MetaTools(cache);
        var clr = new ClrTools(cache);

        var inspect = meta.InspectTrace(PerfViewGcFixture);
        var gc = clr.ClrGcAnalysis(PerfViewGcFixture);

        Assert.True(inspect.Capabilities.HasClrGc);
        Assert.False(inspect.Capabilities.HasCSwitch);
        Assert.False(inspect.Capabilities.HasStackWalks);
        Assert.True(gc.TotalGcCount > 0);
        Assert.Equal(
            [WarningBuilder.LegacyAccountedDurationWarning],
            gc.Warnings);
        Assert.Contains(inspect.CapabilitySupportedTools, r => r.ToolName == "clr_gc_analysis");
        Assert.DoesNotContain(inspect.CapabilitySupportedTools, r => r.ToolName == "clr_alloc_top_stacks");
        Assert.Contains(inspect.Warnings, w => w.Code == "missing_context_switches");
        Assert.Contains(inspect.Warnings, w => w.Code == "missing_stackwalks");
        Assert.DoesNotContain(inspect.Warnings, w => w.Code == "missing_clr_runtime");
    }

    [Fact]
    public void FixtureCapabilities_LockCompositeContractAssumptions()
    {
        var meta = new MetaTools(new TraceCache(capacity: 4));

        var cpu = meta.InspectTrace(CpuFixture);
        Assert.True(cpu.Capabilities.HasCpuSamples);
        Assert.True(cpu.Capabilities.HasCSwitch);
        Assert.True(cpu.Capabilities.HasReadyThread);
        Assert.False(cpu.Capabilities.HasStackWalks);

        var mmap = meta.InspectTrace(MmapFixture);
        Assert.True(mmap.Capabilities.HasHardFaults);
        Assert.False(mmap.Capabilities.HasCSwitch);
        Assert.False(mmap.Capabilities.HasStackWalks);
        Assert.False(mmap.Capabilities.HasMemoryProcessInfo);
        Assert.False(mmap.Capabilities.HasHandleEvents);

        var memory = meta.InspectTrace(MemoryFixture);
        Assert.True(memory.Capabilities.HasMemoryProcessInfo);
        Assert.True(memory.Capabilities.HasHandleEvents);
        Assert.True(memory.Capabilities.HasPoolEvents);
        Assert.Contains(memory.CapabilitySupportedTools, r => r.ToolName == "memory_resource_analysis");

        var fileIo = meta.InspectTrace(FileIoFixture);
        Assert.True(fileIo.Capabilities.HasFileIo);
        Assert.False(fileIo.Capabilities.HasCSwitch);
        Assert.False(fileIo.Capabilities.HasStackWalks);

        var perfViewGc = meta.InspectTrace(PerfViewGcFixture);
        Assert.True(perfViewGc.Capabilities.HasClrGc);
        Assert.False(perfViewGc.Capabilities.HasCSwitch);
        Assert.False(perfViewGc.Capabilities.HasStackWalks);
    }

    [Fact]
    public void BuildTraceQualityWarnings_ReportsLostEvents()
    {
        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 42,
            capabilities: AllCapabilities(),
            symbolQuality: GoodSymbolQuality());

        var warning = Assert.Single(warnings, w => w.Code == "events_lost");
        Assert.Equal("warn", warning.Severity);
        Assert.Empty(warning.AffectedTools);
        Assert.Empty(warning.DegradedTools);
        Assert.Contains("42 events were lost", warning.Message);
    }

    [Fact]
    public void BuildTraceQualityWarnings_ReportsMissingCoreProviders()
    {
        var capabilities = AllCapabilities() with
        {
            HasCpuSamples = false,
            HasCSwitch = false,
            HasStackWalks = false,
        };

        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: capabilities,
            symbolQuality: GoodSymbolQuality());

        Assert.Contains(warnings, w => w.Code == "missing_cpu_samples" && w.AffectedTools.Contains("cpu_top_functions"));
        Assert.Contains(warnings, w => w.Code == "missing_context_switches" && w.AffectedTools.Contains("wait_analysis"));
        Assert.Contains(warnings, w => w.Code == "missing_context_switches" && w.AffectedTools.Contains("diagnose_high_wait"));
        Assert.Contains(warnings, w => w.Code == "missing_stackwalks" && w.AffectedTools.Contains("wait_top_stacks"));
        Assert.Contains(warnings, w => w.Code == "missing_stackwalks"
                                      && w.DegradedTools.Contains("diagnose_high_wait")
                                      && !w.AffectedTools.Contains("diagnose_high_wait"));
        Assert.Contains(warnings, w => w.Code == "missing_cpu_samples"
                                      && w.DegradedTools.Contains("diagnose_slow_startup")
                                      && !w.AffectedTools.Contains("diagnose_slow_startup"));
        Assert.Contains(warnings, w => w.Code == "missing_context_switches"
                                      && w.DegradedTools.Contains("diagnose_slow_startup")
                                      && !w.AffectedTools.Contains("diagnose_slow_startup"));
    }

    [Fact]
    public void BuildTraceQualityWarnings_ReportsWaitStackGapsPerEventFamily()
    {
        var capabilities = AllCapabilities() with
        {
            HasStackWalks = true,
            HasCSwitch = true,
            HasCSwitchStacks = false,
            HasReadyThread = true,
            HasReadyThreadStacks = false,
        };

        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: capabilities,
            symbolQuality: GoodSymbolQuality());

        Assert.DoesNotContain(warnings, w => w.Code == "missing_stackwalks");
        Assert.Contains(warnings, w => w.Code == "missing_cswitch_stacks"
                                      && w.AffectedTools.Contains("wait_top_stacks")
                                      && w.DegradedTools.Contains("diagnose_high_wait"));
        Assert.Contains(warnings, w => w.Code == "missing_ready_thread_stacks"
                                      && w.AffectedTools.Contains("ready_thread_top_stacks")
                                      && w.DegradedTools.Contains("diagnose_high_wait"));
    }

    [Fact]
    public void BuildTraceQualityWarnings_TreatsJitAndExceptionsAsClrRuntimeSignals()
    {
        var noClr = AllCapabilities() with
        {
            HasClrGc = false,
            HasClrJit = false,
            HasClrAlloc = false,
            HasClrException = false,
            HasClrContention = false,
        };

        var noClrWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr,
            symbolQuality: GoodSymbolQuality());
        Assert.Contains(noClrWarnings, w => w.Code == "missing_clr_runtime");

        var exceptionWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr with { HasClrException = true },
            symbolQuality: GoodSymbolQuality());
        Assert.DoesNotContain(exceptionWarnings, w => w.Code == "missing_clr_runtime");

        var jitWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr with { HasClrJit = true },
            symbolQuality: GoodSymbolQuality());
        Assert.DoesNotContain(jitWarnings, w => w.Code == "missing_clr_runtime");
    }

    [Fact]
    public void BuildTraceQualityWarnings_SeparatesNetworkBytesFromConnectionLifecycle()
    {
        var lifecycleOnly = AllCapabilities() with
        {
            HasNetIo = false,
            HasNetConnections = true,
        };

        var lifecycleWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: lifecycleOnly,
            symbolQuality: GoodSymbolQuality());
        var byteWarning = Assert.Single(lifecycleWarnings, w => w.Code == "missing_network_io");
        Assert.Contains("net_top_stacks", byteWarning.AffectedTools);
        Assert.DoesNotContain("net_connections", byteWarning.AffectedTools);
        Assert.DoesNotContain(lifecycleWarnings, w => w.Code == "missing_network_connections");

        var byteOnly = AllCapabilities() with
        {
            HasNetIo = true,
            HasNetConnections = false,
        };

        var byteOnlyWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: byteOnly,
            symbolQuality: GoodSymbolQuality());
        var connectionWarning = Assert.Single(byteOnlyWarnings, w => w.Code == "missing_network_connections");
        Assert.Contains("net_connections", connectionWarning.AffectedTools);
        Assert.DoesNotContain(byteOnlyWarnings, w => w.Code == "missing_network_io");
    }

    [Fact]
    public void BuildCapabilitySupportedTools_IncludeJitExceptionsAndNetworkConnections()
    {
        var capabilities = AllCapabilities() with
        {
            HasClrGc = false,
            HasClrAlloc = false,
            HasClrContention = false,
            HasClrJit = true,
            HasClrException = true,
            HasNetIo = false,
            HasNetConnections = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>(StringComparer.Ordinal)
            {
                ["clr_exception"] = new(
                    Domain: "clr_exception",
                    TotalEventCount: 5,
                    StackedEventCount: 5,
                    StackCoveragePct: 100,
                    CoverageState: "full",
                    TotalMetric: 5,
                    StackedMetric: 5,
                    MetricStackCoveragePct: 100),
            },
        };

        var recommendations = MetaTools.BuildCapabilitySupportedTools(capabilities);

        Assert.Contains(recommendations, r => r.ToolName == "clr_jit_analysis");
        Assert.Contains(recommendations, r => r.ToolName == "clr_exception_top_stacks");
        Assert.Contains(recommendations, r => r.ToolName == "net_connections");
        Assert.DoesNotContain(recommendations, r => r.ToolName == "net_top_stacks");
    }

    [Fact]
    public void InspectTrace_ReportsEnabledCapabilitiesWithoutForcingBoolScanning()
    {
        var meta = new MetaTools(new TraceCache(capacity: 4));

        var cpu = meta.InspectTrace(CpuFixture);
        Assert.Contains("cpu_samples", cpu.EnabledCapabilities);
        Assert.Contains("context_switches", cpu.EnabledCapabilities);
        Assert.DoesNotContain("file_io", cpu.EnabledCapabilities);

        var mmap = meta.InspectTrace(MmapFixture);
        Assert.Contains("hard_faults", mmap.EnabledCapabilities);
        Assert.Contains("image_load", mmap.EnabledCapabilities);
        Assert.DoesNotContain("memory_process_info", mmap.EnabledCapabilities);
    }

    [Fact]
    public void BuildRecommendedDiagnosticFlows_FollowsCapabilityDrivenEvidence()
    {
        var capabilities = AllCapabilities() with
        {
            HasFileIo = false,
            HasMemoryProcessInfo = false,
            HasNetIo = false,
            HasNetConnections = false,
        };

        var flows = MetaTools.BuildRecommendedDiagnosticFlows(capabilities);

        var startup = Assert.Single(flows, flow => flow.FlowName == "slow_startup");
        Assert.Contains("diagnose_slow_startup", startup.ToolSequence);
        Assert.Contains("image_load", startup.EnabledCapabilities);
        Assert.Contains("file_io", startup.MissingCapabilities);

        var window = Assert.Single(flows, flow => flow.FlowName == "window_triage");
        Assert.Contains("diagnose_window", window.ToolSequence);
        Assert.Contains("hard_faults", window.EnabledCapabilities);
        Assert.Contains("file_io", window.MissingCapabilities);
        Assert.Contains(window.Caveats, caveat => caveat.Contains("File IO evidence"));

        Assert.DoesNotContain(flows, flow => flow.FlowName == "network_activity");
    }

    [Fact]
    public void BuildRecommendedDiagnosticFlows_GatesSchedulerStackToolsByTheirOwnDomains()
    {
        var noSchedulerStacks = AllCapabilities() with
        {
            HasStackWalks = true,
            HasCSwitchStacks = true,
            HasReadyThreadStacks = true,
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>(StringComparer.Ordinal)
            {
                ["cswitch"] = Coverage("cswitch", total: 20, stacked: 0),
                ["ready_thread"] = Coverage("ready_thread", total: 10, stacked: 0),
            },
        };

        var noStacksFlows = MetaTools.BuildRecommendedDiagnosticFlows(noSchedulerStacks);
        var noStacksFlow = Assert.Single(
            noStacksFlows,
            flow => flow.FlowName == "high_wait");

        Assert.Contains("diagnose_high_wait", noStacksFlow.ToolSequence);
        Assert.Contains("wait_analysis", noStacksFlow.ToolSequence);
        Assert.DoesNotContain("wait_top_stacks", noStacksFlow.ToolSequence);
        Assert.DoesNotContain("ready_thread_top_stacks", noStacksFlow.ToolSequence);
        Assert.DoesNotContain(noStacksFlows.SelectMany(flow => flow.ToolSequence), tool =>
            tool is "wait_top_stacks" or "ready_thread_top_stacks");
        Assert.Contains(noStacksFlow.Caveats, caveat => caveat.Contains("wait_top_stacks omitted", StringComparison.Ordinal));
        Assert.Contains(noStacksFlow.Caveats, caveat => caveat.Contains("ready_thread_top_stacks omitted", StringComparison.Ordinal));

        var schedulerStacks = noSchedulerStacks with
        {
            StackCoverageByDomain = new Dictionary<string, DomainStackCoverage>(StringComparer.Ordinal)
            {
                ["cswitch"] = Coverage("cswitch", total: 20, stacked: 5),
                ["ready_thread"] = Coverage("ready_thread", total: 10, stacked: 4),
            },
        };

        var stacksFlow = Assert.Single(
            MetaTools.BuildRecommendedDiagnosticFlows(schedulerStacks),
            flow => flow.FlowName == "high_wait");
        Assert.Contains("wait_top_stacks", stacksFlow.ToolSequence);
        Assert.Contains("ready_thread_top_stacks", stacksFlow.ToolSequence);
    }

    [Fact]
    public void BuildRecommendedDiagnosticFlows_UsesOnlyEnabledClrTools()
    {
        var jitOnly = AllCapabilities() with
        {
            HasClrGc = false,
            HasClrAlloc = false,
            HasClrException = false,
            HasClrContention = false,
            HasClrJit = true,
        };

        var flows = MetaTools.BuildRecommendedDiagnosticFlows(jitOnly);

        var dotnet = Assert.Single(flows, flow => flow.FlowName == "dotnet_runtime");
        Assert.Equal(new[] { "clr_jit_analysis" }, dotnet.ToolSequence);
        Assert.Contains("jit", dotnet.Goals);
        Assert.Contains("clr_jit", dotnet.EnabledCapabilities);
        Assert.Contains("clr_gc", dotnet.MissingCapabilities);
        Assert.DoesNotContain("clr_gc_analysis", dotnet.ToolSequence);
        Assert.DoesNotContain("clr_alloc_top_stacks", dotnet.ToolSequence);
        Assert.DoesNotContain("clr_exception_top_stacks", dotnet.ToolSequence);
        Assert.DoesNotContain("clr_contention_top_stacks", dotnet.ToolSequence);
    }

    [Fact]
    public void BuildCapabilitySupportedTools_RequiresWaitEventStacksForStackTools()
    {
        var capabilities = AllCapabilities() with
        {
            HasStackWalks = true,
            HasCSwitch = true,
            HasCSwitchStacks = false,
            HasReadyThread = true,
            HasReadyThreadStacks = false,
        };

        var recommendations = MetaTools.BuildCapabilitySupportedTools(capabilities);

        Assert.Contains(recommendations, r => r.ToolName == "wait_analysis");
        Assert.DoesNotContain(recommendations, r => r.ToolName == "wait_top_stacks");
        Assert.DoesNotContain(recommendations, r => r.ToolName == "ready_thread_top_stacks");
    }

    [Fact]
    public void BuildTraceQualityWarnings_ReportsMissingMemoryResourceSignals()
    {
        var capabilities = AllCapabilities() with
        {
            HasMemoryProcessInfo = false,
            HasHandleEvents = false,
            HasPoolEvents = false,
        };

        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: capabilities,
            symbolQuality: GoodSymbolQuality());

        Assert.Contains(warnings, w => w.Code == "missing_memory_process_info" &&
                                      w.AffectedTools.Contains("memory_resource_analysis"));
        Assert.Contains(warnings, w => w.Code == "missing_handle_events" &&
                                      w.AffectedTools.Contains("memory_resource_analysis"));
        Assert.Contains(warnings, w => w.Code == "missing_pool_events" &&
                                      w.AffectedTools.Contains("memory_resource_analysis"));
    }

    [Fact]
    public void PoolOnlyCapabilityStillRecommendsMemoryResourceAnalysis()
    {
        var capabilities = AllCapabilities() with
        {
            HasMemoryProcessInfo = false,
            HasHandleEvents = false,
            HasPoolEvents = true,
        };

        var recommendations = MetaTools.BuildCapabilitySupportedTools(capabilities);
        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: capabilities,
            symbolQuality: GoodSymbolQuality());

        Assert.Contains(recommendations, r => r.ToolName == "memory_resource_analysis");
        var recommendation = Assert.Single(recommendations, r => r.ToolName == "memory_resource_analysis");
        Assert.Contains("pool allocation/free deltas", recommendation.Reason);
        Assert.DoesNotContain("working set", recommendation.Reason);
        Assert.DoesNotContain("handle", recommendation.Reason);
        Assert.Contains(warnings, w => w.Code == "missing_memory_process_info");
        Assert.Contains(warnings, w => w.Code == "missing_handle_events");
        Assert.DoesNotContain(warnings, w => w.Code == "missing_pool_events");
    }

    private static InspectSymbolQuality GoodSymbolQuality() =>
        new(
            NtSymbolPath: @"SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols",
            CacheDir: @"C:\Symbols",
            ModuleCount: 1,
            ResolvedModuleCount: 1,
            ModuleResolutionRate: 1.0,
            TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
            Recommendations: Array.Empty<SymbolRecommendation>());

    private static DomainStackCoverage Coverage(string domain, long total, long stacked) =>
        new(
            Domain: domain,
            TotalEventCount: total,
            StackedEventCount: stacked,
            StackCoveragePct: total == 0 ? null : stacked * 100.0 / total,
            CoverageState: total == 0 ? "no_events" : stacked == 0 ? "no_stacks" : stacked == total ? "full" : "partial",
            TotalMetric: total,
            StackedMetric: stacked,
            MetricStackCoveragePct: total == 0 ? null : stacked * 100.0 / total);

    private static TraceCapabilities AllCapabilities() =>
        new(
            HasCpuSamples: true,
            HasCSwitch: true,
            HasFileIo: true,
            HasDiskIo: true,
            HasImageLoad: true,
            HasHardFaults: true,
            HasStackWalks: true,
            HasVirtualAlloc: true,
            HasNetIo: true,
            HasNetConnections: true,
            HasRegistry: true,
            HasReadyThread: true,
            HasInterrupt: true,
            HasAlpc: true,
            HasThreadEvents: true,
            HasClrGc: true,
            HasClrJit: true,
            HasClrAlloc: true,
            HasClrException: true,
            HasClrContention: true,
            HasNtHeap: true,
            HasMemoryProcessInfo: true,
            HasHandleEvents: true,
            HasPoolEvents: true,
            HasCSwitchStacks: true,
            HasReadyThreadStacks: true,
            HasInterruptStacks: true);
}
