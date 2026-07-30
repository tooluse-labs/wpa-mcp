namespace WprMcp.Output;

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
    int Pid,
    long ProcessStartUs,
    int ParentPid,
    string Name,
    long StartupEndUs,
    long ObservedStartupWallUs,
    long StartupCpuUs,
    long StartupBlockedUs,
    double? StartupWaitRatio,
    long StartupImageLoadCount,
    bool StartupImageLoadsHasMore,
    IReadOnlyList<WaitReasonBucket> TopStartupWaitReasons,
    IReadOnlyList<ImageLoadRow> FirstStartupImageLoads,
    IReadOnlyList<CpuFunctionRow>? TopStartupCpuFunctions,
    StartupWindowProvenance Window,
    long LifetimeWallUs,
    long LifetimeCpuUs,
    double? LifetimeWaitRatio,
    int LifetimeImageLoadCount);

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
    DiagnoseWindowResponse Window);

public sealed record StartupProcessExclusionRow(
    string EvidenceId,
    int Pid,
    long ProcessStartUs,
    string ProcessName,
    string Code);

public sealed record StartupDiscoverySummary(
    int EligibleStartupInstanceCount,
    int ConsideredStartupInstanceCount,
    bool CandidateInputHasMore,
    int ExcludedUnobservedStartCount,
    int OtherExcludedStartupInstanceCount,
    IReadOnlyList<StartupProcessExclusionRow> ExcludedSamples,
    bool ExcludedSamplesHasMore);

public sealed record DiagnoseSlowStartupResponse(
    IReadOnlyList<SlowStartupCandidate> Candidates,
    [property: Obsolete(
        "Use structured Evidence, NotConcluded, ExecutedToolCalls, and NextTools instead.")]
    string Summary,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<CompositeEvidence>? Evidence = null,
    IReadOnlyList<CompositeNotConcluded>? NotConcluded = null,
    IReadOnlyList<CompositeToolCall>? ExecutedToolCalls = null,
    IReadOnlyList<CompositeNextTool>? NextTools = null,
    IReadOnlyList<StartupGapEvidenceRow>? FirstImageLoadGapEvidence = null,
    StartupDiscoverySummary? Discovery = null);
