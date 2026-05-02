using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class MarkerSearchTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void FindMarker_FindsBuiltinEvent()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 5);
        Assert.NotEmpty(resp.Rows);
        Assert.All(resp.Rows, r => Assert.Contains("Sample", r.EventName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void FindMarker_RespectsTopLimit()
    {
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        var resp = tools.FindMarker(FixturePath, "Sample", top: 3);
        Assert.True(resp.Rows.Count <= 3);
    }

    [Fact]
    public void FindMarker_RejectsEmptyQuery()
    {
        // Empty-substring check is in MarkerTools.FindMarker BEFORE _cache.Get,
        // so an invalid path here does not matter — ArgumentException fires first.
        var tools = new MarkerTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.FindMarker("nonexistent.etl", ""));
    }
}
