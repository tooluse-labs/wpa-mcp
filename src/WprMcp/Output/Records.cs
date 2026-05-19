using System.ComponentModel;

namespace WprMcp.Output;

public sealed record TraceMeta(
    string Path,
    long DurationUs,
    long EventCount,
    long EventsLost,
    int ProcessCount);

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
    IReadOnlyList<ToolRecommendation> CapabilitySupportedTools);

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
    bool HasStackWalkEvents,
    long StackWalkEventCount,
    long EventsWithCallStacks,
    double? EventStackCoveragePct);

public sealed record StackProbeResponse(
    string Path,
    long EventCount,
    long ExplicitStackWalkEvents,
    long EventsWithCallStacks,
    double? EventStackCoveragePct,
    long CSwitchEvents,
    long CSwitchEventsWithCallStacks,
    double? CSwitchStackCoveragePct,
    long ReadyThreadEvents,
    long ReadyThreadEventsWithCallStacks,
    double? ReadyThreadStackCoveragePct,
    bool HasExplicitStackWalkEvents,
    bool HasUsableEventStacks,
    IReadOnlyList<string> Notes);

public sealed record ProviderEventCountSummary(
    int TotalProviderCount,
    long TotalEventCount,
    long OtherEventCount,
    IReadOnlyList<ProviderEventCount> TopProviders);

public sealed record ProviderEventCount(
    string Provider,
    long EventCount,
    long EventsWithCallStacks,
    double? StackCoveragePct);

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
    int ResolvedModuleCount,
    double? ModuleResolutionRate,
    IReadOnlyList<InspectUnresolvedModule> TopUnresolvedModules,
    IReadOnlyList<SymbolRecommendation> Recommendations);

public sealed record InspectUnresolvedModule(
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

// What kernel keywords were active in the capture, inferred from per-event-name counts in
// the trace metadata. Lets a client know upfront whether dependent tools will return data:
// if HasFileIo=false, file_io_top_files / file_io_top_stacks will return empty rows even on
// a busy trace, and the user should re-capture with the FileIO keyword enabled.
//
// All flags are conservative: true iff at least one matching event was observed in the trace.
// A flag of false definitively means the kernel never delivered that event class — either
// the keyword wasn't enabled or no qualifying event happened in the captured window.
public sealed record TraceCapabilities(
    bool HasCpuSamples,
    bool HasCSwitch,
    bool HasFileIo,
    bool HasDiskIo,
    bool HasImageLoad,
    bool HasHardFaults,
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
    bool HasCSwitchStacks = false,
    bool HasReadyThreadStacks = false,
    bool HasInterruptStacks = false);

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
// FirstImageLoadOffsetUs measures the KERNEL-SIDE WINDOW: time from when the kernel emitted
// ProcessStart to when the first DLL was mapped. On AV-heavy hosts that's where seconds get
// burned in process-creation notify callbacks before user-mode code can even run.
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
    IReadOnlyList<string> Warnings);

public sealed record CpuFunctionRow(
    string Function,
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record SymbolStats(
    long Resolved,
    long Unresolved,
    double ResolutionRate,
    IReadOnlyList<UnresolvedModule> TopUnresolvedModules);

public sealed record UnresolvedModule(string Module, long FrameCount);

public sealed record CpuTopFunctionsResponse(
    IReadOnlyList<CpuFunctionRow> Rows,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings);

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
    long PreemptedSwitches);

public sealed record CpuPreciseResponse(
    IReadOnlyList<CpuPreciseThreadRow> Rows,
    long TotalCpuUs,
    long TotalContextSwitches,
    long TotalReadyCount,
    long TotalReadyLatencyUs,
    IReadOnlyList<string> Warnings);

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
    IReadOnlyList<string> Warnings);

public sealed record CpuTopFunctionsBatchResponse(
    IReadOnlyDictionary<int, CpuTopFunctionsResponse> PerPid,
    IReadOnlyList<string> Warnings,
    bool Partial = false,
    IReadOnlyList<int>? SkippedPids = null,
    int RequestedPidCount = 0,
    int CompletedPidCount = 0);

public sealed record FileIoRow(
    string File,
    long ReadBytes,
    long ReadCount,
    long WriteBytes,
    long WriteCount);

public sealed record FileIoResponse(IReadOnlyList<FileIoRow> Rows);

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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

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
    IReadOnlyList<string> Warnings);

// Top-N call-tree frames ranked by hard-fault PAGING-IN BYTES. Different question from
// HardFaultByFileResponse: that bucket is "which file paged in most"; this one is "which call
// chain triggered the page-in". For a slow-startup process loading a large DLL, the per-file
// view points at the heavy module by name; the per-stack view shows whether the page-in came
// from eager linker resolution, lazy use of a constructor, or a third-party scanner.
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
    TimeHistogram? When = null);

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
    IReadOnlyList<MarkerRow>? Rows);

public sealed record SecurityScanTargetRow(
    string Source,
    string ProviderName,
    string Process,
    int? Pid,
    string Path,
    long PairedScanCount,
    long TotalDurationUs,
    double? AvgDurationUs,
    long? MaxDurationUs,
    long EventCount,
    long StartEventCount,
    long StopEventCount,
    long ResultEventCount,
    IReadOnlyList<string> EventNames,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Statuses);

public sealed record SecurityScanRequestRow(
    string Source,
    string ProviderName,
    string Id,
    long StartUs,
    long StopUs,
    long DurationUs,
    string Process,
    int? Pid,
    string Path,
    string? Reason);

public sealed record SecurityScanProviderRow(
    string Source,
    string ProviderName,
    long EventCount,
    IReadOnlyList<string> EventNames);

public sealed record SecurityScanAnalysisResponse(
    IReadOnlyList<SecurityScanTargetRow> Rows,
    IReadOnlyList<SecurityScanRequestRow> SlowScans,
    IReadOnlyList<SecurityScanProviderRow> Providers,
    long MatchedEventCount,
    long PairedScanCount,
    long TotalDurationUs,
    long UnmatchedStartCount,
    long UnmatchedStopCount,
    IReadOnlyList<string> Warnings);

public sealed record ModuleSymbolStatus(
    string Module,
    long FrameCount,
    bool Resolved,
    string Suggestion,
    string? FilePath = null,
    string? ExpectedPdbName = null,
    string? PdbSignature = null,
    int? PdbAge = null,
    string? BinaryFormat = null,
    string LookupStatus = "unknown",
    string? FailureReason = null,
    IReadOnlyList<string>? LocalSymbolCandidates = null);

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
    string CurrentSymbolPath,
    string CacheDir,
    IReadOnlyList<ModuleSymbolStatus> Modules,
    IReadOnlyList<string> Suggestions,
    string TraceDirectory,
    bool TraceDirectoryInSymbolPath,
    NativeSymbolSupportStatus NativeSymbolSupport);

public sealed record WaitReasonBucket(string Reason, long BlockedUs, long Count);

public sealed record WaitAnalysisRow(
    int Pid,
    string ProcessName,
    int Tid,
    long CpuUs,
    long BlockedUs,
    double? WaitRatio,
    long ContextSwitches,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons);

public sealed record WaitAnalysisResponse(
    IReadOnlyList<WaitAnalysisRow> Rows,
    long TotalCSwitches,
    IReadOnlyList<string> Warnings);

// Top-N call-tree frames ranked by blocked-time microseconds.
//
// ExclusiveBlockedUs / InclusiveBlockedUs sum the wait durations attributed to this frame
// (resume-point stack walks at CSwitch). PerfView convention: pct fields are normalized
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
    TimeHistogram? When = null);

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
    string? OrderBy = null);

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
    IReadOnlyList<FrameMetric> Frames);

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
    double? ThresholdPct = null);

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
    string? TestsHypothesis = null);

// Time-bucketed metric histogram. Length of Buckets == requested bucket count; bucket i covers
// [StartUs + i*BucketWidthUs, StartUs + (i+1)*BucketWidthUs). Sum of Buckets equals the total
// metric over the analysis window (modulo bucket-edge rounding).
public sealed record TimeHistogram(
    long StartUs,
    long BucketWidthUs,
    long[] Buckets);

public sealed record ImageLoadRow(
    long TimeUs,
    long TimeFromProcessStartUs,
    string FileName,
    long ImageSize,
    // Microseconds between this load and the previous one in chronological order. Null for
    // the first load (no prior). A long gap (hundreds of ms+) typically means the loader was
    // blocked on something between two adjacent DLLs — minifilter scan, signature check,
    // disk paging, AV inspection.
    long? GapFromPrevUs);

// Top-N call-tree frames ranked by ImageLoad-event count — answers "what call chain is
// loading the most DLLs". Different question from ImageLoadTimingResponse: that one is a
// chronological list of which DLLs loaded when; this one rolls every load event up to the
// stack that triggered it (LoadLibrary / CoCreateInstance / DllMain-cascade / etc.).
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
    TimeHistogram? When = null);

public sealed record ImageLoadTimingResponse(
    int Pid,
    string ProcessName,
    long ProcessStartUs,
    int TotalImageLoads,
    // Microseconds between ProcessStart and the very first ImageLoad event. This is the
    // KERNEL-SIDE GAP: notify callbacks (PsSetCreateProcessNotifyRoutine), image scan,
    // sandbox setup, possibly a CREATE_SUSPENDED + ResumeThread by parent. Null when the
    // process has no ImageLoad events.
    long? FirstLoadOffsetUs,
    // Largest GapFromPrevUs across all loads — quick "is there one outlier interval" signal
    // before scanning the full sequence. Null when fewer than 2 loads exist.
    long? MaxGapUs,
    IReadOnlyList<ImageLoadRow> Loads,
    IReadOnlyList<string> Warnings);

public sealed record ImageLoadTopGapsResponse(
    int Pid,
    string ProcessName,
    long ProcessStartUs,
    int TotalImageLoads,
    long? FirstLoadOffsetUs,
    IReadOnlyList<ImageLoadRow> TopGaps,
    IReadOnlyList<string> Warnings);

public sealed record SlowStartupCandidate(
    int Pid,
    int ParentPid,
    string Name,
    long WallUs,
    long CpuUs,
    double? WaitRatio,
    int ImageLoadCount,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    IReadOnlyList<ImageLoadRow>? FirstImageLoads,
    IReadOnlyList<CpuFunctionRow>? TopCpuFunctions);

public sealed record StartupGapEvidenceRow(
    int Pid,
    string ProcessName,
    long ProcessStartUs,
    long FirstImageLoadTimeUs,
    long FirstImageLoadOffsetUs,
    DiagnoseWindowResponse Window);

public sealed record DiagnoseSlowStartupResponse(
    IReadOnlyList<SlowStartupCandidate> Candidates,
    [property: Obsolete("Use structured Evidence, NotConcluded, ExecutedToolCalls, and NextTools instead.")]
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompositeEvidence>? Evidence = null,
    IReadOnlyList<CompositeNotConcluded>? NotConcluded = null,
    IReadOnlyList<CompositeToolCall>? ExecutedToolCalls = null,
    IReadOnlyList<CompositeNextTool>? NextTools = null,
    IReadOnlyList<StartupGapEvidenceRow>? FirstImageLoadGapEvidence = null);

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
    string? ReadyThreadCallId);

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
    IReadOnlyList<string> Details);

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
    IReadOnlyList<string> Warnings);

// Top-N call-tree frames ranked by VirtualMemAlloc/Free byte count.  Useful for "who's
// reserving address space" / "where do these committed pages come from".
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
    long TotalBytes,
    long TotalOpCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null);

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
    long StoreBytes);

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
    long NetDelta);

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
    long UnknownFreeCount);

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
    long PeakPrivateBytes);

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
    IReadOnlyList<string> Warnings);

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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

// Top-N call-tree frames ranked by ReadyThread event count.  The stack on each event is the
// READIER's stack (the code that woke up the awakened thread) — answers "who unblocked
// the threads in process X / waiting for resource Y", closing the producer→consumer
// causality loop that wait_analysis only opens one side of.
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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

// Per-thread lifecycle row: start / end / lifetime in μs.  TraceResident{Start,End} flags
// say "this side of the lifetime is bounded by trace capture, not the actual thread event"
// — for a trace-resident-start thread, StartTimeUs is 0 (= trace start), not the real spawn.
public sealed record ThreadLifetimeRow(
    int Tid,
    long StartTimeUs,
    long EndTimeUs,
    long LifetimeUs,
    bool TraceResidentStart,
    bool TraceResidentEnd);

public sealed record ThreadLifetimeResponse(
    int Pid,
    string ProcessName,
    int TotalThreads,
    int PeakConcurrentThreads,
    IReadOnlyList<ThreadLifetimeRow> Threads,
    IReadOnlyList<string> Warnings);

// Single GC event: the wall interval bounded by GCStart→GCStop, with PauseUs filled in
// from any covering GCSuspendEEStart→GCRestartEEStop on the same PID.  Generation = -1
// flags a "pause without enclosing GCStart" — rare, happens at trace boundaries.
public sealed record GcEventRow(
    long StartUs,
    long DurationUs,
    int Generation,
    string Reason,
    int Pid,
    long? PauseUs);

public sealed record GcAnalysisResponse(
    int? Pid,
    int TotalGcCount,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    long TotalGcUs,
    long TotalPauseUs,
    IReadOnlyList<GcEventRow> Events,
    IReadOnlyList<string> Warnings);

// Single JIT'd method, with the time spent JITting it and the IL size from the source
// metadata (NOT native code size — the JittingStarted event doesn't carry that).
public sealed record JitMethodRow(
    string Method,
    long JitDurationUs,
    int MethodIlSize,
    int Pid);

public sealed record JitAnalysisResponse(
    int? Pid,
    int TotalMethodsJitted,
    long TotalJitUs,
    IReadOnlyList<JitMethodRow> TopMethods,
    IReadOnlyList<string> Warnings);

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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

// One row of a managed-monitor-contention stack view: blocked μs on this frame.  Includes only
// ContentionFlags.Managed events (the CLR's `lock` / Monitor.Enter waits) — native lock
// contention from the same provider is ignored.
public sealed record ClrContentionStackRow(
    string Function,
    long ExclusiveBlockedUs,
    long InclusiveBlockedUs,
    long ExclusiveCount,
    long InclusiveCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace);

public sealed record ClrContentionStacksResponse(
    IReadOnlyList<ClrContentionStackRow> Rows,
    long TotalBlockedUs,
    long TotalEventCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

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
    TimeHistogram? When = null);

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
    long FinalizationPromotedCount);

public sealed record GcHeapStatsResponse(
    int? Pid,
    IReadOnlyList<GcHeapStatsRow> Rows,
    IReadOnlyList<string> Warnings);

// One run of the finalizer thread — bracketed by GCFinalizersStart→GCFinalizersStop.
// `FinalizersRun` is the number of finalizers executed in this batch.
public sealed record FinalizerBatchRow(
    int Pid,
    long StartUs,
    long DurationUs,
    int FinalizersRun);

public sealed record FinalizedTypeRow(string TypeName, long Count);

public sealed record FinalizerAnalysisResponse(
    int? Pid,
    long TotalObjectsFinalized,
    long TotalBatchUs,
    IReadOnlyList<FinalizerBatchRow> Batches,
    IReadOnlyList<FinalizedTypeRow> TopTypes,
    IReadOnlyList<string> Warnings);

// One TCP connection paired Connect/Accept → Disconnect/Reconnect by `connid`.  CloseTimeUs
// can equal trace-end (with TraceResidentEnd = true) for connections still open when capture
// stopped.  Duration is CloseTimeUs − OpenTimeUs in microseconds.
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
    bool TraceResidentEnd);

public sealed record NetConnectionsResponse(
    int? Pid,
    int TotalConnections,
    IReadOnlyList<NetConnectionRow> Connections,
    IReadOnlyList<string> Warnings);
