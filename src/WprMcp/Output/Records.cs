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
    bool HasRegistry);

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
    IReadOnlyList<string> Warnings);

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

public sealed record MmapHotFileRow(
    string File,
    long PageInBytes,
    long PageInCount,
    long MaxLatencyUs);

public sealed record MmapHotFilesResponse(
    IReadOnlyList<MmapHotFileRow> Rows,
    IReadOnlyList<string> Warnings);

// Top-N call-tree frames ranked by hard-fault PAGING-IN BYTES. Different question from
// MmapHotFilesResponse: that bucket is "which file paged in most"; this one is "which call
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

public sealed record ModuleSymbolStatus(
    string Module,
    long FrameCount,
    bool Resolved,
    string Suggestion);

public sealed record DiagnoseSymbolsResponse(
    string CurrentSymbolPath,
    string CacheDir,
    IReadOnlyList<ModuleSymbolStatus> Modules,
    IReadOnlyList<string> Suggestions);

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

public sealed record DiagnoseSlowStartupResponse(
    IReadOnlyList<SlowStartupCandidate> Candidates,
    string Summary,
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
