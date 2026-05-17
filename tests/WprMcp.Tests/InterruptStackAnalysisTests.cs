using WprMcp.Analyzers;
using WprMcp.Output;

namespace WprMcp.Tests;

public class InterruptStackAnalysisTests
{
    [Fact]
    public void MissingStackWarningUsesInterruptTimeNotEventCount()
    {
        Assert.True(InterruptStackAnalysis.ShouldWarnMissingStacks(noStackUs: 600, totalUs: 1_000));
        Assert.False(InterruptStackAnalysis.ShouldWarnMissingStacks(noStackUs: 400, totalUs: 1_000));
        Assert.False(InterruptStackAnalysis.ShouldWarnMissingStacks(noStackUs: 1, totalUs: 0));
    }

    [Fact]
    public void MissingStackWarningReportsMissingInterruptTime()
    {
        var warning = WarningBuilder.MissingInterruptStacks(
            noStackCount: 1,
            totalCount: 100,
            noStackUs: 600,
            totalUs: 1_000);

        Assert.Contains("600 of 1000 us", warning);
        Assert.Contains("1 of 100 DPC/ISR events", warning);
    }
}
