namespace WprMcp.Core;

internal static class TraceTime
{
    public static long FromMilliseconds(double value)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        var microseconds = Math.Floor(value * 1_000d);
        if (microseconds > long.MaxValue)
        {
            throw new OverflowException("timestamp exceeds Int64 microseconds");
        }

        return checked((long)microseconds);
    }

    public static long FromNanoseconds(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        return value / 1_000;
    }
}
