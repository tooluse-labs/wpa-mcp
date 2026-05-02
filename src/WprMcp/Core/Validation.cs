using System.Runtime.CompilerServices;

namespace WprMcp.Core;

internal static class Validation
{
    public const int MaxTop = 1000;

    public static int RequireTop(int top, [CallerArgumentExpression(nameof(top))] string? paramName = null)
    {
        if (top <= 0 || top > MaxTop)
            throw new ArgumentOutOfRangeException(paramName ?? nameof(top),
                $"must be in [1, {MaxTop}]");
        return top;
    }
}
