using System.ComponentModel;
using Microsoft.Diagnostics.Tracing.Etlx;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class MetaTools
{
    private const long MinCpuUsForWaitRatioSort = 5_000;
    private const double MinCpuShareForWaitRatioSort = 0.00001;

    private readonly TraceCache _cache;
    public MetaTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Loads (or returns cached) a Windows ETW .etl trace. First load can take 30s-3min; subsequent calls are instant. " +
        "Materializing an ETL may create or refresh its ETLX sidecar, so this tool is not filesystem-read-only. " +
        "Response includes symbol-server recommendations based on the modules referenced by the trace. " +
        "No startUs/endUs: this is whole-trace cache/orientation, not event-window analysis.")]
    public LoadTraceResponse LoadTrace(
        [Description("Absolute path to .etl file")] string path)
    {
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var capabilities = traceLease.Capabilities;
        return new LoadTraceResponse(BuildTraceMeta(path, trace), BuildSymbolStatus(trace), capabilities);
    }

    [McpServerTool(
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = true,
        Destructive = true,
        UseStructuredContent = true), Description(
        "Retires the cached trace for a path without interrupting queries that already hold a lease. " +
        "For a raw .etl path, registers an adjacent-.etlx refresh request for the next successful load in this running server process; " +
        "the request does not survive restart and the sidecar is not changed until that load. " +
        "Use after replacing or rewriting an ETL in place, especially when size and timestamps were preserved. " +
        "No startUs/endUs: cache retirement applies to the entire canonical trace path. Repeated calls are safe.")]
    public UnloadTraceResponse UnloadTrace(
        [Description("Absolute or relative path to the cached .etl or .etlx trace")] string path)
    {
        Validation.RequireText(path);
        var canonical = Path.GetFullPath(path);
        var retired = _cache.Unload(canonical);
        var refreshRequested = string.Equals(
            Path.GetExtension(canonical), ".etl", StringComparison.OrdinalIgnoreCase);
        var warnings = new List<string>
        {
            "Active queries keep their existing trace lease until they complete.",
        };
        if (refreshRequested)
        {
            warnings.Add(
                "The ETLX refresh request exists only in this running server process. " +
                "A restart clears it, and regeneration is not proven until a later load succeeds.");
        }
        else
        {
            warnings.Add(
                "No raw-ETL sidecar refresh was requested because the supplied path is not a .etl path.");
        }
        return new UnloadTraceResponse(
            canonical,
            retired,
            refreshRequested,
            warnings,
            RefreshRequestedForCurrentServerProcess: refreshRequested);
    }

    [McpServerTool(
        ReadOnly = false,
        Idempotent = true,
        OpenWorld = true,
        Destructive = true,
        UseStructuredContent = true), Description(
        "Inspects a trace once and returns machine-readable orientation: capture capabilities, " +
        "system metadata, provider counts, stackwalk completeness, PDB identity/configuration quality, quality " +
        "warnings, and capability-driven next-tool hints. Use when the capture profile is unknown, " +
        "the analysis goal is unclear, or prior domain tools returned empty/low-confidence results. " +
        "Stack recommendations require attached stacks in that exact event domain; unrelated global " +
        "stacks never enable them. The AnalysisContract object supplies machine-readable rules for scope, " +
        "counts, empty results, symbols, thread replay, and causal restraint. " +
        "inspect_trace does not probe local PDB candidates or run frame lookup; use diagnose_symbols " +
        "for verified local readiness and a stack tool for observed frame-name resolution. " +
        "Recommendations are capability-driven hints, not goal-specific rankings. " +
        "The first call may materialize or refresh an ETLX sidecar, so this tool is not filesystem-read-only. " +
        "No startUs/endUs: capabilities, metadata, provider counts, and PDB identity/configuration describe the whole trace.")]
    public InspectTraceResponse InspectTrace(
        [Description("Absolute path to .etl file")] string path)
    {
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var capabilities = traceLease.Capabilities;
        var metadata = traceLease.Metadata;
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
            recommendedFlows,
            BuildAnalysisContractGuidance());
    }

    private static AnalysisContractGuidance BuildAnalysisContractGuidance() =>
        new(
            ScopeRule:
                "Only ScopeStatus=ok authorizes attribution. single_process is exact; " +
                "pid_aggregate intentionally combines IncludedProcesses while retaining their instance keys; " +
                "rows and totals may aggregate across them according to each tool's accounting contract. " +
                "Exact-only tools return structured process_start_required for a clean multi-lifetime PID; " +
                "ambiguous_process_instance is reserved for unsafe lifetime evidence.",
            TraceScopedRule:
                "Trace* is whole-trace diagnostic evidence; Scoped* uses the selected identity and " +
                "requested [startUs,endUs) window. Never use Trace* as a scoped denominator.",
            CountRule:
                "MatchedEventCount is scoped raw source events/endpoints; MatchedIntervalCount is " +
                "completed projected intervals; Rows can be aggregated or top-N truncated.",
            CapabilityRule:
                "not_observed requires absence trace-wide under the tool's documented predicate; " +
                "observed requires attributable scoped evidence; otherwise unknown.",
            StackRule:
                "Use event-domain StackCoverage. Unrelated global stacks do not enable a stack tool, " +
                "and ?!? is synthetic unknown evidence rather than a captured call chain.",
            SymbolRule:
                "PDB identity and verified local readiness are not function-name resolution. " +
                "Only stack-tool SymbolStats after lookup measure observed frame resolution.",
            ThreadReplayRule:
                "Replay exact CPU/Wait thread rows with pid + processStartUs + tid + threadStartUs + " +
                "threadGeneration; generation disambiguates equal inferred start times.",
            CausalityRule:
                "Associated/readier stacks and heuristic security matches support hypotheses but do " +
                "not independently prove an unblocker, scanner identity, root cause, or causality.",
            NoDataReasons: new NoDataReasonGuidance());

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
        var ntPath = SymbolPathState.CurrentPath;
        var cacheDir = DefaultSymbolCacheDir();
        var warning = string.IsNullOrEmpty(ntPath)
            ? "_NT_SYMBOL_PATH is not set. Stack queries still probe the trace directory through a query-local path, but no configured local stores or symbol servers are available. " +
              "Call set_symbol_path or add_symbol_server, or configure the environment in MCP config."
            : null;

        return new SymbolStatus(ntPath, cacheDir, warning, BuildSymbolRecommendations(trace));
    }

    private static InspectSymbolQuality BuildInspectSymbolQuality(TraceLog trace)
    {
        var modules = trace.ModuleFiles.ToList();
        var modulesMissingPdbName = modules
            .Where(module => string.IsNullOrWhiteSpace(module.PdbName))
            .OrderBy(module => module.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(module =>
            {
                var name = module.Name ?? "<unknown>";
                return new InspectModuleMissingPdbName(
                    name,
                    "Recapture or merge the ETL on the collection machine so the PDB name, GUID, and Age identity is recorded before choosing a symbol server.");
            })
            .Take(20)
            .ToList();

        var modulesWithPdbName = modules.Count(module =>
            !string.IsNullOrWhiteSpace(module.PdbName));
        var modulesWithCompletePdbIdentity = modules.Count(module =>
            !string.IsNullOrWhiteSpace(module.PdbName) &&
            module.PdbSignature != Guid.Empty &&
            module.PdbAge > 0);
        double? pdbNameRate = modules.Count == 0
            ? null
            : modulesWithPdbName / (double)modules.Count;
        double? completeIdentityRate = modules.Count == 0
            ? null
            : modulesWithCompletePdbIdentity / (double)modules.Count;

        return new InspectSymbolQuality(
            NtSymbolPath: SymbolPathState.CurrentPath,
            CacheDir: DefaultSymbolCacheDir(),
            ModuleCount: modules.Count,
            ResolvedModuleCount: null,
            ModuleResolutionRate: null,
            TopUnresolvedModules: Array.Empty<InspectUnresolvedModule>(),
            Recommendations: BuildSymbolRecommendations(trace),
            ModulesWithPdbName: modulesWithPdbName,
            ModulesWithPdbNameRate: pdbNameRate,
            ModulesWithCompletePdbIdentity: modulesWithCompletePdbIdentity,
            CompletePdbIdentityRate: completeIdentityRate,
            FrameResolutionMeasurementState: "not_measured",
            FrameResolution: null)
        {
            TopModulesMissingPdbName = modulesMissingPdbName,
        };
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

        if (symbolQuality.CompletePdbIdentityRate is < 0.8)
        {
            warnings.Add(new TraceQualityWarning(
                Code: "low_module_pdb_identity_coverage",
                Severity: "warn",
                Message: $"{symbolQuality.CompletePdbIdentityRate.Value * 100:F1}% of loaded modules carry a complete PDB name + GUID + Age identity; this is metadata coverage, not measured frame resolution.",
                NextStep: "Recapture or merge the ETL on the collection machine to preserve complete PDB identities, then run diagnose_symbols and the target stack tool with resolveSymbols=true.",
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
            "Neither explicit StackWalk records nor attached event stacks were observed anywhere in the trace; this aggregate warning does not substitute for per-domain coverage.",
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
            "Recapture with Microsoft-Windows-DotNETRuntime enabled if .NET JIT, GC, allocation, exception, or contention analysis matters. Use tests/WpaMcp.Tests/fixtures/JitOnlyCapture.wprp for minimal JIT-only traces.",
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
            "Recapture with MemoryInfoWS enabled, for example tests/WpaMcp.Tests/fixtures/MemoryCapture.wprp.",
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
            symbolQuality.CompletePdbIdentityRate is < 0.8 ||
            symbolQuality.Recommendations.Count > 0)
        {
            recommendations.Add((
                "diagnose_symbols",
                "Symbol-path configuration or module PDB identity/local readiness needs validation; actual frame-name resolution must still be measured by a stack tool.",
                ["symbols", "quality"]));
        }

        return BuildToolRecommendationRecords(recommendations);
    }

    internal static IReadOnlyList<ToolRecommendation> BuildCapabilitySupportedTools(TraceCapabilities capabilities)
    {
        var recommendations = new List<(string ToolName, string Reason, string[] Goals)>();

        AddStackRecommendation(
            capabilities.HasCpuSamples,
            "cpu",
            "cpu_top_functions",
            "CPU samples with attached stacks are present; rank hot functions first for CPU-bound investigations.",
            ["cpu"]);

        if (capabilities.HasCSwitch)
        {
            recommendations.Add(("cpu_precise_analysis", "Context switch events are present; compute exact on-CPU time, ready latency, and core attribution.", ["cpu", "scheduler"]));
            recommendations.Add(("wait_analysis", "Context switch events are present; identify blocked threads and dominant wait reasons.", ["wait"]));
        }

        AddStackRecommendation(
            capabilities.HasCSwitch,
            "cswitch",
            "wait_top_stacks",
            "Context switches with blocking stacks are present; inspect the switch-out call chains associated with blocked intervals.",
            ["wait", "stacks"],
            legacyHasDomainStacks: capabilities.HasCSwitchStacks);

        AddStackRecommendation(
            capabilities.HasReadyThread,
            "ready_thread",
            "ready_thread_top_stacks",
            "ReadyThread stacks provide associated readier/wakeup evidence; they do not alone prove a fully paired wait-to-wakeup cause.",
            ["wait", "scheduler", "stacks"],
            legacyHasDomainStacks: capabilities.HasReadyThreadStacks);

        if (capabilities.HasImageLoad)
            recommendations.Add(("image_load_top_gaps", "Image load events are present; rank loader gaps for startup and DLL-load investigations.", ["startup", "image_load"]));

        AddStackRecommendation(
            capabilities.HasImageLoad,
            "image_load",
            "image_load_top_stacks",
            "ImageLoad events with attached stacks are present; attribute loads to associated call chains.",
            ["startup", "image_load", "stacks"]);

        if (capabilities.HasFileIo)
            recommendations.Add(("file_io_top_files", "File IO events are present; identify files with the most read/write bytes, optionally narrowed by pid/startUs/endUs.", ["io"]));

        AddStackRecommendation(
            capabilities.HasFileIo,
            "file_io",
            "file_io_top_stacks",
            "File IO events with attached stacks are present; attribute file IO bytes to call stacks.",
            ["io", "stacks"]);

        AddStackRecommendation(
            capabilities.HasDiskIo,
            "disk_io",
            "disk_io_top_stacks",
            "Disk IO events with attached stacks are present; attribute physical media bytes to call stacks.",
            ["io", "disk", "stacks"]);

        if (capabilities.HasHardFaults)
            recommendations.Add(("hard_fault_by_file", "Hard-fault events are present; identify files that caused page-ins, optionally narrowed by pid/startUs/endUs.", ["memory", "hard_faults"]));

        AddStackRecommendation(
            capabilities.HasHardFaults,
            "hard_fault",
            "hard_fault_top_stacks",
            "Hard-fault events with attached stacks are present; attribute page-in bytes to associated call chains.",
            ["memory", "hard_faults", "stacks"]);

        if (capabilities.HasClrGc)
            recommendations.Add(("clr_gc_analysis", "CLR GC events are present; inspect GC duration and stop-the-world pause time.", ["gc", "dotnet"]));

        if (capabilities.HasClrJit)
            recommendations.Add(("clr_jit_analysis", "CLR JIT events are present; rank methods by JIT compilation duration.", ["jit", "dotnet"]));

        AddStackRecommendation(capabilities.HasClrAlloc, "clr_alloc", "clr_alloc_top_stacks",
            "CLR allocation ticks with attached stacks are present; rank managed allocation sources.", ["memory", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasClrException, "clr_exception", "clr_exception_top_stacks",
            "CLR exception events with attached stacks are present; rank thrown exception sources and top exception types.", ["exceptions", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasClrContention, "clr_contention", "clr_contention_top_stacks",
            "Paired CLR contention intervals with attached start stacks are present; rank managed Monitor contention call stacks.", ["locks", "dotnet", "stacks"]);
        AddStackRecommendation(capabilities.HasNetIo, "net_io", "net_top_stacks",
            "Network byte events with attached stacks are present; attribute TCP/UDP bytes to call stacks.", ["network", "stacks"]);

        if (capabilities.HasNetConnections)
            recommendations.Add(("net_connections", "Network connection lifecycle events are present; inspect TCP connect/accept/disconnect timing.", ["network", "connections"]));

        if (capabilities.HasMemoryProcessInfo || capabilities.HasHandleEvents || capabilities.HasPoolEvents)
            recommendations.Add(("memory_resource_analysis", BuildMemoryResourceRecommendationReason(capabilities), ["memory"]));

        AddStackRecommendation(capabilities.HasAlpc, "alpc", "alpc_top_stacks",
            "ALPC events with attached stacks are present; rank cross-process IPC message call chains.", ["ipc", "stacks"]);
        AddStackRecommendation(capabilities.HasInterrupt, "interrupt", "interrupt_top_stacks",
            "DPC/ISR events with attached stacks are present; rank interrupt time by driver call chain.", ["interrupts", "drivers", "stacks"],
            legacyHasDomainStacks: capabilities.HasInterruptStacks);
        AddStackRecommendation(capabilities.HasVirtualAlloc, "virtual_alloc", "virtual_alloc_top_stacks",
            "Virtual memory operations with attached stacks are present; rank operated bytes by call chain.", ["memory", "virtual_memory", "stacks"]);
        AddStackRecommendation(capabilities.HasRegistry, "registry", "registry_top_stacks",
            "Registry operations with attached stacks are present; rank registry activity by call chain.", ["registry", "stacks"]);
        AddStackRecommendation(capabilities.HasNtHeap, "heap_alloc", "heap_alloc_top_stacks",
            "NT heap allocation events with attached stacks are present; rank allocation bytes by call chain.", ["memory", "heap", "stacks"]);

        void AddStackRecommendation(
            bool hasEvents,
            string domain,
            string toolName,
            string reason,
            string[] goals,
            bool legacyHasDomainStacks = false)
        {
            if (!hasEvents)
                return;

            DomainStackCoverage? coverage = null;
            if (capabilities.StackCoverageByDomain is not null)
                capabilities.StackCoverageByDomain.TryGetValue(domain, out coverage);
            if (coverage is null)
            {
                if (legacyHasDomainStacks)
                    recommendations.Add((toolName, reason, goals));
                return;
            }
            if (coverage.StackedEventCount == 0)
                return;

            if (coverage.CoverageState == "partial")
            {
                reason +=
                    $" [stack_coverage_state=partial;stack_coverage_pct={coverage.StackCoveragePct:0.##};" +
                    $"stacked_event_count={coverage.StackedEventCount};total_event_count={coverage.TotalEventCount}]";
            }
            recommendations.Add((toolName, reason, goals));
        }

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
        AddCapability(enabled, capabilities.HasExplicitStackWalkEvents, "explicit_stack_walk_events");
        AddCapability(enabled, capabilities.HasAttachedEventStacks, "attached_event_stacks");
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
            var hasCSwitchStacks = HasDomainStacks(capabilities, "cswitch");
            var hasReadyThreadStacks = HasDomainStacks(capabilities, "ready_thread");
            var tools = new List<string> { "diagnose_high_wait", "wait_analysis" };
            if (hasCSwitchStacks)
                tools.Add("wait_top_stacks");
            if (capabilities.HasReadyThread && hasReadyThreadStacks)
                tools.Add("ready_thread_top_stacks");
            flows.Add(Flow(
                "high_wait",
                "Use diagnose_high_wait for high-wall/low-CPU traces before expanding into detailed wait or ready-thread stacks.",
                tools,
                ["wait", "scheduler"],
                [
                    (capabilities.HasCSwitch, "context_switches"),
                    (hasCSwitchStacks, "cswitch_stacks"),
                    (capabilities.HasReadyThread, "ready_thread"),
                    (hasReadyThreadStacks, "ready_thread_stacks"),
                ],
                BuildDomainStackCaveats(
                    capabilities,
                    (capabilities.HasCSwitch, "cswitch", "wait_top_stacks"),
                    (capabilities.HasReadyThread, "ready_thread", "ready_thread_top_stacks"))));
        }

        if (capabilities.HasCpuSamples || capabilities.HasCSwitch)
        {
            var cpuStackCoverage = GetDomainStackCoverage(capabilities, "cpu");
            var hasCpuStacks = capabilities.HasCpuSamples &&
                               cpuStackCoverage?.StackedEventCount > 0;
            var cpuTools = new List<string>();
            if (hasCpuStacks)
                cpuTools.Add("cpu_top_functions");
            if (capabilities.HasCSwitch)
                cpuTools.Add("cpu_precise_analysis");
            flows.Add(Flow(
                "cpu_hotspot",
                "Use sampled CPU for hot functions and precise CPU when context switches are present; compare sampled hotspots with exact on-CPU time before blaming a process.",
                cpuTools,
                ["cpu", "scheduler"],
                [
                    (capabilities.HasCpuSamples, "cpu_samples"),
                    (hasCpuStacks, "cpu_sample_stacks"),
                    (capabilities.HasCSwitch, "context_switches"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasCpuSamples, "cpu_top_functions will be unavailable without CPU samples."),
                    (capabilities.HasCpuSamples && !hasCpuStacks, $"cpu_top_functions omitted because cpu stack coverage is {cpuStackCoverage?.CoverageState ?? "unknown"}."),
                    (!capabilities.HasCSwitch, "cpu_precise_analysis will be unavailable without context switches."))));
        }

        if (capabilities.HasFileIo || capabilities.HasDiskIo)
        {
            var fileIoStackCoverage = GetDomainStackCoverage(capabilities, "file_io");
            var diskIoStackCoverage = GetDomainStackCoverage(capabilities, "disk_io");
            var hasFileIoStacks = fileIoStackCoverage?.StackedEventCount > 0;
            var hasDiskIoStacks = diskIoStackCoverage?.StackedEventCount > 0;
            var ioTools = new List<string>();
            if (capabilities.HasFileIo)
                ioTools.Add("file_io_top_files");
            if (hasFileIoStacks)
                ioTools.Add("file_io_top_stacks");
            if (hasDiskIoStacks)
                ioTools.Add("disk_io_top_stacks");
            flows.Add(Flow(
                "io_contention",
                "Use file IO by-file rows for path attribution, then only use stack views whose own event domain has attached stacks; compare file IO with disk IO to separate cache-served activity from physical media.",
                ioTools,
                ["io", "disk"],
                [
                    (capabilities.HasFileIo, "file_io"),
                    (capabilities.HasDiskIo, "disk_io"),
                    (hasFileIoStacks, "file_io_stacks"),
                    (hasDiskIoStacks, "disk_io_stacks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasFileIo, "file_io_top_files will be empty without FileIO events."),
                    (capabilities.HasFileIo && !hasFileIoStacks, $"file_io_top_stacks omitted because file_io stack coverage is {fileIoStackCoverage?.CoverageState ?? "unknown"}."),
                    (capabilities.HasDiskIo && !hasDiskIoStacks, $"disk_io_top_stacks omitted because disk_io stack coverage is {diskIoStackCoverage?.CoverageState ?? "unknown"}."))));
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
            var hasClrAllocStacks = HasDomainStacks(capabilities, "clr_alloc");
            var hasClrExceptionStacks = HasDomainStacks(capabilities, "clr_exception");
            var hasClrContentionStacks = HasDomainStacks(capabilities, "clr_contention");
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
                    (hasClrAllocStacks, "clr_alloc_stacks"),
                    (hasClrExceptionStacks, "clr_exception_stacks"),
                    (hasClrContentionStacks, "clr_contention_stacks"),
                ],
                BuildDomainStackCaveats(
                    capabilities,
                    (capabilities.HasClrAlloc, "clr_alloc", "clr_alloc_top_stacks"),
                    (capabilities.HasClrException, "clr_exception", "clr_exception_top_stacks"),
                    (capabilities.HasClrContention, "clr_contention", "clr_contention_top_stacks"))));
        }

        if (capabilities.HasNetIo || capabilities.HasNetConnections)
        {
            var hasNetIoStacks = HasDomainStacks(capabilities, "net_io");
            var networkTools = new List<string>();
            if (capabilities.HasNetConnections)
                networkTools.Add("net_connections");
            if (hasNetIoStacks)
                networkTools.Add("net_top_stacks");
            flows.Add(Flow(
                "network_activity",
                "Use connection lifecycle rows for setup timing and network stack rows for byte attribution; either signal can exist without the other.",
                networkTools,
                ["network"],
                [
                    (capabilities.HasNetConnections, "network_connections"),
                    (capabilities.HasNetIo, "network_io"),
                    (hasNetIoStacks, "network_io_stacks"),
                ],
                BuildFlowCaveats(
                    (!capabilities.HasNetConnections, "net_connections omitted because connection lifecycle events were not observed."),
                    (!capabilities.HasNetIo, "net_top_stacks omitted because network byte events were not observed."))
                    .Concat(BuildDomainStackCaveats(
                        capabilities,
                        (capabilities.HasNetIo, "net_io", "net_top_stacks")))
                    .ToList()));
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
        if (capabilities.HasClrAlloc && HasDomainStacks(capabilities, "clr_alloc"))
            tools.Add("clr_alloc_top_stacks");
        if (capabilities.HasClrException && HasDomainStacks(capabilities, "clr_exception"))
            tools.Add("clr_exception_top_stacks");
        if (capabilities.HasClrContention && HasDomainStacks(capabilities, "clr_contention"))
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

    private static DomainStackCoverage? GetDomainStackCoverage(
        TraceCapabilities capabilities,
        string domain)
    {
        if (capabilities.StackCoverageByDomain is not null &&
            capabilities.StackCoverageByDomain.TryGetValue(domain, out var coverage))
        {
            return coverage;
        }
        return null;
    }

    private static bool HasDomainStacks(TraceCapabilities capabilities, string domain) =>
        GetDomainStackCoverage(capabilities, domain)?.StackedEventCount > 0;

    private static IReadOnlyList<string> BuildDomainStackCaveats(
        TraceCapabilities capabilities,
        params (bool HasEvents, string Domain, string ToolName)[] domains)
    {
        var caveats = new List<string>();
        foreach (var (hasEvents, domain, toolName) in domains)
        {
            if (!hasEvents)
                continue;

            var coverage = GetDomainStackCoverage(capabilities, domain);
            if (coverage is null)
            {
                caveats.Add($"{toolName} omitted: stack_coverage_state=unknown;domain={domain}.");
            }
            else if (coverage.StackedEventCount == 0)
            {
                caveats.Add($"{toolName} omitted: stack_coverage_state={coverage.CoverageState};domain={domain};stacked_event_count=0;total_event_count={coverage.TotalEventCount}.");
            }
            else if (coverage.CoverageState == "partial")
            {
                caveats.Add($"{toolName} is partial evidence: stack_coverage_state=partial;domain={domain};stack_coverage_pct={coverage.StackCoveragePct:0.##};stacked_event_count={coverage.StackedEventCount};total_event_count={coverage.TotalEventCount}.");
            }
        }
        return caveats;
    }

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
            "WpaMcp", "Symbols");

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
            // A symbol server lookup requires the PDB name + GUID + Age key. Modules that
            // lack it need recapture/merge guidance, not a server recommendation that cannot
            // be executed. Identity metadata is still not proof that functions resolved.
            if (!SymbolTools.HasCompletePdbIdentity(
                    module.PdbName,
                    module.PdbSignature,
                    module.PdbAge))
                continue;

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

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Lists processes in the loaded trace. Default order is CPU time descending. " +
        "WaitRatio = WallUs/CpuUs ranks 'high wall, low CPU' process-lifetime candidates; " +
        "the ratio alone does not identify what they waited on. " +
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
        Validation.RequireText(orderBy);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
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

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Per-fork timing for a parent process — given a PID, returns every child the kernel " +
        "reported as having that parent, with FirstImageLoadOffsetUs (the kernel-side window " +
        "between ProcessStart and the first DLL load; this interval can include process-create " +
        "callbacks, security inspection, suspension, and other mechanisms, but does not identify " +
        "which mechanism caused the gap) and GapFromPreviousSpawnUs (lets you spot fork " +
        "bursts vs steady-state). Median/p95/max aggregates across kernel gaps surface " +
        "worst-case in a single number. No startUs/endUs: scope is the parent's child-process lifecycle, " +
        "with rows ordered by child spawn time. Clean parent-PID reuse returns structured " +
        "ScopeStatus/NoDataReason=process_start_required with replayable candidates; conflicting lifetime " +
        "evidence returns ambiguous_process_instance. A missing exact parent lifetime returns scope_not_found.")]
    public ProcessCreateTimingResponse ProcessCreateTiming(
        [Description("Absolute path to .etl file")] string path,
        [Description("Parent process ID — the process whose CreateProcess calls you want timed.")]
        int parentPid,
        [Description("Top N children by spawn order (default 50, max 1000). Children are " +
                     "sorted chronologically; 'top' caps response size on prolific spawners.")]
        int top = 50,
        [Description("Exact parent process start in trace-relative microseconds. Required when the parent PID has multiple lifetimes.")]
        long? processStartUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(parentPid);
        Validation.RequireThreadSelector(
            parentPid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        return ProcessCreateTimingAnalysis.Analyze(
            trace, parentPid, top, processStartUs);
    }

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Per-process thread-lifecycle list — every ThreadStart / ThreadStop in chronological " +
        "order for one PID, with start time, end time, and lifetime in microseconds.  Useful " +
        "for 'did the thread pool spawn 200 threads in the startup window' / 'is something " +
        "thrashing thread creation'.  Threads still alive at trace end are flagged " +
        "TraceResidentEnd; threads alive when capture started are flagged TraceResidentStart " +
        "(their StartTimeUs is 0 = trace start, not the real spawn).  PeakConcurrentThreads " +
        "gives the maximum number of simultaneously-live threads for the selected process instance. " +
        "Clean PID reuse returns structured ScopeStatus/NoDataReason=process_start_required with replayable " +
        "candidates unless processStartUs selects one lifetime; conflicting lifetime evidence returns " +
        "ambiguous_process_instance. Requires the Thread " +
        "keyword in the capture profile (in default kernel profiles). No startUs/endUs: this reports a " +
        "per-PID thread lifecycle timeline; timestamps identify the interval boundaries. A missing exact " +
        "lifetime returns ScopeStatus=scope_not_found rather than falling back to another instance.")]
    public ThreadLifetimeResponse ThreadLifetime(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N threads, ordered by start time (default 200, max 1000)")] int top = 200,
        [Description("Exact process start in trace-relative microseconds. Required when the PID has multiple clean lifetimes; otherwise process_start_required returns candidate keys.")]
        long? processStartUs = null)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(pid);
        Validation.RequireThreadSelector(
            pid, tid: null, processStartUs, threadStartUs: null);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        return ThreadLifetimeAnalysis.Analyze(trace, pid, top, processStartUs);
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
