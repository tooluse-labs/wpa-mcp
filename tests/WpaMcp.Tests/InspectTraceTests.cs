using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
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
        Assert.Contains("file_io_top_files", fileIoInspect.CapabilitySupportedTools);
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
        Assert.Contains("hard_fault_by_file", inspect.CapabilitySupportedTools);
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
        Assert.Contains("image_load_top_gaps", inspect.CapabilitySupportedTools);
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
        Assert.Contains("clr_gc_analysis", inspect.CapabilitySupportedTools);
        Assert.DoesNotContain("clr_alloc_top_stacks", inspect.CapabilitySupportedTools);
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
        Assert.Equal(
            cpu.Capabilities.ObservedThreadLifecycleEndpointEventCount +
            cpu.Capabilities.ThreadRundownEndpointEventCount,
            cpu.Capabilities.ThreadLifecycleSourceEventCount);
        Assert.Equal(
            cpu.Capabilities.HasThreadEvents,
            cpu.Capabilities.ThreadLifecycleSourceEventCount > 0);
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
        Assert.Contains("memory_resource_analysis", memory.CapabilitySupportedTools);

        var fileIo = meta.InspectTrace(FileIoFixture);
        Assert.True(fileIo.Capabilities.HasFileIo);
        Assert.False(fileIo.Capabilities.HasCSwitch);
        Assert.False(fileIo.Capabilities.HasStackWalks);

        var perfViewGc = meta.InspectTrace(PerfViewGcFixture);
        Assert.True(perfViewGc.Capabilities.HasClrGc);
        Assert.True(perfViewGc.Capabilities.ClrGcIntervalEndpointEventCount > 0);
        Assert.Equal(
            perfViewGc.Capabilities.HasClrGc,
            perfViewGc.Capabilities.ClrGcIntervalEndpointEventCount > 0);
        Assert.Equal(
            perfViewGc.Capabilities.ClrFinalizerObjectEventCount +
            perfViewGc.Capabilities.ClrFinalizerBatchStartEndpointEventCount +
            perfViewGc.Capabilities.ClrFinalizerBatchStopEndpointEventCount,
            perfViewGc.Capabilities.ClrFinalizerSourceEventCount);
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
            ClrGcIntervalEndpointEventCount = 0,
            ClrGcHeapStatsEventCount = 0,
            ClrFinalizerSourceEventCount = 0,
            HasClrJit = false,
            ClrJitIntervalEndpointEventCount = 0,
            ClrJitCompletedIntervalCount = 0,
            ClrJitUnmatchedEndpointCount = 0,
            ClrJitBoundaryEvidenceCount = 0,
            HasClrAlloc = false,
            HasClrException = false,
            HasClrContention = false,
        };

        var noClrWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr,
            symbolQuality: GoodSymbolQuality());
        var missingClr = Assert.Single(noClrWarnings, w => w.Code == "missing_clr_runtime");
        Assert.Contains("No supported CLR", missingClr.Message, StringComparison.Ordinal);
        Assert.Contains("does not prove", missingClr.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CLR runtime events were not observed",
            missingClr.Message,
            StringComparison.Ordinal);

        var exceptionWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr with { HasClrException = true },
            symbolQuality: GoodSymbolQuality());
        Assert.DoesNotContain(exceptionWarnings, w => w.Code == "missing_clr_runtime");

        var jitWarnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 0,
            capabilities: noClr with
            {
                HasClrJit = true,
                ClrJitIntervalEndpointEventCount = 1,
                ClrJitUnmatchedEndpointCount = 1,
            },
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
            NetworkConnectionLifecycleEndpointEventCount = 0,
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
            ClrGcIntervalEndpointEventCount = 0,
            ClrGcHeapStatsEventCount = 0,
            ClrFinalizerSourceEventCount = 0,
            HasClrAlloc = false,
            HasClrContention = false,
            HasClrJit = true,
            ClrJitIntervalEndpointEventCount = 2,
            ClrJitCompletedIntervalCount = 1,
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
        Assert.Contains("scheduler.cpu_accounting", cpu.EnabledCapabilities);
        Assert.Contains("scheduler.blocked_time", cpu.EnabledCapabilities);
        Assert.DoesNotContain("io.file.aggregate", cpu.EnabledCapabilities);

        var mmap = meta.InspectTrace(MmapFixture);
        Assert.Contains("memory.hard_fault.files", mmap.EnabledCapabilities);
        Assert.Contains("image.load.timing", mmap.EnabledCapabilities);
        Assert.DoesNotContain("network.connections", mmap.EnabledCapabilities);
    }

    [Fact]
    public void InspectTrace_TraceEvidenceMapIsClosedOverActiveCatalogAndPreservesUnknown()
    {
        using var cache = new TraceCache(capacity: 4);
        var inspect = new MetaTools(cache).InspectTrace(FileIoFixture);
        var firstPage = Assert.IsType<TraceEvidenceMapRecord>(inspect.TraceEvidenceMap);
        var catalog = ActiveToolCatalog.LoadAndValidate();
        using var lease = cache.Acquire(FileIoFixture);
        var map = TraceEvidenceMapBuilder.Build(
            catalog,
            lease.GetFacts(CancellationToken.None),
            GoodSymbolQuality());

        Assert.Equal(catalog.CatalogVersion, map.CatalogVersion);
        Assert.Equal(catalog.Capabilities.Count, map.TotalCapabilities);
        Assert.Equal(map.TotalCapabilities, map.ReturnedCapabilities);
        Assert.Equal(catalog.Capabilities.Count, firstPage.CatalogCapabilityCount);
        Assert.Equal(catalog.Capabilities.Count, firstPage.TotalCapabilities);
        Assert.Equal(firstPage.Capabilities.Count, firstPage.ReturnedCapabilities);
        Assert.True(firstPage.ReturnedCapabilities < firstPage.TotalCapabilities);
        Assert.True(inspect.HasMore);
        Assert.Equal(QueryResultCursorRegistry.PendingDeliveryToken, inspect.NextCursor);
        Assert.Equal(
            catalog.Capabilities.OrderBy(capability => capability.Domain, StringComparer.Ordinal)
                .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
                .Select(capability => capability.CapabilityId),
            map.Capabilities.Select(capability => capability.CapabilityId));
        Assert.Equal(
            catalog.Workflows.OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
                .Select(workflow => workflow.WorkflowId),
            map.Workflows.Select(workflow => workflow.WorkflowId));
        Assert.All(map.Workflows, workflow =>
        {
            var membership = Assert.Single(catalog.Workflows, item =>
                item.WorkflowId == workflow.WorkflowId).CapabilityIds.Length;
            Assert.Equal(membership, workflow.TotalCapabilityCount);
            Assert.Equal(
                workflow.TotalCapabilityCount,
                workflow.AvailableCapabilityCount +
                workflow.PartialCapabilityCount +
                workflow.UnknownCapabilityCount +
                workflow.UnavailableCapabilityCount +
                workflow.NotApplicableCapabilityCount);
        });
        var lifecycleWorkflow = Assert.Single(map.Workflows, workflow =>
            workflow.WorkflowId == "workflow.trace_lifecycle");
        Assert.True(lifecycleWorkflow.NotApplicableCapabilityCount > 0);
        Assert.Contains(
            "trace_not_applicable_members_are_catalog_members_not_missing_evidence",
            lifecycleWorkflow.DoesNotProve);
        Assert.All(map.Capabilities, capability =>
        {
            switch (capability.TraceStatus)
            {
                case ToolCapabilityStatus.Unknown:
                case ToolCapabilityStatus.Unavailable:
                    Assert.Equal(
                        ConclusionStatus.NotConcluded,
                        capability.ConclusionStatus);
                    break;
                case ToolCapabilityStatus.Partial:
                    Assert.Equal(ConclusionStatus.Partial, capability.ConclusionStatus);
                    break;
                case ToolCapabilityStatus.NotApplicable:
                    Assert.Equal(
                        ConclusionStatus.NotApplicable,
                        capability.ConclusionStatus);
                    break;
                case ToolCapabilityStatus.Available:
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            if (capability.ConclusionStatus is
                ConclusionStatus.Observed or ConclusionStatus.Supported)
            {
                Assert.Equal(ToolCapabilityStatus.Available, capability.TraceStatus);
            }
        });

        var file = Assert.Single(map.Capabilities, capability =>
            capability.CapabilityId == "io.file.aggregate");
        Assert.Equal(ToolCapabilityStatus.Available, file.TraceStatus);
        Assert.Contains("file_io_top_files", file.CallableTools);

        var cpu = Assert.Single(map.Capabilities, capability =>
            capability.CapabilityId == "cpu.sampled.stacks");
        Assert.Equal(ToolCapabilityStatus.Unknown, cpu.TraceStatus);
        Assert.Contains(cpu.Warnings, warning => warning.Contains(
            "does_not_prove_capture_or_parser_absence",
            StringComparison.Ordinal));
        Assert.Equal("deprecated_id_only_derived_projection", inspect.LegacyProjectionState);
        Assert.StartsWith("not_concluded_", map.SelfAttribution.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void PolicyDisabledCapability_PreservesTraceEvidenceAndUsesOrthogonalWorkflowSubset()
    {
        var fullCatalog = ActiveToolCatalog.LoadAndValidate();
        var services = new DeferredCatalogServiceProvider();
        var policy = CapabilityPolicyProfile.Parse(
            "cpu.sampled.stacks",
            "test");
        var restrictedCatalog = fullCatalog.ProjectCapabilityPolicy(
                policy,
                fullCatalog.CreateServerTools(services))
            .Catalog;
        using var cache = new TraceCache(capacity: 2);
        using var lease = cache.Acquire(CpuFixture);
        var facts = lease.GetFacts(CancellationToken.None);
        var full = TraceEvidenceMapBuilder.Build(
            fullCatalog,
            facts,
            GoodSymbolQuality());
        var restricted = TraceEvidenceMapBuilder.Build(
            restrictedCatalog,
            facts,
            GoodSymbolQuality());

        var fullCpu = Assert.Single(full.Capabilities, capability =>
            capability.CapabilityId == "cpu.sampled.stacks");
        var restrictedCpu = Assert.Single(restricted.Capabilities, capability =>
            capability.CapabilityId == "cpu.sampled.stacks");
        Assert.Equal(fullCpu.TraceStatus, restrictedCpu.TraceStatus);
        Assert.Equal(fullCpu.TraceEligibleEventCount, restrictedCpu.TraceEligibleEventCount);
        Assert.Equal(fullCpu.StackCoverage, restrictedCpu.StackCoverage);
        Assert.Equal(fullCpu.MeasurementBasis, restrictedCpu.MeasurementBasis);
        Assert.Equal(fullCpu.Relationship, restrictedCpu.Relationship);
        Assert.Equal(fullCpu.ConclusionStatus, restrictedCpu.ConclusionStatus);
        Assert.Equal(fullCpu.CaptureIntegrity, restrictedCpu.CaptureIntegrity);
        Assert.Equal(
            CapabilityAvailabilityStatus.DisabledByPolicy,
            restrictedCpu.AvailabilityState);
        Assert.Empty(restrictedCpu.CallableTools);
        Assert.Equal(
            ["cpu_caller_callee", "cpu_top_functions", "cpu_top_functions_batch"],
            restrictedCpu.DisabledByPolicyTools.Order(StringComparer.Ordinal));

        foreach (var restrictedWorkflow in restricted.Workflows)
        {
            var fullWorkflow = Assert.Single(full.Workflows, workflow =>
                workflow.WorkflowId == restrictedWorkflow.WorkflowId);
            Assert.Equal(fullWorkflow.AvailableCapabilityCount,
                restrictedWorkflow.AvailableCapabilityCount);
            Assert.Equal(fullWorkflow.PartialCapabilityCount,
                restrictedWorkflow.PartialCapabilityCount);
            Assert.Equal(fullWorkflow.UnknownCapabilityCount,
                restrictedWorkflow.UnknownCapabilityCount);
            Assert.Equal(fullWorkflow.UnavailableCapabilityCount,
                restrictedWorkflow.UnavailableCapabilityCount);
            Assert.Equal(fullWorkflow.NotApplicableCapabilityCount,
                restrictedWorkflow.NotApplicableCapabilityCount);
            Assert.Equal(
                restrictedWorkflow.TotalCapabilityCount,
                restrictedWorkflow.AvailableCapabilityCount +
                restrictedWorkflow.PartialCapabilityCount +
                restrictedWorkflow.UnknownCapabilityCount +
                restrictedWorkflow.UnavailableCapabilityCount +
                restrictedWorkflow.NotApplicableCapabilityCount);
            Assert.Equal(
                restrictedWorkflow.DisabledByPolicyCapabilityIds.Count,
                restrictedWorkflow.DisabledByPolicyCapabilityCount);
        }
        Assert.DoesNotContain(
            restricted.Workflows.SelectMany(workflow => workflow.SuggestedTools),
            toolName => toolName is
                "cpu_caller_callee" or "cpu_top_functions" or "cpu_top_functions_batch");
    }

    [Fact]
    public void SplitPredicates_SyntheticMutualExclusionPreventsCrossCapabilityRecommendations()
    {
        using var cache = new TraceCache(capacity: 2);
        using var lease = cache.Acquire(CpuFixture);
        var baseFacts = lease.GetFacts(CancellationToken.None);
        var catalog = ActiveToolCatalog.LoadAndValidate();

        TraceFactsSnapshot Facts(TraceCapabilities capabilities) => baseFacts with
        {
            Capabilities = capabilities,
            CaptureIntegrity = new TraceCaptureIntegrityFacts(
                ReportedEventsLost: 0,
                State: "no_reported_event_loss",
                MeasurementBasis: "TraceLog.EventsLost"),
        };

        CapabilityRuntimeAssessment Assess(
            string capabilityId,
            TraceCapabilities capabilities) => catalog.EvaluatorRegistry.EvaluateTrace(
                Assert.Single(catalog.Capabilities, capability =>
                    capability.CapabilityId == capabilityId),
                Facts(capabilities));

        var none = baseFacts.Capabilities with
        {
            // Deliberately keep the deprecated umbrella flags true. The new evaluator
            // truth is the exact counter and must not be promoted by compatibility flags.
            HasClrGc = true,
            HasNetIo = true,
            HasNetConnections = true,
            HasClrJit = true,
            HasImageLoad = true,
            HasMemoryProcessInfo = false,
            HasMemorySystemInfo = false,
            HasHandleEvents = false,
            HasPoolEvents = false,
            ObservedProcessStartEventCount = 0,
            ObservedThreadLifecycleEndpointEventCount = 0,
            ThreadRundownEndpointEventCount = 0,
            ThreadLifecycleSourceEventCount = 0,
            ThreadCompletedObservedLifetimeCount = 0,
            ThreadUnmatchedLifecycleEndpointCount = 0,
            ThreadInferredBoundaryCount = 0,
            ClrGcIntervalEndpointEventCount = 0,
            ClrGcCompletedIntervalCount = 0,
            ClrGcUnmatchedEndpointCount = 0,
            ClrGcBoundaryEvidenceCount = 0,
            ClrGcHeapStatsEventCount = 0,
            ClrFinalizerObjectEventCount = 0,
            ClrFinalizerBatchStartEndpointEventCount = 0,
            ClrFinalizerBatchStopEndpointEventCount = 0,
            ClrFinalizerCompletedBatchCount = 0,
            ClrFinalizerSourceEventCount = 0,
            NetworkConnectionLifecycleEndpointEventCount = 0,
            NetworkConnectionCompletedLifecycleCount = 0,
            NetworkConnectionUnmatchedEndpointCount = 0,
            NetworkConnectionBoundaryEvidenceCount = 0,
            ClrJitIntervalEndpointEventCount = 0,
            ClrJitCompletedIntervalCount = 0,
            ClrJitUnmatchedEndpointCount = 0,
            ClrJitBoundaryEvidenceCount = 0,
        };

        Assert.Equal(
            ToolCapabilityStatus.Available,
            Assess("trace.process.inventory", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("trace.process.creation", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("trace.thread.lifetime", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.gc.intervals", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.gc.heap_stats", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.finalizer.activity", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("network.connections", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.jit.intervals", none).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("memory.resource.activity", none).TraceStatus);

        var noneTools = MetaTools.BuildCapabilitySupportedTools(none)
            .Select(item => item.ToolName)
            .ToHashSet(StringComparer.Ordinal);
        Assert.DoesNotContain("process_create_timing", noneTools);
        Assert.DoesNotContain("clr_gc_analysis", noneTools);
        Assert.DoesNotContain("clr_gc_heap_stats", noneTools);
        Assert.DoesNotContain("clr_finalizer_analysis", noneTools);
        Assert.DoesNotContain("net_connections", noneTools);

        var startsOnly = none with { ObservedProcessStartEventCount = 3 };
        var creation = Assess("trace.process.creation", startsOnly);
        Assert.Equal(ToolCapabilityStatus.Available, creation.TraceStatus);
        Assert.Equal(3L, creation.TraceEligibleEventCount);
        Assert.Contains(
            MetaTools.BuildCapabilitySupportedTools(startsOnly),
            item => item.ToolName == "process_create_timing");

        var rundownOnly = none with
        {
            HasThreadEvents = true,
            ThreadRundownEndpointEventCount = 2,
            ThreadLifecycleSourceEventCount = 2,
            ThreadInferredBoundaryCount = 2,
        };
        var thread = Assess("trace.thread.lifetime", rundownOnly);
        Assert.Equal(ToolCapabilityStatus.Partial, thread.TraceStatus);
        Assert.Equal(2L, thread.TraceEligibleEventCount);
        Assert.Equal(0L, thread.TraceCompletedEvidenceCount);
        Assert.Equal(0L, thread.TraceUnmatchedEvidenceCount);
        Assert.Equal(2L, thread.TraceBoundaryEvidenceCount);
        Assert.Equal(
            ToolEvidenceCompletionState.SourceWithoutCompletedEvidence,
            thread.EvidenceCompletionState);
        var threadEvidence = Assert.Single(thread.Evidence);
        Assert.Equal(MeasurementBasis.Derived, threadEvidence.MeasurementBasis);
        Assert.Equal(ConclusionStatus.Partial, threadEvidence.ConclusionStatus);

        var intervalsOnly = none with
        {
            ClrGcIntervalEndpointEventCount = 1,
            ClrGcUnmatchedEndpointCount = 1,
        };
        Assert.Equal(
            ToolCapabilityStatus.Partial,
            Assess("clr.gc.intervals", intervalsOnly).TraceStatus);
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.gc.heap_stats", intervalsOnly).TraceStatus);
        var intervalTools = MetaTools.BuildCapabilitySupportedTools(intervalsOnly)
            .Select(item => item.ToolName)
            .ToArray();
        Assert.Contains("clr_gc_analysis", intervalTools);
        Assert.DoesNotContain("clr_gc_heap_stats", intervalTools);

        var completedIntervals = intervalsOnly with
        {
            ClrGcIntervalEndpointEventCount = 4,
            ClrGcCompletedIntervalCount = 2,
            ClrGcUnmatchedEndpointCount = 0,
        };
        var completedGc = Assess("clr.gc.intervals", completedIntervals);
        Assert.Equal(ToolCapabilityStatus.Available, completedGc.TraceStatus);
        Assert.Equal(
            ToolEvidenceCompletionState.Complete,
            completedGc.EvidenceCompletionState);

        var heapOnly = none with { ClrGcHeapStatsEventCount = 2 };
        Assert.Equal(
            ToolCapabilityStatus.Unknown,
            Assess("clr.gc.intervals", heapOnly).TraceStatus);
        var heapAssessment = Assess("clr.gc.heap_stats", heapOnly);
        Assert.Equal(ToolCapabilityStatus.Available, heapAssessment.TraceStatus);
        Assert.Equal(2L, heapAssessment.TraceEligibleEventCount);
        var heapTools = MetaTools.BuildCapabilitySupportedTools(heapOnly)
            .Select(item => item.ToolName)
            .ToArray();
        Assert.Contains("clr_gc_heap_stats", heapTools);
        Assert.DoesNotContain("clr_gc_analysis", heapTools);

        var finalizerOnly = none with
        {
            ClrFinalizerObjectEventCount = 2,
            ClrFinalizerBatchStartEndpointEventCount = 1,
            ClrFinalizerBatchStopEndpointEventCount = 1,
            ClrFinalizerCompletedBatchCount = 1,
            ClrFinalizerSourceEventCount = 4,
        };
        var finalizer = Assess("clr.finalizer.activity", finalizerOnly);
        Assert.Equal(ToolCapabilityStatus.Available, finalizer.TraceStatus);
        Assert.Equal(4L, finalizer.TraceEligibleEventCount);
        Assert.Equal(2, finalizerOnly.ClrFinalizerObjectEventCount);
        Assert.Equal(2,
            finalizerOnly.ClrFinalizerBatchStartEndpointEventCount +
            finalizerOnly.ClrFinalizerBatchStopEndpointEventCount);
        Assert.Equal(1, finalizerOnly.ClrFinalizerCompletedBatchCount);
        Assert.Contains(
            MetaTools.BuildCapabilitySupportedTools(finalizerOnly),
            item => item.ToolName == "clr_finalizer_analysis");

        var lifecycleOnly = none with
        {
            HasNetConnections = false,
            NetworkConnectionLifecycleEndpointEventCount = 1,
            NetworkConnectionBoundaryEvidenceCount = 1,
        };
        var network = Assess("network.connections", lifecycleOnly);
        Assert.Equal(ToolCapabilityStatus.Partial, network.TraceStatus);
        Assert.Equal(1L, network.TraceEligibleEventCount);
        Assert.Equal(
            ToolEvidenceCompletionState.SourceWithoutCompletedEvidence,
            network.EvidenceCompletionState);
        Assert.Contains(
            MetaTools.BuildCapabilitySupportedTools(lifecycleOnly),
            item => item.ToolName == "net_connections");

        var completedNetwork = lifecycleOnly with
        {
            NetworkConnectionLifecycleEndpointEventCount = 2,
            NetworkConnectionCompletedLifecycleCount = 1,
            NetworkConnectionBoundaryEvidenceCount = 0,
        };
        Assert.Equal(
            ToolCapabilityStatus.Available,
            Assess("network.connections", completedNetwork).TraceStatus);

        var jitStartOnly = none with
        {
            ClrJitIntervalEndpointEventCount = 1,
            ClrJitUnmatchedEndpointCount = 1,
        };
        var partialJit = Assess("clr.jit.intervals", jitStartOnly);
        Assert.Equal(ToolCapabilityStatus.Partial, partialJit.TraceStatus);
        Assert.Equal(ConclusionStatus.Partial, Assert.Single(partialJit.Evidence).ConclusionStatus);
        var projectedJit = Assert.Single(
            TraceEvidenceMapBuilder.Build(
                    catalog,
                    Facts(jitStartOnly),
                    GoodSymbolQuality())
                .Capabilities,
            capability => capability.CapabilityId == "clr.jit.intervals");
        Assert.Equal(0L, projectedJit.TraceCompletedEvidenceCount);
        Assert.Equal(1L, projectedJit.TraceUnmatchedEvidenceCount);
        Assert.Equal(0L, projectedJit.TraceBoundaryEvidenceCount);
        Assert.Equal(
            ToolEvidenceCompletionState.SourceWithoutCompletedEvidence,
            projectedJit.EvidenceCompletionState);
        var jitTool = Assert.Single(catalog.Tools, tool =>
            tool.ToolName == "clr_jit_analysis");
        var optimisticOutcome = new ReviewedToolRuntimeOutcome(
            HasUsableData: true,
            NoDataReason: null,
            Partial: false,
            ScopeSource: ReviewedScopeSource.ResultFields,
            TraceCapabilityStatus: ToolCapabilityStatus.Available,
            ScopedCapabilityStatus: ToolCapabilityStatus.Available,
            MatchedEventCount: 1,
            CaptureIntegrity: ToolCaptureIntegrityStatus.Unknown,
            MeasurementBasis: MeasurementBasis.Direct,
            Relationship: Relationship.Temporal,
            ConclusionStatus: ConclusionStatus.Observed,
            PartialErrorCode: null);
        var boundedToolAssessment = catalog.EvaluatorRegistry.EvaluateTool(
            jitTool,
            Assert.Single(jitTool.Capabilities),
            domain: null,
            optimisticOutcome,
            Facts(jitStartOnly),
            failed: false);
        Assert.Equal(ToolCapabilityStatus.Partial, boundedToolAssessment.TraceStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, boundedToolAssessment.ScopedStatus);
        Assert.Equal(
            ConclusionStatus.Partial,
            Assert.Single(boundedToolAssessment.Evidence).ConclusionStatus);
        var pairedJit = jitStartOnly with
        {
            ClrJitIntervalEndpointEventCount = 2,
            ClrJitCompletedIntervalCount = 1,
            ClrJitUnmatchedEndpointCount = 0,
        };
        Assert.Equal(
            ToolCapabilityStatus.Available,
            Assess("clr.jit.intervals", pairedJit).TraceStatus);

        var oneMemoryFacet = none with { HasMemoryProcessInfo = true };
        var partialMemory = Assess("memory.resource.activity", oneMemoryFacet);
        Assert.Equal(ToolCapabilityStatus.Partial, partialMemory.TraceStatus);
        Assert.Contains(partialMemory.Warnings, warning => warning.StartsWith(
            "partial_event_requirements_missing:",
            StringComparison.Ordinal));
        var memoryTool = Assert.Single(catalog.Tools, tool =>
            tool.ToolName == "memory_resource_analysis");
        var boundedMemoryTool = catalog.EvaluatorRegistry.EvaluateTool(
            memoryTool,
            Assert.Single(memoryTool.Capabilities),
            domain: null,
            optimisticOutcome,
            Facts(oneMemoryFacet),
            failed: false);
        Assert.Equal(ToolCapabilityStatus.Partial, boundedMemoryTool.TraceStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, boundedMemoryTool.ScopedStatus);
        var allMemoryFacets = oneMemoryFacet with
        {
            HasMemorySystemInfo = true,
            HasHandleEvents = true,
            HasPoolEvents = true,
        };
        Assert.Equal(
            ToolCapabilityStatus.Available,
            Assess("memory.resource.activity", allMemoryFacets).TraceStatus);
    }

    [Fact]
    public void TraceEvidenceMap_ReportedEventLossCapsTraceAndScopedAvailability()
    {
        using var cache = new TraceCache(capacity: 2);
        using var lease = cache.Acquire(FileIoFixture);
        var facts = lease.GetFacts(CancellationToken.None) with
        {
            CaptureIntegrity = new TraceCaptureIntegrityFacts(
                ReportedEventsLost: 7,
                State: "reported_event_loss",
                MeasurementBasis: "TraceLog.EventsLost"),
        };
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var map = TraceEvidenceMapBuilder.Build(catalog, facts, GoodSymbolQuality());

        var fileCapability = Assert.Single(map.Capabilities, capability =>
            capability.CapabilityId == "io.file.aggregate");
        Assert.Equal(ToolCapabilityStatus.Partial, fileCapability.TraceStatus);
        Assert.Equal(ConclusionStatus.Partial, fileCapability.ConclusionStatus);
        Assert.Equal(ToolCaptureIntegrityStatus.Partial, fileCapability.CaptureIntegrity);
        Assert.Contains(fileCapability.Warnings, warning => warning ==
            "reported_event_loss_may_reduce_observed_evidence");

        var tool = Assert.Single(catalog.Tools, candidate =>
            candidate.ToolName == "file_io_top_files");
        var outcome = new ReviewedToolRuntimeOutcome(
            HasUsableData: true,
            NoDataReason: null,
            Partial: false,
            ScopeSource: ReviewedScopeSource.ResultFields,
            TraceCapabilityStatus: ToolCapabilityStatus.Available,
            ScopedCapabilityStatus: ToolCapabilityStatus.Available,
            MatchedEventCount: 12,
            CaptureIntegrity: ToolCaptureIntegrityStatus.Unknown,
            MeasurementBasis: MeasurementBasis.Direct,
            Relationship: Relationship.Attribution,
            ConclusionStatus: ConclusionStatus.Observed,
            PartialErrorCode: null);
        var assessment = catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            Assert.Single(tool.Capabilities),
            domain: null,
            outcome,
            facts,
            failed: false);

        Assert.Equal(ToolCapabilityStatus.Partial, assessment.TraceStatus);
        Assert.Equal(ToolCapabilityStatus.Partial, assessment.ScopedStatus);
        Assert.All(assessment.Evidence, evidence =>
            Assert.Equal(ConclusionStatus.Partial, evidence.ConclusionStatus));

        var coverageByDomain = facts.Capabilities.StackCoverageByDomain!
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        coverageByDomain["file_io"] = Coverage("file_io", total: 12, stacked: 0);
        var noStackFacts = facts with
        {
            Capabilities = facts.Capabilities with
            {
                HasFileIo = true,
                StackCoverageByDomain = coverageByDomain,
            },
        };
        var fileStackCapability = Assert.Single(catalog.Capabilities, capability =>
            capability.CapabilityId == "io.file.stacks");
        var noStackAssessment = catalog.EvaluatorRegistry.EvaluateTrace(
            fileStackCapability,
            noStackFacts);

        Assert.Equal(ToolCapabilityStatus.Unknown, noStackAssessment.TraceStatus);
        Assert.Null(noStackAssessment.UnavailableReason);
        Assert.Contains(noStackAssessment.Warnings, warning => warning ==
            "target_event_has_no_stacks_observed_but_reported_event_loss_prevents_complete_absence_conclusion");
    }

    [Fact]
    public void StackCapability_TraceFactsRemainAuthoritativeOverScopedCoverage()
    {
        using var cache = new TraceCache(capacity: 2);
        using var lease = cache.Acquire(FileIoFixture);
        var baseFacts = lease.GetFacts(CancellationToken.None);
        var catalog = ActiveToolCatalog.LoadAndValidate();
        var tool = Assert.Single(catalog.Tools, candidate =>
            candidate.ToolName == "file_io_top_stacks");
        var capability = Assert.Single(tool.Capabilities);

        TraceFactsSnapshot WithCoverage(long total, long stacked)
        {
            var byDomain = baseFacts.Capabilities.StackCoverageByDomain!
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            byDomain["file_io"] = Coverage("file_io", total, stacked);
            return baseFacts with
            {
                Capabilities = baseFacts.Capabilities with
                {
                    HasFileIo = true,
                    StackCoverageByDomain = byDomain,
                },
                CaptureIntegrity = new TraceCaptureIntegrityFacts(
                    ReportedEventsLost: 0,
                    State: "no_reported_event_loss",
                    MeasurementBasis: "TraceLog.EventsLost"),
            };
        }

        ReviewedToolRuntimeOutcome Outcome(
            ToolCapabilityStatus trace,
            ToolCapabilityStatus scoped) => new(
                HasUsableData: scoped is ToolCapabilityStatus.Available or ToolCapabilityStatus.Partial,
                NoDataReason: scoped == ToolCapabilityStatus.Unavailable ? "stacks_unavailable" : null,
                Partial: false,
                ScopeSource: ReviewedScopeSource.ResultFields,
                TraceCapabilityStatus: trace,
                ScopedCapabilityStatus: scoped,
                MatchedEventCount: 5,
                CaptureIntegrity: ToolCaptureIntegrityStatus.Unknown,
                MeasurementBasis: MeasurementBasis.Direct,
                Relationship: Relationship.Association,
                ConclusionStatus: ConclusionStatus.NotConcluded,
                PartialErrorCode: null);

        var globalPartialScopedFull = catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            capability,
            domain: null,
            Outcome(ToolCapabilityStatus.Available, ToolCapabilityStatus.Available),
            WithCoverage(total: 12, stacked: 5),
            failed: false);
        Assert.Equal(ToolCapabilityStatus.Partial, globalPartialScopedFull.TraceStatus);
        Assert.Equal(ToolCapabilityStatus.Available, globalPartialScopedFull.ScopedStatus);

        var globalFullScopedNone = catalog.EvaluatorRegistry.EvaluateTool(
            tool,
            capability,
            domain: null,
            Outcome(ToolCapabilityStatus.Unavailable, ToolCapabilityStatus.Unavailable),
            WithCoverage(total: 12, stacked: 12),
            failed: false);
        Assert.Equal(ToolCapabilityStatus.Available, globalFullScopedNone.TraceStatus);
        Assert.Equal(ToolCapabilityStatus.Unavailable, globalFullScopedNone.ScopedStatus);
    }

    [Fact]
    public async Task InspectTrace_ProductionEnvelopeKeepsEvidenceMapCountsConsistentAfterFitting()
    {
        const int frameBudget = ToolResponseBudgetOptions.HardMaxResponseFrameBytes;
        var catalog = ActiveToolCatalog.LoadAndValidate();
        using var traceRuntime = TraceLifecycleProductionTests.TestRuntime.Create();
        // Secure loading is covered by TraceLifecycleProductionTests. This test
        // registers the checked-in fixture directly so the query still exercises
        // the production id-only boundary without depending on temporary-root ACLs.
        var loaded = traceRuntime.Registry.Load(traceRuntime.Principal, FileIoFixture);
        var collection = new ServiceCollection();
        collection.AddSingleton(traceRuntime.Cache);
        collection.AddSingleton(new TraceToolRuntime(
            traceRuntime.Lifecycle,
            traceRuntime.Registry,
            traceRuntime.SessionPrincipal));
        collection.AddSingleton<SymbolService>();
        collection.AddSingleton(new CapabilityDiscoveryRuntime(
            catalog,
            traceRuntime.SessionPrincipal,
            maxResponseFrameBytes: frameBudget));
        using var services = collection.BuildServiceProvider();
        var tool = catalog.CreateServerTools(
                services,
                responseBudget: new ToolResponseBudgetOptions(frameBudget))
            .Single(candidate => candidate.ProtocolTool.Name == "inspect_trace");
        var server = new Mock<McpServer>();
        server.SetupGet(candidate => candidate.Services).Returns(services);
        var capabilityIds = new List<string>();
        var workflowIds = new List<string>();
        var observedCapabilityMiddle = false;
        var observedCapabilityToWorkflowBoundary = false;
        var observedWorkflowMiddle = false;
        var observedWorkflowTerminal = false;
        string? firstCursor = null;
        string? cursor = null;
        var pageNumber = 0;
        do
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["traceId"] = JsonSerializer.SerializeToElement(loaded.TraceId),
            };
            if (cursor is not null)
                arguments["cursor"] = JsonSerializer.SerializeToElement(cursor);
            var parameters = new CallToolRequestParams
            {
                Name = "inspect_trace",
                Arguments = arguments,
            };
            var request = new JsonRpcRequest
            {
                Id = new RequestId(new string('r', 126)),
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
            };
            using var resolved = new TraceReferenceResolver(traceRuntime.Registry)
                .ResolveQuery(
                    traceRuntime.Principal,
                    loaded.TraceId,
                    TraceAccessMode.IdOnly,
                    CancellationToken.None);
            using var execution = TraceQueryExecutionContext.Begin(
                traceRuntime.Cache,
                loaded.TraceId,
                resolved,
                CancellationToken.None);
            var result = await tool.InvokeAsync(
                new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
                CancellationToken.None);

            Assert.False(result.IsError, result.StructuredContent?.GetRawText());
            Assert.True(ToolResponseFrameFitter.MeasureFrame(request.Id, result) <= frameBudget);
            var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
            var text = JsonNode.Parse(
                Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(JsonNode.DeepEquals(text, structured));
            var envelope = structured.AsObject();
            var data = envelope["data"]!.AsObject();
            var map = data["traceEvidenceMap"]!.AsObject();
            var capabilities = map["capabilities"]!.AsArray();
            var workflows = map["workflows"]!.AsArray();
            var page = data["pageContext"]!.AsObject();
            Assert.Equal(catalog.Capabilities.Count, map["catalogCapabilityCount"]!.GetValue<int>());
            Assert.Equal(catalog.Capabilities.Count, map["totalCapabilities"]!.GetValue<int>());
            Assert.Equal(capabilities.Count, map["returnedCapabilities"]!.GetValue<int>());
            Assert.Equal(catalog.Workflows.Count, map["catalogWorkflowCount"]!.GetValue<int>());
            Assert.Equal(catalog.Workflows.Count, map["totalWorkflows"]!.GetValue<int>());
            Assert.Equal(workflows.Count, map["returnedWorkflows"]!.GetValue<int>());
            Assert.Equal(QueryResultCursorCoordinator.InspectOrdering, page["ordering"]!.GetValue<string>());
            Assert.Equal(pageNumber == 0, page["orientationIncluded"]!.GetValue<bool>());
            Assert.Equal(pageNumber == 0 ? "full_orientation" : "evidence_continuation",
                page["contextState"]!.GetValue<string>());
            if (pageNumber == 0)
            {
                Assert.NotNull(data["metadata"]);
                Assert.NotNull(data["symbolQuality"]);
                Assert.NotNull(data["analysisContract"]);
            }
            else
            {
                Assert.Null(data["metadata"]);
                Assert.Null(data["symbolQuality"]);
                Assert.Null(data["analysisContract"]);
                Assert.Empty(data["warnings"]!.AsArray());
                Assert.Empty(data["orientationTools"]!.AsArray());
                Assert.Empty(data["capabilitySupportedTools"]!.AsArray());
                Assert.Empty(data["enabledCapabilities"]!.AsArray());
                Assert.Empty(data["recommendedDiagnosticFlows"]!.AsArray());
            }

            var sections = envelope["sections"]!.AsArray()
                .Select(node => node!.AsObject())
                .ToDictionary(
                    node => node["section"]!.GetValue<string>(),
                    StringComparer.Ordinal);
            Assert.Equal(capabilities.Count.ToString(),
                sections["traceEvidenceMap.capabilities"]["returned"]!.GetValue<string>());
            Assert.Equal(workflows.Count.ToString(),
                sections["traceEvidenceMap.workflows"]["returned"]!.GetValue<string>());
            capabilityIds.AddRange(capabilities.Select(node =>
                node!["capabilityId"]!.GetValue<string>()));
            workflowIds.AddRange(workflows.Select(node =>
                node!["workflowId"]!.GetValue<string>()));
            cursor = data["nextCursor"]?.GetValue<string>();
            if (pageNumber == 0)
                firstCursor = cursor;
            var responseHasMore = cursor is not null;
            Assert.Equal(responseHasMore, data["hasMore"]!.GetValue<bool>());
            Assert.Equal(responseHasMore, envelope["hasMore"]!.GetValue<bool>());
            Assert.Equal(
                responseHasMore,
                envelope["completeness"]!["hasMore"]!.GetValue<bool>());
            Assert.Equal(
                responseHasMore ? "paged" : "complete",
                envelope["completeness"]!["status"]!.GetValue<string>());
            var phase = page["phase"]!.GetValue<string>();
            var startIndex = page["startIndex"]!.GetValue<int>();
            var capabilitySectionHasMore = phase == "capabilities" &&
                startIndex + capabilities.Count < map["totalCapabilities"]!.GetValue<int>();
            var capabilityToWorkflowTransition = phase == "capabilities" &&
                !capabilitySectionHasMore &&
                responseHasMore;
            var workflowSectionHasMore = phase == "workflows" &&
                startIndex + workflows.Count < map["totalWorkflows"]!.GetValue<int>();
            Assert.Equal(capabilitySectionHasMore,
                sections["traceEvidenceMap.capabilities"]["hasMore"]!.GetValue<bool>());
            Assert.Equal(workflowSectionHasMore,
                sections["traceEvidenceMap.workflows"]["hasMore"]!.GetValue<bool>());
            Assert.Equal(capabilitySectionHasMore ? cursor : null,
                sections["traceEvidenceMap.capabilities"]["nextCursor"]?.GetValue<string>());
            Assert.Equal(workflowSectionHasMore ? cursor : null,
                sections["traceEvidenceMap.workflows"]["nextCursor"]?.GetValue<string>());
            Assert.Equal(
                responseHasMore && !capabilityToWorkflowTransition ? 1 : 0,
                sections.Values.Count(section => section["nextCursor"] is not null));
            if (capabilityToWorkflowTransition)
            {
                Assert.Equal(
                    "absent",
                    sections["traceEvidenceMap.capabilities"]["moreState"]!.GetValue<string>());
                Assert.Equal(
                    "absent",
                    sections["traceEvidenceMap.workflows"]["moreState"]!.GetValue<string>());
            }
            observedCapabilityMiddle |= phase == "capabilities" && capabilitySectionHasMore;
            observedCapabilityToWorkflowBoundary |= capabilityToWorkflowTransition &&
                !workflowSectionHasMore;
            observedWorkflowMiddle |= phase == "workflows" && workflowSectionHasMore;
            observedWorkflowTerminal |= phase == "workflows" &&
                !workflowSectionHasMore && cursor is null;
            if (cursor is not null)
                Assert.True(QueryResultCursorRegistry.HasCanonicalShape(cursor));
            pageNumber++;
            Assert.True(pageNumber < 32, "inspect_trace cursor traversal did not terminate.");
        } while (cursor is not null);

        var expectedCapabilities = catalog.Capabilities
            .OrderBy(capability => capability.Domain, StringComparer.Ordinal)
            .ThenBy(capability => capability.CapabilityId, StringComparer.Ordinal)
            .Select(capability => capability.CapabilityId)
            .ToArray();
        var expectedWorkflows = catalog.Workflows
            .OrderBy(workflow => workflow.WorkflowId, StringComparer.Ordinal)
            .Select(workflow => workflow.WorkflowId)
            .ToArray();
        Assert.Equal(51, expectedCapabilities.Length);
        Assert.Equal(expectedCapabilities, capabilityIds);
        Assert.Equal(expectedWorkflows, workflowIds);
        Assert.Equal(capabilityIds.Count, capabilityIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(workflowIds.Count, workflowIds.Distinct(StringComparer.Ordinal).Count());
        Assert.True(observedCapabilityMiddle);
        Assert.True(observedCapabilityToWorkflowBoundary);
        Assert.True(observedWorkflowMiddle);
        Assert.True(observedWorkflowTerminal);

        firstCursor = Assert.IsType<string>(firstCursor);
        var tamperedCursor = firstCursor[..^1] + (firstCursor[^1] == '0' ? '1' : '0');
        foreach (var invalid in new[]
                 {
                     (Cursor: tamperedCursor, Domain: (string?)null),
                     (Cursor: firstCursor, Domain: (string?)"cpu"),
                 })
        {
            var arguments = new Dictionary<string, JsonElement>
            {
                ["traceId"] = JsonSerializer.SerializeToElement(loaded.TraceId),
                ["cursor"] = JsonSerializer.SerializeToElement(invalid.Cursor),
            };
            if (invalid.Domain is not null)
                arguments["domain"] = JsonSerializer.SerializeToElement(invalid.Domain);
            var parameters = new CallToolRequestParams
            {
                Name = "inspect_trace",
                Arguments = arguments,
            };
            var request = new JsonRpcRequest
            {
                Id = new RequestId(new string('r', 126)),
                Method = RequestMethods.ToolsCall,
                Params = JsonSerializer.SerializeToNode(parameters, McpJsonUtilities.DefaultOptions),
            };

            using var resolved = new TraceReferenceResolver(traceRuntime.Registry)
                .ResolveQuery(
                    traceRuntime.Principal,
                    loaded.TraceId,
                    TraceAccessMode.IdOnly,
                    CancellationToken.None);
            using var execution = TraceQueryExecutionContext.Begin(
                traceRuntime.Cache,
                loaded.TraceId,
                resolved,
                CancellationToken.None);
            var result = await tool.InvokeAsync(
                new RequestContext<CallToolRequestParams>(server.Object, request, parameters),
                CancellationToken.None);

            Assert.True(result.IsError);
            Assert.True(ToolResponseFrameFitter.MeasureFrame(request.Id, result) <= frameBudget);
            var structured = JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
            var text = JsonNode.Parse(
                Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text);
            Assert.True(JsonNode.DeepEquals(text, structured));
            Assert.Equal("invalid_cursor", structured["error"]!["code"]!.GetValue<string>());
        }
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
            NetworkConnectionLifecycleEndpointEventCount = 0,
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
            ClrGcIntervalEndpointEventCount = 0,
            ClrGcHeapStatsEventCount = 0,
            ClrFinalizerSourceEventCount = 0,
            HasClrAlloc = false,
            HasClrException = false,
            HasClrContention = false,
            HasClrJit = true,
            ClrJitIntervalEndpointEventCount = 2,
            ClrJitCompletedIntervalCount = 1,
        };

        var flows = MetaTools.BuildRecommendedDiagnosticFlows(jitOnly);

        var dotnet = Assert.Single(flows, flow => flow.FlowName == "dotnet_runtime");
        Assert.Equal(new[] { "clr_jit_analysis" }, dotnet.ToolSequence);
        Assert.Contains("jit", dotnet.Goals);
        Assert.Contains("clr_jit_interval_source_events", dotnet.EnabledCapabilities);
        Assert.Contains("clr_gc_intervals", dotnet.MissingCapabilities);
        Assert.Contains("clr_gc_heap_stats", dotnet.MissingCapabilities);
        Assert.Contains("clr_finalizer_activity", dotnet.MissingCapabilities);
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
            ModuleCount: 1,
            ResolvedModuleCount: 1,
            ModuleResolutionRate: 1.0,
            TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
            ModulesWithPdbName: 1,
            ModulesWithCompletePdbIdentity: 1,
            CompletePdbIdentityRate: 1);

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
            HasMemorySystemInfo: true,
            HasHandleEvents: true,
            HasPoolEvents: true,
            HasCSwitchStacks: true,
            HasReadyThreadStacks: true,
            HasInterruptStacks: true,
            ObservedProcessStartEventCount: 1,
            ObservedThreadLifecycleEndpointEventCount: 2,
            ThreadRundownEndpointEventCount: 0,
            ThreadLifecycleSourceEventCount: 2,
            ThreadCompletedObservedLifetimeCount: 1,
            ClrGcIntervalEndpointEventCount: 2,
            ClrGcCompletedIntervalCount: 1,
            ClrGcHeapStatsEventCount: 1,
            ClrFinalizerObjectEventCount: 1,
            ClrFinalizerBatchStartEndpointEventCount: 1,
            ClrFinalizerBatchStopEndpointEventCount: 1,
            ClrFinalizerCompletedBatchCount: 1,
            ClrFinalizerSourceEventCount: 3,
            NetworkConnectionLifecycleEndpointEventCount: 2,
            NetworkConnectionCompletedLifecycleCount: 1,
            ClrJitIntervalEndpointEventCount: 2,
            ClrJitCompletedIntervalCount: 1);
}
