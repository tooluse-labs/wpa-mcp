using System.ComponentModel;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MetaTools
{
    private const long MinCpuUsForWaitRatioSort = 5_000;
    private const double MinCpuShareForWaitRatioSort = 0.00001;

    private readonly TraceCache _cache;
    public MetaTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Loads (or returns cached) a Windows ETW .etl trace. First load can take 30s-3min; subsequent calls are instant. " +
        "Response includes symbol-server recommendations based on the modules referenced by the trace. " +
        "No startUs/endUs: this is whole-trace cache/orientation, not event-window analysis.")]
    public LoadTraceResponse LoadTrace(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var capabilities = _cache.GetCapabilities(path);
        return new LoadTraceResponse(BuildTraceMeta(path, trace), BuildSymbolStatus(trace), capabilities);
    }

    [McpServerTool(
        ReadOnly = true,
        Idempotent = true,
        OpenWorld = false,
        Destructive = false,
        UseStructuredContent = true), Description(
        "Inspects a trace once and returns machine-readable orientation: capture capabilities, " +
        "system metadata, provider counts, stackwalk completeness, symbol quality, quality " +
        "warnings, and capability-driven next-tool hints. Use when the capture profile is unknown, " +
        "the analysis goal is unclear, or prior domain tools returned empty/low-confidence results. " +
        "Recommendations are capability-driven hints, not goal-specific rankings. " +
        "No startUs/endUs: capabilities, metadata, provider counts, and symbol quality describe the whole trace.")]
    public InspectTraceResponse InspectTrace(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var capabilities = _cache.GetCapabilities(path);
        var metadata = _cache.GetMetadata(path);
        var symbolQuality = BuildInspectSymbolQuality(trace);
        var warnings = BuildTraceQualityWarnings(trace.EventsLost, capabilities, symbolQuality);
        var orientationTools = BuildOrientationTools(symbolQuality);
        var capabilitySupportedTools = BuildCapabilitySupportedTools(capabilities);
        var enabledCapabilities = BuildEnabledCapabilities(capabilities);
        var recommendedFlows = BuildRecommendedDiagnosticFlows(capabilities);

        return new InspectTraceResponse(
            BuildTraceMeta(path, trace),
            capabilities,
            metadata,
            symbolQuality,
            warnings,
            orientationTools,
            capabilitySupportedTools,
            enabledCapabilities,
            recommendedFlows);
    }

    private static TraceMeta BuildTraceMeta(string path, TraceLog trace)
    {
        var processes = trace.Processes;
        return new TraceMeta(
            Path: path,
            DurationUs: (long)trace.SessionDuration.TotalMicroseconds,
            EventCount: trace.EventCount,
            EventsLost: trace.EventsLost,
            ProcessCount: processes.Count);
    }

    private static SymbolStatus BuildSymbolStatus(TraceLog trace)
    {
        var ntPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        var cacheDir = DefaultSymbolCacheDir();
        var warning = string.IsNullOrEmpty(ntPath)
            ? "_NT_SYMBOL_PATH is not set. OS module frames will not resolve. " +
              "Call set_symbol_path or add_symbol_server, or configure env in MCP config."
            : null;

        return new SymbolStatus(ntPath, cacheDir, warning, BuildSymbolRecommendations(trace));
    }

    private static InspectSymbolQuality BuildInspectSymbolQuality(TraceLog trace)
    {
        var modules = trace.ModuleFiles.ToList();
        var unresolved = modules
            .Where(module => string.IsNullOrEmpty(module.PdbName))
            .OrderBy(module => module.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var name = module.Name ?? "<unknown>";
                return new InspectUnresolvedModule(name, SymbolTools.SuggestServerForModule(name));
            })
            .Take(20)
            .ToList();

        var resolvedCount = modules.Count(module => !string.IsNullOrEmpty(module.PdbName));
        double? rate = modules.Count == 0
            ? null
            : resolvedCount / (double)modules.Count;

        return new InspectSymbolQuality(
            NtSymbolPath: Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH"),
            CacheDir: DefaultSymbolCacheDir(),
            ModuleCount: modules.Count,
            ResolvedModuleCount: resolvedCount,
            ModuleResolutionRate: rate,
            TopUnresolvedModules: unresolved,
            Recommendations: BuildSymbolRecommendations(trace));
    }

    internal static IReadOnlyList<TraceQualityWarning> BuildTraceQualityWarnings(
        long eventsLost,
        TraceCapabilities capabilities,
        InspectSymbolQuality symbolQuality)
    {
        var warnings = new List<TraceQualityWarning>();

        if (eventsLost > 0)
        {
            warnings.Add(new TraceQualityWarning(
                Code: "events_lost",
                Severity: "warn",
                Message: $"{eventsLost} events were lost during capture; analysis may be incomplete.",
                NextStep: "Recapture with larger buffers or a narrower profile/window before drawing final conclusions.",
                AffectedTools: Array.Empty<string>(),
                DegradedTools: Array.Empty<string>()));
        }

        if (string.IsNullOrEmpty(symbolQuality.NtSymbolPath))
        {
            warnings.Add(new TraceQualityWarning(
                Code: "symbol_path_unset",
                Severity: "warn",
                Message: "_NT_SYMBOL_PATH is not set, so OS and product module frames may remain unresolved.",
                NextStep: "Run diagnose_symbols, then add_symbol_server or set_symbol_path with the recommended symbol servers.",
                AffectedTools: StackDependentToolNames,
                DegradedTools: Array.Empty<string>()));
        }

        if (symbolQuality.ModuleResolutionRate is < 0.8)
        {
            warnings.Add(new TraceQualityWarning(
                Code: "low_module_symbol_resolution",
                Severity: "warn",
                Message: $"{symbolQuality.ModuleResolutionRate.Value * 100:F1}% of loaded modules have resolved PDBs.",
                NextStep: "Run diagnose_symbols and add the recommended symbol servers or local PDB paths.",
                AffectedTools: StackDependentToolNames,
                DegradedTools: Array.Empty<string>()));
        }

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasCpuSamples,
            "missing_cpu_samples",
            "warn",
            "CPU sample events were not observed.",
            "Recapture with a CPU or CPU.light WPR profile when CPU attribution matters.",
            CpuSampleToolNames,
            CompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasCSwitch,
            "missing_context_switches",
            "warn",
            "Context switch events were not observed.",
            "Recapture with CSwitch enabled before using wait, blocked-time, or ready-thread analysis.",
            ContextSwitchToolNames.Concat(HighWaitCompositeToolNames).ToArray(),
            CompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasStackWalks,
            "missing_stackwalks",
            "warn",
            "StackWalk events were not observed; stack-based tools may return empty or low-value call chains.",
            "Recapture with stack walking enabled for the relevant events.",
            StackDependentToolNames,
            StackWalkDegradedCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasCSwitch || capabilities.HasCSwitchStacks,
            "missing_cswitch_stacks",
            "warn",
            "CSwitch events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for CSwitch before using wait stack analysis.",
            WaitStackToolNames,
            HighWaitCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasReadyThread || capabilities.HasReadyThreadStacks,
            "missing_ready_thread_stacks",
            "warn",
            "ReadyThread events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for ReadyThread before using ready-thread stack analysis.",
            ReadyThreadStackToolNames,
            HighWaitCompositeToolNames);

        AddMissingCapabilityWarning(
            warnings,
            !capabilities.HasInterrupt || capabilities.HasInterruptStacks,
            "missing_interrupt_stacks",
            "warn",
            "DPC/ISR interrupt events were observed, but they did not carry call stacks.",
            "Recapture with stack walking enabled for PerfInfoDPC and PerfInfoISR before using interrupt stack analysis.",
            InterruptStackToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasFileIo,
            "missing_file_io",
            "info",
            "File IO events were not observed.",
            "Recapture with the FileIO keyword if file activity is part of the investigation.",
            FileIoToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasDiskIo,
            "missing_disk_io",
            "info",
            "Physical disk IO events were not observed.",
            "Recapture with the DiskIO keyword if physical media activity is part of the investigation.",
            DiskIoToolNames);

        AddMissingCapabilityWarning(
            warnings,
            HasAnyClrCapability(capabilities),
            "missing_clr_runtime",
            "info",
            "CLR runtime events were not observed.",
            "Recapture with Microsoft-Windows-DotNETRuntime enabled if .NET JIT, GC, allocation, exception, or contention analysis matters. Use tests/WprMcp.Tests/fixtures/JitOnlyCapture.wprp for minimal JIT-only traces.",
            ClrToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasNetIo,
            "missing_network_io",
            "info",
            "Network send/receive byte events were not observed.",
            "Recapture with NetworkTrace enabled if network byte attribution is part of the investigation.",
            NetworkByteToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasNetConnections,
            "missing_network_connections",
            "info",
            "Network connection lifecycle events were not observed.",
            "Recapture with NetworkTrace enabled if TCP connect/accept/disconnect timing is part of the investigation.",
            NetworkConnectionToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasMemoryProcessInfo,
            "missing_memory_process_info",
            "info",
            "Process memory resource snapshots were not observed.",
            "Recapture with MemoryInfoWS enabled, for example tests/WprMcp.Tests/fixtures/MemoryCapture.wprp.",
            MemoryResourceToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasHandleEvents,
            "missing_handle_events",
            "info",
            "Handle create/close events were not observed.",
            "Recapture with the Handle keyword enabled when handle-leak analysis matters.",
            MemoryResourceToolNames);

        AddMissingCapabilityWarning(
            warnings,
            capabilities.HasPoolEvents,
            "missing_pool_events",
            "info",
            "Pool allocation/free events were not observed.",
            "Recapture with the Pool keyword enabled when observed paged/nonpaged pool deltas matter.",
            MemoryResourceToolNames);

        return warnings;
    }

    private static void AddMissingCapabilityWarning(
        List<TraceQualityWarning> warnings,
        bool hasCapability,
        string code,
        string severity,
        string message,
        string nextStep,
        IReadOnlyList<string> affectedTools,
        IReadOnlyList<string>? degradedTools = null)
    {
        if (hasCapability) return;
        warnings.Add(new TraceQualityWarning(
            code,
            severity,
            message,
            nextStep,
            affectedTools,
            degradedTools ?? Array.Empty<string>()));
    }

    internal static IReadOnlyList<ToolRecommendation> BuildOrientationTools(InspectSymbolQuality symbolQuality)
    {
        var recommendations = new List<(string ToolName, string Reason, string[] Goals)>
        {
            (
                "list_processes",
                "Orient on process CPU, wall time, wait ratio, and trace residency when the target process or domain is not already known.",
                ["orientation"]),
        };

        if (string.IsNullOrEmpty(symbolQuality.NtSymbolPath) ||
            symbolQuality.ModuleResolutionRate is < 0.8 ||
            symbolQuality.Recommendations.Count > 0)
        {
            recommendations.Add((
                "diagnose_symbols",
                "Symbol path or module resolution needs validation before trusting stack frame names.",
                ["symbols", "quality"]));
        }

        return BuildToolRecommendationRecords(recommendations);
    }

    internal static IReadOnlyList<ToolRecommendation> BuildCapabilitySupportedTools(TraceCapabilities capabilities)
    {
        var recommendations = new List<(string ToolName, string Reason, string[] Goals)>();

        if (capabilities.HasCpuSamples)
            recommendations.Add(("cpu_top_functions", "CPU samples are present; rank hot functions first for CPU-bound investigations.", ["cpu"]));

        if (capabilities.HasCSwitch)
        {
            recommendations.Add(("cpu_precise_analysis", "Context switch events are present; compute exact on-CPU time, ready latency, and core attribution.", ["cpu", "scheduler"]));
            recommendations.Add(("wait_analysis", "Context switch events are present; identify blocked threads and dominant wait reasons.", ["wait"]));
        }

        if (capabilities.HasCSwitch && capabilities.HasCSwitchStacks)
            recommendations.Add(("wait_top_stacks", "Context switches and stack walks are present; drill into where blocked time resumes.", ["wait", "stacks"]));

        if (capabilities.HasReadyThread && capabilities.HasReadyThreadStacks)
            recommendations.Add(("ready_thread_top_stacks", "ReadyThread events and stack walks are present; inspect who woke target threads.", ["wait", "scheduler", "stacks"]));

        if (capabilities.HasImageLoad)
            recommendations.Add(("image_load_top_gaps", "Image load events are present; rank loader gaps for startup and DLL-load investigations.", ["startup", "image_load"]));

        if (capabilities.HasFileIo)
            recommendations.Add(("file_io_top_files", "File IO events are present; identify files with the most read/write bytes, optionally narrowed by pid/startUs/endUs.", ["io"]));

        if (capabilities.HasFileIo && capabilities.HasStackWalks)
            recommendations.Add(("file_io_top_stacks", "File IO and stack walks are present; attribute file IO bytes to call stacks.", ["io", "stacks"]));

        if (capabilities.HasDiskIo && capabilities.HasStackWalks)
            recommendations.Add(("disk_io_top_stacks", "Disk IO and stack walks are present; attribute physical media bytes to call stacks.", ["io", "disk", "stacks"]));

        if (capabilities.HasHardFaults)
            recommendations.Add(("hard_fault_by_file", "Hard-fault events are present; identify files that caused page-ins, optionally narrowed by pid/startUs/endUs.", ["memory", "hard_faults"]));

        if (capabilities.HasClrGc)
            recommendations.Add(("clr_gc_analysis", "CLR GC events are present; inspect GC duration and stop-the-world pause time.", ["gc", "dotnet"]));

        if (capabilities.HasClrJit)
            recommendations.Add(("clr_jit_analysis", "CLR JIT events are present; rank methods by JIT compilation duration.", ["jit", "dotnet"]));

        if (capabilities.HasClrAlloc && capabilities.HasStackWalks)
            recommendations.Add(("clr_alloc_top_stacks", "CLR allocation ticks and stack walks are present; rank managed allocation sources.", ["memory", "dotnet"]));

        if (capabilities.HasClrException)
            recommendations.Add(("clr_exception_top_stacks", "CLR exception events are present; rank thrown exception sources and top exception types.", ["exceptions", "dotnet"]));

        if (capabilities.HasClrContention)
            recommendations.Add(("clr_contention_top_stacks", "CLR contention events are present; rank managed Monitor contention call stacks.", ["locks", "dotnet"]));

        if (capabilities.HasNetIo)
            recommendations.Add(("net_top_stacks", "Network IO events are present; attribute TCP/UDP bytes to call stacks.", ["network"]));

        if (capabilities.HasNetConnections)
            recommendations.Add(("net_connections", "Network connection lifecycle events are present; inspect TCP connect/accept/disconnect timing.", ["network", "connections"]));

        if (capabilities.HasMemoryProcessInfo || capabilities.HasHandleEvents || capabilities.HasPoolEvents)
            recommendations.Add(("memory_resource_analysis", BuildMemoryResourceRecommendationReason(capabilities), ["memory"]));

        if (capabilities.HasAlpc)
            recommendations.Add(("alpc_top_stacks", "ALPC events are present; rank cross-process IPC message stacks.", ["ipc"]));

        if (capabilities.HasInterrupt)
            recommendations.Add(("interrupt_top_stacks", "DPC/ISR events are present; rank interrupt time, with call stacks when the capture includes them.", ["interrupts", "drivers"]));

        return recommendations
            .Select(recommendation => new ToolRecommendation(
                recommendation.ToolName,
                recommendation.Reason,
                recommendation.Goals))
            .ToList();
    }

    internal static IReadOnlyList<string> BuildEnabledCapabilities(TraceCapabilities capabilities)
    {
        var enabled = new List<string>();
        AddCapability(enabled, capabilities.HasCpuSamples, "cpu_samples");
        AddCapability(enabled, capabilities.HasCSwitch, "context_switches");
        AddCapability(enabled, capabilities.HasFileIo, "file_io");
        AddCapability(enabled, capabilities.HasDiskIo, "disk_io");
        AddCapability(enabled, capabilities.HasImageLoad, "image_load");
        AddCapability(enabled, capabilities.HasHardFaults, "hard_faults");
        AddCapability(enabled, capabilities.HasStackWalks, "stack_walks");
        AddCapability(enabled, capabilities.HasVirtualAlloc, "virtual_alloc");
        AddCapability(enabled, capabilities.HasNetIo, "network_io");
        AddCapability(enabled, capabilities.HasNetConnections, "network_connections");
        AddCapability(enabled, capabilities.HasRegistry, "registry");
        AddCapability(enabled, capabilities.HasReadyThread, "ready_thread");
        AddCapability(enabled, capabilities.HasInterrupt, "interrupts");
        AddCapability(enabled, capabilities.HasAlpc, "alpc");
        AddCapability(enabled, capabilities.HasThreadEvents, "thread_events");
        AddCapability(enabled, capabilities.HasClrGc, "clr_gc");
        AddCapability(enabled, capabilities.HasClrJit, "clr_jit");
        AddCapability(enabled, capabilities.HasClrAlloc, "clr_alloc");
        AddCapability(enabled, capabilities.HasClrException, "clr_exception");
        AddCapability(enabled, capabilities.HasClrContention, "clr_contention");
        AddCapability(enabled, capabilities.HasNtHeap, "nt_heap");
        AddCapability(enabled, capabilities.HasMemoryProcessInfo, "memory_process_info");
        AddCapability(enabled, capabilities.HasHandleEvents, "handle_events");
        AddCapability(enabled, capabilities.HasPoolEvents, "pool_events");
        AddCapability(enabled, capabilities.HasCSwitchStacks, "cswitch_stacks");
        AddCapability(enabled, capabilities.HasReadyThreadStacks, "ready_thread_stacks");
        AddCapability(enabled, capabilities.HasInterruptStacks, "interrupt_stacks");
        return enabled;
    }

    internal static IReadOnlyList<DiagnosticFlowRecommendation> BuildRecommendedDiagnosticFlows(TraceCapabilities capabilities)
    {
        var flows = new List<DiagnosticFlowRecommendation>();

        if (capabilities.HasImageLoad || capabilities.HasCSwitch || capabilities.HasCpuSamples)
        {
            flows.Add(Flow(
                "slow_startup",
                "Use the startup composite when investigating process launch, child-process gaps, or first-DLL delays; it preserves evidence instead of forcing a manual wait/load/CPU reconstruction.",
                ["list_processes", "diagnose_slow_startup"],
                ["startup", "process_creation"],
                [
                    (capabilities.HasImageLoad, "image_load"),
                    (capabilities.HasCSwitch, "context_switches"),
                    (capabilities.HasCpuSamples, "cpu_samples"),
                    (capabilities.HasHardFaults, "hard_faults"),
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasImageLoad, "No ImageLoad events were observed; first-image-load gap evidence will be absent."),
                    (!capabilities.HasCSwitch, "No context switches were observed; wait evidence will be absent."),
                    (!capabilities.HasCpuSamples, "No CPU samples were observed; startup CPU attribution will be absent."))));
        }

        if (capabilities.HasHardFaults || capabilities.HasFileIo || capabilities.HasCSwitch || capabilities.HasMemoryProcessInfo || capabilities.HasHandleEvents || capabilities.HasPoolEvents)
        {
            flows.Add(Flow(
                "window_triage",
                "Use diagnose_window after a timestamp, interaction, or page-in stall is known; it aggregates hard faults, file IO, memory, security scan events, and waits for the same interval.",
                ["diagnose_window"],
                ["window", "triage"],
                [
                    (capabilities.HasHardFaults, "hard_faults"),
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasCSwitch, "context_switches"),
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                    (capabilities.HasHandleEvents, "handle_events"),
                    (capabilities.HasPoolEvents, "pool_events"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasHardFaults, "Hard-fault by-file evidence will be empty without HardFaults events."),
                    (!capabilities.HasCSwitch, "Wait evidence will be empty without context switches."),
                    (!capabilities.HasFileIo, "File IO evidence will be empty without FileIO events."))));
        }

        if (capabilities.HasHardFaults)
        {
            flows.Add(Flow(
                "page_in_stall",
                "Use hard_fault_by_file ordered by max_latency to find cold-page stalls, then zoom around MaxLatencyTimeUs with diagnose_window.",
                ["hard_fault_by_file", "diagnose_window"],
                ["memory", "hard_faults", "window"],
                [(true, "hard_faults")],
                []));
        }

        if (capabilities.HasCSwitch)
        {
            flows.Add(Flow(
                "high_wait",
                "Use diagnose_high_wait for high-wall/low-CPU traces before expanding into detailed wait or ready-thread stacks.",
                ["diagnose_high_wait", "wait_analysis", "wait_top_stacks"],
                ["wait", "scheduler"],
                [
                    (capabilities.HasCSwitch, "context_switches"),
                    (capabilities.HasCSwitchStacks, "cswitch_stacks"),
                    (capabilities.HasReadyThread, "ready_thread"),
                    (capabilities.HasReadyThreadStacks, "ready_thread_stacks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasCSwitchStacks, "wait_top_stacks will be unavailable or degraded without CSwitch stackwalks."),
                    (capabilities.HasReadyThread && !capabilities.HasReadyThreadStacks, "ready_thread_top_stacks will be unavailable or degraded without ReadyThread stackwalks."))));
        }

        if (capabilities.HasCpuSamples || capabilities.HasCSwitch)
        {
            flows.Add(Flow(
                "cpu_hotspot",
                "Use sampled CPU for hot functions and precise CPU when context switches are present; compare sampled hotspots with exact on-CPU time before blaming a process.",
                ["cpu_top_functions", "cpu_precise_analysis"],
                ["cpu", "scheduler"],
                [
                    (capabilities.HasCpuSamples, "cpu_samples"),
                    (capabilities.HasCSwitch, "context_switches"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasCpuSamples, "cpu_top_functions will be unavailable without CPU samples."),
                    (!capabilities.HasCSwitch, "cpu_precise_analysis will be unavailable without context switches."))));
        }

        if (capabilities.HasFileIo || capabilities.HasDiskIo)
        {
            flows.Add(Flow(
                "io_contention",
                "Use file IO by-file rows for path attribution, then stack views only when stackwalks are present; compare file IO with disk IO to separate cache-served activity from physical media.",
                ["file_io_top_files", "file_io_top_stacks", "disk_io_top_stacks"],
                ["io", "disk"],
                [
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasDiskIo, "disk_io"),
                    (capabilities.HasStackWalks, "stack_walks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasFileIo, "file_io_top_files will be empty without FileIO events."),
                    (!capabilities.HasStackWalks, "IO stack attribution will be unavailable without stackwalks."))));
        }

        if (capabilities.HasMemoryProcessInfo || capabilities.HasHardFaults)
        {
            flows.Add(Flow(
                "memory_pressure",
                "Use memory_resource_analysis for sampled process pressure and hard_fault_by_file for page-in evidence; use timestamps from both to choose a diagnose_window interval.",
                ["memory_resource_analysis", "hard_fault_by_file", "diagnose_window"],
                ["memory", "hard_faults"],
                [
                    (capabilities.HasMemoryProcessInfo, "memory_process_info"),
                    (capabilities.HasHardFaults, "hard_faults"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasMemoryProcessInfo, "memory_resource_analysis will not include working set or commit snapshots without MemoryInfoWS events."),
                    (!capabilities.HasHardFaults, "hard_fault_by_file will be empty without HardFaults events."))));
        }

        if (HasAnyClrCapability(capabilities))
        {
            flows.Add(Flow(
                "dotnet_runtime",
                "Use CLR-specific tools only for the runtime signals present in this trace; missing CLR providers cannot be reconstructed after capture.",
                BuildClrRuntimeToolSequence(capabilities),
                BuildClrRuntimeGoals(capabilities),
                [
                    (capabilities.HasClrGc, "clr_gc"),
                    (capabilities.HasClrAlloc, "clr_alloc"),
                    (capabilities.HasClrException, "clr_exception"),
                    (capabilities.HasClrContention, "clr_contention"),
                    (capabilities.HasClrJit, "clr_jit"),
                    (capabilities.HasStackWalks, "stack_walks"),
                ],
                BuildFlowCaveats((!capabilities.HasStackWalks, "CLR stack views will be degraded without stackwalks."))));
        }

        if (capabilities.HasNetIo || capabilities.HasNetConnections)
        {
            flows.Add(Flow(
                "network_activity",
                "Use connection lifecycle rows for setup timing and network stack rows for byte attribution; either signal can exist without the other.",
                ["net_connections", "net_top_stacks"],
                ["network"],
                [
                    (capabilities.HasNetConnections, "network_connections"),
                    (capabilities.HasNetIo, "network_io"),
                    (capabilities.HasStackWalks, "stack_walks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasNetConnections, "net_connections will be empty without connection lifecycle events."),
                    (!capabilities.HasNetIo, "net_top_stacks will be empty without network byte events."),
                    (!capabilities.HasStackWalks, "network stack attribution will be unavailable without stackwalks."))));
        }

        return flows;
    }

    private static void AddCapability(List<string> enabled, bool isEnabled, string name)
    {
        if (isEnabled)
            enabled.Add(name);
    }

    private static DiagnosticFlowRecommendation Flow(
        string name,
        string reason,
        IReadOnlyList<string> toolSequence,
        IReadOnlyList<string> goals,
        IReadOnlyList<(bool IsEnabled, string Name)> capabilities,
        IReadOnlyList<string> caveats)
        => new(
            FlowName: name,
            Reason: reason,
            ToolSequence: toolSequence,
            Goals: goals,
            EnabledCapabilities: capabilities.Where(capability => capability.IsEnabled).Select(capability => capability.Name).ToList(),
            MissingCapabilities: capabilities.Where(capability => !capability.IsEnabled).Select(capability => capability.Name).ToList(),
            Caveats: caveats);

    private static IReadOnlyList<string> BuildFlowCaveats(params (bool Applies, string Message)[] caveats)
        => caveats
            .Where(caveat => caveat.Applies)
            .Select(caveat => caveat.Message)
            .ToList();

    private static IReadOnlyList<string> BuildClrRuntimeToolSequence(TraceCapabilities capabilities)
    {
        var tools = new List<string>();
        if (capabilities.HasClrGc)
        {
            tools.Add("clr_gc_analysis");
            tools.Add("clr_gc_heap_stats");
        }
        if (capabilities.HasClrJit)
            tools.Add("clr_jit_analysis");
        if (capabilities.HasClrAlloc)
            tools.Add("clr_alloc_top_stacks");
        if (capabilities.HasClrException)
            tools.Add("clr_exception_top_stacks");
        if (capabilities.HasClrContention)
            tools.Add("clr_contention_top_stacks");
        return tools;
    }

    private static IReadOnlyList<string> BuildClrRuntimeGoals(TraceCapabilities capabilities)
    {
        var goals = new List<string> { "dotnet" };
        if (capabilities.HasClrGc)
            goals.Add("gc");
        if (capabilities.HasClrJit)
            goals.Add("jit");
        if (capabilities.HasClrAlloc)
            goals.Add("allocations");
        if (capabilities.HasClrException)
            goals.Add("exceptions");
        if (capabilities.HasClrContention)
            goals.Add("locks");
        return goals;
    }

    private static IReadOnlyList<ToolRecommendation> BuildToolRecommendationRecords(
        IReadOnlyList<(string ToolName, string Reason, string[] Goals)> recommendations)
        => recommendations
            .Select(recommendation => new ToolRecommendation(
                recommendation.ToolName,
                recommendation.Reason,
                recommendation.Goals))
            .ToList();

    private static bool HasAnyClrCapability(TraceCapabilities capabilities) =>
        capabilities.HasClrGc ||
        capabilities.HasClrJit ||
        capabilities.HasClrAlloc ||
        capabilities.HasClrException ||
        capabilities.HasClrContention;

    private static string BuildMemoryResourceRecommendationReason(TraceCapabilities capabilities)
    {
        var signals = new List<string>();
        if (capabilities.HasMemoryProcessInfo)
            signals.Add("working set and commit/private-byte snapshots");
        if (capabilities.HasHandleEvents)
            signals.Add("handle create/close deltas");
        if (capabilities.HasPoolEvents)
            signals.Add("observed pool allocation/free deltas");

        return $"Memory resource events are present; inspect {string.Join(", ", signals)} by process.";
    }

    private static string DefaultSymbolCacheDir() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WprMcp", "Symbols");

    private static readonly string[] CpuSampleToolNames =
    [
        "cpu_top_functions",
        "cpu_top_functions_batch",
        "cpu_caller_callee",
    ];

    private static readonly string[] ContextSwitchToolNames =
    [
        "cpu_precise_analysis",
        "wait_top_stacks",
        "wait_caller_callee",
        "wait_analysis",
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
    ];

    private static readonly string[] WaitStackToolNames =
    [
        "wait_top_stacks",
        "wait_caller_callee",
    ];

    private static readonly string[] ReadyThreadStackToolNames =
    [
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
    ];

    private static readonly string[] InterruptStackToolNames =
    [
        "interrupt_top_stacks",
        "interrupt_caller_callee",
    ];

    private static readonly string[] CompositeToolNames =
    [
        "diagnose_slow_startup",
    ];

    private static readonly string[] HighWaitCompositeToolNames =
    [
        "diagnose_high_wait",
    ];

    private static readonly string[] StackWalkDegradedCompositeToolNames =
    [
        "diagnose_high_wait",
    ];

    private static readonly string[] FileIoToolNames =
    [
        "file_io_top_files",
        "file_io_top_stacks",
        "file_io_caller_callee",
    ];

    private static readonly string[] DiskIoToolNames =
    [
        "disk_io_top_stacks",
        "disk_io_caller_callee",
    ];

    private static readonly string[] ClrToolNames =
    [
        "clr_gc_analysis",
        "clr_gc_heap_stats",
        "clr_finalizer_analysis",
        "clr_jit_analysis",
        "clr_alloc_top_stacks",
        "clr_alloc_caller_callee",
        "clr_exception_top_stacks",
        "clr_exception_caller_callee",
        "clr_contention_top_stacks",
        "clr_contention_caller_callee",
    ];

    private static readonly string[] NetworkByteToolNames =
    [
        "net_top_stacks",
        "net_caller_callee",
    ];

    private static readonly string[] NetworkConnectionToolNames =
    [
        "net_connections",
    ];

    private static readonly string[] MemoryResourceToolNames =
    [
        "memory_resource_analysis",
    ];

    private static readonly string[] StackDependentToolNames =
    [
        "cpu_top_functions",
        "cpu_top_functions_batch",
        "cpu_caller_callee",
        "wait_top_stacks",
        "wait_caller_callee",
        "ready_thread_top_stacks",
        "ready_thread_caller_callee",
        "file_io_top_stacks",
        "file_io_caller_callee",
        "disk_io_top_stacks",
        "disk_io_caller_callee",
        "hard_fault_top_stacks",
        "hard_fault_caller_callee",
        "image_load_top_stacks",
        "image_load_caller_callee",
        "registry_top_stacks",
        "registry_caller_callee",
        "net_top_stacks",
        "net_caller_callee",
        "alpc_top_stacks",
        "alpc_caller_callee",
        "interrupt_top_stacks",
        "interrupt_caller_callee",
        "heap_alloc_top_stacks",
        "heap_alloc_caller_callee",
        "virtual_alloc_top_stacks",
        "virtual_alloc_caller_callee",
        "clr_alloc_top_stacks",
        "clr_alloc_caller_callee",
        "clr_exception_top_stacks",
        "clr_exception_caller_callee",
        "clr_contention_top_stacks",
        "clr_contention_caller_callee",
        "generic_event_top_stacks",
        "generic_event_caller_callee",
    ];

    private static IReadOnlyList<SymbolRecommendation> BuildSymbolRecommendations(
        TraceLog trace)
    {
        // Catalog entries that recommend a server (skip the no-public-PDB tier — it has no
        // URL to recommend, only diagnose_symbols consumes it).
        var serverEntries = SymbolHintCatalog.Entries
            .Where(e => e.ServerUrl != null && e.LoadTraceReason != null)
            .ToList();

        var hits = serverEntries
            .Select(e => (Entry: e, Modules: new SortedSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var module in trace.ModuleFiles)
        {
            // Already-resolved modules don't need a recommendation.
            if (!string.IsNullOrEmpty(module.PdbName)) continue;

            var name = module.Name ?? string.Empty;
            for (var i = 0; i < hits.Count; i++)
            {
                if (hits[i].Entry.Matches(name))
                {
                    hits[i].Modules.Add(name);
                    break;
                }
            }
        }

        return hits
            .Where(h => h.Modules.Count > 0)
            .Select(h => new SymbolRecommendation(
                Reason: h.Entry.LoadTraceReason!,
                ServerUrl: h.Entry.ServerUrl!,
                MatchedModuleCount: h.Modules.Count,
                SampleModules: h.Modules.Take(5).ToList()))
            .ToList();
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Lists processes in the loaded trace. Default order is CPU time descending. " +
        "WaitRatio = WallUs/CpuUs surfaces 'high wall, low CPU' processes (blocked on minifilter, IPC, etc.). " +
        "PID 0 (Idle) and PID 4 (System) hidden by default — pass includeSystem=true to surface them. " +
        "When orderBy='wait_ratio', trace-resident processes (alive before trace start AND survived past " +
        "trace end) and processes with near-zero sampled CPU are pushed to the bottom because " +
        "their ratio is denominator-sensitive noise. " +
        "No startUs/endUs: this is a whole-trace process overview; use windowed analyzers for scoped metrics.")]
    public ProcessListResponse ListProcesses(
        [Description("Absolute path to .etl file")] string path,
        [Description("Sort order: 'cpu' (default), 'wall', or 'wait_ratio'")] string orderBy = "cpu",
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Include PID 0 (Idle) and PID 4 (System); default false")] bool includeSystem = false)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var rows = ProcessProjection.Rows(trace, includeSystem).ToList();
        var totalCount = rows.Count;
        var hidden = includeSystem
            ? 0
            : trace.Processes.Count(p => p.ProcessID == 0 || p.ProcessID == 4);

        rows = orderBy.ToLowerInvariant() switch
        {
            "cpu" => rows.OrderByDescending(r => r.CpuUs).ToList(),
            "wall" => rows.OrderByDescending(r => r.WallUs).ToList(),
            "wait_ratio" => rows
                .OrderByDescending(WaitRatioSortKey)
                .ThenByDescending(r => r.WallUs)
                .ToList(),
            _ => throw new ArgumentException(
                $"orderBy must be 'cpu', 'wall', or 'wait_ratio'; got '{orderBy}'", nameof(orderBy)),
        };

        rows = rows.Take(top).ToList();
        return new ProcessListResponse(rows, hidden, totalCount);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-fork timing for a parent process — given a PID, returns every child the kernel " +
        "reported as having that parent, with FirstImageLoadOffsetUs (the kernel-side window " +
        "between ProcessStart and the first DLL load: where AV / process-create callbacks " +
        "burn time invisibly to the child) and GapFromPreviousSpawnUs (lets you spot fork " +
        "bursts vs steady-state). Median/p95/max aggregates across kernel gaps surface " +
        "worst-case in a single number. No startUs/endUs: scope is the parent's child-process lifecycle, " +
        "with rows ordered by child spawn time.")]
    public ProcessCreateTimingResponse ProcessCreateTiming(
        [Description("Absolute path to .etl file")] string path,
        [Description("Parent process ID — the process whose CreateProcess calls you want timed.")]
        int parentPid,
        [Description("Top N children by spawn order (default 50, max 1000). Children are " +
                     "sorted chronologically; 'top' caps response size on prolific spawners.")]
        int top = 50)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(parentPid);
        var trace = _cache.Get(path);
        return ProcessCreateTimingAnalysis.Analyze(trace, parentPid, top);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Per-process thread-lifecycle list — every ThreadStart / ThreadStop in chronological " +
        "order for one PID, with start time, end time, and lifetime in microseconds.  Useful " +
        "for 'did the thread pool spawn 200 threads in the startup window' / 'is something " +
        "thrashing thread creation'.  Threads still alive at trace end are flagged " +
        "TraceResidentEnd; threads alive when capture started are flagged TraceResidentStart " +
        "(their StartTimeUs is 0 = trace start, not the real spawn).  PeakConcurrentThreads " +
        "gives the maximum number of simultaneously-live threads for the PID.  Requires the Thread " +
        "keyword in the capture profile (in default kernel profiles). No startUs/endUs: this reports a " +
        "per-PID thread lifecycle timeline; timestamps identify the interval boundaries.")]
    public ThreadLifetimeResponse ThreadLifetime(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N threads, ordered by start time (default 200, max 1000)")] int top = 200)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(pid);
        var trace = _cache.Get(path);
        return ThreadLifetimeAnalysis.Analyze(trace, pid, top);
    }

    // Trace-resident processes and near-zero-CPU rows get huge ratios from tiny denominators.
    // Keep the floor low enough that real high-wall/low-CPU IPC waits still surface, while
    // 1-4ms bookkeeping rows don't dominate the sort.
    private static double WaitRatioSortKey(ProcessRow r)
        => r.TraceResident || r.CpuUs < WaitRatioMinCpuUs(r)
            ? double.NegativeInfinity
            : r.WaitRatio ?? double.NegativeInfinity;

    private static long WaitRatioMinCpuUs(ProcessRow r)
        => Math.Max(MinCpuUsForWaitRatioSort, (long)(r.WallUs * MinCpuShareForWaitRatioSort));
}
