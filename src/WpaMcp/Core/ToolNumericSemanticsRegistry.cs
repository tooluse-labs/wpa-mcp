using System.Reflection;
using WpaMcp.Output;

namespace WpaMcp.Core;

internal sealed record ReviewedNumericSemantics(
    string Role,
    string Unit,
    string Precision,
    string Aggregation,
    string? Denominator = null,
    string? UnitField = null,
    double Minimum = double.NaN,
    double Maximum = double.NaN,
    bool DeprecatedAlias = false,
    string? Replacement = null);

/// <summary>
/// Exact DTO/property registry for reviewed numeric semantics that would be noisy to
/// repeat on every stack-row record. There are no suffix or substring fallbacks here:
/// adding a DTO/property does not inherit a denominator until it is explicitly listed.
/// </summary>
internal static class ToolNumericSemanticsRegistry
{
    private static readonly IReadOnlyDictionary<(Type Type, string Property), ReviewedNumericSemantics> Entries =
        Build();

    internal static bool TryGet(PropertyInfo property, out ReviewedNumericSemantics semantics) =>
        Entries.TryGetValue((property.DeclaringType!, property.Name), out semantics!);

    private static IReadOnlyDictionary<(Type Type, string Property), ReviewedNumericSemantics> Build()
    {
        var entries = new Dictionary<(Type, string), ReviewedNumericSemantics>();

        var scopedStackPercent = Percent("scoped_source_total_metric");
        var traceStackPercent = Percent("trace_source_total_metric");
        foreach (var type in new[]
        {
            typeof(CpuFunctionRow),
            typeof(WaitStackRow),
            typeof(FileIoStackRow),
            typeof(DiskIoStackRow),
            typeof(HardFaultStackRow),
            typeof(ImageLoadStackRow),
            typeof(VirtualAllocStackRow),
            typeof(NetIoStackRow),
            typeof(RegistryStackRow),
            typeof(ReadyThreadStackRow),
            typeof(InterruptStackRow),
            typeof(AlpcStackRow),
            typeof(ClrAllocStackRow),
            typeof(ClrExceptionStackRow),
            typeof(HeapAllocStackRow),
            typeof(GenericEventStackRow),
            typeof(ClrContentionStackRow),
        })
        {
            AddIfPresent(entries, type, "ExclusivePct", scopedStackPercent);
            AddIfPresent(entries, type, "InclusivePct", scopedStackPercent);
            AddIfPresent(entries, type, "ExclusivePctOfTrace", traceStackPercent);
            AddIfPresent(entries, type, "InclusivePctOfTrace", traceStackPercent);
        }

        Add(entries, typeof(CallerCalleeNode), nameof(CallerCalleeNode.ExclusivePct), scopedStackPercent);
        Add(entries, typeof(CallerCalleeNode), nameof(CallerCalleeNode.InclusivePct), scopedStackPercent);
        Add(entries, typeof(CallerCalleeResponse), nameof(CallerCalleeResponse.FocusExclusivePct), scopedStackPercent);
        Add(entries, typeof(CallerCalleeResponse), nameof(CallerCalleeResponse.FocusInclusivePct), scopedStackPercent);

        Add(entries, typeof(DomainStackCoverage), nameof(DomainStackCoverage.StackCoveragePct),
            Percent("totalEventCount"));
        Add(entries, typeof(DomainStackCoverage), nameof(DomainStackCoverage.MetricStackCoveragePct),
            Percent("totalMetric"));
        Add(entries, typeof(WaitAnalysisResponse), nameof(WaitAnalysisResponse.ScopedStackCoveragePct),
            Percent("scopedCSwitches"));
        Add(entries, typeof(WaitTopStacksResponse), nameof(WaitTopStacksResponse.ScopedStackCoveragePct),
            Percent("scopedCSwitches"));
        Add(entries, typeof(CallerCalleeResponse), nameof(CallerCalleeResponse.ScopedStackCoveragePct),
            Percent("scopedCSwitches"));

        Add(entries, typeof(ProcessRow), nameof(ProcessRow.WaitRatio),
            Ratio("cpuUs", "wall_us_divided_by_cpu_us", "wallToCpuRatio"));
        Add(entries, typeof(ProcessRow), nameof(ProcessRow.WallToCpuRatio),
            Ratio("cpuUs", "wall_us_divided_by_cpu_us"));
        Add(entries, typeof(WaitAnalysisRow), nameof(WaitAnalysisRow.WaitRatio),
            Ratio("cpuUs", "blocked_us_divided_by_cpu_us", "blockedToCpuRatio"));
        Add(entries, typeof(WaitAnalysisRow), nameof(WaitAnalysisRow.BlockedToCpuRatio),
            Ratio("cpuUs", "blocked_us_divided_by_cpu_us"));
        Add(entries, typeof(HighWaitCandidate), nameof(HighWaitCandidate.WaitRatio),
            Ratio("totalCpuUs", "total_blocked_us_divided_by_total_cpu_us", "blockedToCpuRatio"));
        Add(entries, typeof(HighWaitCandidate), nameof(HighWaitCandidate.BlockedToCpuRatio),
            Ratio("totalCpuUs", "total_blocked_us_divided_by_total_cpu_us"));
        Add(entries, typeof(SlowStartupCandidate), nameof(SlowStartupCandidate.StartupWaitRatio),
            Ratio("startupCpuUs", "observed_startup_wall_us_divided_by_startup_cpu_us", "observedStartupWallToCpuRatio"));
        Add(entries, typeof(SlowStartupCandidate), nameof(SlowStartupCandidate.ObservedStartupWallToCpuRatio),
            Ratio("startupCpuUs", "observed_startup_wall_us_divided_by_startup_cpu_us"));
        Add(entries, typeof(SlowStartupCandidate), nameof(SlowStartupCandidate.LifetimeWaitRatio),
            Ratio("lifetimeCpuUs", "lifetime_wall_us_divided_by_lifetime_cpu_us", "lifetimeWallToCpuRatio"));
        Add(entries, typeof(SlowStartupCandidate), nameof(SlowStartupCandidate.LifetimeWallToCpuRatio),
            Ratio("lifetimeCpuUs", "lifetime_wall_us_divided_by_lifetime_cpu_us"));

        Add(entries, typeof(CpuCoreBucket), nameof(CpuCoreBucket.CpuPct),
            Percent("containing_thread_cpu_us"));

        Add(entries, typeof(TraceMeta), nameof(TraceMeta.ParserCoverageRate),
            UnitRatio("rawEtwRecordCount", "materialized_logical_event_count_divided_by_raw_etw_record_count"));
        Add(entries, typeof(SymbolStatus), nameof(SymbolStatus.CompletePdbIdentityRate),
            UnitRatio("moduleCount", "modules_with_complete_pdb_identity_divided_by_module_count"));
        Add(entries, typeof(InspectSymbolQuality), nameof(InspectSymbolQuality.ModulesWithPdbNameRate),
            UnitRatio("moduleCount", "modules_with_pdb_name_divided_by_module_count"));
        Add(entries, typeof(InspectSymbolQuality), nameof(InspectSymbolQuality.CompletePdbIdentityRate),
            UnitRatio("moduleCount", "modules_with_complete_pdb_identity_divided_by_module_count"));
        Add(entries, typeof(SymbolStats), nameof(SymbolStats.ResolutionRate),
            UnitRatio("uniqueCodeFrameCount", "deprecated_alias_unique_resolved_code_frames_divided_by_unique_code_frames"));
        Add(entries, typeof(SymbolStats), nameof(SymbolStats.ObservedUniqueCodeFrameNameResolutionRate),
            UnitRatio("uniqueCodeFrameCount", "unique_resolved_code_frame_count_divided_by_unique_code_frame_count"));
        Add(entries, typeof(SymbolStats), nameof(SymbolStats.ObservedMetricWeightedCodeFrameNameResolutionRate),
            UnitRatio("totalCodeFrameMetric", "resolved_code_frame_metric_divided_by_total_code_frame_metric"));
        Add(entries, typeof(PrepareSymbolsResponse), nameof(PrepareSymbolsResponse.FrameResolutionRate),
            UnitRatio("framesAttempted", "frames_resolved_divided_by_frames_attempted"));

        Add(entries, typeof(CpuPreciseThreadRow), nameof(CpuPreciseThreadRow.AvgReadyLatencyUs),
            Mean("microseconds", "readyCount"));
        Add(entries, typeof(SecurityScanTargetRow), nameof(SecurityScanTargetRow.AvgDurationUs),
            Mean("microseconds", "pairedScanCount"));
        Add(entries, typeof(SecurityScanTargetRow), nameof(SecurityScanTargetRow.AvgAccountedDurationUs),
            Mean("microseconds", "pairedScanCount"));

        Add(entries, typeof(CompositeEvidence), nameof(CompositeEvidence.MetricValue),
            DynamicExactMetric("unit"));
        Add(entries, typeof(WindowEvidenceRow), nameof(WindowEvidenceRow.MetricValue),
            DynamicExactMetric("unit"));
        Add(entries, typeof(FrameMetric), nameof(FrameMetric.ExclusiveMetric),
            DynamicExactMetric("unit"));
        Add(entries, typeof(FrameMetric), nameof(FrameMetric.InclusiveMetric),
            DynamicExactMetric("unit"));
        Add(entries, typeof(CompositeNotConcluded), nameof(CompositeNotConcluded.MetricValue),
            new ReviewedNumericSemantics(
                "metric", "dynamic", "rounded_binary64", "observed_value",
                UnitField: "unit"));

        AddEnvelopeSemantics(entries);
        AddTimelineSemantics(entries);
        AddStackMetricSemantics(entries);
        AddSymbolMetricSemantics(entries);
        ToolNumericSemanticsReviewedManifest.Populate(entries);

        return entries;
    }

    private static void AddEnvelopeSemantics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        AddMany(entries, typeof(ToolScopeSelector), Identifier("process_id"), nameof(ToolScopeSelector.Pid));
        AddMany(entries, typeof(ToolScopeSelector), Identifier("thread_id"), nameof(ToolScopeSelector.Tid));
        AddMany(entries, typeof(ToolScopeSelector), TimePoint(),
            nameof(ToolScopeSelector.ProcessStartUs),
            nameof(ToolScopeSelector.ThreadStartUs),
            nameof(ToolScopeSelector.WindowStartUs),
            nameof(ToolScopeSelector.WindowEndUs));
        AddMany(entries, typeof(ToolScopeIdentity), Identifier("process_id"), nameof(ToolScopeIdentity.Pid));
        AddMany(entries, typeof(ToolScopeIdentity), Identifier("thread_id"), nameof(ToolScopeIdentity.Tid));
        AddMany(entries, typeof(ToolScopeIdentity), TimePoint(),
            nameof(ToolScopeIdentity.ProcessStartUs),
            nameof(ToolScopeIdentity.ThreadStartUs));
        AddMany(entries, typeof(ToolScope), ExactMetric("identity_count", "count"),
            nameof(ToolScope.CandidateTotal),
            nameof(ToolScope.IncludedTotal));

        AddMany(entries, typeof(ToolSectionPage), ExactMetric("row_count", "requested_limit"),
            nameof(ToolSectionPage.Requested));
        AddMany(entries, typeof(ToolSectionPage), ExactMetric("row_count", "returned_count"),
            nameof(ToolSectionPage.Returned));
        AddMany(entries, typeof(ToolSectionPage), ExactMetric("row_count", "available_count"),
            nameof(ToolSectionPage.TotalAvailable));
        var traceEligibleCount = ExactMetric(
            "evidence_count",
            "whole_trace_evaluator_eligible_count");
        var scopedMatchedCount = ExactMetric(
            "evidence_count",
            "selected_scope_matched_count");
        Add(entries, typeof(ToolCapabilityEvidence),
            nameof(ToolCapabilityEvidence.TraceEligibleEventCount),
            traceEligibleCount);
        Add(entries, typeof(ToolCapabilityEvidence),
            nameof(ToolCapabilityEvidence.ScopedMatchedEventCount),
            scopedMatchedCount);
        Add(entries, typeof(ToolCapabilityEvidence),
            nameof(ToolCapabilityEvidence.TotalEventCount),
            traceEligibleCount with
            {
                DeprecatedAlias = true,
                Replacement = "traceEligibleEventCount",
            });
        Add(entries, typeof(ToolCapabilityEvidence),
            nameof(ToolCapabilityEvidence.MatchedEventCount),
            scopedMatchedCount with
            {
                DeprecatedAlias = true,
                Replacement = "scopedMatchedEventCount",
            });
        AddMany(entries, typeof(ToolCapabilityEvidence),
            ExactMetric("evidence_count", "whole_trace_completion_evidence_count"),
            nameof(ToolCapabilityEvidence.TraceCompletedEvidenceCount),
            nameof(ToolCapabilityEvidence.TraceUnmatchedEvidenceCount),
            nameof(ToolCapabilityEvidence.TraceBoundaryEvidenceCount));
        AddMany(entries, typeof(ToolCompleteness), ExactMetric("section_count", "count"),
            nameof(ToolCompleteness.RequestedSectionCount),
            nameof(ToolCompleteness.SectionsWithData),
            nameof(ToolCompleteness.FailedSectionCount));
        Add(entries, typeof(TraceWorkflowEvidenceRecord),
            nameof(TraceWorkflowEvidenceRecord.TotalCapabilityCount),
            ExactMetric("capability_count", "workflow_membership_count"));
        AddMany(entries, typeof(TraceWorkflowEvidenceRecord),
            ExactMetric("capability_count", "workflow_trace_status_bucket_count"),
            nameof(TraceWorkflowEvidenceRecord.AvailableCapabilityCount),
            nameof(TraceWorkflowEvidenceRecord.PartialCapabilityCount),
            nameof(TraceWorkflowEvidenceRecord.UnknownCapabilityCount),
            nameof(TraceWorkflowEvidenceRecord.UnavailableCapabilityCount),
            nameof(TraceWorkflowEvidenceRecord.NotApplicableCapabilityCount));
    }

    private static void AddTimelineSemantics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        Add(entries, typeof(TimelinePageContext), nameof(TimelinePageContext.StartIndex),
            ExactMetric("row_index", "cursor_offset"));
        Add(entries, typeof(TimelinePageContext), nameof(TimelinePageContext.RequestedPageSize),
            ExactMetric("row_count", "requested_limit"));
        Add(entries, typeof(TimelinePageContext), nameof(TimelinePageContext.TotalCount),
            ExactMetric("row_count", "available_count"));
        Add(entries, typeof(TimelinePageContext), nameof(TimelinePageContext.ReturnedCount),
            ExactMetric("row_count", "returned_count"));
        Add(entries, typeof(ChildSpawnTiming), nameof(ChildSpawnTiming.SourceOrdinal),
            ExactMetric("source_event_ordinal", "source_sequence"));
        Add(entries, typeof(ImageLoadRow), nameof(ImageLoadRow.EventIndex),
            ExactMetric("source_event_ordinal", "source_sequence"));
        Add(entries, typeof(ProcessCreateTimingResponse), nameof(ProcessCreateTimingResponse.ReturnedCount),
            ExactMetric("row_count", "returned_count"));
        Add(entries, typeof(ProcessCreateTimingResponse), nameof(ProcessCreateTimingResponse.BackfilledChildrenExcluded),
            ExactMetric("process_count", "excluded_count"));
        Add(entries, typeof(ImageLoadTimingResponse), nameof(ImageLoadTimingResponse.ReturnedCount),
            ExactMetric("row_count", "returned_count"));
        Add(entries, typeof(ThreadLifetimeResponse), nameof(ThreadLifetimeResponse.ReturnedCount),
            ExactMetric("row_count", "returned_count"));
        Add(entries, typeof(ThreadLifetimeResponse), nameof(ThreadLifetimeResponse.InvalidLifetimeCount),
            ExactMetric("thread_lifetime_count", "excluded_count"));
        Add(entries, typeof(ThreadLifetimeResponse), nameof(ThreadLifetimeResponse.MatchedObservedEndpointCount),
            ExactMetric("thread_endpoint_count", "matched_count"));
        Add(entries, typeof(ThreadLifetimeResponse), nameof(ThreadLifetimeResponse.MatchedRundownEndpointCount),
            ExactMetric("thread_endpoint_count", "matched_count"));
    }

    private static void AddStackMetricSemantics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        AddMany(entries, typeof(DomainStackCoverage), ExactMetric("event_count", "count"),
            nameof(DomainStackCoverage.TotalEventCount),
            nameof(DomainStackCoverage.StackedEventCount));
        AddMany(entries, typeof(DomainStackCoverage), DynamicExactMetric("metricName", "sum"),
            nameof(DomainStackCoverage.TotalMetric),
            nameof(DomainStackCoverage.StackedMetric));

        AddMany(entries, typeof(CpuFunctionRow), ExactMetric("sample_count", "per_frame_exclusive"),
            nameof(CpuFunctionRow.ExclusiveSamples));
        AddMany(entries, typeof(CpuFunctionRow), ExactMetric("sample_count", "per_frame_inclusive"),
            nameof(CpuFunctionRow.InclusiveSamples));
        AddMany(entries, typeof(WaitStackRow), ExactMetric("microseconds", "per_frame_exclusive"),
            nameof(WaitStackRow.ExclusiveBlockedUs));
        AddMany(entries, typeof(WaitStackRow), ExactMetric("microseconds", "per_frame_inclusive"),
            nameof(WaitStackRow.InclusiveBlockedUs));

        AddFramePair(entries, typeof(FileIoStackRow), "bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(DiskIoStackRow), "bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(HardFaultStackRow), "bytes", "ExclusivePageInBytes", "InclusivePageInBytes");
        AddFramePair(entries, typeof(VirtualAllocStackRow), "virtual_memory_operation_bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(NetIoStackRow), "bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(ClrAllocStackRow), "bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(HeapAllocStackRow), "bytes", "ExclusiveBytes", "InclusiveBytes");
        AddFramePair(entries, typeof(InterruptStackRow), "microseconds", "ExclusiveUs", "InclusiveUs");
        AddFramePair(entries, typeof(ClrContentionStackRow), "microseconds", "ExclusiveBlockedUs", "InclusiveBlockedUs");
        AddFramePair(entries, typeof(ClrContentionStackRow), "accounted_microseconds", "ExclusiveAccountedBlockedUs", "InclusiveAccountedBlockedUs");

        AddFramePair(entries, typeof(FileIoStackRow), "operation_count", "ExclusiveOpCount", "InclusiveOpCount");
        AddFramePair(entries, typeof(DiskIoStackRow), "operation_count", "ExclusiveOpCount", "InclusiveOpCount");
        AddFramePair(entries, typeof(HardFaultStackRow), "fault_count", "ExclusiveFaultCount", "InclusiveFaultCount");
        AddFramePair(entries, typeof(VirtualAllocStackRow), "operation_count", "ExclusiveOpCount", "InclusiveOpCount");
        AddFramePair(entries, typeof(NetIoStackRow), "operation_count", "ExclusiveOpCount", "InclusiveOpCount");
        AddFramePair(entries, typeof(RegistryStackRow), "operation_count", "ExclusiveOps", "InclusiveOps");
        AddFramePair(entries, typeof(ReadyThreadStackRow), "event_count", "ExclusiveReadyCount", "InclusiveReadyCount");
        AddFramePair(entries, typeof(InterruptStackRow), "event_count", "ExclusiveCount", "InclusiveCount");
        AddFramePair(entries, typeof(AlpcStackRow), "event_count", "ExclusiveEvents", "InclusiveEvents");
        AddFramePair(entries, typeof(ClrAllocStackRow), "event_count", "ExclusiveEventCount", "InclusiveEventCount");
        AddFramePair(entries, typeof(ClrExceptionStackRow), "event_count", "ExclusiveCount", "InclusiveCount");
        AddFramePair(entries, typeof(HeapAllocStackRow), "event_count", "ExclusiveEventCount", "InclusiveEventCount");
        AddFramePair(entries, typeof(GenericEventStackRow), "event_count", "ExclusiveCount", "InclusiveCount");
        AddFramePair(entries, typeof(ClrContentionStackRow), "interval_count", "ExclusiveCount", "InclusiveCount");

        AddMany(entries, typeof(CallerCalleeNode),
            DynamicExactMetric("$.data.metricName", "per_frame_exclusive"),
            nameof(CallerCalleeNode.ExclusiveMetric));
        AddMany(entries, typeof(CallerCalleeNode),
            DynamicExactMetric("$.data.metricName", "per_frame_inclusive"),
            nameof(CallerCalleeNode.InclusiveMetric));
        AddMany(entries, typeof(CallerCalleeResponse),
            DynamicExactMetric("metricName", "per_frame_exclusive"),
            nameof(CallerCalleeResponse.FocusExclusiveMetric));
        AddMany(entries, typeof(CallerCalleeResponse),
            DynamicExactMetric("metricName", "per_frame_inclusive"),
            nameof(CallerCalleeResponse.FocusInclusiveMetric));
        AddMany(entries, typeof(CallerCalleeResponse),
            DynamicExactMetric("metricName", "source_scope_total"),
            nameof(CallerCalleeResponse.SourceTotalMetric));
    }

    private static void AddSymbolMetricSemantics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        AddMany(entries, typeof(SymbolStatus), ExactMetric("module_count", "count"),
            nameof(SymbolStatus.ModuleCount),
            nameof(SymbolStatus.ModulesWithPdbName),
            nameof(SymbolStatus.ModulesWithCompletePdbIdentity));
        AddMany(entries, typeof(InspectSymbolQuality), ExactMetric("module_count", "count"),
            nameof(InspectSymbolQuality.ModuleCount),
            nameof(InspectSymbolQuality.ModulesWithPdbName),
            nameof(InspectSymbolQuality.ModulesWithCompletePdbIdentity));
        AddMany(entries, typeof(PrepareSymbolsResponse), ExactMetric("module_count", "count"),
            nameof(PrepareSymbolsResponse.ModulesWithPdbIdentity),
            nameof(PrepareSymbolsResponse.ModulesWithVerifiedSymbolArtifact));
        AddMany(entries, typeof(PrepareSymbolsResponse), ExactMetric("artifact_count", "count"),
            nameof(PrepareSymbolsResponse.VerifiedSymbolArtifactCount));
        AddMany(entries, typeof(PrepareSymbolsResponse), ExactMetric("frame_count", "count"),
            nameof(PrepareSymbolsResponse.FramesAttempted),
            nameof(PrepareSymbolsResponse.FramesResolved));

        AddMany(entries, typeof(SymbolStats), ExactMetric("unique_code_frame_count", "count"),
            nameof(SymbolStats.UniqueCodeFrameCount),
            nameof(SymbolStats.UniqueResolvedCodeFrameCount),
            nameof(SymbolStats.UniqueUnresolvedCodeFrameCount));
        AddMany(entries, typeof(SymbolStats), DynamicExactMetric("metricName", "code_frame_occurrence_sum"),
            nameof(SymbolStats.TotalCodeFrameMetric),
            nameof(SymbolStats.ResolvedCodeFrameMetric),
            nameof(SymbolStats.UnresolvedCodeFrameMetric),
            nameof(SymbolStats.ExcludedSyntheticOrPseudoFrameMetric));
        AddMany(entries, typeof(SymbolStats), ExactMetric("unique_frame_count", "count"),
            nameof(SymbolStats.ExcludedSyntheticOrPseudoUniqueFrames));
        AddMany(entries, typeof(UnresolvedModule), ExactMetric("frame_count", "count"),
            nameof(UnresolvedModule.FrameCount));
    }

    private static ReviewedNumericSemantics Percent(string denominator) =>
        new("metric", "percent", "rounded_binary64", "ratio", denominator, Minimum: 0, Maximum: 100);

    private static ReviewedNumericSemantics Ratio(
        string denominator,
        string aggregation,
        string? replacement = null) =>
        new(
            "metric",
            "ratio",
            "rounded_binary64",
            aggregation,
            denominator,
            Minimum: 0,
            DeprecatedAlias: replacement is not null,
            Replacement: replacement);

    private static ReviewedNumericSemantics UnitRatio(string denominator, string aggregation) =>
        new("metric", "ratio", "rounded_binary64", aggregation, denominator, Minimum: 0, Maximum: 1);

    private static ReviewedNumericSemantics Mean(string unit, string denominator) =>
        new("metric", unit, "rounded_binary64", "mean", denominator, Minimum: 0);

    private static ReviewedNumericSemantics DynamicExactMetric(
        string unitField,
        string aggregation = "value") =>
        new("metric", "dynamic", "exact", aggregation, UnitField: unitField);

    private static ReviewedNumericSemantics ExactMetric(string unit, string aggregation) =>
        new("metric", unit, "exact", aggregation);

    private static ReviewedNumericSemantics Identifier(string unit) =>
        new("identifier", unit, "exact", "not_applicable");

    private static ReviewedNumericSemantics TimePoint() =>
        new("time_point", "microseconds_since_trace_start", "exact", "point");

    private static void AddFramePair(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries,
        Type type,
        string unit,
        string exclusive,
        string inclusive)
    {
        Add(entries, type, exclusive, ExactMetric(unit, "per_frame_exclusive"));
        Add(entries, type, inclusive, ExactMetric(unit, "per_frame_inclusive"));
    }

    private static void AddMany(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries,
        Type type,
        ReviewedNumericSemantics semantics,
        params string[] properties)
    {
        foreach (var property in properties)
            Add(entries, type, property, semantics);
    }

    private static void AddIfPresent(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries,
        Type type,
        string property,
        ReviewedNumericSemantics semantics)
    {
        if (type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance) is not null)
            Add(entries, type, property, semantics);
    }

    private static void Add(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries,
        Type type,
        string property,
        ReviewedNumericSemantics semantics)
    {
        if (type.GetProperty(property, BindingFlags.Public | BindingFlags.Instance) is null)
            throw new InvalidOperationException($"Reviewed numeric property '{type.Name}.{property}' does not exist.");
        if (!entries.TryAdd((type, property), semantics))
            throw new InvalidOperationException($"Reviewed numeric property '{type.Name}.{property}' is duplicated.");
    }
}
