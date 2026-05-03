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
    public void ListProcesses_RespectsTopParameter()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        // Note: trace.Processes can exceed Validation.MaxTop (1000) on busy hosts due to
        // rundown events; TotalCount must reflect the unfiltered count, not be capped.
        var full = tools.ListProcesses(FixturePath, top: 1000);
        Assert.True(full.Rows.Count >= 1);
        Assert.True(full.TotalCount >= full.Rows.Count);

        var capped = tools.ListProcesses(FixturePath, top: 1);
        Assert.Single(capped.Rows);
        // TotalCount survives truncation so callers can detect "this was capped".
        Assert.Equal(full.TotalCount, capped.TotalCount);
    }

    [Fact]
    public void ListProcesses_RejectsBadTop()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ListProcesses(FixturePath, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.ListProcesses(FixturePath, top: 1001));
    }

    [Fact]
    public void ListProcesses_WaitRatioOrderDoesNotPutTraceResidentFirst()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.ListProcesses(FixturePath, orderBy: "wait_ratio", top: 50);
        Assert.NotEmpty(resp.Rows);

        // If there is at least one non-trace-resident process with a CPU sample, it must
        // come before any trace-resident peer in wait_ratio order. (If the small_cpu fixture
        // has zero qualifying non-resident processes, the assertion is vacuously satisfied.)
        var firstNonResidentIdx = -1;
        var firstResidentIdx = -1;
        for (var i = 0; i < resp.Rows.Count; i++)
        {
            if (resp.Rows[i].TraceResident && firstResidentIdx < 0) firstResidentIdx = i;
            if (!resp.Rows[i].TraceResident && resp.Rows[i].WaitRatio is not null && firstNonResidentIdx < 0)
                firstNonResidentIdx = i;
        }
        if (firstNonResidentIdx >= 0 && firstResidentIdx >= 0)
            Assert.True(firstNonResidentIdx < firstResidentIdx,
                $"non-resident @ {firstNonResidentIdx} should sort before trace-resident @ {firstResidentIdx}");
    }

    [Fact]
    public void LoadTrace_DetectsCpuSamplesOnCpuFixture()
    {
        // small_cpu was captured with CPU.light. Positive assertion only — content of
        // negative flags varies between OS builds and capture conditions (e.g., FileIO.light
        // can incidentally generate HardFault events when files page in), so we only check
        // the keyword that the profile is GUARANTEED to enable.
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.True(resp.Capabilities.HasCpuSamples);
    }

    [Fact]
    public void LoadTrace_DetectsFileIoOnFileIoFixture()
    {
        const string FileIoFixture = "fixtures/small_fileio.etl";
        if (!File.Exists(FileIoFixture)) return;
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FileIoFixture);
        Assert.True(resp.Capabilities.HasFileIo);
    }

    [Fact]
    public void LoadTrace_DetectsHardFaultsOnMmapFixture()
    {
        const string MmapFixture = "fixtures/small_mmap.etl";
        if (!File.Exists(MmapFixture)) return;
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(MmapFixture);
        // small_mmap uses MmapCapture.wprp which explicitly enables HardFaults.
        Assert.True(resp.Capabilities.HasHardFaults);
    }

    [Fact]
    public void LoadTrace_CapabilitiesNeverNull()
    {
        var tools = new MetaTools(new TraceCache(capacity: 2));
        var resp = tools.LoadTrace(FixturePath);
        Assert.NotNull(resp.Capabilities);
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
