using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class MetaToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl"; // captured in Task 17

    [Fact]
    public void LoadTrace_ReturnsNonZeroEventCount()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.True(resp.Trace.EventCount > 0);
        Assert.True(resp.Trace.ProcessCount > 0);
        Assert.Equal(FixturePath, resp.Trace.Path);
    }

    [Fact]
    public void ListProcesses_OrdersByCpuDescending()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        Assert.NotEmpty(resp.Rows);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].CpuUs >= resp.Rows[i].CpuUs);
    }

    [Fact]
    public void ListProcesses_HidesIdleAndSystemByDefault()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        Assert.DoesNotContain(resp.Rows, r => r.Pid == 0 || r.Pid == 4);
        // Either PID 0 (Idle) or PID 4 (System) is present in any non-trivial Windows trace.
        Assert.True(resp.IdleProcessesHidden >= 1);
    }

    [Fact]
    public void ListProcesses_IncludeSystemTrueSurfacesIdle()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, includeSystem: true);
        Assert.Contains(resp.Rows, r => r.Pid == 0 || r.Pid == 4);
        Assert.Equal(0, resp.IdleProcessesHidden);
    }

    [Fact]
    public void ListProcesses_OrderByWallSortsByWallDesc()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, orderBy: "wall");
        Assert.NotEmpty(resp.Rows);
        for (var i = 1; i < resp.Rows.Count; i++)
            Assert.True(resp.Rows[i - 1].WallUs >= resp.Rows[i].WallUs);
    }

    [Fact]
    public void ListProcesses_PopulatesParentPidAndImageLoadCount()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath);
        // At least one process should have ImageLoad events (any non-empty trace).
        Assert.Contains(resp.Rows, r => r.ImageLoadCount > 0);
        // ParentPid is 0 for parent-less processes, but at least some children must have a real parent.
        Assert.Contains(resp.Rows, r => r.ParentPid > 0);
    }

    [Fact]
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
