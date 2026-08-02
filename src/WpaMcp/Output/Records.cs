using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using WpaMcp.Core;

namespace WpaMcp.Output;

public sealed record TraceMeta(
    [property: Description("Canonical principal-scoped TraceId in active Contract 2.0 responses; direct legacy test construction with a file path is outside the active wire contract.")]
    [property: ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
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
    int ModuleCount,
    int ModulesWithPdbName,
    int ModulesWithCompletePdbIdentity,
    double? CompletePdbIdentityRate,
    [property: Description("Always unmeasured during load_trace: this projection performs no disk, cache, environment, or network probe. Call prepare_symbols explicitly for approved local readiness.")]
    string LocalReadinessMeasurementState,
    [property: Description("Always unmeasured during load_trace: PDB identity metadata does not prove frame-name lookup.")]
    string FrameResolutionMeasurementState,
    string NextStep,
    IReadOnlyList<string> EvidenceBoundaries);

public sealed record SymbolRecommendation(
    string Reason,
    string ServerUrl,
    int MatchedModuleCount,
    IReadOnlyList<string> SampleModules);

public sealed record LoadTraceResponse(
    TraceMeta Trace,
    SymbolStatus SymbolStatus,
    TraceCapabilities Capabilities,
    [property: Description("Canonical principal-scoped trace reference. Null only for direct legacy test construction outside the MCP host.")]
    [property: ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
    string? TraceId = null,
    string TraceRefKind = "unavailable",
    bool ReusedExisting = false,
    string Persistence = "persistent",
    [property: Description("opened_handle_snapshot_content_hash_verified when this call read and hashed an opened source handle; cached_file_identity_length_timestamps when it reused a prior immutable artifact using only the source final-path/file-id/length/creation/last-write tuple. Use forceRefresh=true when that tuple may have been adversarially preserved.")]
    string SourceGenerationAssurance = "unavailable",
    bool ForceRefreshApplied = false,
    [property: Description("Trace-handle unload and immutable artifact retention are independent lifecycles.")]
    string ArtifactRetention = "independent_retention_policy",
    IReadOnlyList<string>? Warnings = null);

public sealed record UnloadTraceResponse(
    [property: Description("Canonical principal-scoped TraceId retired by this operation.")]
    [property: ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
    string TraceId,
    [property: Description("unloaded, already_unloaded, expired, or unknown.")]
    string LifecycleStatus,
    [property: Description("drained, pending, or timed_out. Retirement remains in effect after timeout.")]
    string DrainStatus,
    [property: Description("Number of active analysis leases observed when retirement linearized.")]
    int ActiveLeases,
    [property: Description("True: repeated retirement is safe and returns stable lifecycle state.")]
    bool Idempotent,
    [property: Description("Always states that handle retirement does not delete the independently retained artifact.")]
    string ArtifactDisposition,
    [property: Description("Operational evidence boundaries; these do not contain source or artifact paths.")]
    IReadOnlyList<string> Warnings);

public sealed record NoDataReasonGuidance(
    [property: Description("event_class_not_observed: the documented target event class or predicate was absent trace-wide. This never proves that a capture keyword was disabled.")]
    string EventClassNotObserved = "event_class_not_observed",
    [property: Description("no_events_in_scope: the event class exists elsewhere, but no attributable source event matched the resolved selector and half-open window.")]
    string NoEventsInScope = "no_events_in_scope",
    [property: Description("no_completed_intervals_in_scope: scoped interval endpoints were observed, but no valid completed interval could be projected into the requested scope.")]
    string NoCompletedIntervalsInScope = "no_completed_intervals_in_scope",
    [property: Description("unpaired_endpoints_in_scope: attributable interval endpoints remained unmatched; no completed duration was invented.")]
    string UnpairedEndpointsInScope = "unpaired_endpoints_in_scope",
    [property: Description("source_events_unattributed: raw source events could match the requested PID/TID/time, but required process, thread, or CLR identity was unresolved or ambiguous; no attribution was guessed.")]
    string SourceEventsUnattributed = "source_events_unattributed",
    [property: Description("stacks_unavailable: scoped events or completed intervals exist, but none carried the event-domain stack required by this tool.")]
    string StacksUnavailable = "stacks_unavailable",
    [property: Description("symbols_unresolved: captured code frames exist, but the selected symbol context did not resolve names required by the query.")]
    string SymbolsUnresolved = "symbols_unresolved",
    [property: Description("focus_not_found: stacked scoped evidence was actually scanned, but the exact case-sensitive focus frame was absent.")]
    string FocusNotFound = "focus_not_found",
    [property: Description("no_name_match: no materialized event name or task matched the requested marker substring; this does not prove provider or keyword absence.")]
    string NoNameMatch = "no_name_match",
    [property: Description("no_candidates_in_considered_input: the reviewed candidate predicate produced no candidate in the fully considered input.")]
    string NoCandidatesInConsideredInput = "no_candidates_in_considered_input",
    [property: Description("no_candidates_in_retained_input: no retained candidate matched, but a structurally disclosed upstream cap omitted eligible input; global absence is not concluded.")]
    string NoCandidatesInRetainedInput = "no_candidates_in_retained_input",
    [property: Description("no_capabilities_match_filter: the capability catalog is valid, but no capability matched the normalized discovery filter.")]
    string NoCapabilitiesMatchFilter = "no_capabilities_match_filter",
    [property: Description("invalid_lifetime_boundaries: lifecycle records were attributable, but every projected interval had EndTimeUs <= StartTimeUs and was excluded rather than reported as a duration.")]
    string InvalidLifetimeBoundaries = "invalid_lifetime_boundaries");

public sealed record ScopeFailureErrorGuidance(
    [property: Description("process_instance_not_found: no process lifetime matched the selector and half-open window; returned as error.code with status=failed, not as noData.reason.")]
    string ProcessInstanceNotFound = "process_instance_not_found",
    [property: Description("process_start_required: the PID matched multiple clean lifetimes and an exact-only tool requires processStartUs; returned as error.code.")]
    string ProcessStartRequired = "process_start_required",
    [property: Description("ambiguous_process_instance: process-lifetime evidence was unsafe to resolve uniquely; returned as error.code.")]
    string AmbiguousProcessInstance = "ambiguous_process_instance",
    [property: Description("thread_instance_not_found: no thread lifetime matched the exact process/thread selector; returned as error.code.")]
    string ThreadInstanceNotFound = "thread_instance_not_found",
    [property: Description("ambiguous_thread_instance: multiple thread-lifetime candidates matched without a safe unique selection; returned as error.code.")]
    string AmbiguousThreadInstance = "ambiguous_thread_instance");

public sealed record AnalysisContractGuidance(
    [property: Description("Interpret scope first: only ScopeStatus=ok authorizes attribution. Use ScopeMode, SelectedProcess, IncludedProcesses, and PidReuseObserved to distinguish an exact instance from explicit PID aggregation.")]
    string ScopeRule,
    [property: Description("Trace* fields are whole-trace diagnostics; Scoped* fields use the selected identity and requested half-open window. Never use a Trace* value as the selected-process denominator.")]
    string TraceScopedRule,
    [property: Description("MatchedEventCount counts scoped raw source events/endpoints under the tool's documented predicate. MatchedIntervalCount counts completed projected intervals. Neither is necessarily the row count after aggregation or top-N truncation.")]
    string CountRule,
    [property: Description("not_observed requires whole-trace absence under the tool's documented event predicate; observed requires attributable scoped evidence; unknown means the requested scope cannot support either conclusion.")]
    string CapabilityRule,
    [property: Description("Use the per-domain StackCoverage. Global stacks do not imply this event domain has stacks; ?!? is a synthetic unknown bucket, not a captured call chain.")]
    string StackRule,
    [property: Description("PDB name/GUID/Age metadata and LocalPdbReady are not frame resolution. Only stack-tool SymbolStats after lookup measure observed code-frame name resolution.")]
    string SymbolRule,
    [property: Description("Replay an exact thread with pid + processStartUs + tid + threadStartUs + threadGeneration. Generation is required when inferred thread starts collide.")]
    string ThreadReplayRule,
    [property: Description("Associated stacks, readier stacks, and heuristic security-event matches are evidence, not standalone causal or root-cause proof.")]
    string CausalityRule,
    [property: Description("Selector-resolution failures are failed envelopes with stable error.code values; they are not successful no-data outcomes.")]
    ScopeFailureErrorGuidance ScopeFailureErrors,
    [property: Description("Stable reasons that disambiguate empty or degraded results; a bare empty Rows array has no stable meaning.")]
    NoDataReasonGuidance NoDataReasons);

public sealed record InspectTraceResponse(
    TraceMeta Trace,
    [property: JsonIgnore]
    TraceCapabilities Capabilities,
    TraceMetadata? Metadata,
    InspectSymbolQuality? SymbolQuality,
    IReadOnlyList<TraceQualityWarning> Warnings,
    [property: Description("Deprecated ID-only projection of bootstrap tool names derived from the Active Catalog. Resolve definitions through tools/list; this list is not an independent capability authority.")]
    IReadOnlyList<string> OrientationTools,
    [property: Description("Deprecated ID-only projection of tool names whose mapped capabilities have available or partial whole-trace evidence. Scoped tool outcomes remain authoritative.")]
    IReadOnlyList<string> CapabilitySupportedTools,
    IReadOnlyList<string> EnabledCapabilities,
    [property: Description("Deprecated ID-only projection of workflow IDs. Resolve static workflow definitions through wpa://workflows/{workflowId}; trace-specific evidence state is in TraceEvidenceMap.Workflows.")]
    IReadOnlyList<string> RecommendedDiagnosticFlows,
    [property: Description("Cursor page semantics and immutable trace-generation binding. full_orientation appears only on the first page; evidence_continuation pages intentionally omit nullable orientation blocks.")]
    InspectTracePageContext PageContext,
    bool HasMore,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor,
    [property: Description("Machine-readable interpretation rules for downstream analysis responses. Read this before treating empty rows, trace-wide diagnostics, stack availability, or symbol metadata as scoped conclusions.")]
    AnalysisContractGuidance? AnalysisContract = null,
    [property: Description("Same-source trace evidence assessment for every capability and workflow in the validated Active Catalog. Unknown is preserved when capture or parser completeness cannot support absence.")]
    TraceEvidenceMapRecord? TraceEvidenceMap = null,
    [property: Description("Legacy recommendation fields are ID-only compatibility projections derived from TraceEvidenceMap and the Active Catalog; they are not an independent capability authority.")]
    string LegacyProjectionState = "deprecated_id_only_derived_projection",
    [property: Description("Per-call QueryPlanner admission, generation-facts reuse, physical-pass, count-basis, phase-duration, and budget-termination evidence. PhysicalTracePassCount is scoped to this call, not accumulated across the trace generation.")]
    PlannerExecutionTelemetry? PlannerExecution = null);

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
    [property: ToolMetricSemantics("ratio", "ratio", "materialized_event_count", 0, 1)]
    double? EventStackCoveragePct,
    bool HasExplicitStackWalkEvents = false,
    bool HasUsableEventStacks = false,
    [property: Description("Percentage of TraceLog-materialized events carrying attached stacks, in [0,100].")]
    [property: ToolMetricSemantics("percent", "ratio", "materialized_event_count", 0, 100)]
    double? EventStackCoveragePercent = null);

public sealed record StackProbeResponse(
    string Path,
    [property: Description("Count of TraceLog/ETLX-materialized logical events, not raw ETW records.")]
    long EventCount,
    long ExplicitStackWalkEvents,
    long EventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use EventStackCoveragePercent.")]
    [property: ToolMetricSemantics("ratio", "ratio", "materialized_event_count", 0, 1)]
    double? EventStackCoveragePct,
    long CSwitchEvents,
    long CSwitchEventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use CSwitchStackCoveragePercent.")]
    [property: ToolMetricSemantics("ratio", "ratio", "cswitch_event_count", 0, 1)]
    double? CSwitchStackCoveragePct,
    long ReadyThreadEvents,
    long ReadyThreadEventsWithCallStacks,
    [property: Description("Deprecated legacy ratio in [0,1]. Use ReadyThreadStackCoveragePercent.")]
    [property: ToolMetricSemantics("ratio", "ratio", "ready_thread_event_count", 0, 1)]
    double? ReadyThreadStackCoveragePct,
    bool HasExplicitStackWalkEvents,
    bool HasUsableEventStacks,
    IReadOnlyList<string> Notes,
    string EventCountRepresentation = "tracelog_etlx_materialized_logical_events",
    long? RawEtwRecordCount = null,
    string RawEtwRecordCountState = "not_measured",
    string ParserCoverageState = "not_computed",
    [property: ToolMetricSemantics("percent", "ratio", "materialized_event_count", 0, 100)]
    double? EventStackCoveragePercent = null,
    [property: ToolMetricSemantics("percent", "ratio", "cswitch_event_count", 0, 100)]
    double? CSwitchStackCoveragePercent = null,
    [property: ToolMetricSemantics("percent", "ratio", "ready_thread_event_count", 0, 100)]
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
    [property: ToolMetricSemantics("ratio", "ratio", "provider_event_count", 0, 1)]
    double? StackCoveragePct,
    [property: Description("Percentage of this provider's TraceLog-materialized events carrying attached stacks, in [0,100].")]
    [property: ToolMetricSemantics("percent", "ratio", "provider_event_count", 0, 100)]
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
    int ModuleCount,
    [property: Description("Deprecated. Module metadata cannot prove frame resolution; this field is always null. Use ModulesWithPdbName and ModulesWithCompletePdbIdentity.")]
    int? ResolvedModuleCount,
    [property: Description("Deprecated. Module metadata cannot prove frame resolution; this field is always null. Use the explicit PDB identity coverage fields.")]
    double? ModuleResolutionRate,
    [property: Description("Deprecated. Missing PDB metadata does not prove stack-frame lookup failure; this compatibility field is always empty. Use TopModulesMissingPdbName for the metadata-only list and stack-tool SymbolStats for observed frame-name resolution.")]
    IReadOnlyList<InspectUnresolvedModule> TopUnresolvedModules,
    int ModulesWithPdbName = 0,
    double? ModulesWithPdbNameRate = null,
    int ModulesWithCompletePdbIdentity = 0,
    double? CompletePdbIdentityRate = null,
    [property: Description("inspect_trace does not execute stack frame lookup. PDB identity metadata and preparation readiness never substitute for an explicit context-bound frame lookup measurement.")]
    string FrameResolutionMeasurementState = "not_measured",
    SymbolStats? FrameResolution = null,
    [property: Description("inspect_trace performs no local readiness probe. Call prepare_symbols with an approved named policy.")]
    string LocalReadinessMeasurementState = "unmeasured",
    string NextStep = "prepare_symbols")
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
    [property: Description("Compatibility flag derived only from parsed TCP Connect/Accept/Disconnect/Reconnect lifecycle endpoints; Send/Recv byte events never set it. See NetworkConnectionLifecycleEndpointEventCount for the exact predicate count.")]
    bool HasNetConnections,
    bool HasRegistry,
    bool HasReadyThread,
    bool HasInterrupt,
    bool HasAlpc,
    [property: Description("Compatibility aggregate for observed ThreadStart/Stop plus ThreadDC rundown endpoints. It does not prove an observed lifecycle boundary; use the split thread endpoint counters.")]
    bool HasThreadEvents,
    [property: Description("Deprecated compatibility flag for CLR GC/pause interval endpoints. It does not imply GCHeapStats or finalizer events; use the split exact counters below.")]
    bool HasClrGc,
    [property: Description("Compatibility source flag: true when MethodJittingStarted or MethodLoadVerbose was observed. It does not prove a completed JIT interval; use the split JIT evidence counters.")]
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
    [property: ToolDictionaryRows("domain", "coverage")]
    IReadOnlyDictionary<string, DomainStackCoverage>? StackCoverageByDomain = null,
    [property: Description("True when at least one MemorySystemMemInfo or MemoryMemInfo system-wide memory snapshot was observed. This does not imply per-process working-set/private-byte snapshots.")]
    bool HasMemorySystemInfo = false,
    [property: Description("Exact number of materialized kernel ProcessStart events. Process inventory rows can exist from process-table/rundown backfill even when this count is zero; process-creation capability uses this count, not inventory size.")]
    long ObservedProcessStartEventCount = 0,
    [property: Description("Exact number of materialized observed ThreadStart/ThreadStop lifecycle endpoints. ThreadDC rundown endpoints are excluded.")]
    long ObservedThreadLifecycleEndpointEventCount = 0,
    [property: Description("Exact number of materialized ThreadDCStart/ThreadDCStop rundown endpoints. Rundown proves a bounded snapshot/lifetime input, not an observed thread creation or termination boundary.")]
    long ThreadRundownEndpointEventCount = 0,
    [property: Description("Exact source-event predicate for trace.thread.lifetime: observed ThreadStart/Stop plus ThreadDCStart/Stop rundown endpoints.")]
    long ThreadLifecycleSourceEventCount = 0,
    [property: Description("Number of valid thread lifetimes whose ThreadStart and ThreadStop boundaries were both directly observed.")]
    long ThreadCompletedObservedLifetimeCount = 0,
    [property: Description("Observed or identity-unresolved thread lifecycle endpoints that were not consumed by a valid fully observed lifetime. Rundown-derived boundaries are reported separately.")]
    long ThreadUnmatchedLifecycleEndpointCount = 0,
    [property: Description("Count of thread boundary evidence not supplied by directly observed ThreadStart/Stop pairs: inferred lifetime boundary sides, with observed rundown endpoint count retained as a lower bound when stronger lifecycle events suppress duplicate rundown rows.")]
    long ThreadInferredBoundaryCount = 0,
    [property: Description("Exact number of materialized CLR GC interval endpoints consumed by clr_gc_analysis: GCStart, GCStop, GCSuspendEEStart, and GCRestartEEStop. This excludes GCHeapStats.")]
    long ClrGcIntervalEndpointEventCount = 0,
    [property: Description("Exact number of valid completed GC wall plus pause intervals paired across the whole trace by process lifetime, CLR instance, and interval identity.")]
    long ClrGcCompletedIntervalCount = 0,
    [property: Description("CLR GC/pause start or stop endpoints left unmatched after whole-trace pairing.")]
    long ClrGcUnmatchedEndpointCount = 0,
    [property: Description("CLR GC/pause endpoints or pairs rejected because process/CLR identity was unresolved or interval ordering was invalid.")]
    long ClrGcBoundaryEvidenceCount = 0,
    [property: Description("Exact number of materialized GCHeapStats snapshot events consumed by clr_gc_heap_stats. This excludes GC and pause interval endpoints.")]
    long ClrGcHeapStatsEventCount = 0,
    [property: Description("Exact number of materialized GCFinalizeObject point events. Object events are distinct from finalizer-batch endpoints and completed batch pairs.")]
    long ClrFinalizerObjectEventCount = 0,
    [property: Description("Exact number of materialized GCFinalizersStart batch endpoints.")]
    long ClrFinalizerBatchStartEndpointEventCount = 0,
    [property: Description("Exact number of materialized GCFinalizersStop batch endpoints.")]
    long ClrFinalizerBatchStopEndpointEventCount = 0,
    [property: Description("Exact number of valid GCFinalizersStart/Stop pairs reconstructed across the whole trace by process lifetime and CLR instance. Unmatched or invalid endpoints are not counted as completed batches.")]
    long ClrFinalizerCompletedBatchCount = 0,
    [property: Description("Exact source-event predicate for clr.finalizer.activity: GCFinalizeObject plus GCFinalizersStart plus GCFinalizersStop. This is an event count, not a completed-batch count.")]
    long ClrFinalizerSourceEventCount = 0,
    [property: Description("Exact number of materialized TCP lifecycle endpoints consumed by net_connections: IPv4/IPv6 Connect, Accept, Disconnect, and Reconnect. Send/Recv byte events are excluded.")]
    long NetworkConnectionLifecycleEndpointEventCount = 0,
    [property: Description("Exact number of Connect/Accept to Disconnect/Reconnect lifecycle pairs reconstructed by process lifetime and connection identifier.")]
    long NetworkConnectionCompletedLifecycleCount = 0,
    [property: Description("Disconnect/Reconnect endpoints with no preceding Connect/Accept for the same process lifetime and connection identifier.")]
    long NetworkConnectionUnmatchedEndpointCount = 0,
    [property: Description("Network endpoints or open lifecycles whose identity or closing boundary was unresolved, replaced, process-bounded, or trace-bounded.")]
    long NetworkConnectionBoundaryEvidenceCount = 0,
    [property: Description("Exact number of materialized MethodJittingStarted plus MethodLoadVerbose endpoints consumed by clr_jit_analysis.")]
    long ClrJitIntervalEndpointEventCount = 0,
    [property: Description("Exact number of valid MethodJittingStarted to MethodLoadVerbose intervals paired by process lifetime, CLR instance, and method identifier.")]
    long ClrJitCompletedIntervalCount = 0,
    [property: Description("CLR JIT start/load endpoints left unmatched after whole-trace pairing.")]
    long ClrJitUnmatchedEndpointCount = 0,
    [property: Description("CLR JIT endpoints or pairs rejected because process/CLR identity was unresolved or interval ordering was invalid.")]
    long ClrJitBoundaryEvidenceCount = 0);

public sealed record ProcessRow(
    int Pid,
    int ParentPid,
    string Name,
    long StartUs,
    long EndUs,
    long WallUs,
    long CpuUs,
    [property: Description("Deprecated alias of WallToCpuRatio; this is wall_us / cpu_us, not blocked time, not a percentage, and not bounded to [0,1].")]
    double? WaitRatio,
    int ImageLoadCount,
    // True when the process was alive before the trace started AND survived past the end:
    // its WallUs ≈ trace duration, and any tiny CpuUs makes WaitRatio numerically huge but
    // semantically meaningless (denominator saturation). Clients sorting by WaitRatio should
    // skip these, otherwise short-lived but actually-blocked processes get buried.
    [property: Description("Deprecated compatibility flag for a row spanning approximately the whole trace. It does not identify which lifecycle endpoints were observed; use ProcessStartObserved, ProcessEndObserved, StartBoundaryKind, and EndBoundaryKind.")]
    bool TraceResident,
    bool ProcessStartObserved = false,
    bool ProcessEndObserved = false,
    [property: Description("Endpoint provenance: observed, trace_start, or inventory_start.")]
    string StartBoundaryKind = "unknown",
    [property: Description("Endpoint provenance: observed, replacement, trace_end, or inventory_end.")]
    string EndBoundaryKind = "unknown")
{
    [Description("Authoritative wall-to-CPU ratio: WallUs / CpuUs; null when CpuUs is zero. This is not a percentage and may exceed 1.")]
    public double? WallToCpuRatio => WaitRatio;
}

public sealed record ProcessListResponse(
    IReadOnlyList<ProcessRow> Rows,
    int IdleProcessesHidden,
    int TotalCount,
    [property: Description("Generation/query-bound page context. Its exact TotalCount covers the complete includeSystem-filtered process inventory, not only this page.")]
    TimelinePageContext? PageContext = null,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: Description("Opaque qrc_ continuation. Repeat path, orderBy, top, and includeSystem unchanged; null means the inventory is complete.")]
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null);

// Per-child timing for one fork. Gap-from-previous lets clients spot burst patterns (e.g.,
// 23 children spawned in 56 seconds = 2.4s avg gap — was that uniform or clustered?), and
// FirstImageLoadOffsetUs measures the observed interval from the kernel ProcessStart event
// to the first mapped DLL. The interval can contain callbacks, scanning, suspension,
// scheduling, and other work; it does not identify which mechanism consumed the time.
public sealed record TimelinePageContext(
    [property: ToolOpaqueLocator("trace_id", "^trc_[0-9a-f]{32}$")]
    string TraceId,
    [property: ToolOpaqueLocator("trace_generation_id", "^tgen_[0-9a-f]{32}$")]
    string TraceGenerationId,
    string ToolName,
    string ContractVersion,
    [property: ToolOpaqueLocator("symbol_context_id", "^sym_[0-9a-f]{32}$")]
    string? SymbolContextId,
    string QueryHash,
    string Ordering,
    int StartIndex,
    int RequestedPageSize,
    int TotalCount,
    int ReturnedCount);

public sealed record ChildSpawnTiming(
    int Pid,
    string Name,
    long StartTimeUs,
    long? FirstImageLoadOffsetUs,
    int ImageLoadCount,
    long? GapFromPreviousSpawnUs,
    [property: Description("Stable source ordinal used as the final pagination tie-breaker; it is not a timestamp.")]
    long SourceOrdinal = 0);

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
    [property: Description("single_process for a safely resolved parent lifetime, or unresolved for scope_not_found, process_start_required, or conflicting observed stop evidence. Candidate lifetimes remain in IncludedProcesses.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("ok, scope_not_found, process_start_required for clean multi-lifetime reuse, or ambiguous_process_instance for conflicting lifetime evidence.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of child ProcessStart records matched to the selected parent lifetime.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, process_start_required, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, or null.")]
    string? NoDataReason = null,
    TimelinePageContext? PageContext = null,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null,
    [property: Description("Trace inventory children excluded because no observed ProcessStart record established an exact spawn time.")]
    int BackfilledChildrenExcluded = 0,
    [property: Description("Arithmetic mean of observed sibling spawn gaps, rounded to the nearest integer microsecond with midpoint away from zero.")]
    string AvgSpawnGapEstimator = "arithmetic_mean_nearest_integer_us_midpoint_away_from_zero",
    [property: Description("Median of observed ProcessStart-to-first-ImageLoad intervals; even N averages the two middle values and rounds to the nearest integer microsecond with midpoint away from zero.")]
    string MedianKernelGapEstimator = "median_even_n_middle_pair_mean_nearest_integer_us_midpoint_away_from_zero",
    [property: Description("P95 uses nearest-rank: sorted value at ceil(0.95*N)-1, zero-based.")]
    string P95KernelGapEstimator = "nearest_rank_ceil_0_95_n_minus_1",
    string TimingPrecision = "exact_integer_microsecond_inputs_declared_integer_rounding");

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
    string MetricAccounting = "exact_long",
    [property: Description("Exact number of distinct unresolved modules before TopUnresolvedModules is capped at 10.")]
    int UnresolvedModuleCount = 0);

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
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Each candidate's Thread.Generation can be passed as threadGeneration to replay equal-start lifetimes. Empty for process-only scopes and missing selectors.")]
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
    [property: Description("Exhaustive ordered per-core CPU breakdown for this returned thread row; despite the legacy TopCores name, this collection is not capped.")]
    IReadOnlyList<CpuCoreBucket> TopCores,
    long QuantumEndSwitches,
    long PreemptedSwitches,
    [property: Description("Trace-relative process start; combine with Pid to identify the process lifetime. Rows are never merged across this value.")]
    long ProcessStartUs = 0,
    [property: Description("Generation of this TID within the selected process lifetime. Rows are never merged across generations; pass this value as threadGeneration with pid and tid to replay the exact lifetime.")]
    long ThreadGeneration = 1,
    [property: Description("Trace-relative thread start. It can repeat across generations; combine Pid, Tid, and ThreadGeneration (plus ProcessStartUs when PID reuse is observed) to replay the exact thread instance.")]
    long ThreadStartUs = 0);

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
    [property: Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, ambiguous_thread_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, or null.")]
    string? NoDataReason = null,
    [property: Description("True when the analyzed trace contains at least one CSwitch event, regardless of the requested process/thread/window scope; null when trace-wide events were not scanned because scope resolution failed.")]
    bool? TraceHasContextSwitches = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Each candidate's Thread.Generation can be passed as threadGeneration to replay equal-start lifetimes. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    [property: Description("Whole-trace CSwitch event sides whose thread-instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedCSwitchSideCount = 0,
    [property: Description("Identity-unresolved CSwitch sides whose raw PID/TID/time matched the requested scope/window; they are not included in MatchedEventCount.")]
    long ScopedIdentityUnresolvedCSwitchSideCount = 0);

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
    [property: Description("Deprecated compatibility alias for TraceUnmatchedIntervalCount. It is trace-global, not scoped to the selected process/thread/window.")]
    int UnmatchedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    [property: Description("True only when a context switch matched the requested process/thread/window scope.")]
    bool HasContextSwitches = false,
    bool HasContextSwitchBlockingStacks = false,
    bool HasSampledProfileStacks = false,
    string SymbolResolutionState = "not_applicable",
    DomainStackCoverage? StackCoverage = null,
    [property: Description("Precision of Focus*Metric and caller/callee row metrics: exact_integer_count for unit-count samples, otherwise exact_long.")]
    string MetricPrecision = "exact_long",
    [property: Description("Machine-readable accounting of per-frame metrics from the parallel checked Int64 accumulator; TraceEvent's float StackSourceSample.Metric is not used for public integer values.")]
    string RowMetricAccounting = "exact_long",
    [property: Description("SourceTotalMetric and DomainStackCoverage totals are accumulated independently with checked 64-bit integer arithmetic.")]
    string ExactTotalAccounting = "exact_long",
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, ambiguous_thread_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, no_completed_intervals_in_scope, stacks_unavailable, focus_not_found, or null.")]
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Each candidate's Thread.Generation can be passed as threadGeneration to replay equal-start lifetimes. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    [property: Description("Whole-trace unmatched endpoint/interval count for interval-backed caller/callee sources; zero/default for point-event domains. Never attribute this field to the selected scope.")]
    int TraceUnmatchedIntervalCount = 0,
    [property: Description("Unmatched interval endpoints whose resolved evidence belongs to the selected process/thread and requested half-open window; zero/default for point-event domains.")]
    int ScopedUnmatchedIntervalCount = 0,
    [property: Description("Whether any context switch was observed anywhere in the trace for CSwitch-backed sources; null for non-scheduler domains.")]
    bool? TraceHasContextSwitches = null,
    [property: Description("Number of switch-out events matched to the selected process/thread/window for CSwitch-backed sources.")]
    long ScopedCSwitches = 0,
    [property: Description("Number of scoped switch-out events carrying the blocking stack used by the selected CSwitch-backed source.")]
    long ScopedStackedSwitches = 0,
    [property: Description("100 * ScopedStackedSwitches / ScopedCSwitches; null when the scoped denominator is zero or for non-scheduler domains.")]
    double? ScopedStackCoveragePct = null,
    [property: Description("Whole-trace raw start/stop or scheduler source-endpoint count for interval-backed caller/callee sources; zero/default for point-event domains.")]
    long TraceSourceEndpointCount = 0,
    [property: Description("Resolved raw source endpoints attributed to the selected process/thread/window for interval-backed caller/callee sources.")]
    long ScopedSourceEndpointCount = 0,
    [property: Description("Completed intervals projected into the selected scope. This is distinct from raw source endpoint counts.")]
    long MatchedIntervalCount = 0,
    [property: Description("Whole-trace source endpoints dropped because the process, thread, or CLR instance identity could not be resolved.")]
    long TraceIdentityUnresolvedEndpointCount = 0,
    [property: Description("Identity-unresolved source endpoints whose raw PID/TID/time could belong to the selected scope; they are not included in ScopedSourceEndpointCount.")]
    long ScopedIdentityUnresolvedEndpointCount = 0);

public sealed record CpuBatchScopeResult(
    int Pid,
    long? RequestedProcessStartUs,
    [property: Description("One of completed, completed_no_samples, scope_not_found, ambiguous_process_instance, budget_skipped, or analysis_failed.")]
    string ResultStatus,
    [property: Description("Process selector status: ok, scope_not_found, or ambiguous_process_instance.")]
    string ScopeStatus,
    [property: Description("One of single_process, pid_aggregate, or unresolved.")]
    string ScopeMode,
    ProcessInstanceKey? SelectedProcess,
    bool PidReuseObserved,
    IReadOnlyList<ProcessInstanceKey> IncludedProcesses,
    long MatchedSampleCount,
    [property: Description("observed when this selector matched CPU samples; otherwise unknown. unknown does not imply that the CPU keyword was disabled.")]
    string CapabilityStatus,
    string? NoDataReason = null,
    [property: Description("Authoritative per-selector boundary for ScopeResults[].Result.Rows.")]
    EmbeddedTopNBoundary? RowsBoundary = null,
    [property: Description("Authoritative per-selector fixed-limit boundary for ScopeResults[].Result.Stats.TopUnresolvedModules.")]
    EmbeddedTopNBoundary? TopUnresolvedModulesBoundary = null)
{
    [Description("Complete CPU projection for completed selectors; null for unavailable selectors.")]
    public CpuTopFunctionsResponse? Result { get; init; }
}

public sealed record CpuTopFunctionsBatchResponse(
    [property: Description("Request-ordered, indivisible per-selector status and CPU result rows.")]
    IReadOnlyList<CpuBatchScopeResult> ScopeResults,
    IReadOnlyList<string> Warnings,
    bool Partial = false,
    string? PartialErrorCode = null,
    int RequestedPidCount = 0,
    int CompletedPidCount = 0,
    TimelinePageContext? PageContext = null,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null,
    [property: Description("Opaque identity of the bounded immutable result snapshot reused by continuation pages.")]
    [property: ToolOpaqueLocator("cpu_batch_result_set", "^cbr_[0-9a-f]{32}$")]
    string? ResultSetId = null);

public sealed record FileMappingStateCounts(
    [property: Description("event_name events.")]
    long EventNameEventCount,
    [property: Description("temporal_file_key events.")]
    long TemporalFileKeyEventCount,
    [property: Description("temporal_file_object events.")]
    long TemporalFileObjectEventCount,
    [property: Description("ambiguous_temporal_mapping events; neither conflicting name was selected.")]
    long AmbiguousTemporalMappingEventCount,
    [property: Description("unresolved_file_identity events.")]
    long UnresolvedFileIdentityEventCount);

public sealed record FileIoRow(
    string File,
    long ReadBytes,
    long ReadCount,
    long WriteBytes,
    long WriteCount,
    [property: Description("Aggregate mapping state: event_name, temporal_file_key, temporal_file_object, ambiguous_temporal_mapping, unresolved_file_identity, or mixed. File sentinels are display-only.")]
    string MappingState,
    [property: Description("Exact state counts; sum equals ReadCount + WriteCount.")]
    FileMappingStateCounts MappingStateEventCounts);

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
    [property: Description("Process selector status: ok, scope_not_found, or ambiguous_process_instance.")]
    string ScopeStatus = "ok",
    [property: Description("Scoped source-event status: observed only when this resolved selector matched FileIO events; not_observed only when the event class was absent trace-wide; otherwise unknown. This never proves a capture keyword was disabled.")]
    string CapabilityStatus = "unknown",
    [property: Description("Number of FileIO Read/Write events matched before top-N file aggregation.")]
    long MatchedEventCount = 0,
    [property: Description("Stable reason for an empty result: scope_not_found, ambiguous_process_instance, no_events_in_scope, event_class_not_observed, or null when data matched.")]
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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
    string ExactTotalAccounting = "exact_long",
    ProcessInstanceKey? SelectedProcess = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
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
    long MaxLatencyTimeUs,
    [property: Description("Aggregate mapping state: event_name, temporal_file_key, unresolved_file_identity, or mixed. File sentinels are display-only.")]
    string MappingState,
    [property: Description("Exact state counts; sum equals PageInCount. FileObject states are zero.")]
    FileMappingStateCounts MappingStateEventCounts);

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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    [property: ToolDictionaryRows("name", "value")]
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
    [property: Description("Exhaustive ordinally sorted distinct event names accumulated for this returned provider aggregate; this nested list is not independently capped.")]
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
    [property: Description("Aggregate local-candidate state: exact_identity_match, candidate_identity_unverified, identity_mismatch, invalid_local_pdb_candidate, missing_pdb_identity, or not_found_in_local_symbol_path. This is not observed stack-frame resolution.")]
    string LookupStatus = "unknown",
    [property: Description("Human-readable evidence boundary for LookupStatus. Ambiguous Windows PDB/DIA failures are reported as unverified and never asserted to be candidate corruption.")]
    string? FailureReason = null,
    [property: Description("At most 10 verified-or-probed local candidate paths. All discovered candidates are validated before this display cap is applied; exact GUID/Age matches appear first and remaining paths retain configured discovery order. Remote SRV/UNC entries are not actively accessed, but the OS may redirect a local-looking root through a mapped drive or reparse point.")]
    IReadOnlyList<string>? LocalSymbolCandidates = null,
    bool HasPdbName = false,
    bool HasCompletePdbIdentity = false,
    [property: Description("True only when at least one discovered local PDB was opened and its actual GUID/Age exactly matched the trace identity. This does not mean stack frames were resolved.")]
    bool LocalPdbReady = false,
    string FrameResolutionState = "not_measured",
    string EvidenceScope = "module_metadata_and_local_candidate_probe",
    [property: Description("Total local candidates discovered and evaluated before LocalSymbolCandidates is capped for display.")]
    int LocalSymbolCandidateCount = 0,
    [property: Description("True when LocalSymbolCandidateCount exceeds the number of paths shown in LocalSymbolCandidates. Aggregate LookupStatus and LocalPdbReady still include every discovered candidate.")]
    bool LocalSymbolCandidatesTruncated = false);

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
    [property: Description("Deprecated compatibility alias of DefaultCacheDir. This is the fallback used by add_symbol_server when cacheDir is omitted; it does not prove that the current ConfiguredSymbolPath uses this directory.")]
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
    string FrameResolutionMeasurementState = "not_measured",
    [property: Description("Fallback cache directory used by add_symbol_server only when its cacheDir argument is omitted. It is not an inferred effective cache for arbitrary ConfiguredSymbolPath entries.")]
    string DefaultCacheDir = "");

public sealed record WaitReasonBucket(string Reason, long BlockedUs, long Count);

public sealed record WaitAnalysisRow(
    int Pid,
    string ProcessName,
    int Tid,
    long CpuUs,
    long BlockedUs,
    [property: Description("Deprecated alias of BlockedToCpuRatio; this is blocked_us / cpu_us, not a percentage and not bounded to [0,1].")]
    double? WaitRatio,
    [property: Description("Number of scoped CSwitch switch-out events for this thread instance. Switch-in appearances are not counted.")]
    long ContextSwitches,
    [property: Description("Exhaustive ordered wait-reason buckets for this returned thread row; despite the legacy TopWaitReasons name, this collection is not capped.")]
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    [property: Description("Trace-relative process start; combine with Pid to identify the process lifetime.")]
    long ProcessStartUs = 0,
    [property: Description("Generation of this TID within the selected process lifetime. Pass this value as threadGeneration with pid and tid to replay the exact lifetime.")]
    long ThreadGeneration = 0,
    [property: Description("Trace-relative thread start. It can repeat across generations; combine Pid, Tid, and ThreadGeneration (plus ProcessStartUs when PID reuse is observed) to replay the exact thread instance.")]
    long ThreadStartUs = 0)
{
    [Description("Authoritative blocked-to-CPU ratio: BlockedUs / CpuUs; null when CpuUs is zero. This is not a percentage and may exceed 1.")]
    public double? BlockedToCpuRatio => WaitRatio;
}

public sealed record WaitAnalysisResponse(
    IReadOnlyList<WaitAnalysisRow> Rows,
    [property: Description("Deprecated compatibility alias for WindowCSwitchesAllThreads. It is not scoped to the selected PID or thread.")]
    long TotalCSwitches,
    IReadOnlyList<string> Warnings,
    long TotalBlockedUs = 0,
    [property: Description("Deprecated compatibility alias for TraceUnmatchedBlockedIntervalCount. It is trace-global, not scoped to the selected process/thread/window.")]
    int UnmatchedBlockedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    [property: Description("True only when at least one context switch matched the requested process/thread/window scope.")]
    bool HasContextSwitches = false,
    [property: Description("True only when at least one selected switch-out event in the requested window has a blocking stack.")]
    bool HasContextSwitchBlockingStacks = false,
    string SymbolResolutionState = "not_applicable",
    [property: Description("All context switches from every thread in the requested half-open window; trace-window orientation only, not the selected-process denominator.")]
    long WindowCSwitchesAllThreads = 0,
    [property: Description("CSwitch switch-out events matched to the selected process/thread and requested half-open window.")]
    long ScopedCSwitches = 0,
    [property: Description("Scoped switch-out events carrying a blocking stack.")]
    long ScopedStackedSwitches = 0,
    [property: Description("100 * ScopedStackedSwitches / ScopedCSwitches; null when ScopedCSwitches is zero.")]
    double? ScopedStackCoveragePct = null,
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Resolved scoped CSwitch switch-out event count; equivalent to ScopedCSwitches and distinct from MatchedIntervalCount.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, ambiguous_thread_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, or null.")]
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Each candidate's Thread.Generation can be passed as threadGeneration to replay equal-start lifetimes. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    [property: Description("Whole-trace unmatched blocked-interval count. Never attribute this field to the selected process/thread/window.")]
    int TraceUnmatchedBlockedIntervalCount = 0,
    [property: Description("Unmatched blocked intervals whose endpoint evidence belongs to the selected process/thread and intersects the requested half-open window.")]
    int ScopedUnmatchedBlockedIntervalCount = 0,
    [property: Description("Whether any context switch was observed anywhere in the trace; distinct from scoped HasContextSwitches.")]
    bool? TraceHasContextSwitches = null,
    [property: Description("Whole-trace raw CSwitch event count. This is distinct from WindowCSwitchesAllThreads and scoped switch-outs.")]
    long TraceCSwitches = 0,
    [property: Description("Completed blocked intervals projected into the selected process/thread/window.")]
    long MatchedIntervalCount = 0,
    [property: Description("Whole-trace CSwitch event sides dropped because thread-instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedCSwitchSideCount = 0,
    [property: Description("Identity-unresolved CSwitch event sides whose raw PID/TID/time could belong to the selected scope.")]
    long ScopedIdentityUnresolvedCSwitchSideCount = 0);

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
    [property: Description("Deprecated compatibility alias for TraceUnmatchedBlockedIntervalCount. It is trace-global, not scoped to the selected process/thread/window.")]
    int UnmatchedBlockedIntervalCount = 0,
    ProcessInstanceKey? SelectedProcess = null,
    ThreadInstanceKey? SelectedThread = null,
    [property: Description("True only when at least one context switch matched the requested process/thread/window scope.")]
    bool HasContextSwitches = false,
    bool HasContextSwitchBlockingStacks = false,
    string SymbolResolutionState = "not_applicable",
    DomainStackCoverage? StackCoverage = null,
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
    string ExactTotalAccounting = "exact_long",
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Scope status: ok, scope_not_found, ambiguous_process_instance, or ambiguous_thread_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Resolved scoped CSwitch switch-out event count; equivalent to ScopedCSwitches and distinct from MatchedIntervalCount.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, ambiguous_thread_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, no_completed_intervals_in_scope, stacks_unavailable, focus_not_found, or null.")]
    string? NoDataReason = null,
    [property: Description("Resolved thread instance, or candidate thread instances when ScopeStatus is ambiguous_thread_instance. Each candidate's Thread.Generation can be passed as threadGeneration to replay equal-start lifetimes. Empty for process-only scopes and missing selectors.")]
    IReadOnlyList<ThreadScopeCandidate>? IncludedThreads = null,
    [property: Description("Whole-trace unmatched blocked-interval count. Never attribute this field to the selected process/thread/window.")]
    int TraceUnmatchedBlockedIntervalCount = 0,
    [property: Description("Unmatched blocked intervals whose endpoint evidence belongs to the selected process/thread and intersects the requested half-open window.")]
    int ScopedUnmatchedBlockedIntervalCount = 0,
    [property: Description("Whether any context switch was observed anywhere in the trace; distinct from scoped HasContextSwitches.")]
    bool? TraceHasContextSwitches = null,
    [property: Description("Switch-out events matched to the selected process/thread and requested half-open window.")]
    long ScopedCSwitches = 0,
    [property: Description("Scoped switch-out events carrying the blocking stack used by this response.")]
    long ScopedStackedSwitches = 0,
    [property: Description("100 * ScopedStackedSwitches / ScopedCSwitches; null when ScopedCSwitches is zero.")]
    double? ScopedStackCoveragePct = null,
    [property: Description("Whole-trace raw CSwitch event count. A nonzero value prevents event_class_not_observed even when no blocked interval completed.")]
    long TraceCSwitches = 0,
    [property: Description("Completed blocked intervals projected into the selected process/thread/window; equivalent to SampleCount for this response.")]
    long MatchedIntervalCount = 0,
    [property: Description("Whole-trace CSwitch event sides dropped because thread-instance identity was unresolved or ambiguous.")]
    long TraceIdentityUnresolvedCSwitchSideCount = 0,
    [property: Description("Identity-unresolved CSwitch event sides whose raw PID/TID/time could belong to the selected scope.")]
    long ScopedIdentityUnresolvedCSwitchSideCount = 0);

public sealed record ThreadComparisonWindowInput(
    string Name,
    long StartUs,
    long EndUs);

public sealed record ThreadComparisonWindowRow(
    string Name,
    long StartUs,
    long EndUs,
    long WindowDurationUs,
    [property: Description("Exact sampled-profile event count for this thread/window; this is not CPU time.")]
    long SampledCpuSamples,
    [property: Description("Scheduler-derived on-CPU duration from matched CSwitch intervals.")]
    long RunningUs,
    [property: Description("Scoped CSwitch events whose old or new thread is the selected exact instance.")]
    long ContextSwitches,
    long ReadyCount,
    [property: Description("ReadyThread-to-switch-in latency. It can overlap an off-CPU interval and is not additive with BlockedUs.")]
    long ReadyLatencyUs,
    [property: Description("Off-CPU switch-out-to-next-switch-in duration intersected with this window.")]
    long BlockedUs,
    [property: Description("Scoped switch-out endpoint count used by blocked-time analysis.")]
    long BlockedSwitchOutCount,
    [property: Description("Completed blocked intervals projected into this exact thread/window.")]
    long BlockedIntervalCount,
    IReadOnlyList<CpuFunctionRow> TopCpuFunctions,
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    [property: Description("Bounded frames from switch-out blocking stacks. These are associations, not proof of the responsible method or root cause.")]
    IReadOnlyList<WaitStackRow> TopWaitFunctions,
    DomainStackCoverage? CpuStackCoverage,
    DomainStackCoverage? WaitStackCoverage,
    string CpuCapabilityStatus,
    string SchedulerCapabilityStatus,
    string WaitCapabilityStatus,
    string WaitStackCapabilityStatus,
    string CpuSymbolResolutionState,
    string WaitSymbolResolutionState,
    string? CpuNoDataReason,
    string? SchedulerNoDataReason,
    string? WaitNoDataReason,
    string? WaitStackNoDataReason,
    IReadOnlyList<string> Warnings,
    string SampledCpuAccounting = "exact_integer_sample_count_not_cpu_time",
    string RunningAccounting = "scheduler_cswitch_interval_microseconds",
    string ReadyAccounting = "readythread_to_switch_in_latency_not_additive_with_blocked",
    string BlockedAccounting = "cswitch_out_to_next_switch_in_off_cpu_microseconds");

public sealed record ThreadCompareWindowsResponse(
    [property: Description("Request-ordered, indivisible per-window comparison rows.")]
    IReadOnlyList<ThreadComparisonWindowRow> Rows,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess,
    ThreadInstanceKey? SelectedThread,
    string ScopeMode,
    bool PidReuseObserved,
    IReadOnlyList<ProcessInstanceKey> IncludedProcesses,
    IReadOnlyList<ThreadScopeCandidate> IncludedThreads,
    string ScopeStatus,
    string CapabilityStatus,
    [property: Description("Sum of scheduler-side matched CSwitch event counts across comparison windows; CPU samples and wait endpoints are not mixed into this count.")]
    long MatchedEventCount,
    string? NoDataReason,
    [property: Description("Stable conclusion-boundary codes that apply to every comparison row.")]
    IReadOnlyList<string> DoesNotProve,
    string? BaselineWindowName,
    TimelinePageContext? PageContext = null,
    int TotalWindowCount = 0,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null,
    [property: Description("Opaque identity of the bounded immutable result snapshot reused by continuation pages.")]
    [property: ToolOpaqueLocator("thread_comparison_result_set", "^twr_[0-9a-f]{32}$")]
    string? ResultSetId = null);

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
    long? ProcessStartUs = null,
    [property: Description("Authoritative omission boundary for Frames. Stack summaries use the executed call's effective top; non-stack evidence reports an exact empty collection.")]
    EmbeddedTopNBoundary? FramesBoundary = null,
    [property: Description("Authoritative omission boundary for TopWaitReasons.")]
    EmbeddedTopNBoundary? TopWaitReasonsBoundary = null);

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
    [property: ToolMetricSemantics("ratio", "ratio", "decision_metric_denominator", 0, 1)]
    double? ObservedPct = null,
    [property: Description("Normalized decision threshold in [0,1]. Compare ObservedPct against this value.")]
    [property: ToolMetricSemantics("ratio", "ratio", "decision_metric_denominator", 0, 1)]
    double? ThresholdPct = null,
    long? ProcessStartUs = null,
    [property: Description("Stable boundary id for this not-concluded statement. This is not an evidence reference and need not resolve to CompositeEvidence.")]
    string? BoundaryId = null,
    [property: Description("Child process-scope status when this item represents an analyzer result.")]
    string? ScopeStatus = null,
    [property: Description("Child event-family status when this item represents an analyzer result.")]
    string? CapabilityStatus = null,
    [property: Description("Stable child no-data reason such as scope_not_found, ambiguous_process_instance, source_events_unattributed, event_class_not_observed, or no_events_in_scope.")]
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
    [property: ToolNumericSemantics("time_point", "microseconds_since_trace_start", "exact", "point")]
    long StartUs,
    [property: ToolNumericSemantics("time_point", "microseconds_since_trace_start", "exact", "point")]
    long EndUs,
    [property: ToolNumericSemantics("metric", "microseconds", "exact", "bucket_width")]
    long BucketWidthUs,
    [property: ToolNumericSemantics("metric", "dynamic", "exact", "bucket_sum", unitProperty: "Unit")]
    long[] Buckets,
    [property: Description("Unit of every Buckets element; values are not comparable across different units.")]
    string Unit)
{
    [Description("Authoritative exact-requested boundary for /when/buckets. Histogram construction always returns exactly the requested bucket count and never paginates or silently truncates it.")]
    public EmbeddedTopNBoundary BucketsBoundary => new(
        "/when/buckets",
        Buckets.LongLength,
        Buckets.LongLength,
        Buckets.LongLength,
        ToolSectionTotalState.Exact,
        ToolSectionMoreState.Absent,
        HasMore: false,
        ContinuationAvailable: false,
        TruncationReason: null,
        SortKey: "bucket_index_asc",
        SortDirection: ToolSortDirection.Ascending,
        TieBreakers: Array.Empty<string>());
}

public sealed record ImageLoadRow(
    long TimeUs,
    [property: Description("Microseconds since an observed ProcessStart. Null when the selected lifetime start came only from rundown or inventory backfill.")]
    long? TimeFromProcessStartUs,
    string FileName,
    long ImageSize,
    // Microseconds between this load and the previous one in chronological order. Null for
    // the first load (no prior). A long gap establishes only that no ImageLoad event for this
    // process lifetime was observed in the interval; it does not identify the intervening work.
    long? GapFromPrevUs,
    [property: Description("Stable source event ordinal used as the final pagination tie-breaker; it is not a timestamp.")]
    long EventIndex = 0);

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
    [property: Description("single_process for a safely resolved lifecycle, or unresolved for scope_not_found, process_start_required, or conflicting observed stop evidence. Candidate lifetimes remain in IncludedProcesses.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("ok, scope_not_found, process_start_required for clean multi-lifetime reuse, or ambiguous_process_instance for conflicting lifetime evidence.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ImageLoad source events matched to the selected process lifetime.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, process_start_required, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, or null.")]
    string? NoDataReason = null,
    TimelinePageContext? PageContext = null,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null,
    [property: Description("observed_process_start when startup-relative offsets are valid; inferred_start_boundary when ProcessStart was not observed; unresolved when scope selection failed.")]
    string ProcessStartEvidenceState = "unresolved");

public sealed record ImageLoadTopGapsResponse(
    int Pid,
    string ProcessName,
    long? ProcessStartUs,
    int TotalImageLoads,
    long? FirstLoadOffsetUs,
    IReadOnlyList<ImageLoadRow> TopGaps,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for a safely resolved lifecycle, or unresolved for scope_not_found, process_start_required, or conflicting observed stop evidence. Candidate lifetimes remain in IncludedProcesses.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("ok, scope_not_found, process_start_required for clean multi-lifetime reuse, or ambiguous_process_instance for conflicting lifetime evidence.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ImageLoad source events matched to the selected process lifetime. TopGaps may be truncated and excludes the first load.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, process_start_required, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, or null.")]
    string? NoDataReason = null,
    [property: Description("observed_process_start when startup-relative offsets are valid; inferred_start_boundary when ProcessStart was not observed; unresolved when scope selection failed.")]
    string ProcessStartEvidenceState = "unresolved");

public sealed record HighWaitCandidate(
    int Pid,
    string ProcessName,
    long TotalCpuUs,
    long TotalBlockedUs,
    [property: Description("Deprecated alias of BlockedToCpuRatio; this is blocked_us / cpu_us, not a percentage and not bounded to [0,1].")]
    double? WaitRatio,
    long ContextSwitches,
    [property: Description("Exhaustive collapsed wait-reason buckets for this candidate across its complete wait rows; the collection is not capped.")]
    IReadOnlyList<WaitReasonBucket> TopWaitReasons,
    string WaitAnalysisCallId,
    string? WaitStacksCallId,
    string? ReadyThreadCallId,
    [property: Description("Trace-relative process start. Combine with Pid; candidates from reused PID lifetimes are never merged.")]
    long ProcessStartUs = 0)
{
    [Description("Authoritative blocked-to-CPU ratio: TotalBlockedUs / TotalCpuUs; null when TotalCpuUs is zero. This is not a percentage and may exceed 1.")]
    public double? BlockedToCpuRatio => TotalCpuUs == 0
        ? null
        : TotalBlockedUs / (double)TotalCpuUs;
}

public sealed record DiagnoseHighWaitResponse(
    [property: Description("Ordered by total blocked microseconds, not impact, severity, or causality.")]
    IReadOnlyList<HighWaitCandidate> Candidates,
    [property: Description("Authoritative exact-total omission boundary for Candidates. Candidate aggregation is completed before maxCandidates is applied; no embedded continuation is exposed.")]
    EmbeddedTopNBoundary CandidateBoundary,
    IReadOnlyList<CompositeEvidence> Evidence,
    IReadOnlyList<CompositeNotConcluded> NotConcluded,
    IReadOnlyList<CompositeNextTool> NextTools,
    IReadOnlyList<CompositeToolCall> ExecutedToolCalls,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("One of all_processes, single_process, pid_aggregate, or unresolved.")]
    string ScopeMode = "all_processes",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Common process-selector status: ok, scope_not_found, or ambiguous_process_instance.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("True only when the post-wait fan-out budget omitted requested stack work; completed evidence remains usable.")]
    bool Partial = false,
    [property: Description("Stable partial boundary code. time_budget_exhausted accompanies Partial=true; null otherwise.")]
    string? PartialCode = null,
    [property: Description("Planner admission boundary for this composite. Until equivalence evidence is approved, the tool executes directly and planner logical/pass/scan counts remain null with unavailable states; no single-dispatch claim is made.")]
    PlannerExecutionTelemetry? PlannerExecution = null);

public sealed record WindowEvidenceSample(
    string? ProviderName,
    string? Process,
    string? Path,
    [property: ToolNumericSemantics("time_point", "microseconds_since_trace_start", "exact", "returned_row_point", minimum: 0)]
    long? TimeUs,
    [property: ToolNumericSemantics("metric", "events", "exact", "returned_row_source_count", minimum: 0)]
    long? EventCount,
    [property: ToolNumericSemantics("identifier", "process_id", "exact", "process_identity", minimum: 0)]
    int? Pid,
    [property: ToolNumericSemantics("time_point", "microseconds_since_trace_start", "exact", "process_instance_identity", minimum: 0)]
    long? ProcessStartUs,
    [property: Description("Always false for diagnostic samples that do not own or represent the aggregate evidence metric.")]
    bool Representative,
    [property: Description("Always false when the sample must not be used to attribute the containing WindowEvidenceRow.MetricValue.")]
    bool MetricAttributable,
    [property: Description("Machine-readable sampling scope, currently returned_rows_only.")]
    string SampleScope);

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
    [property: Description("Raw PID selector for process-scoped evidence. For single_process it is paired with ProcessStartUs to identify one lifetime; for pid_aggregate it intentionally names only the reused PID and ProcessStartUs is null; null for all-process or window-global evidence.")]
    int? Pid,
    [property: Description("Exact-process scope label only when ScopeMode=single_process and the returned sample identity matches Pid + ProcessStartUs. Null for all_processes, pid_aggregate, window_global, or unproven attribution; never infer aggregate metric ownership from sample details.")]
    string? ProcessName,
    [property: Description("Exact file grouping key for MetricValue when the metric is grouped by one file; null for cross-file aggregate metrics. Paths in Samples are contextual returned-row examples and do not own MetricValue unless MetricAttributable is explicitly true.")]
    string? File,
    [property: Description("Microseconds since trace start only when this timestamp directly locates MetricValue (for example maxLatencyUs). Null for aggregate metrics; timestamps in Samples are contextual returned-row examples and do not anchor MetricValue unless MetricAttributable is explicitly true.")]
    long? TimeUs,
    [property: Description("Ordered human-readable annotations. Security returned-row samples are never encoded here; use Samples and SamplesBoundary. DetailsBoundary is present only when the annotations themselves are sampled, such as wait-reason details.")]
    IReadOnlyList<string> Details,
    [property: Description("Structured non-representative samples separated from explanatory Details. Empty when this evidence type emits no samples.")]
    IReadOnlyList<WindowEvidenceSample> Samples,
    [property: Description("Authoritative omission and ordering boundary for Samples; null exactly when Samples is empty and the evidence type has no sample surface.")]
    EmbeddedTopNBoundary? SamplesBoundary,
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
    string EvidenceScope = "process_scope",
    [property: Description("Stable evidence id within this composite response.")]
    string? EvidenceId = null,
    [property: Description("CompositeToolCall.CallId that produced this evidence.")]
    string? CallId = null,
    [property: Description("Optional authoritative boundary for Details when the annotations themselves are sampled. Returned equals Details.Count; null for exhaustive fixed annotations.")]
    EmbeddedTopNBoundary? DetailsBoundary = null);

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
    [property: Description("Process lifetime candidates included by the common selector/window; empty for all_processes and missing scopes, but may be populated for ambiguous_process_instance diagnostics.")]
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("Common process-selector status: ok, scope_not_found, ambiguous_process_instance, or not_evaluated for a pre-load width guard.")]
    string ScopeStatus = "ok",
    [property: Description("observed when any scoped child event family matched; not_observed only when every child class was unobserved; otherwise unknown.")]
    string CapabilityStatus = "unknown",
    [property: Description("Sum of scoped event counts from distinct child event families; hard-fault events are counted once across both sort views.")]
    long MatchedEventCount = 0,
    [property: Description("Stable composite empty-result reason; inspect NotConcluded for per-child scope and capability reasons.")]
    string? NoDataReason = null,
    [property: Description("Planner admission boundary for this composite. Until equivalence evidence is approved, the tool executes directly and planner logical/pass/scan counts remain null with unavailable states; no single-dispatch claim is made.")]
    PlannerExecutionTelemetry? PlannerExecution = null);

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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    [property: Description("window_global means SystemMemory and system-pressure samples use only the requested time window, not pid filtering. When ScopeStatus is not ok these fields are intentionally empty.")]
    string SystemMemoryScope = "window_global",
    string ScopeStatus = "ok",
    [property: Description("observed when target-process evidence matched; partial when only window-global system-memory evidence matched; not_observed when no supported event class appears anywhere in the trace; unknown when scope/evidence cannot support a conclusion.")]
    string CapabilityStatus = "unknown",
    [property: Description("Count of target-process evidence in scope: Memory/ProcessMemInfo process entries plus matched handle events plus matched pool events. System-memory samples are counted separately by Pressure.SystemSampleCount and are excluded.")]
    long MatchedEventCount = 0,
    string? NoDataReason = null,
    [property: Description("Process or event-side identities required by supported process/handle/pool evidence that were unresolved anywhere in the trace. This diagnostic is not MatchedEventCount.")]
    long TraceIdentityUnresolvedEventCount = 0,
    [property: Description("Identity-unresolved source evidence whose raw PID and timestamp could belong to the selected scope/window; it is not attributed or counted in MatchedEventCount.")]
    long ScopedIdentityUnresolvedEventCount = 0);

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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    long ThreadGeneration = 1,
    [property: Description("Start endpoint provenance: observed or process_start.")]
    string StartBoundaryKind = "unknown",
    [property: Description("End endpoint provenance: observed, replacement, process_end, trace_end, or inferred_boundary.")]
    string EndBoundaryKind = "unknown",
    [property: Description("exact_observed_interval only when both endpoints were observed; otherwise bounded_by_inferred_endpoint.")]
    string MeasurementState = "unknown");

public sealed record ThreadLifetimeResponse(
    int Pid,
    string ProcessName,
    int TotalThreads,
    int PeakConcurrentThreads,
    IReadOnlyList<ThreadLifetimeRow> Threads,
    IReadOnlyList<string> Warnings,
    ProcessInstanceKey? SelectedProcess = null,
    [property: Description("single_process for a safely resolved lifecycle, or unresolved for scope_not_found, process_start_required, or conflicting observed stop evidence. Candidate lifetimes remain in IncludedProcesses.")]
    string ScopeMode = "single_process",
    bool PidReuseObserved = false,
    IReadOnlyList<ProcessInstanceKey>? IncludedProcesses = null,
    [property: Description("ok, scope_not_found, process_start_required for clean multi-lifetime reuse, or ambiguous_process_instance for conflicting lifetime evidence.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of ThreadStart/Stop or thread-rundown source records attributed to the selected process lifetime. TotalThreads instead counts projected logical thread lifecycles before top truncation.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, process_start_required, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, or null.")]
    string? NoDataReason = null,
    TimelinePageContext? PageContext = null,
    int ReturnedCount = 0,
    bool HasMore = false,
    [property: ToolOpaqueLocator("query_result_cursor", "^qrc_[0-9a-f]{32}$")]
    string? NextCursor = null,
    [property: Description("Number of materialized thread lifetimes excluded because EndTimeUs <= StartTimeUs.")]
    int InvalidLifetimeCount = 0,
    [property: Description("Number of observed ThreadStart/ThreadStop endpoints attributed to the selected process lifetime. Only this positive count can support CapabilityStatus=observed.")]
    long MatchedObservedEndpointCount = 0,
    [property: Description("Number of ThreadDCStart/ThreadDCStop rundown endpoints attributed to the selected process lifetime. Rundown-only evidence yields CapabilityStatus=partial, never observed.")]
    long MatchedRundownEndpointCount = 0);

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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    string MetricPrecision = "exact_long",
    string RowMetricAccounting = "exact_long",
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
    [property: Description("ok, scope_not_found, process_start_required for a PID trend spanning clean reused lifetimes, or ambiguous_process_instance for conflicting lifetime evidence.")]
    string ScopeStatus = "ok",
    string CapabilityStatus = "unknown",
    [property: Description("Number of GCHeapStats source events matched to the requested process scope and half-open window.")]
    long MatchedEventCount = 0,
    [property: Description("Stable empty-result reason: scope_not_found, process_start_required, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, or null.")]
    string? NoDataReason = null,
    [property: Description("Whole-trace GCHeapStats events whose process lifetime was unresolved or ambiguous. This diagnostic is not MatchedEventCount.")]
    long TraceIdentityUnresolvedEventCount = 0,
    [property: Description("GCHeapStats events whose raw PID/time matched the requested scope/window but whose process lifetime could not be attributed safely.")]
    long ScopedIdentityUnresolvedEventCount = 0);

public sealed record FinalizedTypeRow(string TypeName, long Count);

// One TCP connection paired Connect/Accept → Disconnect/Reconnect by emitter process
// lifetime + `connid`. CloseTimeUs and DurationUs are null when no closing event was observed;
// TraceResidentEnd distinguishes connections that remained open at trace end. A repeated open
// is only a connid-slot replacement boundary and is never projected as an exact close.
public sealed record NetConnectionRow(
    int Pid,
    [property: Description("Authoritative exact TCP connection identifier as a canonical unsigned decimal string. Use this field for identity, comparison, and replay; ConnId is only a deprecated JavaScript-safe numeric projection.")]
    string ConnIdText,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    [property: Range(0d, 9_007_199_254_740_991d)]
    [property: ToolSafeIntegerCompatibility("ConnIdText", "ConnIdLegacyStatus")]
    [property: Description("Deprecated numeric projection of ConnIdText. Exact only when the identifier is <= 9007199254740991 (JavaScript Number.MAX_SAFE_INTEGER); required null for larger identifiers. Never substitute a rounded value.")]
    ulong? ConnId,
    [property: Description("Precision/deprecation state of ConnId: exact_safe_integer_deprecated when ConnId is an exact projection, or null_unsafe_integer_deprecated when ConnId is null because the identifier exceeds JavaScript's safe-integer range. ConnIdText remains authoritative in both cases.")]
    string ConnIdLegacyStatus,
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
    [property: Description("How the row ended: disconnect, reconnect, replaced_open_unobserved, process_end_unobserved, or trace_end_unobserved. Only disconnect/reconnect are observed closes with exact CloseTimeUs and DurationUs; every unobserved state requires both fields to be null.")]
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
    [property: Description("Stable empty-result reason: scope_not_found, ambiguous_process_instance, event_class_not_observed, no_events_in_scope, source_events_unattributed, or unpaired_endpoints_in_scope.")]
    string? NoDataReason = null,
    [property: Description("Number of in-scope Disconnect/Reconnect endpoints that could not be paired with a preceding Connect/Accept for the same process instance and connection ID.")]
    long UnpairedCloseCount = 0,
    [property: Description("Number of projected rows whose connid slot received a second Connect/Accept before a Disconnect/Reconnect was observed. These rows have EndState=replaced_open_unobserved and null CloseTimeUs/DurationUs; the replacement timestamp is not an exact close.")]
    long ReplacedOpenUnobservedCount = 0,
    [property: Description("Selected-PID (or all-process) source endpoints anywhere in the trace whose process lifetime could not be resolved. This is diagnostic and is not MatchedEventCount.")]
    long TraceIdentityUnresolvedEndpointCount = 0,
    [property: Description("Identity-unresolved source endpoints whose raw PID and timestamp fall in the requested scope/window; these are not attributed or counted in MatchedEventCount.")]
    long ScopedIdentityUnresolvedEndpointCount = 0);
