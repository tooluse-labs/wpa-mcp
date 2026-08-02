using System.Runtime.CompilerServices;

namespace WpaMcp.Core;

internal static class Validation
{
    public const int MaxTop = 1000;
    public const int MaxWhenBuckets = 1000;
    public const int MaxSerializedArgumentsBytes = 16 * 1024;
    public const int MaxStringChars = 4_096;
    public const int MaxCollectionItems = 128;

    public static TimeWindowInput RequireWindowInput(
        long? startUs,
        long? endUs,
        long? maxDurationUs = null) =>
        TimeWindowInput.Validate(startUs, endUs, maxDurationUs);

    public static void RequirePidTid(int? pid, int? tid)
    {
        if (pid is <= 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(pid)));
        if (tid is <= 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(tid)));
        if (tid.HasValue && !pid.HasValue)
            throw ToolFailureCaptureContext.Capture(new ArgumentException("tid requires pid", nameof(tid)));
    }

    public static void RequireThreadSelector(
        int? pid,
        int? tid,
        long? processStartUs,
        long? threadStartUs,
        long? threadGeneration = null)
    {
        RequirePidTid(pid, tid);
        if (processStartUs is < 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(processStartUs)));
        if (threadStartUs is < 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(threadStartUs)));
        if (threadGeneration is <= 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(threadGeneration)));
        if (processStartUs.HasValue && !pid.HasValue)
            throw ToolFailureCaptureContext.Capture(new ArgumentException("processStartUs requires pid", nameof(processStartUs)));
        if (threadStartUs.HasValue && (!pid.HasValue || !tid.HasValue))
            throw ToolFailureCaptureContext.Capture(new ArgumentException("threadStartUs requires pid and tid", nameof(threadStartUs)));
        if (threadGeneration.HasValue && (!pid.HasValue || !tid.HasValue))
            throw ToolFailureCaptureContext.Capture(new ArgumentException(
                "threadGeneration requires pid and tid",
                nameof(threadGeneration)));
    }

    public static string RequireText(string value, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!allowEmpty && string.IsNullOrWhiteSpace(value))
            throw ToolFailureCaptureContext.Capture(new ArgumentException("text is required", nameof(value)));
        if (value.Length > MaxStringChars)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(value)));
        return value;
    }

    public static int RequireCollectionCount(int count)
    {
        if (count < 0 || count > MaxCollectionItems)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(nameof(count)));
        return count;
    }

    public static int RequireTop(int top, [CallerArgumentExpression(nameof(top))] string? paramName = null)
    {
        var maximum = ToolOverfetchExecutionContext.MaximumAllowed(MaxTop);
        if (top <= 0 || top > maximum)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(
                paramName ?? nameof(top),
                $"must be in [1, {maximum}]"));
        return top;
    }

    public static int RequireWhenBuckets(int whenBuckets,
        [CallerArgumentExpression(nameof(whenBuckets))] string? paramName = null)
    {
        if (whenBuckets < 0 || whenBuckets > MaxWhenBuckets)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(
                paramName ?? nameof(whenBuckets),
                $"must be in [0, {MaxWhenBuckets}]"));
        return whenBuckets;
    }

    public static int RequireTimeBudgetMs(int timeBudgetMs,
        [CallerArgumentExpression(nameof(timeBudgetMs))] string? paramName = null)
    {
        if (timeBudgetMs <= 0 || timeBudgetMs > 3_600_000)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(
                paramName ?? nameof(timeBudgetMs),
                "must be in [1, 3600000]"));
        return timeBudgetMs;
    }

    public static int RequirePositivePid(int pid,
        [CallerArgumentExpression(nameof(pid))] string? paramName = null)
    {
        if (pid <= 0)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(
                paramName ?? nameof(pid),
                "must be a positive PID"));
        return pid;
    }

    public static string RequireFunctionName(string function,
        [CallerArgumentExpression(nameof(function))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(function))
            throw ToolFailureCaptureContext.Capture(new ArgumentException(
                "function name required",
                paramName ?? nameof(function)));
        if (function.Length > MaxStringChars)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(paramName ?? nameof(function)));
        return function;
    }

    public static string RequireProviderName(string providerName,
        [CallerArgumentExpression(nameof(providerName))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw ToolFailureCaptureContext.Capture(new ArgumentException(
                "provider name required",
                paramName ?? nameof(providerName)));
        if (providerName.Length > MaxStringChars)
            throw ToolFailureCaptureContext.Capture(new ArgumentOutOfRangeException(paramName ?? nameof(providerName)));
        return providerName;
    }
}
