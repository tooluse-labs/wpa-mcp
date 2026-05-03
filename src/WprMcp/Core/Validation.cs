using System.Runtime.CompilerServices;

namespace WprMcp.Core;

internal static class Validation
{
    public const int MaxTop = 1000;
    public const int MaxWhenBuckets = 1000;

    public static int RequireTop(int top, [CallerArgumentExpression(nameof(top))] string? paramName = null)
    {
        if (top <= 0 || top > MaxTop)
            throw new ArgumentOutOfRangeException(paramName ?? nameof(top),
                $"must be in [1, {MaxTop}]");
        return top;
    }

    public static int RequireWhenBuckets(int whenBuckets,
        [CallerArgumentExpression(nameof(whenBuckets))] string? paramName = null)
    {
        if (whenBuckets < 0 || whenBuckets > MaxWhenBuckets)
            throw new ArgumentOutOfRangeException(paramName ?? nameof(whenBuckets),
                $"must be in [0, {MaxWhenBuckets}]");
        return whenBuckets;
    }

    public static int RequirePositivePid(int pid,
        [CallerArgumentExpression(nameof(pid))] string? paramName = null)
    {
        if (pid <= 0)
            throw new ArgumentOutOfRangeException(paramName ?? nameof(pid), "must be a positive PID");
        return pid;
    }

    public static string RequireFunctionName(string function,
        [CallerArgumentExpression(nameof(function))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(function))
            throw new ArgumentException("function name required", paramName ?? nameof(function));
        return function;
    }

    public static string RequireProviderName(string providerName,
        [CallerArgumentExpression(nameof(providerName))] string? paramName = null)
    {
        if (string.IsNullOrWhiteSpace(providerName))
            throw new ArgumentException("provider name required", paramName ?? nameof(providerName));
        return providerName;
    }
}
