using WprMcp.Analyzers;
using WprMcp.Core;
using Xunit;

namespace WprMcp.Tests;

public class TimeWindowSemanticsTests
{
    [Fact]
    public void StackAnalysisRequest_UsesHalfOpenWindow()
    {
        var trace = new TraceCache(capacity: 1).Get("fixtures/small_cpu.etl");
        var when = StackSourceTopN.WhenHistogram.ForWindow(startUs: 10, endUs: 20, trace, bucketCount: 0);
        var req = new StackAnalysisRequest(
            Pid: 123,
            StartUs: 10,
            EndUs: 20,
            SymbolLog: TextWriter.Null,
            When: when);

        Assert.False(req.PassesFilter(processId: 123, nowUs: 9));
        Assert.True(req.PassesFilter(processId: 123, nowUs: 10));
        Assert.True(req.PassesFilter(processId: 123, nowUs: 19));
        Assert.False(req.PassesFilter(processId: 123, nowUs: 20));
        Assert.False(req.PassesFilter(processId: 456, nowUs: 10));
    }
}
