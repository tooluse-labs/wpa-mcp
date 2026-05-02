using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class DiagnoseToolsTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void DiagnoseSlowStartup_RejectsBadArguments()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", maxCandidates: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", maxCandidates: 21));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", minWaitRatio: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.DiagnoseSlowStartup("nonexistent.etl", startupWindowUs: 0));
    }

    [Fact]
    public void DiagnoseSlowStartup_ReturnsCandidatesOrEmptyWithWarning()
    {
        var tools = new DiagnoseTools(new TraceCache(capacity: 2));
        // Aggressive threshold = many candidates; fall through to "no candidates" warning if not.
        var resp = tools.DiagnoseSlowStartup(FixturePath, minWaitRatio: 1.0, maxCandidates: 3);
        Assert.NotNull(resp.Summary);
        if (resp.Candidates.Count == 0)
            Assert.Contains(resp.Warnings, w => w.Contains("No processes matched"));
        else
            Assert.All(resp.Candidates, c => Assert.True(c.WaitRatio is null || c.WaitRatio >= 1.0));
    }
}
