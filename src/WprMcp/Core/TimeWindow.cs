namespace WprMcp.Core;

internal readonly record struct TimeWindow
{
    public TimeWindow(long startUs, long endUs)
    {
        if (startUs < 0 || endUs <= startUs)
        {
            throw new ArgumentOutOfRangeException(nameof(endUs));
        }

        StartUs = startUs;
        EndUs = endUs;
    }

    public long StartUs { get; }

    public long EndUs { get; }

    public long DurationUs => checked(EndUs - StartUs);

    public bool ContainsPoint(long timestampUs) => StartUs <= timestampUs && timestampUs < EndUs;

    public static long ClipStart(long intervalStartUs, long windowStartUs) =>
        Math.Max(intervalStartUs, windowStartUs);

    public static long ClipEnd(long intervalEndUs, long windowEndUs) =>
        Math.Min(intervalEndUs, windowEndUs);

    public long IntersectDurationUs(long intervalStartUs, long intervalEndUs)
    {
        if (intervalEndUs <= intervalStartUs)
        {
            return 0;
        }

        return Math.Max(
            0,
            ClipEnd(intervalEndUs, EndUs) - ClipStart(intervalStartUs, StartUs));
    }
}

internal readonly record struct TimeWindowInput(long? StartUs, long? EndUs)
{
    public static TimeWindowInput Validate(long? startUs, long? endUs, long? maxDurationUs)
    {
        if (startUs is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startUs));
        }

        if (endUs is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(endUs));
        }

        if (maxDurationUs is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDurationUs));
        }

        if (startUs.HasValue && endUs.HasValue)
        {
            if (endUs.Value <= startUs.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(endUs));
            }

            if (maxDurationUs.HasValue && endUs.Value - startUs.Value > maxDurationUs.Value)
            {
                throw new ArgumentOutOfRangeException(nameof(endUs));
            }
        }

        return new TimeWindowInput(startUs, endUs);
    }

    public TimeWindow Resolve(long traceDurationUs, long? maxDurationUs)
    {
        if (traceDurationUs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(traceDurationUs));
        }

        var resolved = new TimeWindow(StartUs ?? 0, EndUs ?? traceDurationUs);
        if (resolved.EndUs > traceDurationUs)
        {
            throw new ArgumentOutOfRangeException(nameof(EndUs));
        }

        if (maxDurationUs.HasValue && resolved.DurationUs > maxDurationUs.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(EndUs));
        }

        return resolved;
    }
}
