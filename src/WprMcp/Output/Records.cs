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
    SymbolStatus SymbolStatus);

public sealed record ProcessRow(
    int Pid,
    int ParentPid,
    string Name,
    long StartUs,
    long EndUs,
    long WallUs,
    long CpuUs,
    double? WaitRatio,
    int ImageLoadCount);

public sealed record ProcessListResponse(
    IReadOnlyList<ProcessRow> Rows,
    int IdleProcessesHidden);

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

public sealed record MmapHotFileRow(
    string File,
    long PageInBytes,
    long PageInCount,
    long MaxLatencyUs);

public sealed record MmapHotFilesResponse(
    IReadOnlyList<MmapHotFileRow> Rows,
    IReadOnlyList<string> Warnings);

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

public sealed record ImageLoadRow(
    long TimeUs,
    long TimeFromProcessStartUs,
    string FileName,
    long ImageSize);

public sealed record ImageLoadTimingResponse(
    int Pid,
    string ProcessName,
    long ProcessStartUs,
    int TotalImageLoads,
    IReadOnlyList<ImageLoadRow> Loads,
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
