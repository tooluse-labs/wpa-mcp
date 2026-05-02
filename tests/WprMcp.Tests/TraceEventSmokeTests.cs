using Microsoft.Diagnostics.Tracing.Etlx;
using Xunit;

namespace WprMcp.Tests;

public class TraceEventSmokeTests
{
    private const string FixturePath =
        "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void CanOpenOrConvertSanityEtl()
    {
        Assert.True(File.Exists(FixturePath),
            $"fixture missing: {FixturePath} — see Task 17 (fixtures/CAPTURE.md when written)");

        using var trace = TraceLog.OpenOrConvert(FixturePath);
        Assert.True(trace.EventCount > 0);
        Assert.True(trace.SessionDuration.TotalMilliseconds > 0);
    }
}
