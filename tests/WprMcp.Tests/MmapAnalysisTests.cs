using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class MmapAnalysisTests
{
    private const string FixturePath = "fixtures/small_mmap.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_mmap.etl from Task 17 capture")]
    public void MmapHotFiles_ReturnsAtLeastOneRow()
    {
        var tools = new MmapTools(new TraceCache(capacity: 2));
        var resp = tools.MmapHotFiles(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
    }

    [Fact(Skip = "Requires fixtures/small_mmap.etl from Task 17 capture")]
    public void MmapHotFiles_AlwaysIncludesKeywordHint()
    {
        var tools = new MmapTools(new TraceCache(capacity: 2));
        var resp = tools.MmapHotFiles(FixturePath, top: 10);
        Assert.Contains(resp.Warnings, w => w.Contains("MemoryHardFaults"));
    }

    [Fact]
    public void MmapHotFiles_RejectsBadTop()
    {
        var tools = new MmapTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MmapHotFiles("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MmapHotFiles("nonexistent.etl", top: 1001));
    }
}
