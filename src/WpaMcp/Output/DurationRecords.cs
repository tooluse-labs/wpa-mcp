namespace WpaMcp.Output;

public sealed record GcEventRow(
    long StartUs,
    long DurationUs,
    int Generation,
    string Reason,
    int Pid,
    long? PauseUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    long? FullPauseUs,
    long? AccountedPauseUs,
    string AccountingMode,
    long ProcessStartUs,
    int? ClrInstanceId,
    int? GcCount,
    bool IsOrphanPause);

public sealed record GcAnalysisResponse(
    int? Pid,
    int TotalGcCount,
    int Gen0Count,
    int Gen1Count,
    int Gen2Count,
    long TotalGcUs,
    long TotalPauseUs,
    IReadOnlyList<GcEventRow> Events,
    IReadOnlyList<string> Warnings,
    long TotalFullGcUs,
    long TotalAccountedGcUs,
    long TotalFullPauseUs,
    long TotalAccountedPauseUs,
    string AccountingMode,
    int IncompleteClrIdentityCount,
    int UnmatchedGcIntervalCount,
    int UnmatchedPauseIntervalCount,
    int InvalidIntervalCount);

// One method compilation paired over the full trace and projected into the query window.
public sealed record JitMethodRow(
    string Method,
    long JitDurationUs,
    int MethodIlSize,
    int Pid,
    long StartUs,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode,
    long ProcessStartUs);

public sealed record JitAnalysisResponse(
    int? Pid,
    int TotalMethodsJitted,
    long TotalJitUs,
    IReadOnlyList<JitMethodRow> TopMethods,
    IReadOnlyList<string> Warnings,
    long TotalFullJitUs,
    long TotalAccountedJitUs,
    bool HasMore,
    int UnmatchedIntervalCount,
    int InvalidIntervalCount,
    string AccountingMode);

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
    string? Reason,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode);

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
    IReadOnlyList<string> Statuses,
    long TotalFullDurationUs,
    long TotalAccountedDurationUs,
    double? AvgAccountedDurationUs,
    long? MaxAccountedDurationUs,
    string AccountingMode);

public sealed record SecurityScanAnalysisResponse(
    IReadOnlyList<SecurityScanTargetRow> Rows,
    IReadOnlyList<SecurityScanRequestRow> SlowScans,
    IReadOnlyList<SecurityScanProviderRow> Providers,
    long MatchedEventCount,
    long PairedScanCount,
    long TotalDurationUs,
    long UnmatchedStartCount,
    long UnmatchedStopCount,
    IReadOnlyList<string> Warnings,
    long TotalFullDurationUs,
    long TotalAccountedDurationUs,
    bool RowsHasMore,
    bool SlowScansHasMore,
    bool ProvidersHasMore,
    int InvalidIntervalCount,
    string AccountingMode);

// One run of the finalizer thread, paired over the trace before window projection.
public sealed record FinalizerBatchRow(
    int Pid,
    long StartUs,
    long DurationUs,
    int FinalizersRun,
    long EndUs,
    long FullDurationUs,
    long AccountedDurationUs,
    string AccountingMode,
    long ProcessStartUs);

public sealed record FinalizerAnalysisResponse(
    int? Pid,
    long TotalObjectsFinalized,
    long TotalBatchUs,
    IReadOnlyList<FinalizerBatchRow> Batches,
    IReadOnlyList<FinalizedTypeRow> TopTypes,
    IReadOnlyList<string> Warnings,
    long TotalFullBatchUs,
    long TotalAccountedBatchUs,
    int UnmatchedIntervalCount,
    int InvalidIntervalCount,
    string AccountingMode);

// Stack rows deliberately expose accounted contribution; complete interval duration is a
// response-level total because an interval can extend outside the queried stack view.
public sealed record ClrContentionStackRow(
    string Function,
    long ExclusiveBlockedUs,
    long InclusiveBlockedUs,
    long ExclusiveCount,
    long InclusiveCount,
    double ExclusivePct,
    double InclusivePct,
    double? ExclusivePctOfTrace,
    double? InclusivePctOfTrace,
    long ExclusiveAccountedBlockedUs,
    long InclusiveAccountedBlockedUs,
    string AccountingMode);

public sealed record ClrContentionStacksResponse(
    IReadOnlyList<ClrContentionStackRow> Rows,
    long TotalBlockedUs,
    long TotalEventCount,
    SymbolStats Stats,
    IReadOnlyList<string> Warnings,
    TimeHistogram? When,
    long TotalFullBlockedUs,
    long TotalAccountedBlockedUs,
    int UnmatchedIntervalCount,
    int InvalidIntervalCount,
    bool HasMore,
    string AccountingMode);
