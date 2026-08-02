namespace WpaMcp.Output;

public sealed record EmbeddedTopNBoundary
{
    public EmbeddedTopNBoundary(
        string SectionPointer,
        long Requested,
        long Returned,
        long? TotalAvailable,
        ToolSectionTotalState TotalState,
        ToolSectionMoreState MoreState,
        bool HasMore,
        bool ContinuationAvailable,
        string? TruncationReason,
        string SortKey,
        ToolSortDirection SortDirection,
        IReadOnlyList<string> TieBreakers)
    {
        if (string.IsNullOrWhiteSpace(SectionPointer) || Requested < 0 || Returned < 0 ||
            Returned > Requested || ContinuationAvailable || HasMore !=
                (MoreState == ToolSectionMoreState.Present))
            throw new ArgumentException("Embedded top-N boundary has invalid identity or counts.");
        if (TotalState == ToolSectionTotalState.Exact)
        {
            if (TotalAvailable is null || TotalAvailable < Returned ||
                MoreState == ToolSectionMoreState.Unknown ||
                (MoreState == ToolSectionMoreState.Absent && TotalAvailable != Returned) ||
                (MoreState == ToolSectionMoreState.Present && TotalAvailable <= Returned))
                throw new ArgumentException("Embedded exact-total boundary is inconsistent.");
        }
        else if (TotalState == ToolSectionTotalState.Unknown)
        {
            if (TotalAvailable is not null || MoreState != ToolSectionMoreState.Unknown || HasMore)
                throw new ArgumentException("Embedded unknown-total boundary is inconsistent.");
        }
        else if (TotalState == ToolSectionTotalState.LowerBound)
        {
            if (TotalAvailable is null || TotalAvailable <= Returned ||
                MoreState != ToolSectionMoreState.Present || !HasMore)
                throw new ArgumentException("Embedded lower-bound boundary requires a witnessed omitted item.");
        }
        else
        {
            throw new ArgumentException("Embedded top-N boundary has an unsupported total state.");
        }
        if ((MoreState is ToolSectionMoreState.Present or ToolSectionMoreState.Unknown) !=
                !string.IsNullOrWhiteSpace(TruncationReason) ||
            string.IsNullOrWhiteSpace(SortKey) || TieBreakers is null)
            throw new ArgumentException("Embedded top-N boundary has invalid truncation or ordering metadata.");

        this.SectionPointer = SectionPointer;
        this.Requested = Requested;
        this.Returned = Returned;
        this.TotalAvailable = TotalAvailable;
        this.TotalState = TotalState;
        this.MoreState = MoreState;
        this.HasMore = HasMore;
        this.ContinuationAvailable = ContinuationAvailable;
        this.TruncationReason = TruncationReason;
        this.SortKey = SortKey;
        this.SortDirection = SortDirection;
        this.TieBreakers = TieBreakers.ToArray();
    }

    [System.ComponentModel.Description("JSON pointer, relative to the containing composite object, for the bounded embedded collection.")]
    public string SectionPointer { get; init; }
    public long Requested { get; init; }
    public long Returned { get; init; }
    public long? TotalAvailable { get; init; }
    public ToolSectionTotalState TotalState { get; init; }
    public ToolSectionMoreState MoreState { get; init; }
    [System.ComponentModel.Description("Compatibility boolean: true only when MoreState=present. False is not terminal when MoreState=unknown.")]
    public bool HasMore { get; init; }
    [System.ComponentModel.Description("Always false for embedded composite collections: no continuation token is exposed for this boundary.")]
    public bool ContinuationAvailable { get; init; }
    public string? TruncationReason { get; init; }
    public string SortKey { get; init; }
    public ToolSortDirection SortDirection { get; init; }
    public IReadOnlyList<string> TieBreakers { get; init; }
}

public sealed record StartupWindowProvenance(
    int Pid,
    long ProcessStartUs,
    long StartUs,
    long EndUs,
    long RequestedEndUs,
    long TraceDurationUs,
    bool ProcessStartObserved,
    bool ProcessEndObserved,
    string Status,
    string? Code);

public sealed record SlowStartupCandidate(
    string EvidenceId,
    [property: System.ComponentModel.Description("CompositeToolCall.CallId for the internal startup-candidate projection that produced this row.")]
    string CallId,
    int Pid,
    long ProcessStartUs,
    int ParentPid,
    string Name,
    long StartupEndUs,
    long ObservedStartupWallUs,
    long StartupCpuUs,
    long StartupBlockedUs,
    [property: System.ComponentModel.Description("Deprecated alias of ObservedStartupWallToCpuRatio; observed_startup_wall_us / startup_cpu_us, not a percentage and not bounded to [0,1].")]
    double? StartupWaitRatio,
    long StartupImageLoadCount,
    bool StartupImageLoadsHasMore,
    IReadOnlyList<WaitReasonBucket> TopStartupWaitReasons,
    [property: System.ComponentModel.Description("Authoritative exact-total boundary for TopStartupWaitReasons, which is capped at five reason buckets.")]
    EmbeddedTopNBoundary TopStartupWaitReasonsBoundary,
    IReadOnlyList<ImageLoadRow> FirstStartupImageLoads,
    [property: System.ComponentModel.Description("Authoritative exact-total omission boundary for FirstStartupImageLoads.")]
    EmbeddedTopNBoundary FirstStartupImageLoadsBoundary,
    IReadOnlyList<CpuFunctionRow>? TopStartupCpuFunctions,
    [property: System.ComponentModel.Description("Authoritative omission boundary for TopStartupCpuFunctions. Unknown includes a saturated source limit or unavailable CPU analysis; no embedded continuation is exposed.")]
    EmbeddedTopNBoundary TopStartupCpuFunctionsBoundary,
    StartupWindowProvenance Window,
    long LifetimeWallUs,
    long LifetimeCpuUs,
    [property: System.ComponentModel.Description("Deprecated alias of LifetimeWallToCpuRatio; lifetime_wall_us / lifetime_cpu_us, not a percentage and not bounded to [0,1].")]
    double? LifetimeWaitRatio,
    int LifetimeImageLoadCount)
{
    [System.ComponentModel.Description("Authoritative observed-startup wall-to-CPU ratio: ObservedStartupWallUs / StartupCpuUs; null when StartupCpuUs is zero. This is not a percentage and may exceed 1.")]
    public double? ObservedStartupWallToCpuRatio => StartupCpuUs == 0
        ? null
        : ObservedStartupWallUs / (double)StartupCpuUs;

    [System.ComponentModel.Description("Authoritative lifetime wall-to-CPU ratio: LifetimeWallUs / LifetimeCpuUs; null when LifetimeCpuUs is zero. This is not a percentage and may exceed 1.")]
    public double? LifetimeWallToCpuRatio => LifetimeCpuUs == 0
        ? null
        : LifetimeWallUs / (double)LifetimeCpuUs;
}

public sealed record StartupGapEvidenceRow(
    string EvidenceId,
    string CallId,
    int Pid,
    long ProcessStartUs,
    string ProcessName,
    long FirstImageLoadTimeUs,
    long FirstImageLoadOffsetUs,
    StartupWindowProvenance ParentWindow,
    long ChildStartUs,
    long ChildEndUs,
    DiagnoseWindowResponse Window,
    [property: System.ComponentModel.Description("Authoritative omission and ordering boundaries for the eight top-limited collections embedded in Window. A saturated source limit reports MoreState=unknown, not a false terminal result.")]
    IReadOnlyList<EmbeddedTopNBoundary> WindowSectionBoundaries);

public sealed record StartupProcessExclusionRow(
    string EvidenceId,
    [property: System.ComponentModel.Description("CompositeToolCall.CallId for the internal startup-candidate discovery operation that produced this exclusion boundary.")]
    string CallId,
    int Pid,
    long ProcessStartUs,
    string ProcessName,
    string Code);

public sealed record StartupDiscoverySummary(
    [property: System.ComponentModel.Description("CompositeToolCall.CallId for the startup-candidate discovery operation summarized here.")]
    string CallId,
    int EligibleStartupInstanceCount,
    int ConsideredStartupInstanceCount,
    bool CandidateInputHasMore,
    [property: System.ComponentModel.Description("Authoritative retained-input boundary. It exposes the exact eligible total and whether the fixed discovery cap omitted candidate inputs; no continuation is available inside this composite.")]
    EmbeddedTopNBoundary CandidateInputBoundary,
    [property: System.ComponentModel.Description("Exact number of excluded startup instances before fixed-size evidence sampling.")]
    int ExcludedStartupInstanceCount,
    [property: System.ComponentModel.Description("Exact number of exclusions caused by an unobserved ProcessStart boundary.")]
    int ExcludedUnobservedStartCount,
    [property: System.ComponentModel.Description("Exact number of exclusions for all other reviewed exclusion codes.")]
    int OtherExcludedStartupInstanceCount,
    IReadOnlyList<StartupProcessExclusionRow> ExcludedSamples,
    [property: System.ComponentModel.Description("True when the fixed-size ExcludedSamples evidence sample omits one or more of ExcludedStartupInstanceCount exclusions.")]
    bool ExcludedSamplesHasMore)
{
    public const int ExcludedSampleLimit = 20;
}

public sealed record DiagnoseSlowStartupResponse(
    IReadOnlyList<SlowStartupCandidate> Candidates,
    [property: System.ComponentModel.Description("Authoritative omission boundary for Candidates. Exact only when all eligible startup inputs were ranked; unknown when the fixed discovery input cap saturated. No embedded continuation is exposed.")]
    EmbeddedTopNBoundary CandidateBoundary,
    [property: Obsolete(
        "Use structured Evidence, NotConcluded, ExecutedToolCalls, and NextTools instead.")]
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompositeEvidence>? Evidence = null,
    IReadOnlyList<CompositeNotConcluded>? NotConcluded = null,
    IReadOnlyList<CompositeToolCall>? ExecutedToolCalls = null,
    IReadOnlyList<CompositeNextTool>? NextTools = null,
    [property: System.ComponentModel.Description("Exhaustive for the retained Candidates collection. Each row's WindowSectionBoundaries, not this outer section, is authoritative for omission inside its embedded diagnose_window result.")]
    IReadOnlyList<StartupGapEvidenceRow>? FirstImageLoadGapEvidence = null,
    StartupDiscoverySummary? Discovery = null,
    [property: System.ComponentModel.Description("Planner admission boundary for this composite. Until equivalence evidence is approved, the tool executes directly and planner logical/pass/scan counts remain null with unavailable states; no single-dispatch claim is made.")]
    PlannerExecutionTelemetry? PlannerExecution = null);
