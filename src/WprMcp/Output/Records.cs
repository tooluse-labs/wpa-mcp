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
    bool HasNtHeap);

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

// Per-file aggregate of MemoryHardFault events: bytes paged in from disk for one file.  Most
// hard faults come from memory-mapped files (DLLs, data files, network-share content) being
// touched for the first time; some also come from paged-out heap/stack pages and the page file.
public sealed record HardFaultFileRow(
    string File,
    long PageInBytes,
    long PageInCount,
    long MaxLatencyUs);

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
