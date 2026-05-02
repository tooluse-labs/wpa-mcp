using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public class FileIoAnalysisTests
{
    private const string FixturePath = "fixtures/small_fileio.etl"; // captured in Task 17

    [Fact(Skip = "Requires fixtures/small_fileio.etl from Task 17 capture")]
    public void FileIoTopFiles_ReturnsRows()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopFiles(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
    }

    [Fact(Skip = "Requires fixtures/small_fileio.etl from Task 17 capture")]
    public void FileIoTopFiles_OrdersByTotalBytesDescending()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopFiles(FixturePath, top: 50);
        for (var i = 1; i < resp.Rows.Count; i++)
        {
            var prev = resp.Rows[i - 1].ReadBytes + resp.Rows[i - 1].WriteBytes;
            var cur = resp.Rows[i].ReadBytes + resp.Rows[i].WriteBytes;
            Assert.True(prev >= cur);
        }
    }

    [Fact]
    public void FileIoTopFiles_RejectsBadTop()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 1001));
    }
}
