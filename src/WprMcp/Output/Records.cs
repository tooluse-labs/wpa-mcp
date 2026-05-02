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
    string? Warning);

public sealed record LoadTraceResponse(
    TraceMeta Trace,
    SymbolStatus SymbolStatus);

public sealed record ProcessRow(
    int Pid,
    string Name,
    long StartUs,
    long EndUs,
    long CpuUs);

public sealed record ProcessListResponse(IReadOnlyList<ProcessRow> Rows);

public sealed record CpuFunctionRow(
    string Function,
    long ExclusiveSamples,
    long InclusiveSamples,
    double ExclusivePct,
    double InclusivePct);

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

public sealed record MarkerSearchResponse(IReadOnlyList<MarkerRow> Rows);

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
