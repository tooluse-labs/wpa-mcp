using System.Reflection;

namespace WpaMcp.Core;

/// <summary>
/// Closed, property-identity manifest for the remaining active output numeric surface.
/// Entries are intentionally exact <c>DeclaringType.Property</c> pairs. No suffix rule
/// applies semantics to a newly-added field; the active-schema closure test forces an
/// explicit review whenever the surface changes.
/// </summary>
internal static class ToolNumericSemanticsReviewedManifest
{
    internal static void Populate(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        AddIdentifiersAndCategories(entries);
        AddTimePoints(entries);
        AddByteMetrics(entries);
        AddDurationMetrics(entries);
        AddCountMetrics(entries);
        AddRemainingNumerics(entries);
    }

    private static void AddIdentifiersAndCategories(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        Add(entries, NonMetric("identifier", "process_id"),
            "WpaMcp.Core.ProcessInstanceKey|Pid",
            "WpaMcp.Output.ChildSpawnTiming|Pid",
            "WpaMcp.Output.CompositeEvidence|Pid",
            "WpaMcp.Output.CompositeNextTool|AwakenedPid,Pid",
            "WpaMcp.Output.CompositeNotConcluded|Pid",
            "WpaMcp.Output.CompositeToolCall|AwakenedPid,Pid",
            "WpaMcp.Output.CpuBatchScopeResult|Pid",
            "WpaMcp.Output.CpuPreciseThreadRow|Pid",
            "WpaMcp.Output.DiagnoseWindowResponse|Pid",
            "WpaMcp.Output.FinalizerAnalysisResponse|Pid",
            "WpaMcp.Output.FinalizerBatchRow|Pid",
            "WpaMcp.Output.GcAnalysisResponse|Pid",
            "WpaMcp.Output.GcEventRow|Pid",
            "WpaMcp.Output.GcHeapStatsResponse|Pid",
            "WpaMcp.Output.GcHeapStatsRow|Pid",
            "WpaMcp.Output.HighWaitCandidate|Pid",
            "WpaMcp.Output.ImageLoadTimingResponse|Pid",
            "WpaMcp.Output.ImageLoadTopGapsResponse|Pid",
            "WpaMcp.Output.JitAnalysisResponse|Pid",
            "WpaMcp.Output.JitMethodRow|Pid",
            "WpaMcp.Output.MemoryHandleProcessRow|Pid",
            "WpaMcp.Output.MemoryPoolProcessRow|Pid",
            "WpaMcp.Output.MemoryPressureProcessRow|Pid",
            "WpaMcp.Output.MemoryResourceProcessRow|Pid",
            "WpaMcp.Output.NetConnectionRow|Pid",
            "WpaMcp.Output.NetConnectionsResponse|Pid",
            "WpaMcp.Output.ProcessCreateTimingResponse|ParentPid",
            "WpaMcp.Output.ProcessRow|ParentPid,Pid",
            "WpaMcp.Output.SecurityScanRequestRow|Pid",
            "WpaMcp.Output.SecurityScanTargetRow|Pid",
            "WpaMcp.Output.SlowStartupCandidate|ParentPid,Pid",
            "WpaMcp.Output.StartupGapEvidenceRow|Pid",
            "WpaMcp.Output.StartupProcessExclusionRow|Pid",
            "WpaMcp.Output.StartupWindowProvenance|Pid",
            "WpaMcp.Output.ThreadLifetimeResponse|Pid",
            "WpaMcp.Output.WaitAnalysisRow|Pid",
            "WpaMcp.Output.WindowEvidenceRow|Pid");

        Add(entries, NonMetric("identifier", "thread_id"),
            "WpaMcp.Core.ThreadInstanceKey|Tid",
            "WpaMcp.Output.CompositeEvidence|Tid",
            "WpaMcp.Output.CpuPreciseThreadRow|Tid",
            "WpaMcp.Output.MarkerRow|ThreadId",
            "WpaMcp.Output.ThreadLifetimeRow|Tid",
            "WpaMcp.Output.WaitAnalysisRow|Tid");
        Add(entries, NonMetric("identifier", "connection_id"),
            "WpaMcp.Output.NetConnectionRow|ConnId");
        Add(entries, NonMetric("identifier", "clr_instance_id"),
            "WpaMcp.Output.GcEventRow|ClrInstanceId");

        Add(entries, NonMetric("category", "generation_index"),
            "WpaMcp.Core.ThreadInstanceKey|Generation",
            "WpaMcp.Output.CpuPreciseThreadRow|ThreadGeneration",
            "WpaMcp.Output.ThreadLifetimeRow|ThreadGeneration",
            "WpaMcp.Output.WaitAnalysisRow|ThreadGeneration");
        Add(entries, NonMetric("category", "gc_sequence_number"),
            "WpaMcp.Output.GcEventRow|GcCount");
        Add(entries, NonMetric("category", "gc_generation"),
            "WpaMcp.Output.GcEventRow|Generation");
        Add(entries, NonMetric("category", "processor_index"),
            "WpaMcp.Output.CpuCoreBucket|Core",
            "WpaMcp.Output.CpuPreciseThreadRow|PrimaryCore");
        Add(entries, NonMetric("category", "network_port"),
            "WpaMcp.Output.NetConnectionRow|LocalPort,RemotePort");
        Add(entries, NonMetric("category", "pdb_age"),
            "WpaMcp.Output.PreparedSymbolModuleIdentity|PdbAge");
        Add(entries, NonMetric("configuration", "frame_count_threshold"),
            "WpaMcp.Output.SymbolStats|WarmSymbolThreshold");
        Add(entries, NonMetric("configuration", "megahertz"),
            "WpaMcp.Output.TraceSystemConfiguration|CpuSpeedMhz");
        Add(entries, NonMetric("configuration", "logical_processor_count"),
            "WpaMcp.Output.TraceSystemConfiguration|ProcessorCount");
        Add(entries, NonMetric("configuration", "minutes_from_utc"),
            "WpaMcp.Output.TraceSystemConfiguration|UtcOffsetMinutes");
    }

    private static void AddTimePoints(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        var point = NonMetric("time_point", "microseconds_since_trace_start");
        Add(entries, point,
            "WpaMcp.Core.ProcessInstanceKey|StartUs",
            "WpaMcp.Core.ThreadScopeCandidate|ThreadEndUs,ThreadStartUs",
            "WpaMcp.Output.ChildSpawnTiming|StartTimeUs",
            "WpaMcp.Output.CompositeEvidence|ProcessStartUs",
            "WpaMcp.Output.CompositeNextTool|AwakenedProcessStartUs,EndUs,ProcessStartUs,StartUs",
            "WpaMcp.Output.CompositeNotConcluded|ProcessStartUs",
            "WpaMcp.Output.CompositeToolCall|AwakenedProcessStartUs,EndUs,ParentEndUs,ParentStartUs,ProcessStartUs,StartUs,TargetProcessStartUs",
            "WpaMcp.Output.CpuBatchScopeResult|RequestedProcessStartUs",
            "WpaMcp.Output.CpuPreciseThreadRow|ProcessStartUs,ThreadStartUs",
            "WpaMcp.Output.DiagnoseWindowResponse|WindowEndUs,WindowStartUs",
            "WpaMcp.Output.FinalizerBatchRow|EndUs,ProcessStartUs,StartUs",
            "WpaMcp.Output.GcEventRow|EndUs,ProcessStartUs,StartUs",
            "WpaMcp.Output.GcHeapStatsRow|ProcessStartUs,TimeUs",
            "WpaMcp.Output.HardFaultFileRow|MaxLatencyTimeUs",
            "WpaMcp.Output.HighWaitCandidate|ProcessStartUs",
            "WpaMcp.Output.ImageLoadRow|TimeUs",
            "WpaMcp.Output.ImageLoadTimingResponse|ProcessStartUs",
            "WpaMcp.Output.ImageLoadTopGapsResponse|ProcessStartUs",
            "WpaMcp.Output.JitMethodRow|EndUs,ProcessStartUs,StartUs",
            "WpaMcp.Output.MarkerRow|TimeUs",
            "WpaMcp.Output.MemoryHandleProcessRow|ProcessStartUs",
            "WpaMcp.Output.MemoryPoolProcessRow|ProcessStartUs",
            "WpaMcp.Output.MemoryPressureProcessRow|ProcessStartUs",
            "WpaMcp.Output.MemoryPressureSummary|MaxModifiedTimeUs,MaxObservedTotalCommitTimeUs,MaxObservedTotalPrivateTimeUs,MaxObservedTotalWorkingSetTimeUs,MinAvailableTimeUs,MinFreeTimeUs",
            "WpaMcp.Output.MemoryResourceProcessRow|FirstSampleUs,LastSampleUs,ProcessStartUs",
            "WpaMcp.Output.MemoryResourceSystemRow|TimeUs",
            "WpaMcp.Output.NetConnectionRow|CloseTimeUs,OpenTimeUs,ProcessStartUs",
            "WpaMcp.Output.ProcessCreateTimingResponse|FirstSpawnTimeUs,LastSpawnTimeUs,ParentProcessStartUs",
            "WpaMcp.Output.ProcessRow|EndUs,StartUs",
            "WpaMcp.Output.SecurityScanRequestRow|ProcessStartUs,StartUs,StopUs",
            "WpaMcp.Output.SecurityScanTargetRow|ProcessStartUs",
            "WpaMcp.Output.SlowStartupCandidate|ProcessStartUs,StartupEndUs",
            "WpaMcp.Output.StartupGapEvidenceRow|ChildEndUs,ChildStartUs,FirstImageLoadTimeUs,ProcessStartUs",
            "WpaMcp.Output.StartupProcessExclusionRow|ProcessStartUs",
            "WpaMcp.Output.StartupWindowProvenance|EndUs,ProcessStartUs,RequestedEndUs,StartUs",
            "WpaMcp.Output.ThreadLifetimeRow|EndTimeUs,ProcessStartUs,StartTimeUs",
            "WpaMcp.Output.ThreadComparisonWindowRow|EndUs,StartUs",
            "WpaMcp.Output.WaitAnalysisRow|ProcessStartUs,ThreadStartUs",
            "WpaMcp.Output.WindowEvidenceRow|ProcessStartUs,TimeUs");
    }

    private static void AddByteMetrics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        Add(entries, Metric("bytes", "sum"),
            "WpaMcp.Output.ClrAllocStacksResponse|TotalBytes",
            "WpaMcp.Output.ClrAllocTypeRow|Bytes",
            "WpaMcp.Output.DiskIoStacksResponse|TotalBytes",
            "WpaMcp.Output.FileIoRow|ReadBytes,WriteBytes",
            "WpaMcp.Output.FileIoStacksResponse|TotalBytes",
            "WpaMcp.Output.HardFaultFileRow|PageInBytes",
            "WpaMcp.Output.HardFaultStacksResponse|TotalPageInBytes",
            "WpaMcp.Output.HeapAllocStacksResponse|AllocBytes,ReallocBytes,TotalBytes",
            "WpaMcp.Output.MemoryPoolProcessRow|NonPagedAllocatedBytes,NonPagedFreedBytes,PagedAllocatedBytes,PagedFreedBytes",
            "WpaMcp.Output.MemoryPoolTagRow|AllocatedBytes,FreedBytes",
            "WpaMcp.Output.NetIoStacksResponse|TcpBytes,TotalBytes,UdpBytes",
            "WpaMcp.Output.VirtualAllocStacksResponse|AllocatedBytes,FreedBytes,TotalBytes,TotalOperationBytes");

        Add(entries, Metric("bytes", "observed_value"),
            "WpaMcp.Output.GcHeapStatsRow|FinalizationPromotedBytes,Gen0Bytes,Gen1Bytes,Gen2Bytes,LohBytes,PohBytes,TotalHeapBytes",
            "WpaMcp.Output.ImageLoadRow|ImageSize",
            "WpaMcp.Output.MemoryResourceProcessRow|CommitBytes,CommitDebtBytes,PrivateBytes,PrivateWorkingSetBytes,SharedCommitBytes,StoreBytes,VirtualSizeBytes,WorkingSetBytes",
            "WpaMcp.Output.MemoryResourceSystemRow|BadBytes,FreeBytes,ModifiedBytes,ModifiedNoWriteBytes,ZeroBytes",
            "WpaMcp.Output.TraceDriverModule|ImageSizeBytes");

        Add(entries, Metric("bytes", "maximum"),
            "WpaMcp.Output.MemoryPressureProcessRow|PeakCommitBytes,PeakPrivateBytes,PeakWorkingSetBytes",
            "WpaMcp.Output.MemoryPressureSummary|MaxModifiedBytes,MaxObservedTotalCommitBytes,MaxObservedTotalPrivateBytes,MaxObservedTotalWorkingSetBytes",
            "WpaMcp.Output.MemoryResourceProcessRow|PeakCommitBytes,PeakPrivateBytes,PeakPrivateWorkingSetBytes,PeakWorkingSetBytes");
        Add(entries, Metric("bytes", "minimum"),
            "WpaMcp.Output.MemoryPressureSummary|MinAvailableBytes,MinFreeBytes");
        Add(entries, Metric("bytes", "observed_outstanding"),
            "WpaMcp.Output.MemoryPoolProcessRow|NonPagedOutstandingBytes,PagedOutstandingBytes",
            "WpaMcp.Output.MemoryPoolTagRow|OutstandingBytes");
        Add(entries, Metric("bytes", "signed_alloc_minus_free"),
            "WpaMcp.Output.VirtualAllocStacksResponse|NetObservedOperationBytes");
    }

    private static void AddDurationMetrics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        Add(entries, Metric("microseconds", "sum"),
            "WpaMcp.Output.ClrContentionStacksResponse|TotalAccountedBlockedUs,TotalBlockedUs,TotalFullBlockedUs",
            "WpaMcp.Output.CpuPreciseResponse|TotalCpuUs,TotalReadyLatencyUs",
            "WpaMcp.Output.DiagnoseWindowResponse|SecurityTotalDurationUs",
            "WpaMcp.Output.FinalizerAnalysisResponse|TotalAccountedBatchUs,TotalBatchUs,TotalFullBatchUs",
            "WpaMcp.Output.GcAnalysisResponse|TotalAccountedGcUs,TotalAccountedPauseUs,TotalFullGcUs,TotalFullPauseUs,TotalGcUs,TotalPauseUs",
            "WpaMcp.Output.InterruptStacksResponse|DpcUs,IsrUs,TotalUs",
            "WpaMcp.Output.JitAnalysisResponse|TotalAccountedJitUs,TotalFullJitUs,TotalJitUs",
            "WpaMcp.Output.SecurityScanAnalysisResponse|TotalAccountedDurationUs,TotalDurationUs,TotalFullDurationUs",
            "WpaMcp.Output.SecurityScanTargetRow|TotalAccountedDurationUs,TotalDurationUs,TotalFullDurationUs",
            "WpaMcp.Output.WaitAnalysisResponse|TotalBlockedUs",
            "WpaMcp.Output.WaitTopStacksResponse|TotalBlockedUs");

        Add(entries, Metric("microseconds", "interval_duration"),
            "WpaMcp.Output.ChildSpawnTiming|FirstImageLoadOffsetUs,GapFromPreviousSpawnUs",
            "WpaMcp.Output.CpuCoreBucket|CpuUs",
            "WpaMcp.Output.CpuPreciseThreadRow|CpuUs,ReadyLatencyUs",
            "WpaMcp.Output.DiagnoseWindowResponse|DurationUs",
            "WpaMcp.Output.FinalizerBatchRow|AccountedDurationUs,DurationUs,FullDurationUs",
            "WpaMcp.Output.GcEventRow|AccountedDurationUs,AccountedPauseUs,DurationUs,FullDurationUs,FullPauseUs,PauseUs",
            "WpaMcp.Output.HighWaitCandidate|TotalBlockedUs,TotalCpuUs",
            "WpaMcp.Output.ImageLoadRow|GapFromPrevUs,TimeFromProcessStartUs",
            "WpaMcp.Output.ImageLoadTimingResponse|FirstLoadOffsetUs",
            "WpaMcp.Output.ImageLoadTopGapsResponse|FirstLoadOffsetUs",
            "WpaMcp.Output.JitMethodRow|AccountedDurationUs,FullDurationUs,JitDurationUs",
            "WpaMcp.Output.NetConnectionRow|DurationUs",
            "WpaMcp.Output.ProcessRow|CpuUs,WallUs",
            "WpaMcp.Output.SecurityScanRequestRow|AccountedDurationUs,DurationUs,FullDurationUs",
            "WpaMcp.Output.SlowStartupCandidate|LifetimeCpuUs,LifetimeWallUs,ObservedStartupWallUs,StartupBlockedUs,StartupCpuUs",
            "WpaMcp.Output.StartupGapEvidenceRow|FirstImageLoadOffsetUs",
            "WpaMcp.Output.StartupWindowProvenance|TraceDurationUs",
            "WpaMcp.Output.ThreadLifetimeRow|LifetimeUs",
            "WpaMcp.Output.ThreadComparisonWindowRow|BlockedUs,ReadyLatencyUs,RunningUs,WindowDurationUs",
            "WpaMcp.Output.TraceMeta|DurationUs",
            "WpaMcp.Output.WaitAnalysisRow|BlockedUs,CpuUs",
            "WpaMcp.Output.WaitReasonBucket|BlockedUs");

        Add(entries, Metric("microseconds", "maximum"),
            "WpaMcp.Output.CpuPreciseThreadRow|MaxReadyLatencyUs",
            "WpaMcp.Output.HardFaultFileRow|MaxLatencyUs",
            "WpaMcp.Output.ImageLoadTimingResponse|MaxGapUs",
            "WpaMcp.Output.ProcessCreateTimingResponse|MaxKernelGapUs",
            "WpaMcp.Output.SecurityScanTargetRow|MaxAccountedDurationUs,MaxDurationUs");
        Add(entries, Metric("microseconds", "mean", "rounded_integer"),
            "WpaMcp.Output.ProcessCreateTimingResponse|AvgSpawnGapUs");
        Add(entries, Metric("microseconds", "median", "rounded_integer"),
            "WpaMcp.Output.ProcessCreateTimingResponse|MedianKernelGapUs");
        Add(entries, Metric("microseconds", "percentile_95", "rounded_integer"),
            "WpaMcp.Output.ProcessCreateTimingResponse|P95KernelGapUs");
    }

    private static void AddCountMetrics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        var count = Metric("count", "count");
        Add(entries, count,
            "WpaMcp.Output.AlpcStacksResponse|MatchedEventCount,ReceiveCount,SendCount,TotalEvents",
            "WpaMcp.Output.CallerCalleeResponse|MatchedEventCount,MatchedIntervalCount,ScopedCSwitches,ScopedIdentityUnresolvedEndpointCount,ScopedSourceEndpointCount,ScopedStackedSwitches,ScopedUnmatchedIntervalCount,TraceIdentityUnresolvedEndpointCount,TraceSourceEndpointCount,TraceUnmatchedIntervalCount,UnmatchedIntervalCount",
            "WpaMcp.Output.ChildSpawnTiming|ImageLoadCount",
            "WpaMcp.Output.ClrAllocStacksResponse|MatchedEventCount,TotalEventCount",
            "WpaMcp.Output.ClrContentionStacksResponse|InvalidIntervalCount,MatchedEventCount,MatchedIntervalCount,ScopedIdentityUnresolvedEndpointCount,ScopedSourceEndpointCount,ScopedUnmatchedIntervalCount,TotalEventCount,TraceIdentityUnresolvedEndpointCount,TraceSourceEndpointCount,TraceUnmatchedIntervalCount,UnmatchedIntervalCount",
            "WpaMcp.Output.ClrExceptionStacksResponse|MatchedEventCount,TotalEventCount",
            "WpaMcp.Output.ClrExceptionTypeRow|Count",
            "WpaMcp.Output.CpuBatchScopeResult|MatchedSampleCount",
            "WpaMcp.Output.CpuPreciseResponse|MatchedEventCount,ScopedIdentityUnresolvedCSwitchSideCount,TotalContextSwitches,TotalReadyCount,TraceIdentityUnresolvedCSwitchSideCount",
            "WpaMcp.Output.CpuPreciseThreadRow|ContextSwitches,PreemptedSwitches,QuantumEndSwitches,ReadyCount",
            "WpaMcp.Output.CpuTopFunctionsBatchResponse|CompletedPidCount,RequestedPidCount,ReturnedCount",
            "WpaMcp.Output.CpuTopFunctionsResponse|MatchedEventCount,TotalSamples",
            "WpaMcp.Output.DiagnoseHighWaitResponse|MatchedEventCount",
            "WpaMcp.Output.DiagnoseWindowResponse|MatchedEventCount,SecurityMatchedEventCount,SecurityPairedScanCount",
            "WpaMcp.Output.DiskIoStacksResponse|MatchedEventCount,TotalOpCount",
            "WpaMcp.Output.DriverModuleSummary|TotalDriverModuleCount",
            "WpaMcp.Output.EmbeddedTopNBoundary|Returned,TotalAvailable",
            "WpaMcp.Output.FileIoResponse|MatchedEventCount",
            "WpaMcp.Output.FileIoRow|ReadCount,WriteCount",
            "WpaMcp.Output.FileIoStacksResponse|MatchedEventCount,TotalOpCount",
            "WpaMcp.Output.FileMappingStateCounts|AmbiguousTemporalMappingEventCount,EventNameEventCount,TemporalFileKeyEventCount,TemporalFileObjectEventCount,UnresolvedFileIdentityEventCount",
            "WpaMcp.Output.FinalizedTypeRow|Count",
            "WpaMcp.Output.FinalizerAnalysisResponse|InvalidIntervalCount,MatchedBatchCount,MatchedBatchEndpointEventCount,MatchedEventCount,MatchedObjectEventCount,ScopedIdentityUnresolvedEventCount,TraceIdentityUnresolvedEventCount,UnmatchedIntervalCount",
            "WpaMcp.Output.GcAnalysisResponse|Gen0Count,Gen1Count,Gen2Count,IncompleteClrIdentityCount,InvalidIntervalCount,MatchedEventCount,MatchedIntervalCount,ScopedIdentityUnresolvedEndpointCount,ScopedInvalidIntervalCount,ScopedUnmatchedGcIntervalCount,ScopedUnmatchedPauseIntervalCount,TotalGcCount,TraceIdentityUnresolvedEndpointCount,TraceInvalidIntervalCount,TraceUnmatchedGcIntervalCount,TraceUnmatchedGcStartCount,TraceUnmatchedGcStopCount,TraceUnmatchedPauseIntervalCount,TraceUnmatchedPauseStartCount,TraceUnmatchedPauseStopCount,UnmatchedGcIntervalCount,UnmatchedPauseIntervalCount",
            "WpaMcp.Output.GcHeapStatsResponse|MatchedEventCount,ScopedIdentityUnresolvedEventCount,TraceIdentityUnresolvedEventCount",
            "WpaMcp.Output.GcHeapStatsRow|FinalizationPromotedCount,GcHandleCount,PinnedObjectCount",
            "WpaMcp.Output.GenericEventNameRow|Count",
            "WpaMcp.Output.GenericEventStacksResponse|MatchedEventCount,TotalEventCount",
            "WpaMcp.Output.HardFaultByFileResponse|MatchedEventCount",
            "WpaMcp.Output.HardFaultFileRow|PageInCount",
            "WpaMcp.Output.HardFaultStacksResponse|MatchedEventCount,TotalFaultCount",
            "WpaMcp.Output.HeapAllocStacksResponse|MatchedEventCount,TotalEventCount",
            "WpaMcp.Output.HighWaitCandidate|ContextSwitches",
            "WpaMcp.Output.ImageLoadStacksResponse|MatchedEventCount",
            "WpaMcp.Output.ImageLoadTimingResponse|MatchedEventCount",
            "WpaMcp.Output.ImageLoadTopGapsResponse|MatchedEventCount",
            "WpaMcp.Output.InterruptStacksResponse|MatchedEventCount,TotalCount",
            "WpaMcp.Output.JitAnalysisResponse|InvalidIntervalCount,MatchedEventCount,MatchedIntervalCount,ScopedIdentityUnresolvedEndpointCount,ScopedInvalidIntervalCount,ScopedUnmatchedIntervalCount,ScopedUnmatchedStartCount,ScopedUnmatchedStopCount,TraceIdentityUnresolvedEndpointCount,TraceInvalidIntervalCount,TraceUnmatchedIntervalCount,TraceUnmatchedStartCount,TraceUnmatchedStopCount,UnmatchedIntervalCount",
            "WpaMcp.Output.MarkerCountRow|Count",
            "WpaMcp.Output.MarkerSearchResponse|MatchedEventCount",
            "WpaMcp.Output.MemoryPoolProcessRow|AllocationCount,FreeCount,UnknownFreeCount",
            "WpaMcp.Output.MemoryPoolTagRow|AllocationCount,FreeCount,UnknownFreeCount",
            "WpaMcp.Output.MemoryPressureSummary|ProcessSnapshotBatchCount,SystemSampleCount",
            "WpaMcp.Output.MemoryResourceProcessRow|SampleCount",
            "WpaMcp.Output.MemoryResourceResponse|HandleEventCount,MatchedEventCount,PoolEventCount,ProcessSampleCount,ScopedIdentityUnresolvedEventCount,TraceIdentityUnresolvedEventCount",
            "WpaMcp.Output.NetConnectionsResponse|MatchedEventCount,ReplacedOpenUnobservedCount,ScopedIdentityUnresolvedEndpointCount,TraceIdentityUnresolvedEndpointCount,UnpairedCloseCount",
            "WpaMcp.Output.NetIoStacksResponse|MatchedEventCount,TotalOpCount",
            "WpaMcp.Output.ProcessCreateTimingResponse|MatchedEventCount,SpawnCount",
            "WpaMcp.Output.ProcessListResponse|ReturnedCount,TotalCount",
            "WpaMcp.Output.ProcessRow|ImageLoadCount",
            "WpaMcp.Output.ProviderEventCount|EventCount",
            "WpaMcp.Output.ProviderEventCountSummary|OtherEventCount,RawEtwRecordCount,TotalEventCount,TotalProviderCount",
            "WpaMcp.Output.ReadyThreadStacksResponse|MatchedEventCount,TotalReadyCount",
            "WpaMcp.Output.RegistryStacksResponse|MatchedEventCount",
            "WpaMcp.Output.SecurityScanAnalysisResponse|EmitterFallbackIdentityCount,InvalidIntervalCount,MatchedEventCount,PairedScanCount,PayloadTargetIdentityCount,ScopedUnattributedEventCount,TargetIdentityMismatchCount,UnmatchedStartCount,UnmatchedStopCount,UnresolvedTargetIdentityCount",
            "WpaMcp.Output.SecurityScanProviderRow|EventCount",
            "WpaMcp.Output.SecurityScanTargetRow|EventCount,PairedScanCount,ResultEventCount,StartEventCount,StopEventCount",
            "WpaMcp.Output.SlowStartupCandidate|LifetimeImageLoadCount,StartupImageLoadCount",
            "WpaMcp.Output.StartupDiscoverySummary|ConsideredStartupInstanceCount,EligibleStartupInstanceCount,ExcludedStartupInstanceCount,ExcludedUnobservedStartCount,OtherExcludedStartupInstanceCount",
            "WpaMcp.Output.SymbolStats|Resolved,Unresolved,UnresolvedModuleCount",
            "WpaMcp.Output.ThreadLifetimeResponse|MatchedEventCount",
            "WpaMcp.Output.ThreadCompareWindowsResponse|MatchedEventCount,ReturnedCount,TotalWindowCount",
            "WpaMcp.Output.ThreadComparisonWindowRow|BlockedIntervalCount,BlockedSwitchOutCount,ContextSwitches,ReadyCount,SampledCpuSamples",
            "WpaMcp.Output.TraceCapabilities|ClrFinalizerBatchStartEndpointEventCount,ClrFinalizerBatchStopEndpointEventCount,ClrFinalizerCompletedBatchCount,ClrFinalizerObjectEventCount,ClrFinalizerSourceEventCount,ClrGcBoundaryEvidenceCount,ClrGcCompletedIntervalCount,ClrGcHeapStatsEventCount,ClrGcIntervalEndpointEventCount,ClrGcUnmatchedEndpointCount,ClrJitBoundaryEvidenceCount,ClrJitCompletedIntervalCount,ClrJitIntervalEndpointEventCount,ClrJitUnmatchedEndpointCount,ExplicitStackWalkEventCount,NetworkConnectionBoundaryEvidenceCount,NetworkConnectionCompletedLifecycleCount,NetworkConnectionLifecycleEndpointEventCount,NetworkConnectionUnmatchedEndpointCount,ObservedProcessStartEventCount,ObservedThreadLifecycleEndpointEventCount,ThreadCompletedObservedLifetimeCount,ThreadInferredBoundaryCount,ThreadLifecycleSourceEventCount,ThreadRundownEndpointEventCount,ThreadUnmatchedLifecycleEndpointCount",
            "WpaMcp.Output.TraceCapabilityEvidenceRecord|TraceBoundaryEvidenceCount,TraceCompletedEvidenceCount,TraceEligibleEventCount,TraceUnmatchedEvidenceCount",
            "WpaMcp.Output.TraceMeta|EventCount,ProcessCount,RawEtwRecordCount",
            "WpaMcp.Output.TraceSelfAttributionEvidence|ExactNameMatchCount",
            "WpaMcp.Output.TraceStackwalkSummary|StackWalkEventCount",
            "WpaMcp.Output.TraceSymbolEvidenceBoundary|ModuleCount",
            "WpaMcp.Output.VirtualAllocStacksResponse|AllocatedCount,FreedCount,MatchedEventCount,TotalOpCount,TotalOperationCount",
            "WpaMcp.Output.WaitAnalysisResponse|MatchedEventCount,MatchedIntervalCount,ScopedCSwitches,ScopedIdentityUnresolvedCSwitchSideCount,ScopedStackedSwitches,ScopedUnmatchedBlockedIntervalCount,TotalCSwitches,TraceCSwitches,TraceIdentityUnresolvedCSwitchSideCount,TraceUnmatchedBlockedIntervalCount,UnmatchedBlockedIntervalCount",
            "WpaMcp.Output.WaitAnalysisRow|ContextSwitches",
            "WpaMcp.Output.WaitReasonBucket|Count",
            "WpaMcp.Output.WaitTopStacksResponse|MatchedEventCount,MatchedIntervalCount,SampleCount,ScopedCSwitches,ScopedIdentityUnresolvedCSwitchSideCount,ScopedStackedSwitches,ScopedUnmatchedBlockedIntervalCount,TraceCSwitches,TraceIdentityUnresolvedCSwitchSideCount,TraceUnmatchedBlockedIntervalCount,UnmatchedBlockedIntervalCount");

        Add(entries, NonMetric("unavailable_legacy", "not_applicable"),
            "WpaMcp.Output.InspectSymbolQuality|ResolvedModuleCount");
    }

    private static void AddRemainingNumerics(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries)
    {
        Add(entries, Metric("count", "count"),
            "WpaMcp.Output.CapabilityMapTotals|ReturnedCapabilities,TotalCapabilitiesAfterFilter,TotalCapabilitiesBeforeFilter",
            "WpaMcp.Output.FinalizerAnalysisResponse|TotalObjectsFinalized",
            "WpaMcp.Output.FinalizerBatchRow|FinalizersRun",
            "WpaMcp.Output.ImageLoadStackRow|ExclusiveLoads,InclusiveLoads",
            "WpaMcp.Output.ImageLoadStacksResponse|TotalLoads",
            "WpaMcp.Output.ImageLoadTimingResponse|TotalImageLoads",
            "WpaMcp.Output.ImageLoadTopGapsResponse|TotalImageLoads",
            "WpaMcp.Output.JitAnalysisResponse|TotalMethodsJitted",
            "WpaMcp.Output.MarkerSearchResponse|TotalMatched",
            "WpaMcp.Output.MemoryHandleProcessRow|Closed,Created,DuplicatedIn,DuplicatedOut",
            "WpaMcp.Output.NetConnectionsResponse|TotalConnections",
            "WpaMcp.Output.ProcessListResponse|IdleProcessesHidden",
            "WpaMcp.Output.ProviderEventCount|EventsWithCallStacks",
            "WpaMcp.Output.RegistryStacksResponse|TotalOps",
            "WpaMcp.Output.ThreadLifetimeResponse|TotalThreads",
            "WpaMcp.Output.TraceCaptureEvidenceBoundary|ReportedEventsLost",
            "WpaMcp.Output.TraceEvidenceMapRecord|CatalogCapabilityCount,CatalogWorkflowCount,ReturnedCapabilities,ReturnedWorkflows,TotalCapabilities,TotalWorkflows",
            "WpaMcp.Output.TraceMeta|EventsLost",
            "WpaMcp.Output.TraceStackwalkSummary|EventsWithCallStacks",
            "WpaMcp.Output.TraceSymbolEvidenceBoundary|ModulesWithCompletePdbIdentity,ModulesWithPdbName",
            "WpaMcp.Output.UnloadTraceResponse|ActiveLeases",
            "WpaMcp.Output.WaitAnalysisResponse|WindowCSwitchesAllThreads");
        Add(entries, Metric("count", "maximum"),
            "WpaMcp.Output.ThreadLifetimeResponse|PeakConcurrentThreads");
        Add(entries, Metric("count", "signed_delta"),
            "WpaMcp.Output.MemoryHandleProcessRow|NetDelta");
        Add(entries, Metric("bytes", "method_il_size"),
            "WpaMcp.Output.JitMethodRow|MethodIlSize");

        Add(entries, NonMetric("configuration", "row_limit"),
            "WpaMcp.Output.CompositeToolCall|EffectiveTop,InternalTop,Top",
            "WpaMcp.Output.EmbeddedTopNBoundary|Requested");
        Add(entries, NonMetric("cursor_position", "zero_based_index"),
            "WpaMcp.Output.InspectTracePageContext|StartIndex");
        Add(entries, NonMetric("configuration", "histogram_bucket_count"),
            "WpaMcp.Output.CompositeToolCall|WhenBuckets");
        Add(entries, NonMetric("unavailable_legacy", "not_applicable"),
            "WpaMcp.Output.InspectSymbolQuality|ModuleResolutionRate");
    }

    private static ReviewedNumericSemantics Metric(
        string unit,
        string aggregation,
        string precision = "exact") =>
        new("metric", unit, precision, aggregation);

    private static ReviewedNumericSemantics NonMetric(string role, string unit) =>
        new(role, unit, "exact", "not_applicable");

    private static void Add(
        IDictionary<(Type Type, string Property), ReviewedNumericSemantics> entries,
        ReviewedNumericSemantics semantics,
        params string[] declarations)
    {
        var assembly = typeof(ToolNumericSemanticsReviewedManifest).Assembly;
        foreach (var declaration in declarations)
        {
            var separator = declaration.IndexOf('|');
            if (separator <= 0 || separator == declaration.Length - 1)
                throw new InvalidOperationException($"Invalid reviewed numeric declaration '{declaration}'.");

            var typeName = declaration[..separator];
            var type = assembly.GetType(typeName, throwOnError: true)!;
            foreach (var propertyName in declaration[(separator + 1)..].Split(','))
            {
                var property = type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException(
                        $"Reviewed numeric property '{typeName}.{propertyName}' does not exist.");
                if (!IsNumeric(property.PropertyType))
                {
                    throw new InvalidOperationException(
                        $"Reviewed property '{typeName}.{propertyName}' is not numeric or a numeric collection.");
                }
                if (!entries.TryAdd((type, propertyName), semantics))
                {
                    throw new InvalidOperationException(
                        $"Reviewed numeric property '{typeName}.{propertyName}' is duplicated.");
                }
            }
        }
    }

    private static bool IsNumeric(Type declaredType)
    {
        var type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type.IsArray)
            type = Nullable.GetUnderlyingType(type.GetElementType()!) ?? type.GetElementType()!;
        else
        {
            var enumerable = type.GetInterfaces().Append(type).FirstOrDefault(candidate =>
                candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable is not null && type != typeof(string))
            {
                var element = enumerable.GetGenericArguments()[0];
                type = Nullable.GetUnderlyingType(element) ?? element;
            }
        }

        return type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) ||
            type == typeof(ushort) || type == typeof(int) || type == typeof(uint) ||
            type == typeof(long) || type == typeof(ulong) || type == typeof(nint) ||
            type == typeof(nuint) || type == typeof(float) || type == typeof(double) ||
            type == typeof(decimal);
    }
}
