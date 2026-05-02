using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class MetaToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void LoadTrace_ReturnsNonZeroEventCount()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.True(resp.Trace.EventCount > 0);
        Assert.True(resp.Trace.ProcessCount > 0);
        Assert.Equal(FixturePath, resp.Trace.Path);
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void ListProcesses_OrdersByCpuDescending()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        Assert.NotEmpty(resp.Rows);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].CpuUs >= resp.Rows[i].CpuUs);
    }

    [Fact(Skip = "Requires fixtures/small_cpu.etl from Task 17 capture")]
    public void LoadTrace_EmitsWarningWhenSymbolPathUnset()
    {
        var saved = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        try
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", null);
            var tools = new MetaTools(new TraceCache(capacity: 2));
            var resp = tools.LoadTrace(FixturePath);
            Assert.NotNull(resp.SymbolStatus.Warning);
        }
        finally
        {
            Environment.SetEnvironmentVariable("_NT_SYMBOL_PATH", saved);
        }
    }
}
