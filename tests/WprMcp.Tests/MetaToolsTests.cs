using System.Reflection;
using ModelContextProtocol.Server;
using WprMcp.Core;
using WprMcp.Output;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

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
        Assert.Equal(FixturePath, resp.Trace.Path);
    }

    [Fact]
    public void InspectTrace_ReturnsOrientationShape()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        Assert.Equal(FixturePath, resp.Trace.Path);
        Assert.True(resp.Trace.EventCount > 0);
        Assert.True(resp.Trace.ProcessCount > 0);
        Assert.NotNull(resp.Capabilities);
        Assert.NotNull(resp.Metadata);
        Assert.Equal(resp.Trace.EventCount, resp.Metadata.ProviderEvents.TotalEventCount);
        Assert.True(resp.Metadata.ProviderEvents.TotalProviderCount > 0);
        Assert.True(resp.SymbolQuality.ModuleCount >= resp.SymbolQuality.ResolvedModuleCount);
        var unresolvedModules = resp.SymbolQuality.TopUnresolvedModules.Select(module => module.Module).ToList();
        Assert.Equal(
            unresolvedModules.OrderBy(module => module, StringComparer.OrdinalIgnoreCase),
            unresolvedModules);
        Assert.Contains(resp.OrientationTools, r => r.ToolName == "list_processes");
        Assert.DoesNotContain(resp.CapabilitySupportedTools, r => r.ToolName == "list_processes");
    }

    [Fact]
    public void InspectTrace_RecommendedToolsResolveToRegisteredTools()
    {
        var registeredToolNames = ToolListPayload.MeasureCurrentToolNames()
            .ToHashSet(StringComparer.Ordinal);
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        Assert.NotEmpty(registeredToolNames);
        foreach (var recommendation in resp.OrientationTools.Concat(resp.CapabilitySupportedTools))
            Assert.Contains(recommendation.ToolName, registeredToolNames);
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
                NtSymbolPath: null,
                CacheDir: "",
                ModuleCount: 1,
                ResolvedModuleCount: 0,
                ModuleResolutionRate: 0,
                TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
                Recommendations: Array.Empty<SymbolRecommendation>()));

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

        if (resp.Capabilities.HasCpuSamples)
            Assert.Contains(resp.CapabilitySupportedTools, r => r.ToolName == "cpu_top_functions");
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_cpu_samples");

        if (resp.Capabilities.HasCSwitch)
        {
            Assert.Contains(resp.CapabilitySupportedTools, r => r.ToolName == "cpu_precise_analysis");
            Assert.Contains(resp.CapabilitySupportedTools, r => r.ToolName == "wait_analysis");
        }
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_context_switches");

        if (resp.Capabilities.HasFileIo)
            Assert.Contains(resp.CapabilitySupportedTools, r => r.ToolName == "file_io_top_files");
        else
            Assert.Contains(resp.Warnings, w => w.Code == "missing_file_io");
    }

    [Fact]
    public void InspectTrace_MetadataSummarizesProviderCountsAndStackwalks()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        var topProviderEvents = resp.Metadata.ProviderEvents.TopProviders.Sum(provider => provider.EventCount);
        Assert.Equal(
            resp.Trace.EventCount,
            topProviderEvents + resp.Metadata.ProviderEvents.OtherEventCount);
        Assert.NotEmpty(resp.Metadata.ProviderEvents.TopProviders);
        Assert.All(resp.Metadata.ProviderEvents.TopProviders, provider =>
        {
            Assert.False(string.IsNullOrWhiteSpace(provider.Provider));
            Assert.True(provider.EventCount > 0);
            Assert.True(provider.EventsWithCallStacks >= 0);
            Assert.True(provider.StackCoveragePct is null or (>= 0.0 and <= 1.0));
        });

        Assert.Equal(resp.Capabilities.HasStackWalks, resp.Metadata.Stackwalks.HasStackWalkEvents);
        Assert.True(resp.Metadata.Stackwalks.EventStackCoveragePct is null or (>= 0.0 and <= 1.0));
    }

    [Fact]
    public void InspectTrace_MetadataIncludesTraceSystemConfiguration()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        Assert.Equal("TraceLog/TraceEventSource", resp.Metadata.System.MetadataSource);
        Assert.True(resp.Metadata.System.ProcessorCount is null or > 0);
        Assert.True(resp.Metadata.System.CpuSpeedMhz is null or > 0);
        Assert.Contains(resp.Metadata.Limitations, limitation => limitation.StartsWith(
            "cpu_model_not_available_from_trace_metadata",
            StringComparison.Ordinal));
    }

    [Fact]
    public void InspectTrace_MetadataDriverListIsBounded()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.InspectTrace(FixturePath);

        Assert.True(resp.Metadata.Drivers.TopDrivers.Count <= 50);
        Assert.True(resp.Metadata.Drivers.TotalDriverModuleCount >= resp.Metadata.Drivers.TopDrivers.Count);
        Assert.All(resp.Metadata.Drivers.TopDrivers, driver =>
        {
            Assert.False(string.IsNullOrWhiteSpace(driver.Module));
            Assert.True(
                driver.Module.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) ||
                driver.Path.EndsWith(".sys", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void InspectTrace_EmitsWarningWhenSymbolPathUnset()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var tools = new MetaTools(new TraceCache(capacity: 2));
            var resp = tools.InspectTrace(FixturePath);
            Assert.Contains(resp.Warnings, w => w.Code == "symbol_path_unset");
            Assert.Contains(resp.OrientationTools, r => r.ToolName == "diagnose_symbols");
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
    public void LoadTrace_EmitsWarningWhenSymbolPathUnset()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var tools = new MetaTools(new TraceCache(capacity: 2));
            var resp = tools.LoadTrace(FixturePath);
            Assert.NotNull(resp.SymbolStatus.Warning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }
}
