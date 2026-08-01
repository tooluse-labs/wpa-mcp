using WpaMcp.Core;

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
    int InvalidIntervalCount,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    [property: System.ComponentModel.Description("Scoped source-event status: observed only when the resolved selector matched GC/pause endpoints; not_observed only when the event class was absent trace-wide; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Number of GCStart/Stop and GCSuspendEEStart/GCRestartEEStop source endpoints attributed to the requested process scope and half-open window; this is not the completed GC row count.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason. no_completed_intervals_in_scope means one or more scoped endpoints were observed but no valid completed GC/pause interval could be projected; no_events_in_scope is used only when zero endpoints matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of completed GC wall or orphan-pause intervals projected into the requested window. MatchedEventCount counts scoped GC/pause source endpoints instead.")]
    long MatchedIntervalCount = 0);

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
    string AccountingMode,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    [property: System.ComponentModel.Description("Scoped source-event status: observed only when the resolved selector matched JIT endpoints; not_observed only when the event class was absent trace-wide; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Number of MethodJittingStarted/MethodLoadVerbose source endpoints attributed to the requested process scope and half-open window; this is not the completed method interval count.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason. no_completed_intervals_in_scope means one or more scoped endpoints were observed but no valid completed JIT interval could be projected; no_events_in_scope is used only when zero endpoints matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of completed JIT intervals projected into the requested window. MatchedEventCount counts scoped JIT source endpoints instead.")]
    long MatchedIntervalCount = 0);

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
    string AccountingMode,
    string? EvidenceKind = null,
    string? Provenance = null,
    string? Confidence = null,
    long? ProcessStartUs = null,
    string? TargetIdentitySource = null);

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
    string AccountingMode,
    string? EvidenceKind = null,
    string? Provenance = null,
    string? Confidence = null,
    long? ProcessStartUs = null,
    string? TargetIdentitySource = null);

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
    string AccountingMode,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    [property: System.ComponentModel.Description("Scoped source-event status: observed only when the resolved target scope and filters matched security evidence; not_observed only when no supported security event class was recognized trace-wide; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    string? NoDataReason = null,
    string TargetIdentitySource = "not_observed",
    long PayloadTargetIdentityCount = 0,
    long EmitterFallbackIdentityCount = 0,
    long UnresolvedTargetIdentityCount = 0,
    long TargetIdentityMismatchCount = 0);

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
    string AccountingMode,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Total GCFinalizeObject plus GCFinalizersStart/Stop source records attributed to the requested process scope and half-open window; this is not the completed batch count.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason. no_completed_intervals_in_scope means one or more scoped finalizer batch endpoints were observed but no valid completed batch could be projected; no_events_in_scope is used only when zero finalizer events matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of GCFinalizeObject point events matched to the requested scope and window.")]
    long MatchedObjectEventCount = 0,
    [property: System.ComponentModel.Description("Number of GCFinalizersStart/Stop endpoint events matched to the requested scope and window.")]
    long MatchedBatchEndpointEventCount = 0,
    [property: System.ComponentModel.Description("Number of completed finalizer batch intervals projected into the requested window.")]
    long MatchedBatchCount = 0);

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
    string AccountingMode,
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
