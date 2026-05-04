using Xunit;

namespace WprMcp.Tests;

internal static class StackAssertions
{
    /// <summary>
    /// Assert the "no matching events" shape of a stack analyzer's row list: the underlying
    /// CallTree always emits a synthetic ROOT node even when the stack source is empty, so
    /// `Assert.Empty(rows)` would fail.  Instead we assert that every row carries zero on
    /// both metric fields — exclusive AND inclusive.  A row with `Exclusive=0, Inclusive=999`
    /// would slip through a one-field check.
    /// </summary>
    public static void AssertRootOnly<TRow>(
        IEnumerable<TRow> rows,
        Func<TRow, long> exclusive,
        Func<TRow, long> inclusive)
    {
        Assert.All(rows, r =>
        {
            Assert.Equal(0, exclusive(r));
            Assert.Equal(0, inclusive(r));
        });
    }
}
