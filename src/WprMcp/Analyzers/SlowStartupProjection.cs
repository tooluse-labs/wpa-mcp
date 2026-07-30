using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

internal sealed record SlowStartupCandidateData(
    ProcessInstanceKey Process,
    int ParentPid,
    string Name,
    StartupWindow StartupWindow,
    long ObservedStartupWallUs,
    long StartupCpuUs,
    long StartupBlockedUs,
    double? StartupWaitRatio,
    IReadOnlyDictionary<string, long> StartupBlockedUsByReason,
    long StartupImageLoadCount,
    bool StartupImageLoadsHasMore,
    IReadOnlyList<ImageLoadRow> StartupImageLoads,
    long LifetimeWallUs,
    long LifetimeCpuUs,
    double? LifetimeWaitRatio,
    int LifetimeImageLoadCount);

internal sealed record StartupEvidenceWindowPlan(
    string EvidenceIdPrefix,
    ProcessInstanceKey Process,
    TimeWindow ParentWindow,
    TimeWindow? FirstImageChildWindow,
    string? NotConcludedCode);

internal static class SlowStartupProjection
{
    public static double? StartupWaitRatio(
        long observedStartupWallUs,
        long startupCpuUs) =>
        startupCpuUs == 0
            ? null
            : observedStartupWallUs / (double)startupCpuUs;

    public static IReadOnlyList<SlowStartupCandidateData> Rank(
        IReadOnlyList<StartupProcessObservation> processes,
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler,
        IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> imageLoads,
        string? nameSubstring,
        double minWaitRatio,
        int maxCandidates)
    {
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(imageLoads);
        Validation.RequireTop(maxCandidates);
        if (minWaitRatio < 0)
            throw new ArgumentOutOfRangeException(nameof(minWaitRatio));

        var hasNameFilter = !string.IsNullOrEmpty(nameSubstring);
        var ranked = processes
            .Where(process =>
                !hasNameFilter ||
                process.Metadata.Name.Contains(
                    nameSubstring!, StringComparison.OrdinalIgnoreCase))
            .Select(process => CreateSeed(process, scheduler, imageLoads))
            .Where(seed =>
                seed.StartupWaitRatio.HasValue &&
                seed.StartupWaitRatio.Value >= minWaitRatio)
            .OrderByDescending(seed => seed.StartupWaitRatio!.Value)
            .ThenByDescending(seed => seed.ObservedStartupWallUs)
            .ThenBy(seed => seed.Observation.Process.StartUs)
            .ThenBy(seed => seed.Observation.Process.Pid)
            .Take(maxCandidates)
            .Select(ToCandidate)
            .ToArray();
        return ranked;
    }

    public static StartupEvidenceWindowPlan PlanEvidence(
        SlowStartupCandidateData candidate,
        long slowFirstImageLoadThresholdUs)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (slowFirstImageLoadThresholdUs < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(slowFirstImageLoadThresholdUs));
        }

        var prefix =
            $"slow-startup.pid-{candidate.Process.Pid}.start-{candidate.Process.StartUs}";
        var parent = candidate.StartupWindow.Bounds;
        if (candidate.StartupImageLoads.Count == 0)
        {
            return new StartupEvidenceWindowPlan(
                prefix,
                candidate.Process,
                parent,
                FirstImageChildWindow: null,
                NotConcludedCode: "first_image_load_not_observed");
        }

        var firstLoad = candidate.StartupImageLoads[0];
        var offsetUs = checked(firstLoad.TimeUs - candidate.Process.StartUs);
        TimeWindow? child = null;
        if (offsetUs > 0 && offsetUs >= slowFirstImageLoadThresholdUs)
        {
            child = new TimeWindow(candidate.Process.StartUs, firstLoad.TimeUs);
            if (child.Value.StartUs < parent.StartUs ||
                child.Value.EndUs > parent.EndUs)
            {
                throw new InvalidOperationException(
                    "first_image_load_outside_startup_window");
            }
        }

        return new StartupEvidenceWindowPlan(
            prefix,
            candidate.Process,
            parent,
            child,
            NotConcludedCode: null);
    }

    private static RankedCandidateSeed CreateSeed(
        StartupProcessObservation process,
        IReadOnlyDictionary<ProcessInstanceKey, StartupSchedulerMetrics> scheduler,
        IReadOnlyDictionary<ProcessInstanceKey, StartupImageLoadBucket> imageLoads)
    {
        if (!scheduler.TryGetValue(process.Process, out var metrics))
            throw new InvalidOperationException("startup_scheduler_metrics_missing");
        if (!imageLoads.TryGetValue(process.Process, out var loads))
            throw new InvalidOperationException("startup_image_load_bucket_missing");

        var wallUs = process.Window.Bounds.DurationUs;
        return new RankedCandidateSeed(
            process,
            metrics,
            loads,
            wallUs,
            StartupWaitRatio(wallUs, metrics.StartupCpuUs));
    }

    private static SlowStartupCandidateData ToCandidate(RankedCandidateSeed seed)
    {
        var process = seed.Observation;
        return new SlowStartupCandidateData(
            process.Process,
            process.Metadata.ParentPid,
            process.Metadata.Name,
            process.Window,
            seed.ObservedStartupWallUs,
            seed.Scheduler.StartupCpuUs,
            seed.Scheduler.StartupBlockedUs,
            seed.StartupWaitRatio,
            seed.Scheduler.BlockedUsByReason,
            seed.ImageLoads.TotalAvailable,
            seed.ImageLoads.HasMore,
            seed.ImageLoads.FirstLoads,
            process.LifetimeWallUs,
            process.Metadata.LifetimeCpuUs,
            process.LifetimeWaitRatio,
            process.Metadata.LifetimeImageLoadCount);
    }

    private sealed record RankedCandidateSeed(
        StartupProcessObservation Observation,
        StartupSchedulerMetrics Scheduler,
        StartupImageLoadBucket ImageLoads,
        long ObservedStartupWallUs,
        double? StartupWaitRatio);
}
