using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class HardFaultByFileAnalysisTests
{
    private const string FixturePath = "fixtures/small_mmap.etl";

    [Fact]
    public void HardFaultByFile_ReturnsAtLeastOneRow()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
    }

    [Fact]
    public void HardFaultByFile_AlwaysIncludesKeywordHint()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.Contains(resp.Warnings, w => w.Contains("MemoryHardFaults"));
    }

    [Fact]
    public void HardFaultByFile_RejectsBadTop()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 1001));
    }
}
