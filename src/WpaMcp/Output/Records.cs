using System.ComponentModel;
using WpaMcp.Core;

namespace WpaMcp.Output;

public sealed record TraceMeta(
    string Path,
    long DurationUs,
    [property: Description("Count of TraceLog/ETLX-materialized logical events. This is not a raw ETW record count and must not be used as a parser-coverage denominator.")]
    long EventCount,
    long EventsLost,
    int ProcessCount,
    string EventCountRepresentation = "tracelog_etlx_materialized_logical_events",
    [property: Description("Raw ETW record count when measured independently of TraceLog materialization; null means not measured.")]
    long? RawEtwRecordCount = null,
    string RawEtwRecordCountState = "not_measured",
    [property: Description("Parser coverage is not computed from TraceLog EventCount. Null prevents materialized/raw representation differences from being presented as parser loss.")]
    double? ParserCoverageRate = null,
    string ParserCoverageState = "not_computed");

public sealed record SymbolStatus(
    string? NtSymbolPath,
    string CacheDir,
    string? Warning,
    IReadOnlyList<SymbolRecommendation> Recommendations);

public sealed record SymbolRecommendation(
    string Reason,
    string ServerUrl,
    int MatchedModuleCount,
    IReadOnlyList<string> SampleModules);

public sealed record LoadTraceResponse(
    TraceMeta Trace,
    SymbolStatus SymbolStatus,
    TraceCapabilities Capabilities);

public sealed record InspectTraceResponse(
    TraceMeta Trace,
    TraceCapabilities Capabilities,
    TraceMetadata Metadata,
    InspectSymbolQuality SymbolQuality,
    IReadOnlyList<TraceQualityWarning> Warnings,
    IReadOnlyList<ToolRecommendation> OrientationTools,
    IReadOnlyList<ToolRecommendation> CapabilitySupportedTools,
    IReadOnlyList<string> EnabledCapabilities,
    IReadOnlyList<DiagnosticFlowRecommendation> RecommendedDiagnosticFlows);

public sealed record DiagnosticFlowRecommendation(
    string FlowName,
    string Reason,
    IReadOnlyList<string> ToolSequence,
    IReadOnlyList<string> Goals,
    IReadOnlyList<string> EnabledCapabilities,
    IReadOnlyList<string> MissingCapabilities,
    IReadOnlyList<string> Caveats);

public sealed record TraceMetadata(
    TraceSystemConfiguration System,
    TraceStackwalkSummary Stackwalks,
    ProviderEventCountSummary ProviderEvents,
    DriverModuleSummary Drivers,
    IReadOnlyList<string> Limitations);

public sealed record TraceSystemConfiguration(
    string? MachineName,
    string? OsName,
    string? OsBuild,
    string? OsVersion,
    int? ProcessorCount,
    int? CpuSpeedMhz,
    string? CpuModel,
    string? BootTimeUtc,
    int? UtcOffsetMinutes,
    string MetadataSource);

public sealed record TraceStackwalkSummary(
    [property: Description("True only when explicit StackWalk records remain as materialized TraceLog events. Event-attached stacks are reported by HasUsableEventStacks.")]
    bool HasStackWalkEvents,
    long StackWalkEventCount,
    long EventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1], despite the Pct suffix. Use EventStackCoveragePercent for a true [0,100] percentage.")]
    double? EventStackCoveragePct,
    bool HasExplicitStackWalkEvents = false,
    bool HasUsableEventStacks = false,
    [property: Description("Percentage of TraceLog-materialized events carrying attached stacks, in [0,100].")]
    double? EventStackCoveragePercent = null);

public sealed record StackProbeResponse(
    string Path,
    [property: Description("Count of TraceLog/ETLX-materialized logical events, not raw ETW records.")]
    long EventCount,
    long ExplicitStackWalkEvents,
    long EventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use EventStackCoveragePercent.")]
    double? EventStackCoveragePct,
    long CSwitchEvents,
    long CSwitchEventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use CSwitchStackCoveragePercent.")]
    double? CSwitchStackCoveragePct,
    long ReadyThreadEvents,
    long ReadyThreadEventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use ReadyThreadStackCoveragePercent.")]
    double? ReadyThreadStackCoveragePct,
    bool HasExplicitStackWalkEvents,
    bool HasUsableEventStacks,
    IReadOnlyList<string> Notes,
    string EventCountRepresentation = "tracelog_etlx_materialized_logical_events",
    long? RawEtwRecordCount = null,
    string RawEtwRecordCountState = "not_measured",
    string ParserCoverageState = "not_computed",
    double? EventStackCoveragePercent = null,
    double? CSwitchStackCoveragePercent = null,
    double? ReadyThreadStackCoveragePercent = null,
    [property: Description("CSwitchEventsWithCallStacks measures the ordinary CSwitch event CallStackIndex. It is not the switch-out BlockingStack used by wait_top_stacks, wait_analysis, and inspect_trace's cswitch domain, so the two coverage values can differ.")]
    string CSwitchStackSemantics = "event_call_stack_not_switch_out_blocking_stack");

public sealed record ProviderEventCountSummary(
    int TotalProviderCount,
    [property: Description("TraceLog/ETLX-materialized logical events grouped by materialized ProviderName; not a raw ETW record count.")]
    long TotalEventCount,
    long OtherEventCount,
    IReadOnlyList<ProviderEventCount> TopProviders,
    string CountRepresentation = "tracelog_etlx_materialized_logical_events_grouped_by_provider",
    long? RawEtwRecordCount = null,
    string RawEtwRecordCountState = "not_measured",
    string ParserCoverageState = "not_computed");

public sealed record ProviderEventCount(
    string Provider,
    long EventCount,
    long EventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1], despite the Pct suffix. Use StackCoveragePercent for [0,100].")]
    double? StackCoveragePct,
    [property: Description("Percentage of this provider's TraceLog-materialized events carrying attached stacks, in [0,100].")]
    double? StackCoveragePercent = null);

public sealed record DriverModuleSummary(
    int TotalDriverModuleCount,
    IReadOnlyList<TraceDriverModule> TopDrivers);

public sealed record TraceDriverModule(
    string Module,
    string Path,
    long ImageSizeBytes,
    string? FileVersion,
    string? ProductName,
    string? ProductVersion);

public sealed record InspectSymbolQuality(
    string? NtSymbolPath,
    string CacheDir,
    int ModuleCount,
    [property: Description("Deprecated. Module metadata cannot prove frame resolution; this field is always null. Use ModulesWithPdbName and ModulesWithCompletePdbIdentity.")]
    int? ResolvedModuleCount,
    [property: Description("Deprecated. Module metadata cannot prove frame resolution; this field is always null. Use the explicit PDB identity coverage fields.")]
    double? ModuleResolutionRate,
    [property: Description("Deprecated. Missing PDB metadata does not prove stack-frame lookup failure; this compatibility field is always empty. Use TopModulesMissingPdbName for the metadata-only list and stack-tool SymbolStats for observed frame-name resolution.")]
    IReadOnlyList<InspectUnresolvedModule> TopUnresolvedModules,
    IReadOnlyList<SymbolRecommendation> Recommendations,
    int ModulesWithPdbName = 0,
    double? ModulesWithPdbNameRate = null,
    int ModulesWithCompletePdbIdentity = 0,
    double? CompletePdbIdentityRate = null,
    [property: Description("inspect_trace does not execute stack frame lookup; actual observed frame-name resolution is reported by stack analysis tools.")]
    string FrameResolutionMeasurementState = "not_measured",
    SymbolStats? FrameResolution = null)
{
    [Description("Modules whose trace metadata has no PdbName, ordered by module name and capped at 20. This is PDB identity metadata only and does not indicate whether stack-frame lookup succeeded or failed.")]
    public IReadOnlyList<InspectModuleMissingPdbName> TopModulesMissingPdbName { get; init; } = Array.Empty<InspectModuleMissingPdbName>();
}

public sealed record InspectUnresolvedModule(
    string Module,
    string Hint);

public sealed record InspectModuleMissingPdbName(
    string Module,
    string Hint);

/// <summary>
/// Trace-quality warning. Severity is global to the trace, not relative to a specific
/// analysis goal; an "info" warning can still block a goal-specific investigation.
/// </summary>
public sealed record TraceQualityWarning(
    string Code,
    string Severity,
    string Message,
    string NextStep,
    IReadOnlyList<string> AffectedTools,
    IReadOnlyList<string> DegradedTools);

public sealed record ToolRecommendation(
    string ToolName,
    string Reason,
    IReadOnlyList<string> Goals);

public sealed record DomainStackCoverage(
    string Domain,
    long TotalEventCount,
    long StackedEventCount,
    double? StackCoveragePct,
    [property: Description("One of no_events, no_stacks, partial, or full.")]
    string CoverageState,
    long TotalMetric,
    long StackedMetric,
    double? MetricStackCoveragePct,
    [property: Description("Contract rule: the ?!? label, whenever present, is synthetic unknown evidence rather than a captured call chain. Use ContainsSyntheticUnknown to determine whether this result actually contains it.")]
    bool UnknownStackFrameIsSynthetic = true,
    [property: Description("Unit accumulated by TotalMetric and StackedMetric, for example count, bytes, or us.")]
    string MetricName = "count",
    [property: Description("True only when this response actually contains one or more unstacked events represented by the synthetic unknown frame.")]
    bool ContainsSyntheticUnknown = false,
    [property: Description("The synthetic frame label when ContainsSyntheticUnknown is true; otherwise null.")]
    string? SyntheticUnknownFrame = null,
    [property: Description("TotalMetric and StackedMetric are accumulated with checked 64-bit integer arithmetic before any float call-tree projection.")]
    string MetricAccounting = "exact_long",
    [property: Description("Machine-readable identity of the stack source measured by this coverage. In particular, switch_out_blocking_stack is distinct from an ordinary CSwitch event CallStackIndex.")]
    string StackSemantics = "event_call_stack");

// What kernel keywords were active in the capture, inferred from per-event-name counts in
// the trace metadata. Lets a client know upfront whether dependent tools will return data:
// if HasFileIo=false, file_io_top_files / file_io_top_stacks will return empty rows even on
// a busy trace, and the user should re-capture with the FileIO keyword enabled.
//
// All flags are conservative: true iff at least one matching event was observed in the trace.
// A false flag means only that the event class was not observed. It does not prove the
// corresponding capture keyword was disabled.
public sealed record TraceCapabilities(
    bool HasCpuSamples,
    bool HasCSwitch,
    bool HasFileIo,
    bool HasDiskIo,
    bool HasImageLoad,
    bool HasHardFaults,
    [property: Description("Compatibility aggregate: true when explicit StackWalk events or any attached event stacks were observed anywhere in the trace. Never use this as proof that a specific event domain has stacks; inspect StackCoverageByDomain.")]
    bool HasStackWalks,
    bool HasVirtualAlloc,
    bool HasNetIo,
    bool HasNetConnections,
    bool HasRegistry,
    bool HasReadyThread,
    bool HasInterrupt,
    bool HasAlpc,
    bool HasThreadEvents,
    bool HasClrGc,
    bool HasClrJit,
    bool HasClrAlloc,
    bool HasClrException,
    bool HasClrContention,
    bool HasNtHeap,
    bool HasMemoryProcessInfo,
    bool HasHandleEvents,
    bool HasPoolEvents,
    [property: Description("True when at least one CSwitch event carried a blocking stack; see the cswitch entry in StackCoverageByDomain for counts and partiality.")]
    bool HasCSwitchStacks = false,
    bool HasReadyThreadStacks = false,
    bool HasInterruptStacks = false,
    [property: Description("True only when explicit StackWalk records remained as materialized events.")]
    bool HasExplicitStackWalkEvents = false,
    long ExplicitStackWalkEventCount = 0,
    [property: Description("True when at least one event carried an attached stack, independently of explicit StackWalk records.")]
    bool HasAttachedEventStacks = false,
    [property: Description("Whole-trace coverage for every detector-supported target event domain (including CPU, scheduler, IO, memory, CLR, network, registry, ALPC, interrupt, heap, and generic events). Typed entries are computed only from their named domain. generic_event is a trace-wide aggregate; each generic provider/filter query returns its authoritative query-specific coverage.")]
    IReadOnlyDictionary<string, DomainStackCoverage>? StackCoverageByDomain = null);

public sealed record ProcessRow(
    int Pid,
    int ParentPid,
    string Name,
    long StartUs,
    long EndUs,
    long WallUs,
    long CpuUs,
    double? WaitRatio,
    int ImageLoadCount,
    // True when the process was alive before the trace started AND survived past the end:
    // its WallUs ≈ trace duration, and any tiny CpuUs makes WaitRatio numerically huge but
    // semantically meaningless (denominator saturation). Clients sorting by WaitRatio should
    // skip these, otherwise short-lived but actually-blocked processes get buried.
    bool TraceResident);

public sealed record ProcessListResponse(
    IReadOnlyList<ProcessRow> Rows,
    int IdleProcessesHidden,
    int TotalCount);

// Per-child timing for one fork. Gap-from-previous lets clients spot burst patterns (e.g.,
// 23 children spawned in 56 seconds = 2.4s avg gap — was that uniform or clustered?), and
// FirstImageLoadOffsetUs measures the observed interval from the kernel ProcessStart event
// to the first mapped DLL. The interval can contain callbacks, scanning, suspension,
// scheduling, and other work; it does not identify which mechanism consumed the time.
public sealed record ChildSpawnTiming(
    int Pid,
    string Name,
    long StartTimeUs,
    long? FirstImageLoadOffsetUs,
    int ImageLoadCount,
    long? GapFromPreviousSpawnUs);

public sealed record ProcessCreateTimingResponse(
    int ParentPid,
    string? ParentName,
    int SpawnCount,
    long? FirstSpawnTimeUs,
    long? LastSpawnTimeUs,
    long? AvgSpawnGapUs,
    long? MedianKernelGapUs,
    long? P95KernelGapUs,
    long? MaxKernelGapUs,
    IReadOnlyList<ChildSpawnTiming> Children,
    IReadOnlyList<string> Warnings,
    long? ParentProcessStartUs = null,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for a resolved parent lifetime, or unresolved when the requested parent lifetime does not exist. Reused-PID ambiguity is rejected unless processStartUs selects one lifetime.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of child ProcessStart records matched to the selected parent lifetime.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record CpuFunctionRow(
    string Function,
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record SymbolStats(
    [property: Description("Compatibility alias of UniqueResolvedCodeFrameCount. Synthetic and pseudo frames are excluded.")]
    long Resolved,
    [property: Description("Compatibility alias of UniqueUnresolvedCodeFrameCount. Synthetic and pseudo frames are excluded.")]
    long Unresolved,
    [property: Description("Compatibility alias of ObservedUniqueCodeFrameNameResolutionRate; null when no actual code frames were observed.")]
    double? ResolutionRate,
    IReadOnlyList<UnresolvedModule> TopUnresolvedModules,
    long UniqueCodeFrameCount = 0,
    long UniqueResolvedCodeFrameCount = 0,
    long UniqueUnresolvedCodeFrameCount = 0,
    [property: Description("Observed resolved-name share across unique reachable code-frame indexes after the requested lookup policy; null when there are no code frames. This is a frame-name heuristic, not a per-PDB lookup success rate.")]
    double? ObservedUniqueCodeFrameNameResolutionRate = null,
    [property: Description("Sum of each sample's metric once per reachable code-frame occurrence. A sample with metric 10 and three code frames contributes 30; this is not the source event metric total.")]
    long TotalCodeFrameMetric = 0,
    long ResolvedCodeFrameMetric = 0,
    long UnresolvedCodeFrameMetric = 0,
    [property: Description("Resolved-name share weighted by code-frame occurrences and the source sample metric; null when total code-frame metric is zero.")]
    double? ObservedMetricWeightedCodeFrameNameResolutionRate = null,
    [property: Description("Reachable non-code frame identities excluded via GetFrameCodeAddress == Invalid, including synthetic ?!? and process/thread/activity pseudo frames.")]
    long ExcludedSyntheticOrPseudoUniqueFrames = 0,
    long ExcludedSyntheticOrPseudoFrameMetric = 0,
    string MetricName = "count",
    [property: Description("One of skipped, executed, failed, or unknown. Executed means LookupWarmSymbols returned; it does not mean every module was attempted.")]
    string LookupState = "unknown",
    int WarmSymbolThreshold = 50,
    string ResolutionEvidence = "post_lookup_frame_name_heuristic",
    string? LookupFailure = null,
    string MetricAccounting = "exact_long");

public sealed record UnresolvedModule(string Module, long FrameCount);

public sealed record CpuTopFunctionsResponse(
    IReadOnlyList<CpuFunctionRow> Rows,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    long TotalSamples = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    bool HasSampledProfileStacks = false,
    string SymbolResolutionState = "not_applicable",
    DomainStackCoverage? StackCoverage = null,
    [property: Description("One of all_processes, single_process, pid_aggregate, or unresolved. Batch responses always populate this from the requested process selector.")]
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    [property: Description("Stable reason for an empty result. no_events_in_scope does not imply that CPU sampling was disabled.")]
    string? NoDataReason = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null);

public sealed record CpuCoreBucket(
    int Core,
    long CpuUs,
    double CpuPct);

public sealed record CpuPreciseThreadRow(
    int Pid,
    string ProcessName,
    int Tid,
    long CpuUs,
    long ContextSwitches,
    long ReadyCount,
    long ReadyLatencyUs,
    double? AvgReadyLatencyUs,
    long? MaxReadyLatencyUs,
    int? PrimaryCore,
    IReadOnlyList<CpuCoreBucket> TopCores,
    long QuantumEndSwitches,
    long PreemptedSwitches,
    [property: Description("Trace-relative process start; combine with Pid to identify the process lifetime. Rows are never merged across this value.")]
    long ProcessStartUs = 0,
    [property: Description("Generation of this TID within the selected process lifetime. Rows are never merged across generations.")]
    long ThreadGeneration = 1);

public sealed record CpuPreciseResponse(
    IReadOnlyList<CpuPreciseThreadRow> Rows,
    long TotalCpuUs,
    [property: Description("Number of CSwitch events whose old or new thread matched the requested process/thread/window scope; this is the scoped count, not the trace-wide count.")]
    long TotalContextSwitches,
    long TotalReadyCount,
    long TotalReadyLatencyUs,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    [property: Description("True when at least one CSwitch event matched the requested process/thread/window scope. This is not a trace-wide capability flag; use TraceHasContextSwitches for that.")]
    bool HasContextSwitches = false,
    bool HasSampledProfileStacks = false,
    string SymbolResolutionState = "not_applicable",
    [property: Description("One of all_processes, pid_aggregate, or single_process. pid_aggregate totals cover all included PID lifetimes, while rows remain instance-separated.")]
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of CSwitch source events whose old or new thread matched the requested scope and half-open window.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("True when the analyzed trace contains at least one CSwitch event, regardless of the requested process/thread/window scope; null when trace-wide events were not scanned because scope resolution failed.")]
    bool? TraceHasContextSwitches = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null);

// Caller/callee neighbor of a focus frame. Same shape across all stack sources (CPU samples,
// blocked μs, hard-fault bytes, etc.); the parent response carries `MetricName` so consumers
// know what "Metric" is in (samples / microseconds / bytes / loads / ops).
public sealed record CallerCalleeNode(
    string Function,
    long ExclusiveMetric,
    long InclusiveMetric,
    double ExclusivePct,
    double InclusivePct);

// Drill-down view: for a given focus frame, list the frames that call into it (Callers) and
// the frames it calls out to (Callees), each ranked by inclusive metric. PerfView equivalent:
// "Caller / Callee" tabs of any stack-source view.
//
// Pcts are normalized over the FILTERED total (whatever pid/window the caller passed). When
// the focus frame doesn't appear in the data at all, Callers and Callees are both empty and
// FocusInclusiveMetric == 0 — distinguishable from "frame had zero samples after filtering"
// by the warning list.
public sealed record CallerCalleeResponse(
    string FocusFunction,
    long FocusExclusiveMetric,
    long FocusInclusiveMetric,
    double FocusExclusivePct,
    double FocusInclusivePct,
    string MetricName,
    IReadOnlyList<CallerCalleeNode> Callers,
    IReadOnlyList<CallerCalleeNode> Callees,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    long SourceTotalMetric = 0,
    int UnmatchedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    bool HasContextSwitches = false,
    bool HasContextSwitchBlockingStacks = false,
    bool HasSampledProfileStacks = false,
    string SymbolResolutionState = "not_applicable",
    DomainStackCoverage? StackCoverage = null,
    [property: Description("Precision of Focus*Metric and caller/callee row metrics: exact_integer_count for unit-count samples, otherwise float32_per_sample_approximate.")]
    string MetricPrecision = "float32_per_sample_approximate",
    [property: Description("Machine-readable accounting of per-frame metrics after projection through TraceEvent's float StackSourceSample.Metric.")]
    string RowMetricAccounting = "float32_per_sample_approximate",
    [property: Description("SourceTotalMetric and DomainStackCoverage totals are accumulated independently with checked 64-bit integer arithmetic.")]
    string ExactTotalAccounting = "exact_long",
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null);

public sealed record CpuBatchScopeResult(
    int Pid,
    long? RequestedProcessStartUs,
    [property: Description("One of completed, completed_no_samples, scope_not_found, budget_skipped, or analysis_failed.")]
    string ResultStatus,
    [property: Description("Process selector status: ok or scope_not_found.")]
    string ScopeStatus,
    [property: Description("One of single_process, pid_aggregate, or unresolved.")]
    string ScopeMode,
    ProcessInstanceKey? SelectedProcess,
    bool PidReuseObserved,
    IReadOnlyList<ProcessInstanceKey> IncludedProcesses,
    long MatchedSampleCount,
    [property: Description("observed when this selector matched CPU samples; otherwise unknown. unknown does not imply that the CPU keyword was disabled.")]
    string CapabilityStatus,
    string? NoDataReason = null);

public sealed record CpuTopFunctionsBatchResponse(
    IReadOnlyDictionary<int, CpuTopFunctionsResponse> PerPid,
    IReadOnlyList<string> Warnings,
    bool Partial = false,
    IReadOnlyList<int>? SkippedPids = null,
    int RequestedPidCount = 0,
    int CompletedPidCount = 0,
    [property: Description("PIDs whose shared event scan completed and whose per-PID projection completed. Includes PidsWithNoSamples.")]
    IReadOnlyList<int>? CompletedPids = null,
    [property: Description("Requested PIDs/process instances that did not exist in the requested half-open window.")]
    IReadOnlyList<int>? PidsNotFound = null,
    [property: Description("Completed process selectors that matched zero sampled CPU events. This does not imply that CPU sampling was disabled.")]
    IReadOnlyList<int>? PidsWithNoSamples = null,
    [property: Description("Per-selector scope and completion metadata; use RequestedProcessStartUs to retain process-instance precision.")]
    IReadOnlyList<CpuBatchScopeResult>? ScopeResults = null);

public sealed record FileIoRow(
    string File,
    long ReadBytes,
    long ReadCount,
    long WriteBytes,
    long WriteCount);

public sealed record FileIoResponse(
    IReadOnlyList<FileIoRow> Rows,
    [property: Description("Exact selected process lifetime for single_process scope; null for aggregates, all-process queries, or unresolved selectors.")]
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("One of all_processes, single_process, pid_aggregate, or unresolved.")]
    string ScopeMode = "all_processes",
    [property: Description("True when the selected PID (or any PID for all_processes) has multiple lifetimes anywhere in the trace, even if the requested window includes only one.")]
    bool PidReuseObserved = false,
    [property: Description("Known process lifetime keys included by the selector and half-open window.")]
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Process selector status: ok or scope_not_found.")]
    string ScopeStatus = "ok",
    [property: Description("Scoped source-event status: observed only when this resolved selector matched FileIO events; not_observed only when the event class was absent trace-wide; otherwise unknown. This never proves a capture keyword was disabled.")]
    string CapabilityStatus = "unknown",
    [property: Description("Number of FileIO Read/Write events matched before top-N file aggregation.")]
    long MatchedEventCount = 0,
    [property: Description("Stable reason for an empty result: scope_not_found, no_events_in_scope, event_class_not_observed, or null when data matched.")]
    string? NoDataReason = null,
    IReadOnlyList<string>? Warnings = null);

// Top-N call-tree frames ranked by file-IO bytes. Pairs with FileIoResponse (per-file
// bucket): the per-file view says "which files saw the most read/write traffic", this
// per-stack view says "which call chain is doing all that IO" — useful for finding code
// paths that hammer the file system in tight loops.
public sealed record FileIoStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveOpCount,
    long InclusiveOpCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record FileIoStacksResponse(
    IReadOnlyList<FileIoStackRow> Rows,
    long TotalBytes,
    long TotalOpCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by physical disk-IO bytes. Different layer from FileIoStack:
// disk-IO events fire only when the IO actually hits a physical disk (not cache-served), so
// rows here represent code paths that drove genuine disk activity. Diff against
// FileIoStacksResponse to find cache-served reads (present in file IO, absent in disk IO).
public sealed record DiskIoStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveOpCount,
    long InclusiveOpCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record DiskIoStacksResponse(
    IReadOnlyList<DiskIoStackRow> Rows,
    long TotalBytes,
    long TotalOpCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Per-file aggregate of MemoryHardFault events: bytes paged in from disk for one file.  Most
// hard faults come from memory-mapped files (DLLs, data files, network-share content) being
// touched for the first time; some also come from paged-out heap/stack pages and the page file.
public sealed record HardFaultFileRow(
    string File,
    long PageInBytes,
    long PageInCount,
    long MaxLatencyUs,
    long MaxLatencyTimeUs);

public sealed record HardFaultByFileResponse(
    IReadOnlyList<HardFaultFileRow> Rows,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames attached to hard-fault events, ranked by PAGING-IN BYTES.
// HardFaultByFileResponse identifies the backing file mapping; this view provides the
// stack captured with the event. It supports hypotheses about eager/lazy access or
// concurrent scanning but does not establish the higher-level cause.
public sealed record HardFaultStackRow(
    string Function,
    long ExclusivePageInBytes,
    long InclusivePageInBytes,
    long ExclusiveFaultCount,
    long InclusiveFaultCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record HardFaultStacksResponse(
    IReadOnlyList<HardFaultStackRow> Rows,
    long TotalPageInBytes,
    long TotalFaultCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record MarkerRow(
    long TimeUs,
    string Provider,
    string EventName,
    string ProcessName,
    int ThreadId,
    IReadOnlyDictionary<string, string> Fields);

public sealed record MarkerCountRow(string Key, long Count);

public sealed record MarkerSearchResponse(
    string Mode,
    long TotalMatched,
    IReadOnlyList<MarkerCountRow>? Counts,
    IReadOnlyList<MarkerRow>? Rows,
    string ScopeStatus = "ok",
    [property: Description("observed when the whole-trace name/task search matched at least one materialized event; not_observed means no name match, not that a provider or keyword was disabled.")]
    string CapabilityStatus = "unknown",
    [property: Description("Exact number of materialized events whose EventName or TaskName matched the query before top-N truncation.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    IReadOnlyList<string>? Warnings = null);

public sealed record SecurityScanProviderRow(
    string Source,
    string ProviderName,
    long EventCount,
    IReadOnlyList<string> EventNames,
    string? EvidenceKind = null,
    string? Provenance = null,
    string? Confidence = null);

public sealed record ModuleSymbolStatus(
    string Module,
    [property: Description("Deprecated. diagnose_symbols does not build a stack source, so frame count is unavailable and this field is null.")]
    long? FrameCount,
    [property: Description("Deprecated. Local PDB readiness is not actual frame resolution, so this field is null. Use LocalPdbReady and run a stack tool for observed frame resolution.")]
    bool? Resolved,
    string Suggestion,
    string? FilePath = null,
    string? ExpectedPdbName = null,
    string? PdbSignature = null,
    int? PdbAge = null,
    string? BinaryFormat = null,
    string LookupStatus = "unknown",
    string? FailureReason = null,
    IReadOnlyList<string>? LocalSymbolCandidates = null,
    bool HasPdbName = false,
    bool HasCompletePdbIdentity = false,
    bool LocalPdbReady = false,
    string FrameResolutionState = "not_measured",
    string EvidenceScope = "module_metadata_and_local_candidate_probe");

public sealed record NativeSymbolSupportStatus(
    string Architecture,
    bool Msdia140Present,
    bool KernelTraceControlPresent,
    string Status,
    IReadOnlyList<NativeDependencyStatus> Dependencies,
    string? Suggestion);

public sealed record NativeDependencyStatus(
    string Name,
    string ExpectedPath,
    bool Present);

public sealed record DiagnoseSymbolsResponse(
    [property: Description("Compatibility alias of ConfiguredSymbolPath. Query-local trace-directory additions appear only in EffectiveSymbolPath.")]
    string CurrentSymbolPath,
    string CacheDir,
    IReadOnlyList<ModuleSymbolStatus> Modules,
    IReadOnlyList<string> Suggestions,
    string TraceDirectory,
    [property: Description("Compatibility alias of TraceDirectoryInEffectiveSymbolPath.")]
    bool TraceDirectoryInSymbolPath,
    NativeSymbolSupportStatus NativeSymbolSupport,
    string ConfiguredSymbolPath = "<unset>",
    string EffectiveSymbolPath = "",
    bool TraceDirectoryInConfiguredSymbolPath = false,
    bool TraceDirectoryInEffectiveSymbolPath = false,
    string FrameResolutionMeasurementState = "not_measured");

public sealed record WaitReasonBucket(string Reason, long BlockedUs, long Count);

public sealed record WaitAnalysisRow(
    int Pid,
    string ProcessName,
    int Tid,
    long CpuUs,
    long BlockedUs,
    double? WaitRatio,
    long ContextSwitches,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    long ProcessStartUs = 0,
    long ThreadGeneration = 0);

public sealed record WaitAnalysisResponse(
    IReadOnlyList<WaitAnalysisRow> Rows,
    [property: Description("Deprecated compatibility alias for WindowCSwitchesAllThreads. It is not scoped to the selected PID or thread.")]
    long TotalCSwitches,
    IReadOnlyList<string> Warnings,
    long TotalBlockedUs = 0,
    int UnmatchedBlockedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    bool HasContextSwitches = false,
    [property: Description("True only when at least one selected switch-out event in the requested window has a blocking stack.")]
    bool HasContextSwitchBlockingStacks = false,
    string SymbolResolutionState = "not_applicable",
    long WindowCSwitchesAllThreads = 0,
    long ScopedCSwitches = 0,
    long ScopedStackedSwitches = 0,
    double? ScopedStackCoveragePct = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null);

// Top-N call-tree frames ranked by blocked-time microseconds.
//
// ExclusiveBlockedUs / InclusiveBlockedUs sum the wait durations attributed to this frame
// (the blocked thread's switch-out stack at CSwitch). PerfView convention: pct fields are normalized
// over the FILTERED set (whatever pid/window the caller asked for); *PctOfTrace are over
// the whole trace and are populated only when a filter was applied.
public sealed record WaitStackRow(
    string Function,
    long ExclusiveBlockedUs,
    long InclusiveBlockedUs,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

// `When` carries a time-bucketed histogram of the analysis metric across the filter window —
// populated only when the caller passes whenBuckets > 0. Same pattern repeats on every
// stack-source response below.
public sealed record WaitTopStacksResponse(
    IReadOnlyList<WaitStackRow> Rows,
    long TotalBlockedUs,
    long SampleCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    int UnmatchedBlockedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    bool HasContextSwitches = false,
    bool HasContextSwitchBlockingStacks = false,
    string SymbolResolutionState = "not_applicable",
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null);

// Provenance for composite tools. Every evidence item should point at one of these calls
// so callers can replay the public query shape, or see explicitly when the composite used
// an internal-only aggregation that cannot be replayed through the public tool contract.
public sealed record CompositeToolCall(
    string CallId,
    string ToolName,
    int? Pid,
    int? AwakenedPid,
    long? StartUs,
    long? EndUs,
    [property: Description("Replayable public MCP top argument; null for audit-only internal aggregation.")]
    int? Top,
    bool? CompactStacks,
    bool? SummaryOnly,
    int? WhenBuckets,
    IReadOnlyList<string> Warnings,
    int? EffectiveTop = null,
    [property: Description("False = audit-only internal aggregation; do not replay public tool expecting identical output. See InternalNote.")]
    bool Replayable = true,
    [property: Description("Internal-only top when Replayable=false; do not pass to public tool.")]
    int? InternalTop = null,
    [property: Description("Why Replayable=false.")]
    string? InternalNote = null,
    [property: Description("Optional public orderBy argument when replaying tools that support sorted views.")]
    string? OrderBy = null,
    long? ProcessStartUs = null,
    long? ParentStartUs = null,
    long? ParentEndUs = null,
    [property: Description("Exact trace-relative start paired with AwakenedPid when replaying ReadyThread tools. Distinct from ProcessStartUs, which pairs with Pid.")]
    long? AwakenedProcessStartUs = null,
    [property: Description("Exact trace-relative target-process start for tools whose public selector is targetProcessStartUs, such as security_scan_analysis.")]
    long? TargetProcessStartUs = null);

// Evidence metric values are raw metric amounts in the declared Unit (for example blocked
// microseconds or ready-thread event count). Do not compare evidence rows across different
// MetricName/Unit pairs; use them only within the same metric family.
public sealed record CompositeEvidence(
    [property: Description("Stable evidence id within this composite response.")]
    string EvidenceId,
    [property: Description("CompositeToolCall.CallId that produced this evidence.")]
    string CallId,
    [property: Description("Closed set: process_wait_summary | wait_reason | wait_stack_summary | ready_thread_stack_summary.")]
    string EvidenceType,
    int? Pid,
    int? Tid,
    string? ProcessName,
    string Label,
    [property: Description("Metric family; compare only within the same MetricName/Unit.")]
    string MetricName,
    [property: Description("Raw amount in Unit, not severity; compare only within the same MetricName/Unit.")]
    long MetricValue,
    [property: Description("Unit for MetricValue, for example us or events.")]
    string Unit,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    [property: Description("Per-frame metrics for stack evidence. Empty for process and wait-reason summaries.")]
    IReadOnlyList<FrameMetric> Frames,
    long? ProcessStartUs = null);

public sealed record FrameMetric(
    string Function,
    [property: Description("Exclusive metric for this frame in Unit.")]
    long ExclusiveMetric,
    [property: Description("Inclusive metric for this frame in Unit.")]
    long InclusiveMetric,
    string Unit);

// Not-concluded metric values describe why a branch was skipped or degraded. MetricValue is
// the raw observed value when one exists (for example blocked microseconds); ObservedPct is
// the normalized ratio to compare against ThresholdPct. Unit applies to MetricValue, not to
// ObservedPct/ThresholdPct.
public sealed record CompositeNotConcluded(
    string Code,
    string Reason,
    int? Pid,
    string? BlockingCapability,
    string? RelatedCallId,
    [property: Description("Metric family for MetricValue when the skipped/degraded branch has an observed amount.")]
    string? MetricName = null,
    [property: Description("Raw amount; if pct fields exist, compare ObservedPct with ThresholdPct instead.")]
    double? MetricValue = null,
    [property: Description("Unit for MetricValue. Does not apply to ObservedPct or ThresholdPct.")]
    string? Unit = null,
    [property: Description("Normalized observed ratio in [0,1]. Compare this with ThresholdPct when both are present.")]
    double? ObservedPct = null,
    [property: Description("Normalized decision threshold in [0,1]. Compare ObservedPct against this value.")]
    double? ThresholdPct = null,
    long? ProcessStartUs = null,
    string? EvidenceId = null,
    [property: Description("Child process-scope status when this item represents an analyzer result.")]
    string? ScopeStatus = null,
    [property: Description("Child event-family status when this item represents an analyzer result.")]
    string? CapabilityStatus = null,
    [property: Description("Stable child no-data reason such as scope_not_found, event_class_not_observed, or no_events_in_scope.")]
    string? NoDataReason = null);

public sealed record CompositeNextTool(
    string ToolName,
    string Reason,
    int? Pid,
    int? AwakenedPid,
    long? StartUs,
    long? EndUs,
    bool? CompactStacks,
    bool? SummaryOnly,
    [property: Description("Hypothesis tested by this optional follow-up; not an ordered checklist.")]
    string? TestsHypothesis = null,
    [property: Description("Exact trace-relative process start to preserve process-instance scope when replaying a PID-targeted follow-up.")]
    long? ProcessStartUs = null,
    [property: Description("Exact trace-relative start paired with AwakenedPid when replaying a ReadyThread follow-up.")]
    long? AwakenedProcessStartUs = null);

// Time-bucketed metric histogram. Length of Buckets == requested bucket count; bucket i covers
// [StartUs + i*BucketWidthUs, min(EndUs, StartUs + (i+1)*BucketWidthUs)). Duration metrics
// are split by exact overlap, so their bucket sum equals the accounted window total.
public sealed record TimeHistogram(
    long StartUs,
    long EndUs,
    long BucketWidthUs,
    long[] Buckets);

public sealed record ImageLoadRow(
    long TimeUs,
    long TimeFromProcessStartUs,
    string FileName,
    long ImageSize,
    // Microseconds between this load and the previous one in chronological order. Null for
    // the first load (no prior). A long gap establishes only that no ImageLoad event for this
    // process lifetime was observed in the interval; it does not identify the intervening work.
    long? GapFromPrevUs);

// Top-N call-tree frames ranked by ImageLoad-event count — answers "what call chain is
// loading the most DLLs". Different question from ImageLoadTimingResponse: that one is a
// chronological list of which DLLs loaded when; this one rolls every load event up to the
// attached to the event (LoadLibrary / CoCreateInstance / DllMain-cascade / etc. may
// appear when captured). Association does not establish the higher-level cause.
public sealed record ImageLoadStackRow(
    string Function,
    long ExclusiveLoads,
    long InclusiveLoads,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record ImageLoadStacksResponse(
    IReadOnlyList<ImageLoadStackRow> Rows,
    long TotalLoads,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record ImageLoadTimingResponse(
    int Pid,
    string ProcessName,
    long? ProcessStartUs,
    int TotalImageLoads,
    // Microseconds between ProcessStart and the first observed ImageLoad event. The interval
    // may contain callbacks, scanning, sandbox setup, suspension, scheduling, or other work;
    // this field alone cannot distinguish them. Null when the process has no ImageLoad events.
    long? FirstLoadOffsetUs,
    // Largest GapFromPrevUs across all loads — quick "is there one outlier interval" signal
    // before scanning the full sequence. Null when fewer than 2 loads exist.
    long? MaxGapUs,
    IReadOnlyList<ImageLoadRow> Loads,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for a resolved lifecycle, or unresolved when it was not found. Reused-PID ambiguity is rejected unless processStartUs selects one lifetime.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ImageLoad source events matched to the selected process lifetime.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record ImageLoadTopGapsResponse(
    int Pid,
    string ProcessName,
    long? ProcessStartUs,
    int TotalImageLoads,
    long? FirstLoadOffsetUs,
    IReadOnlyList<ImageLoadRow> TopGaps,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for a resolved lifecycle, or unresolved when it was not found. Reused-PID ambiguity is rejected unless processStartUs selects one lifetime.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ImageLoad source events matched to the selected process lifetime. TopGaps may be truncated and excludes the first load.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record HighWaitCandidate(
    int Pid,
    string ProcessName,
    long TotalCpuUs,
    long TotalBlockedUs,
    double? WaitRatio,
    long ContextSwitches,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    string WaitAnalysisCallId,
    string? WaitStacksCallId,
    string? ReadyThreadCallId,
    [property: Description("Trace-relative process start. Combine with Pid; candidates from reused PID lifetimes are never merged.")]
    long ProcessStartUs = 0);

public sealed record DiagnoseHighWaitResponse(
    [property: Description("Ordered by total blocked microseconds, not impact, severity, or causality.")]
    IReadOnlyList<HighWaitCandidate> Candidates,
    IReadOnlyList<CompositeEvidence> Evidence,
    IReadOnlyList<CompositeNotConcluded> NotConcluded,
    IReadOnlyList<CompositeNextTool> NextTools,
    IReadOnlyList<CompositeToolCall> ExecutedToolCalls,
    IReadOnlyList<string> Warnings);

public sealed record WindowEvidenceRow(
    [property: Description("Evidence class such as hard_fault_max_latency, file_io_top_file, memory_pressure, security_scan, or wait_summary.")]
    string EvidenceType,
    string Label,
    [property: Description("Metric family; compare only within the same MetricName/Unit.")]
    string MetricName,
    [property: Description("Raw amount in Unit, not a severity verdict.")]
    long MetricValue,
    [property: Description("Unit for MetricValue, for example bytes, us, events, or count.")]
    string Unit,
    int? Pid,
    string? ProcessName,
    string? File,
    [property: Description("Microseconds since trace start for point-in-time evidence when known.")]
    long? TimeUs,
    IReadOnlyList<string> Details,
    [property: Description("Machine-readable evidence kind when the source analyzer exposes one.")]
    string? EvidenceKind = null,
    [property: Description("Machine-readable provenance for the evidence classification.")]
    string? Provenance = null,
    [property: Description("Evidence confidence such as high or low; not a severity score.")]
    string? Confidence = null,
    [property: Description("Exact process start paired with Pid for single_process evidence; null for aggregate or window-global evidence.")]
    long? ProcessStartUs = null,
    [property: Description("Process scope used for this row, or window_global for system-wide evidence.")]
    string ScopeMode = "all_processes",
    [property: Description("Evidence boundary: process_scope or window_global.")]
    string EvidenceScope = "process_scope");

public sealed record DiagnoseWindowResponse(
    long WindowStartUs,
    long WindowEndUs,
    long DurationUs,
    int? Pid,
    IReadOnlyList<HardFaultFileRow> HardFaultsByBytes,
    IReadOnlyList<HardFaultFileRow> HardFaultsByMaxLatency,
    IReadOnlyList<FileIoRow> FileIoTopFiles,
    MemoryPressureSummary? Pressure,
    IReadOnlyList<SecurityScanTargetRow> SecurityScanTargets,
    IReadOnlyList<SecurityScanRequestRow> SlowScans,
    long SecurityMatchedEventCount,
    long SecurityPairedScanCount,
    long SecurityTotalDurationUs,
    IReadOnlyList<WaitAnalysisRow> Waits,
    IReadOnlyList<WindowEvidenceRow> Evidence,
    IReadOnlyList<CompositeNotConcluded> NotConcluded,
    IReadOnlyList<CompositeNextTool> NextTools,
    IReadOnlyList<CompositeToolCall> ExecutedToolCalls,
    IReadOnlyList<string> Warnings,
    [property: Description("Exact process lifetime used by every process-targeted child when ScopeMode is single_process.")]
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("One of all_processes, single_process, pid_aggregate, unresolved, or not_evaluated.")]
    string ScopeMode = "all_processes",
    [property: Description("True when the selected PID has multiple lifetimes anywhere in the trace.")]
    bool PidReuseObserved = false,
    [property: Description("Process lifetimes included by the common selector/window; empty for all_processes or unresolved scope.")]
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Common process-selector status: ok, scope_not_found, or not_evaluated for a pre-load width guard.")]
    string ScopeStatus = "ok",
    [property: Description("observed when any scoped child event family matched; not_observed only when every child class was unobserved; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: Description("Sum of scoped event counts from distinct child event families; hard-fault events are counted once across both sort views.")]
    long MatchedEventCount = 0,
    [property: Description("Stable composite empty-result reason; inspect NotConcluded for per-child scope and capability reasons.")]
    string? NoDataReason = null);

// Top-N call-tree frames ranked by VirtualMemAlloc/Free operation bytes. This is event-flow
// traffic, not a live virtual-size, commit, or leak measurement.
public sealed record VirtualAllocStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveOpCount,
    long InclusiveOpCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record VirtualAllocStacksResponse(
    IReadOnlyList<VirtualAllocStackRow> Rows,
    [property: Description("Deprecated compatibility alias of TotalOperationBytes. It is alloc bytes plus free bytes, not net growth or live virtual size.")]
    long TotalBytes,
    [property: Description("Deprecated compatibility alias of TotalOperationCount.")]
    long TotalOpCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    long AllocatedBytes = 0,
    long AllocatedCount = 0,
    long FreedBytes = 0,
    long FreedCount = 0,
    [property: Description("Exact AllocatedBytes + FreedBytes. This measures observed operation traffic, not retained memory.")]
    long TotalOperationBytes = 0,
    long TotalOperationCount = 0,
    [property: Description("Exact AllocatedBytes - FreedBytes over observed events. This is not live virtual size, committed bytes, retained memory, or proof of a leak.")]
    long NetObservedOperationBytes = 0,
    string NetObservedOperationBytesSemantics = "alloc_minus_free_event_bytes_not_live_virtual_size_commit_or_leak",
    string MetricName = "virtualMemoryOperationBytes",
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record MemoryResourceProcessRow(
    int Pid,
    string ProcessName,
    long FirstSampleUs,
    long LastSampleUs,
    int SampleCount,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long PrivateWorkingSetBytes,
    long PeakPrivateWorkingSetBytes,
    long CommitBytes,
    long PeakCommitBytes,
    long PrivateBytes,
    long PeakPrivateBytes,
    long SharedCommitBytes,
    long VirtualSizeBytes,
    long CommitDebtBytes,
    long StoreBytes,
    long ProcessStartUs = 0);

public sealed record MemoryResourceSystemRow(
    long TimeUs,
    long? FreeBytes,
    long? ZeroBytes,
    long? ModifiedBytes,
    long? ModifiedNoWriteBytes,
    long? BadBytes);

public sealed record MemoryHandleProcessRow(
    int Pid,
    string ProcessName,
    long Created,
    long Closed,
    long DuplicatedIn,
    long DuplicatedOut,
    long NetDelta,
    long ProcessStartUs = 0);

public sealed record MemoryPoolProcessRow(
    int Pid,
    string ProcessName,
    long PagedOutstandingBytes,
    long NonPagedOutstandingBytes,
    long PagedAllocatedBytes,
    long NonPagedAllocatedBytes,
    long PagedFreedBytes,
    long NonPagedFreedBytes,
    long AllocationCount,
    long FreeCount,
    long UnknownFreeCount,
    long ProcessStartUs = 0);

public sealed record MemoryPoolTagRow(
    string Tag,
    string PoolKind,
    long OutstandingBytes,
    long AllocatedBytes,
    long FreedBytes,
    long AllocationCount,
    long FreeCount,
    long UnknownFreeCount);

public sealed record MemoryPressureProcessRow(
    int Pid,
    string ProcessName,
    long PeakWorkingSetBytes,
    long PeakCommitBytes,
    long PeakPrivateBytes,
    long ProcessStartUs = 0);

public sealed record MemoryPressureSummary(
    long SystemSampleCount,
    long ProcessSnapshotBatchCount,
    long? MinFreeBytes,
    long? MinFreeTimeUs,
    long? MinAvailableBytes,
    long? MinAvailableTimeUs,
    long? MaxModifiedBytes,
    long? MaxModifiedTimeUs,
    long? MaxObservedTotalWorkingSetBytes,
    long? MaxObservedTotalWorkingSetTimeUs,
    long? MaxObservedTotalCommitBytes,
    long? MaxObservedTotalCommitTimeUs,
    long? MaxObservedTotalPrivateBytes,
    long? MaxObservedTotalPrivateTimeUs,
    IReadOnlyList<MemoryPressureProcessRow> TopPeakWorkingSetProcesses,
    IReadOnlyList<MemoryPressureProcessRow> TopPeakCommitProcesses);

public sealed record MemoryResourceResponse(
    IReadOnlyList<MemoryResourceProcessRow> Processes,
    IReadOnlyList<MemoryHandleProcessRow> Handles,
    IReadOnlyList<MemoryPoolProcessRow> PoolProcesses,
    IReadOnlyList<MemoryPoolTagRow> PoolTags,
    MemoryPressureSummary Pressure,
    IReadOnlyList<MemoryResourceSystemRow> SystemMemory,
    long ProcessSampleCount,
    long HandleEventCount,
    long PoolEventCount,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("window_global means SystemMemory and system-pressure samples use only the requested time window, not pid filtering. When ScopeStatus is scope_not_found these fields are intentionally empty.")]
    string SystemMemoryScope = "window_global",
    string ScopeStatus = "ok",
    [property: Description("observed when target-process evidence matched, not_observed when no supported target event class appears anywhere in the trace, unknown when the scope is invalid or relevant events exist only outside the selected scope/window.")]
    string CapabilityStatus = "unknown",
    [property: Description("Count of target-process evidence in scope: Memory/ProcessMemInfo process entries plus matched handle events plus matched pool events. System-memory samples are counted separately by Pressure.SystemSampleCount and are excluded.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by TCP+UDP send/receive byte count.  Pairs IPv4 and IPv6
// variants of each event family.  TcpBytes / UdpBytes break out the totals so consumers
// can tell whether the workload is mostly TCP or UDP without re-aggregating.
public sealed record NetIoStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveOpCount,
    long InclusiveOpCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record NetIoStacksResponse(
    IReadOnlyList<NetIoStackRow> Rows,
    long TotalBytes,
    long TotalOpCount,
    long TcpBytes,
    long UdpBytes,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by registry-operation count.  Different from byte-metric
// stacks because registry operations don't have a natural byte cost — every Query / Open /
// Set is one unit, and the volume on a hot path is what matters.
public sealed record RegistryStackRow(
    string Function,
    long ExclusiveOps,
    long InclusiveOps,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record RegistryStacksResponse(
    IReadOnlyList<RegistryStackRow> Rows,
    long TotalOps,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by ReadyThread event count. The event stack is associated
// with the readier/wakeup observation. It is useful supporting evidence, but is not by itself
// a fully paired wait-to-wakeup causal chain or proof of the root cause.
public sealed record ReadyThreadStackRow(
    string Function,
    long ExclusiveReadyCount,
    long InclusiveReadyCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record ReadyThreadStacksResponse(
    IReadOnlyList<ReadyThreadStackRow> Rows,
    long TotalReadyCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by interrupt time (DPC + ISR), in microseconds.  Combined
// metric — a hot driver routine shows up regardless of whether its work runs in the ISR or
// the DPC.  DpcUs / IsrUs break out the totals so callers can see the split.
public sealed record InterruptStackRow(
    string Function,
    long ExclusiveUs,
    long InclusiveUs,
    long ExclusiveCount,
    long InclusiveCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record InterruptStacksResponse(
    IReadOnlyList<InterruptStackRow> Rows,
    long TotalUs,
    long DpcUs,
    long IsrUs,
    long TotalCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Top-N call-tree frames ranked by ALPC event count (Send + Receive merged).  Useful for
// finding code paths that do heavy cross-process IPC (RPC, COM, AppContainer broker calls,
// Windows service interactions, etc.).
//
// NOTE on counting: one logical ALPC message produces TWO events — a Send on the sender
// side and a Receive on the receiver side.  TotalEvents and the Exclusive/Inclusive*Events
// fields count both; SendCount and ReceiveCount break the total out per direction.
// "Number of distinct messages" ≈ min(SendCount, ReceiveCount).
public sealed record AlpcStackRow(
    string Function,
    long ExclusiveEvents,
    long InclusiveEvents,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record AlpcStacksResponse(
    IReadOnlyList<AlpcStackRow> Rows,
    long TotalEvents,
    long SendCount,
    long ReceiveCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// Per-thread lifecycle row: start / end / lifetime in μs.  TraceResident{Start,End} flags
// say "this side of the lifetime is bounded by trace capture, not the actual thread event"
// — for a trace-resident-start thread, StartTimeUs is 0 (= trace start), not the real spawn.
public sealed record ThreadLifetimeRow(
    int Tid,
    long StartTimeUs,
    long EndTimeUs,
    long LifetimeUs,
    bool TraceResidentStart,
    bool TraceResidentEnd,
    [property: Description("Trace-relative process start; combine with the response Pid to identify the owning process lifetime.")]
    long ProcessStartUs = 0,
    [property: Description("Generation of this TID within its process lifetime; repeated TIDs have separate rows.")]
    long ThreadGeneration = 1);

public sealed record ThreadLifetimeResponse(
    int Pid,
    string ProcessName,
    int TotalThreads,
    int PeakConcurrentThreads,
    IReadOnlyList<ThreadLifetimeRow> Threads,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for this lifecycle view. Reused-PID ambiguity is rejected before a response is produced.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ThreadStart/Stop or thread-rundown source records attributed to the selected process lifetime. TotalThreads instead counts projected logical thread lifecycles before top truncation.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// One row of a managed-allocation stack view: bytes the CLR observed flowing through this
// frame, plus the GCAllocationTick event count (tick ≈ ~100 KB allocated per (heap, gen, type),
// so absolute bytes are sampled, not exhaustive).
public sealed record ClrAllocStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveEventCount,
    long InclusiveEventCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record ClrAllocTypeRow(string TypeName, long Bytes);

public sealed record ClrAllocStacksResponse(
    IReadOnlyList<ClrAllocStackRow> Rows,
    long TotalBytes,
    long TotalEventCount,
    IReadOnlyList<ClrAllocTypeRow> TopTypes,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// One row of an exception-throw stack view: count of ExceptionStart events on this frame.
public sealed record ClrExceptionStackRow(
    string Function,
    long ExclusiveCount,
    long InclusiveCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record ClrExceptionTypeRow(string ExceptionType, long Count);

public sealed record ClrExceptionStacksResponse(
    IReadOnlyList<ClrExceptionStackRow> Rows,
    long TotalEventCount,
    IReadOnlyList<ClrExceptionTypeRow> TopTypes,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// One row of an NT-heap allocation stack view: bytes allocated through this frame from the
// user-mode heap (RtlAllocateHeap / HeapAlloc / malloc / new / etc.).  Distinct from
// VirtualAlloc, which is page-granular reservations the heap allocator sub-allocates from.
public sealed record HeapAllocStackRow(
    string Function,
    long ExclusiveBytes,
    long InclusiveBytes,
    long ExclusiveEventCount,
    long InclusiveEventCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record HeapAllocStacksResponse(
    IReadOnlyList<HeapAllocStackRow> Rows,
    long TotalBytes,
    long TotalEventCount,
    long AllocBytes,
    long ReallocBytes,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "float32_per_sample_approximate",
    string RowMetricAccounting = "float32_per_sample_approximate",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// One row of a generic-provider stack view: count of events from a user-supplied provider
// observed flowing through this frame.  Metric is event count.  Stack quality depends on
// whether stack-walks were enabled for this provider+keyword in the capture profile.
public sealed record GenericEventStackRow(
    string Function,
    long ExclusiveCount,
    long InclusiveCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record GenericEventNameRow(string EventName, long Count);

public sealed record GenericEventStacksResponse(
    IReadOnlyList<GenericEventStackRow> Rows,
    string ProviderName,
    string? EventNameSubstring,
    long TotalEventCount,
    IReadOnlyList<GenericEventNameRow> TopEventNames,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_integer_count",
    string RowMetricAccounting = "exact_integer_count",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null);

// One row of the GCHeapStats time series — a snapshot of the managed heap right after a GC.
// Generations 0/1/2 are the classical heap; LOH = generation 3 (large object heap); POH =
// generation 4 (pinned object heap, .NET 5+).
public sealed record GcHeapStatsRow(
    long TimeUs,
    int Pid,
    long TotalHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LohBytes,
    long PohBytes,
    int PinnedObjectCount,
    int GcHandleCount,
    long FinalizationPromotedBytes,
    long FinalizationPromotedCount,
    [property: Description("Trace-relative process start; heap snapshots from reused PID lifetimes are never merged across this value.")]
    long ProcessStartUs = 0);

public sealed record GcHeapStatsResponse(
    int? Pid,
    IReadOnlyList<GcHeapStatsRow> Rows,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of GCHeapStats source events matched to the requested process scope and half-open window.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);

public sealed record FinalizedTypeRow(string TypeName, long Count);

// One TCP connection paired Connect/Accept → Disconnect/Reconnect by emitter process
// lifetime + `connid`. CloseTimeUs and DurationUs are null when no closing event was observed;
// TraceResidentEnd distinguishes connections that remained open at trace end.
public sealed record NetConnectionRow(
    int Pid,
    ulong ConnId,
    string Role,
    bool IsIPv6,
    string LocalAddress,
    int LocalPort,
    string RemoteAddress,
    int RemotePort,
    long OpenTimeUs,
    long? CloseTimeUs,
    long? DurationUs,
    bool TraceResidentEnd,
    [property: Description("Trace-relative start of the process that emitted this connection lifecycle. It is part of the pairing identity with Pid and ConnId.")]
    long ProcessStartUs = 0,
    [property: Description("How the row ended: disconnect, reconnect, replaced_open, process_end_unobserved, or trace_end_unobserved. Unobserved ends have null CloseTimeUs and DurationUs.")]
    string EndState = "disconnect");

public sealed record NetConnectionsResponse(
    int? Pid,
    int TotalConnections,
    IReadOnlyList<NetConnectionRow> Connections,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of network connect/accept/disconnect/reconnect source endpoints matched to the requested process scope and half-open window. This differs from TotalConnections, which counts projected lifecycles overlapping the window.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null);
