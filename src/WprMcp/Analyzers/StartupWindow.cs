using WprMcp.Core;

namespace WprMcp.Analyzers;

internal sealed record StartupWindow(
    ProcessInstanceKey Process,
    TimeWindow Bounds,
    long RequestedEndUs,
    long TraceDurationUs,
    bool ProcessStartObserved,
    bool ProcessEndObserved,
    string Status,
    string? Code)
{
    public static StartupWindow Create(
        ProcessLifetime lifetime,
        long startupWindowUs,
        long traceDurationUs)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        if (!lifetime.StartObserved)
            throw new InvalidOperationException("startup_start_not_observed");
        if (startupWindowUs <= 0)
            throw new ArgumentOutOfRangeException(nameof(startupWindowUs));
        if (traceDurationUs <= lifetime.Key.StartUs)
            throw new InvalidOperationException("startup_window_empty");

        var requestedEndUs = checked(lifetime.Key.StartUs + startupWindowUs);
        var endUs = requestedEndUs;
        if (lifetime.EndObserved && lifetime.EndUs < endUs)
            endUs = lifetime.EndUs;
        if (traceDurationUs < endUs)
            endUs = traceDurationUs;
        if (endUs <= lifetime.Key.StartUs)
            throw new InvalidOperationException("startup_window_empty");

        var observedExitAtOrBeforeTraceEnd =
            lifetime.EndObserved && lifetime.EndUs <= traceDurationUs;
        var truncatedByTraceEnd =
            traceDurationUs < requestedEndUs && !observedExitAtOrBeforeTraceEnd;

        return new StartupWindow(
            lifetime.Key,
            new TimeWindow(lifetime.Key.StartUs, endUs),
            requestedEndUs,
            traceDurationUs,
            ProcessStartObserved: true,
            ProcessEndObserved: lifetime.EndObserved,
            Status: truncatedByTraceEnd ? "Partial" : "Complete",
            Code: truncatedByTraceEnd ? "startup_window_truncated" : null);
    }
}
