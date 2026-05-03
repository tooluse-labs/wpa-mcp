using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class FinalizerAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void ClrFinalizerAnalysis_EmptyTrace_ReturnsEmptyAndWarns()
    {
        var tools = new ClrTools(new TraceCache(capacity: 2));
        var resp = tools.ClrFinalizerAnalysis(FixturePath);
        Assert.Equal(0, resp.TotalObjectsFinalized);
        Assert.Equal(0, resp.TotalBatchUs);
        Assert.Empty(resp.Batches);
        Assert.Empty(resp.TopTypes);
        Assert.Contains(resp.Warnings, w => w.Contains("CLR", StringComparison.OrdinalIgnoreCase));
    }
}
