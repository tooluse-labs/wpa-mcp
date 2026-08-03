using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Core.Catalog;
using WpaMcp.Output;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class MetaToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured by fixtures/capture_all.ps1

    [Fact]
    public void LoadTrace_ReturnsNonZeroEventCount()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.True(resp.Trace.EventCount > 0);
        Assert.True(resp.Trace.ProcessCount > 0);
        Assert.Equal(FixturePath, resp.Trace.TraceId);
    }

    [Fact]
    public void UnloadTrace_RequiresProductionTraceIdLifecycle()
    {
        using var cache = new TraceCache(capacity: 2);
        var tools = new MetaTools(cache);

        var error = Assert.Throws<InvalidOperationException>(() =>
            tools.UnloadTrace("trc_0123456789abcdef0123456789abcdef"));

        Assert.Contains("TraceId lifecycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EventCountsDeclareTraceLogMaterializedRepresentation_NotParserCoverage()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new MetaTools(cache);

        var loaded = tools.LoadTrace(FixturePath);
        var inspected = tools.InspectTrace(FixturePath);
        var metadata = Assert.IsType<TraceMetadata>(inspected.Metadata);
        var probe = StackProbeAnalysis.Analyze(cache.Get(FixturePath), FixturePath);

        Assert.Equal("tracelog_etlx_materialized_logical_events", loaded.Trace.EventCountRepresentation);
        Assert.Null(loaded.Trace.RawEtwRecordCount);
        Assert.Equal("not_measured", loaded.Trace.RawEtwRecordCountState);
        Assert.Null(loaded.Trace.ParserCoverageRate);
        Assert.Equal("not_computed", loaded.Trace.ParserCoverageState);

        Assert.Equal(
            "tracelog_etlx_materialized_logical_events_grouped_by_provider",
            metadata.ProviderEvents.CountRepresentation);
        Assert.Null(metadata.ProviderEvents.RawEtwRecordCount);
        Assert.Equal("not_measured", metadata.ProviderEvents.RawEtwRecordCountState);
        Assert.Equal("not_computed", metadata.ProviderEvents.ParserCoverageState);
        Assert.Contains(metadata.Limitations, limitation =>
            limitation.StartsWith("event_count_representation=tracelog_etlx_materialized_logical_events", StringComparison.Ordinal));

        Assert.Equal(
            metadata.Stackwalks.EventStackCoveragePct!.Value * 100,
            metadata.Stackwalks.EventStackCoveragePercent!.Value,
            precision: 8);
        Assert.All(metadata.ProviderEvents.TopProviders, provider =>
            Assert.Equal(
                provider.StackCoveragePct!.Value * 100,
                provider.StackCoveragePercent!.Value,
                precision: 8));

        Assert.Equal("tracelog_etlx_materialized_logical_events", probe.EventCountRepresentation);
        Assert.Null(probe.RawEtwRecordCount);
        Assert.Equal("not_measured", probe.RawEtwRecordCountState);
        Assert.Equal("not_computed", probe.ParserCoverageState);
        Assert.Equal(probe.EventStackCoveragePct!.Value * 100, probe.EventStackCoveragePercent!.Value, precision: 8);
        Assert.Equal(probe.CSwitchStackCoveragePct!.Value * 100, probe.CSwitchStackCoveragePercent!.Value, precision: 8);
        Assert.Equal(probe.ReadyThreadStackCoveragePct!.Value * 100, probe.ReadyThreadStackCoveragePercent!.Value, precision: 8);
        Assert.Equal("event_call_stack_not_switch_out_blocking_stack", probe.CSwitchStackSemantics);

        var cswitchCoverage = inspected.Capabilities.StackCoverageByDomain!["cswitch"];
        Assert.Equal("switch_out_blocking_stack", cswitchCoverage.StackSemantics);
    }

    [Fact]
    public void InspectTrace_ReturnsOrientationShape()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new MetaTools(cache);
        var resp = tools.InspectTrace(FixturePath);
        var trace = cache.Get(FixturePath);

        Assert.Equal(FixturePath, resp.Trace.TraceId);
        Assert.True(resp.Trace.EventCount > 0);
        Assert.True(resp.Trace.ProcessCount > 0);
        Assert.NotNull(resp.Capabilities);
        var metadata = Assert.IsType<TraceMetadata>(resp.Metadata);
        var symbolQuality = Assert.IsType<InspectSymbolQuality>(resp.SymbolQuality);
        Assert.Equal(resp.Trace.EventCount, metadata.ProviderEvents.TotalEventCount);
        Assert.True(metadata.ProviderEvents.TotalProviderCount > 0);
        Assert.Null(symbolQuality.ResolvedModuleCount);
        Assert.Null(symbolQuality.ModuleResolutionRate);
        Assert.Equal(
            trace.ModuleFiles.Count(module => !string.IsNullOrWhiteSpace(module.PdbName)),
            symbolQuality.ModulesWithPdbName);
        Assert.Equal(
            trace.ModuleFiles.Count(module =>
                !string.IsNullOrWhiteSpace(module.PdbName) &&
                module.PdbSignature != Guid.Empty &&
                module.PdbAge > 0),
            symbolQuality.ModulesWithCompletePdbIdentity);
        Assert.Equal("not_measured", symbolQuality.FrameResolutionMeasurementState);
        Assert.Null(symbolQuality.FrameResolution);
        Assert.Empty(symbolQuality.TopUnresolvedModules);
        var unresolvedModules = symbolQuality.TopModulesMissingPdbName.Select(module => module.Module).ToList();
        var expectedModulesMissingPdbName = trace.ModuleFiles
            .Where(module => string.IsNullOrWhiteSpace(module.PdbName))
            .OrderBy(module => module.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(module => module.Name ?? "<unknown>")
            .Take(20)
            .ToList();
        Assert.Equal(expectedModulesMissingPdbName, unresolvedModules);
        Assert.Equal(
            unresolvedModules.OrderBy(module => module, StringComparer.OrdinalIgnoreCase),
            unresolvedModules);
        Assert.All(symbolQuality.TopModulesMissingPdbName, module =>
            Assert.Contains("Recapture or merge", module.Hint, StringComparison.Ordinal));
        Assert.Contains("list_processes", resp.OrientationTools);
        Assert.DoesNotContain("list_processes", resp.CapabilitySupportedTools);
        Assert.NotEmpty(resp.EnabledCapabilities);
        Assert.DoesNotContain(resp.EnabledCapabilities, capability => capability.StartsWith("missing_", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(resp.RecommendedDiagnosticFlows);
        var contract = Assert.IsType<AnalysisContractGuidance>(resp.AnalysisContract);
        Assert.Contains("ScopeStatus=ok", contract.ScopeRule, StringComparison.Ordinal);
        Assert.Contains("MatchedIntervalCount", contract.CountRule, StringComparison.Ordinal);
        Assert.Equal(
            "source_events_unattributed",
            contract.NoDataReasons.SourceEventsUnattributed);
    }

    [Fact]
    public void InspectTrace_UsesPrepareSymbolsAndDoesNotExposeLegacyServerRecommendations()
    {
        var cache = new TraceCache(capacity: 2);
        var response = new MetaTools(cache).InspectTrace(FixturePath);
        var symbolQuality = Assert.IsType<InspectSymbolQuality>(response.SymbolQuality);

        Assert.Equal("prepare_symbols", symbolQuality.NextStep);
        Assert.Equal("unmeasured", symbolQuality.LocalReadinessMeasurementState);
        Assert.Contains("prepare_symbols", response.OrientationTools);
        var json = JsonSerializer.Serialize(response);
        Assert.DoesNotContain("msdl.microsoft.com", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("add_symbol_server", json, StringComparison.Ordinal);
        Assert.DoesNotContain("set_symbol_path", json, StringComparison.Ordinal);
        Assert.DoesNotContain("diagnose_symbols", json, StringComparison.Ordinal);
    }

    [Fact]
    public void InspectSymbolQuality_LabelsMissingPdbMetadataWithoutClaimingFrameFailure()
    {
        var legacy = typeof(InspectSymbolQuality).GetProperty(nameof(InspectSymbolQuality.TopUnresolvedModules));
        var missingPdb = typeof(InspectSymbolQuality).GetProperty(nameof(InspectSymbolQuality.TopModulesMissingPdbName));

        Assert.NotNull(legacy);
        Assert.NotNull(missingPdb);
        var legacyDescription = Assert.IsType<DescriptionAttribute>(legacy.GetCustomAttribute<DescriptionAttribute>()).Description;
        var missingPdbDescription = Assert.IsType<DescriptionAttribute>(missingPdb.GetCustomAttribute<DescriptionAttribute>()).Description;
        Assert.Contains("Deprecated", legacyDescription);
        Assert.Contains("does not prove stack-frame lookup failure", legacyDescription);
        Assert.Contains("does not indicate whether stack-frame lookup succeeded or failed", missingPdbDescription);
    }

    [Fact]
    public void InspectTrace_RecommendedToolsResolveToRegisteredTools()
    {
        var registeredToolNames = ToolListPayload.MeasureCurrentToolNames()
            .ToHashSet(StringComparer.Ordinal);
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        Assert.NotEmpty(registeredToolNames);
        foreach (var toolName in resp.OrientationTools.Concat(resp.CapabilitySupportedTools))
            Assert.Contains(toolName, registeredToolNames);

        var catalog = ActiveToolCatalog.LoadAndValidate();
        Assert.All(resp.RecommendedDiagnosticFlows, workflowId =>
            Assert.Contains(catalog.Workflows, workflow => workflow.WorkflowId == workflowId));
        var evidenceMap = Assert.IsType<TraceEvidenceMapRecord>(resp.TraceEvidenceMap);
        foreach (var toolName in evidenceMap.Workflows.SelectMany(flow => flow.SuggestedTools).Distinct())
            Assert.Contains(toolName, registeredToolNames);
    }

    [Fact]
    public void InspectTrace_WarningAffectedToolsResolveToRegisteredTools()
    {
        var registeredToolNames = ToolListPayload.MeasureCurrentToolNames()
            .ToHashSet(StringComparer.Ordinal);
        var warnings = MetaTools.BuildTraceQualityWarnings(
            eventsLost: 1,
            new TraceCapabilities(
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
                HasPoolEvents: false),
            new InspectSymbolQuality(
                ModuleCount: 1,
                ResolvedModuleCount: null,
                ModuleResolutionRate: null,
                TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
                ModulesWithPdbName: 0,
                ModulesWithPdbNameRate: 0,
                ModulesWithCompletePdbIdentity: 0,
                CompletePdbIdentityRate: 0));

        Assert.Contains(warnings, warning => warning.Code == "low_module_pdb_identity_coverage");
        var identityWarning = Assert.Single(
            warnings,
            warning => warning.Code == "low_module_pdb_identity_coverage");
        Assert.Contains("Recapture or merge", identityWarning.NextStep, StringComparison.Ordinal);
        Assert.DoesNotContain(warnings, warning =>
            warning.Message.Contains("resolved PDB", StringComparison.OrdinalIgnoreCase));

        foreach (var toolName in warnings
                     .SelectMany(warning => warning.AffectedTools.Concat(warning.DegradedTools))
                     .Distinct())
        {
            Assert.Contains(toolName, registeredToolNames);
        }
    }

    [Fact]
    public void InspectTrace_RecommendationsFollowCapabilities()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        var cpuStacks = resp.Capabilities.StackCoverageByDomain?["cpu"];
        if (resp.Capabilities.HasCpuSamples && cpuStacks?.StackedEventCount > 0)
            Assert.Contains("cpu_top_functions", resp.CapabilitySupportedTools);
        else if (resp.Capabilities.HasCpuSamples)
            Assert.DoesNotContain("cpu_top_functions", resp.CapabilitySupportedTools);
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_cpu_samples");

        if (resp.Capabilities.HasCSwitch)
        {
            Assert.Contains("cpu_precise_analysis", resp.CapabilitySupportedTools);
            Assert.Contains("wait_analysis", resp.CapabilitySupportedTools);
        }
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_context_switches");

        if (resp.Capabilities.HasFileIo)
            Assert.Contains("file_io_top_files", resp.CapabilitySupportedTools);
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_file_io");
    }

    [Fact]
    public void InspectTrace_MetadataSummarizesProviderCountsAndStackwalks()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);
        var metadata = Assert.IsType<TraceMetadata>(resp.Metadata);

        var topProviderEvents = metadata.ProviderEvents.TopProviders.Sum(provider => provider.EventCount);
        Assert.Equal(
            resp.Trace.EventCount,
            topProviderEvents + metadata.ProviderEvents.OtherEventCount);
        Assert.NotEmpty(metadata.ProviderEvents.TopProviders);
        Assert.All(metadata.ProviderEvents.TopProviders, provider =>
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Provider));
            Assert.True(provider.EventCount > 0);
            Assert.True(provider.EventsWithCallStacks >= 0);
            Assert.True(provider.StackCoveragePct is null or (>= 0.0 and <= 1.0));
        });

        Assert.Equal(
            metadata.Stackwalks.StackWalkEventCount > 0,
            metadata.Stackwalks.HasStackWalkEvents);
        Assert.Equal(
            metadata.Stackwalks.StackWalkEventCount > 0,
            metadata.Stackwalks.HasExplicitStackWalkEvents);
        Assert.Equal(
            resp.Capabilities.HasExplicitStackWalkEvents,
            metadata.Stackwalks.HasExplicitStackWalkEvents);
        Assert.Equal(
            resp.Capabilities.ExplicitStackWalkEventCount,
            metadata.Stackwalks.StackWalkEventCount);
        Assert.Equal(
            resp.Capabilities.HasAttachedEventStacks,
            metadata.Stackwalks.HasUsableEventStacks);
        Assert.True(metadata.Stackwalks.EventStackCoveragePct is null or (>= 0.0 and <= 1.0));
    }

    [Fact]
    public void InspectTrace_MetadataIncludesTraceSystemConfiguration()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);
        var metadata = Assert.IsType<TraceMetadata>(resp.Metadata);

        Assert.Equal("TraceLog/TraceEventSource", metadata.System.MetadataSource);
        Assert.True(metadata.System.ProcessorCount is null or > 0);
        Assert.True(metadata.System.CpuSpeedMhz is null or > 0);
        Assert.Contains(metadata.Limitations, limitation => limitation.StartsWith(
            "cpu_model_not_available_from_trace_metadata",
            StringComparison.Ordinal));
    }

    [Fact]
    public void InspectTrace_MetadataDriverListIsBounded()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);
        var metadata = Assert.IsType<TraceMetadata>(resp.Metadata);

        Assert.True(metadata.Drivers.TopDrivers.Count <= 50);
        Assert.True(metadata.Drivers.TotalDriverModuleCount >= metadata.Drivers.TopDrivers.Count);
        Assert.All(metadata.Drivers.TopDrivers, driver =>
        {
            Assert.False(string.IsNullOrWhiteSpace(driver.Module));
            Assert.True(
                driver.Module.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) ||
                driver.Path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void InspectTrace_DoesNotMutateUnsetConfiguredSymbolPath()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var tools = new MetaTools(new TraceCache(capacity: 2));
            var resp = tools.InspectTrace(FixturePath);
            Assert.DoesNotContain(resp.Warnings, w => w.Code == "symbol_path_unset");
            Assert.Null(Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
            Assert.Contains("prepare_symbols", resp.OrientationTools);
            var json = JsonSerializer.Serialize(resp);
            Assert.DoesNotContain("ntSymbolPath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cacheDir", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }

    [Fact]
    public void InspectTrace_ToolAttributeRequestsStructuredContent()
    {
        var method = typeof(MetaTools).GetMethod(nameof(MetaTools.InspectTrace));
        var attribute = method?.GetCustomAttribute<McpServerToolAttribute>();

        Assert.NotNull(attribute);
        Assert.True(attribute.ReadOnly);
        Assert.True(attribute.Idempotent);
        Assert.False(attribute.OpenWorld);
        Assert.False(attribute.Destructive);
        Assert.True(attribute.UseStructuredContent);
    }

    [Fact]
    public void ListProcesses_OrdersByCpuDescending()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        Assert.NotEmpty(resp.Rows);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].CpuUs >= resp.Rows[i].CpuUs);
    }

    [Fact]
    public void ListProcesses_HidesIdleAndSystemByDefault()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        Assert.DoesNotContain(resp.Rows, r => r.Pid == 0 || r.Pid == 4);
        // Either PID 0 (Idle) or PID 4 (System) is present in any non-trivial Windows trace.
        Assert.True(resp.IdleProcessesHidden >= 1);
    }

    [Fact]
    public void ListProcesses_IncludeSystemTrueSurfacesIdle()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, includeSystem: true);
        Assert.Contains(resp.Rows, r => r.Pid == 0 || r.Pid == 4);
        Assert.Equal(0, resp.IdleProcessesHidden);
    }

    [Fact]
    public void ListProcesses_OrderByWallSortsByWallDesc()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, orderBy: "wall");
        Assert.NotEmpty(resp.Rows);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].WallUs >= resp.Rows[i].WallUs);
    }

    [Fact]
    public void ListProcesses_PopulatesParentPidAndImageLoadCount()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        // At least one process should have ImageLoad events (any non-empty trace).
        Assert.Contains(resp.Rows, r => r.ImageLoadCount > 0);
        // ParentPid is 0 for parent-less processes, but at least some children must have a real parent.
        Assert.Contains(resp.Rows, r => r.ParentPid > 0);
    }

    [Fact]
    public void ListProcesses_RespectsTopParameter()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        // Note: trace.Processes can exceed Validation.MaxTop (1000) on busy hosts due to
        // rundown events; TotalCount must reflect the unfiltered count, not be capped.
        var full = tools.ListProcesses(FixturePath, top: 1000);
        Assert.True(full.Rows.Count >= 1);
        Assert.True(full.TotalCount >= full.Rows.Count);

        var capped = tools.ListProcesses(FixturePath, top: 1);
        Assert.Single(capped.Rows);
        // TotalCount survives truncation so callers can detect "this was capped".
        Assert.Equal(full.TotalCount, capped.TotalCount);
        Assert.Equal(0, capped.PageContext?.StartIndex);
        Assert.Equal(1, capped.ReturnedCount);
        Assert.Equal(capped.TotalCount > 1, capped.HasMore);
        Assert.Equal(
            capped.HasMore ? QueryResultCursorRegistry.PendingDeliveryToken : null,
            capped.NextCursor);
    }

    [Fact]
    public void ListProcesses_RejectsBadTop()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ListProcesses(FixturePath, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ListProcesses(FixturePath, top: 1001));
    }

    [Fact]
    public void ListProcesses_WaitRatioOrderDoesNotPutTraceResidentFirst()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, orderBy: "wait_ratio", top: 50);
        Assert.NotEmpty(resp.Rows);

        // If there is at least one non-trace-resident process with a CPU sample, it must
        // come before any trace-resident peer in wait_ratio order. (If the small_cpu fixture
        // has zero qualifying non-resident processes, the assertion is vacuously satisfied.)
        var firstNonResidentIdx = -1;
        var firstResidentIdx = -1;
        for (var i = 0; i < resp.Rows.Count; i++)
        {
            if (resp.Rows[i].TraceResident && firstResidentIdx < 0) firstResidentIdx = i;
            if (!resp.Rows[i].TraceResident && resp.Rows[i].WaitRatio is not null && firstNonResidentIdx < 0)
                firstNonResidentIdx = i;
        }
        if (firstNonResidentIdx >= 0 && firstResidentIdx >= 0)
            Assert.True(firstNonResidentIdx < firstResidentIdx,
                $"non-resident @ {firstNonResidentIdx} should sort before trace-resident @ {firstResidentIdx}");
    }

    [Fact]
    public void ListProcesses_WaitRatioSortDemotesTinyCpuDenominators()
    {
        var sortKey = typeof(MetaTools).GetMethod(
            "WaitRatioSortKey",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(sortKey);

        var tinyCpu = new ProcessRow(
            Pid: 1,
            ParentPid: 0,
            Name: "tiny-cpu",
            StartUs: 1_000_000,
            EndUs: 201_000_000,
            WallUs: 200_000_000,
            CpuUs: 4_000,
            WaitRatio: 50_000,
            ImageLoadCount: 0,
            TraceResident: false);
        var legitimateLowCpuWait = tinyCpu with { Pid = 2, CpuUs = 30_000, WaitRatio = 6_667 };
        var meaningfulCpu = tinyCpu with { Pid = 3, CpuUs = 100_000, WaitRatio = 2_000 };

        Assert.Equal(double.NegativeInfinity, Assert.IsType<double>(sortKey.Invoke(null, new object[] { tinyCpu })));
        Assert.True(Assert.IsType<double>(sortKey.Invoke(null, new object[] { legitimateLowCpuWait })) > 0);
        Assert.True(Assert.IsType<double>(sortKey.Invoke(null, new object[] { meaningfulCpu })) > 0);
    }

    [Fact]
    public void LoadTrace_DetectsCpuSamplesOnCpuFixture()
    {
        // small_cpu was captured with CPU.light. Positive assertion only — content of
        // negative flags varies between OS builds and capture conditions (e.g., FileIO.light
        // can incidentally generate HardFault events when files page in), so we only check
        // the keyword that the profile is GUARANTEED to enable.
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.True(resp.Capabilities.HasCpuSamples);
    }

    [Fact]
    public void LoadTrace_DetectsFileIoOnFileIoFixture()
    {
        const string FileIoFixture = "fixtures/small_fileio.etl";
        if (!File.Exists(FileIoFixture)) return;
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FileIoFixture);
        Assert.True(resp.Capabilities.HasFileIo);
    }

    [Fact]
    public void LoadTrace_DetectsHardFaultsOnMmapFixture()
    {
        const string MmapFixture = "fixtures/small_mmap.etl";
        if (!File.Exists(MmapFixture)) return;
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(MmapFixture);
        // small_mmap uses MmapCapture.wprp which explicitly enables HardFaults.
        Assert.True(resp.Capabilities.HasHardFaults);
    }

    [Fact]
    public void LoadTrace_CapabilitiesNeverNull()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.NotNull(resp.Capabilities);
    }

    [Fact]
    public void LoadTrace_DoesNotMutateUnsetConfiguredSymbolPath()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var tools = new MetaTools(new TraceCache(capacity: 2));
            var resp = tools.LoadTrace(FixturePath);
            Assert.Equal("prepare_symbols", resp.SymbolStatus.NextStep);
            Assert.Equal("unmeasured", resp.SymbolStatus.LocalReadinessMeasurementState);
            Assert.Equal("unmeasured", resp.SymbolStatus.FrameResolutionMeasurementState);
            Assert.Null(Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"));
            var json = JsonSerializer.Serialize(resp);
            Assert.DoesNotContain("ntSymbolPath", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cacheDir", json, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }
}
