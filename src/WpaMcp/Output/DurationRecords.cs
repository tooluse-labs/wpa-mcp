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
    [property: System.ComponentModel.Description("True only when the pause had no compatible GC wall trace-wide. Use IntervalKind to distinguish a pause associated with a GC wall that simply did not overlap this query window.")]
    bool IsOrphanPause,
    [property: System.ComponentModel.Description("gc_wall, pause_only_associated_gc_wall_outside_window, or orphan_pause. Only gc_wall rows contribute to TotalGcCount and generation counts.")]
    string IntervalKind = "gc_wall");

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
    [property: System.ComponentModel.Description("Scoped GC/pause endpoints with a resolved process instance but no CLR instance identity. These endpoints were not paired.")]
    int IncompleteClrIdentityCount,
    [property: System.ComponentModel.Description("Deprecated trace-global compatibility alias for TraceUnmatchedGcIntervalCount.")]
    int UnmatchedGcIntervalCount,
    [property: System.ComponentModel.Description("Deprecated trace-global compatibility alias for TraceUnmatchedPauseIntervalCount.")]
    int UnmatchedPauseIntervalCount,
    [property: System.ComponentModel.Description("Deprecated trace-global compatibility alias for TraceInvalidIntervalCount.")]
    int InvalidIntervalCount,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    [property: System.ComponentModel.Description("Scoped source-event status: observed only when an identity-usable GC/pause endpoint or completed interval matched; not_observed only when the event class was absent trace-wide; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Number of GCStart/Stop and GCSuspendEEStart/GCRestartEEStop source endpoints attributed to the requested process scope and half-open window; this is not the completed GC row count.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason. scope_not_found or ambiguous_process_instance means the selector did not resolve safely; source_events_unattributed means raw scoped evidence was dropped for unresolved identity; no_completed_intervals_in_scope means usable scoped endpoints did not form a projected interval; no_events_in_scope means no scoped evidence matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of projected rows: GC walls plus pause-only intervals. IntervalKind identifies which; only gc_wall rows contribute to TotalGcCount. MatchedEventCount counts scoped raw endpoints instead.")]
    long MatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace unmatched GCStart plus unmatched GCStop endpoints. Use TraceUnmatchedGcStartCount and TraceUnmatchedGcStopCount for direction.")]
    int TraceUnmatchedGcIntervalCount = 0,
    [property: System.ComponentModel.Description("Unmatched GC intervals attributable to the selected process instance and requested half-open window.")]
    int ScopedUnmatchedGcIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace unmatched GCSuspendEEStart plus unmatched GCRestartEEStop endpoints. Use the split pause fields for direction.")]
    int TraceUnmatchedPauseIntervalCount = 0,
    [property: System.ComponentModel.Description("Unmatched pause intervals attributable to the selected process instance and requested half-open window.")]
    int ScopedUnmatchedPauseIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace invalid GC/pause intervals, including non-positive or inconsistent endpoint pairs.")]
    int TraceInvalidIntervalCount = 0,
    [property: System.ComponentModel.Description("Invalid GC/pause intervals attributable to the selected process instance and requested half-open window.")]
    int ScopedInvalidIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace source endpoints dropped because process or CLR instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Identity-unresolved source endpoints whose raw PID/time could belong to the selected scope.")]
    long ScopedIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Whole-trace GCStart endpoints without a matching GCStop endpoint.")]
    int TraceUnmatchedGcStartCount = 0,
    [property: System.ComponentModel.Description("Whole-trace GCStop endpoints without a matching GCStart endpoint.")]
    int TraceUnmatchedGcStopCount = 0,
    [property: System.ComponentModel.Description("Whole-trace GCSuspendEEStart endpoints without a matching GCRestartEEStop endpoint.")]
    int TraceUnmatchedPauseStartCount = 0,
    [property: System.ComponentModel.Description("Whole-trace GCRestartEEStop endpoints without a matching GCSuspendEEStart endpoint.")]
    int TraceUnmatchedPauseStopCount = 0);

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
    [property: System.ComponentModel.Description("Deprecated compatibility alias for TraceUnmatchedIntervalCount: whole-trace unmatched resolved endpoints plus trace identity-unresolved endpoints.")]
    int UnmatchedIntervalCount,
    [property: System.ComponentModel.Description("Deprecated trace-global compatibility alias for TraceInvalidIntervalCount.")]
    int InvalidIntervalCount,
    string AccountingMode,
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    [property: System.ComponentModel.Description("Scoped source-event status: observed only when an identity-usable JIT endpoint or completed interval matched; not_observed only when the event class was absent trace-wide; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Number of MethodJittingStarted/MethodLoadVerbose source endpoints attributed to the requested process scope and half-open window; this is not the completed method interval count.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason. scope_not_found or ambiguous_process_instance means the selector did not resolve safely; source_events_unattributed means raw scoped evidence was dropped for unresolved identity; no_completed_intervals_in_scope means usable scoped endpoints did not form a projected interval; no_events_in_scope means no scoped evidence matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of completed JIT intervals projected into the requested window. MatchedEventCount counts scoped JIT source endpoints instead.")]
    long MatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace unmatched JIT endpoints plus identity-unresolved endpoints retained for compatibility. Use the split endpoint and identity fields below for exact semantics.")]
    int TraceUnmatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Scoped unmatched JIT endpoints plus scoped identity-unresolved endpoints retained for compatibility. Use the split fields below.")]
    int ScopedUnmatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace invalid JIT intervals, including non-positive or inconsistent endpoint pairs.")]
    int TraceInvalidIntervalCount = 0,
    [property: System.ComponentModel.Description("Invalid JIT intervals attributable to the selected process instance and requested half-open window.")]
    int ScopedInvalidIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace source endpoints dropped because process or CLR instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Identity-unresolved source endpoints whose raw PID/time could belong to the selected scope.")]
    long ScopedIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Whole-trace MethodJittingStarted endpoints without a matching MethodLoadVerbose endpoint; null when the caller supplied only an aggregate legacy count.")]
    int? TraceUnmatchedStartCount = null,
    [property: System.ComponentModel.Description("Whole-trace MethodLoadVerbose endpoints without a matching MethodJittingStarted endpoint; null when unavailable.")]
    int? TraceUnmatchedStopCount = null,
    [property: System.ComponentModel.Description("Scoped unmatched MethodJittingStarted endpoints; null when unavailable.")]
    int? ScopedUnmatchedStartCount = null,
    [property: System.ComponentModel.Description("Scoped unmatched MethodLoadVerbose endpoints; null when unavailable.")]
    int? ScopedUnmatchedStopCount = null);

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
    [property: System.ComponentModel.Description("Exhaustive ordinally sorted distinct event names accumulated for this returned target aggregate; this nested list is not independently capped.")]
    IReadOnlyList<string> EventNames,
    [property: System.ComponentModel.Description("Exhaustive ordinally sorted distinct reasons accumulated for this returned target aggregate; this nested list is not independently capped.")]
    IReadOnlyList<string> Reasons,
    [property: System.ComponentModel.Description("Exhaustive ordinally sorted distinct statuses accumulated for this returned target aggregate; this nested list is not independently capped.")]
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
    long TargetIdentityMismatchCount = 0,
    [property: System.ComponentModel.Description("Recognized security source observations whose raw target selector/window matched but whose target process lifetime or paired target identity was unsafe to attribute. These are excluded from MatchedEventCount.")]
    long ScopedUnattributedEventCount = 0);

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
    [property: System.ComponentModel.Description("Stable empty-result reason. scope_not_found or ambiguous_process_instance means the selector did not resolve safely; source_events_unattributed means raw scoped evidence could not be assigned required process/CLR identity; no_completed_intervals_in_scope means attributable endpoints did not form a valid completed batch; no_events_in_scope means neither attributable nor raw-unattributed evidence matched.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Number of GCFinalizeObject point events matched to the requested scope and window.")]
    long MatchedObjectEventCount = 0,
    [property: System.ComponentModel.Description("Number of GCFinalizersStart/Stop endpoint events matched to the requested scope and window.")]
    long MatchedBatchEndpointEventCount = 0,
    [property: System.ComponentModel.Description("Number of completed finalizer batch intervals projected into the requested window.")]
    long MatchedBatchCount = 0,
    [property: System.ComponentModel.Description("Whole-trace finalizer object or batch-endpoint events whose process or CLR identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedEventCount = 0,
    [property: System.ComponentModel.Description("Identity-unresolved finalizer object or batch-endpoint events that could belong to the requested process/window scope without guessing a sibling lifetime.")]
    long ScopedIdentityUnresolvedEventCount = 0);

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
    [property: System.ComponentModel.Description("Deprecated compatibility count: scoped unmatched resolved endpoints plus scoped identity-unresolved endpoints.")]
    int UnmatchedIntervalCount,
    int InvalidIntervalCount,
    bool HasMore,
    string AccountingMode,
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: System.ComponentModel.Description("Resolved managed contention start/stop endpoints attributed to the selected process/thread/window.")]
    long MatchedEventCount = 0,
    [property: System.ComponentModel.Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, ambiguous_thread_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, no_completed_intervals_in_scope, stacks_unavailable, or null.")]
    string? NoDataReason = null,
    [property: System.ComponentModel.Description("Whole-trace raw managed ContentionStart/Stop endpoint count.")]
    long TraceSourceEndpointCount = 0,
    [property: System.ComponentModel.Description("Resolved managed contention endpoints attributed to the selected process/thread/window; equivalent to MatchedEventCount.")]
    long ScopedSourceEndpointCount = 0,
    [property: System.ComponentModel.Description("Completed contention intervals projected into the selected scope; equivalent to TotalEventCount.")]
    long MatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Whole-trace contention endpoints dropped because thread-instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Identity-unresolved contention endpoints whose raw PID/TID/time could belong to the selected scope.")]
    long ScopedIdentityUnresolvedEndpointCount = 0,
    [property: System.ComponentModel.Description("Whole-trace resolved contention starts/stops without a matching endpoint.")]
    int TraceUnmatchedIntervalCount = 0,
    [property: System.ComponentModel.Description("Resolved unmatched contention endpoints attributed to the selected scope; excludes identity-unresolved endpoints.")]
    int ScopedUnmatchedIntervalCount = 0);
