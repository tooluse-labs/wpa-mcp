using WprMcp.Analyzers;
using Xunit;

namespace WprMcp.Tests;

public class StackSourceTopNTests
{
    [Fact]
    public void Pct_ClampsFloatingPointOvershoot()
    {
        Assert.Equal(100.0, StackSourceTopN.Pct(total: 100, n: 100.01));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 100, n: -1));
        Assert.Equal(0.0, StackSourceTopN.Pct(total: 0, n: 1));
    }

    [Fact]
    public void PctOfTrace_OnlyEmitsForFilteredViews()
    {
        Assert.Null(StackSourceTopN.PctOfTrace(hasFilter: false, traceTotal: 100, n: 50));
        Assert.Equal(50.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 50));
        Assert.Equal(100.0, StackSourceTopN.PctOfTrace(hasFilter: true, traceTotal: 100, n: 100.01));
    }
}
