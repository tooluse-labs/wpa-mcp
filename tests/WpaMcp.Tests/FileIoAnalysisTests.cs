using WpaMcp.Core;
using WpaMcp.Analyzers;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class FileIoAnalysisTests
{
    private const string FixturePath = "fixtures/small_fileio.etl"; // captured by fixtures/capture_all.ps1

    [Fact]
    public void FileIoTopFiles_ReturnsRows()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopFiles(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
    }

    [Fact]
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
    public void FileIoTopFiles_FiltersHalfOpenTimeWindow()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var eventTimes = new List<long>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.FileIORead += data => eventTimes.Add(ToUs(data.TimeStampRelativeMSec));
            kernel.FileIOWrite += data => eventTimes.Add(ToUs(data.TimeStampRelativeMSec));
        });
        if (eventTimes.Count == 0) return;

        var firstUs = eventTimes.Min();
        var lastUs = eventTimes.Max();
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var tools = new IoTools(cache);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            tools.FileIoTopFiles(
                FixturePath, top: 50, startUs: firstUs, endUs: firstUs));
        var firstTick = tools.FileIoTopFiles(FixturePath, top: 50, startUs: firstUs, endUs: firstUs + 1);

        Assert.NotEmpty(firstTick.Rows);
        Assert.True(firstTick.Rows.Sum(row => row.ReadCount + row.WriteCount) >= 1);
        if (lastUs + 1 < traceEndUs)
        {
            var afterTraceIo = tools.FileIoTopFiles(
                FixturePath, top: 50, startUs: lastUs + 1, endUs: traceEndUs);
            Assert.Empty(afterTraceIo.Rows);
        }
    }

    [Fact]
    public void FileIoTopFiles_RejectsBadTop()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 1001));
    }

    private static long ToUs(double timeStampRelativeMSec) => (long)(timeStampRelativeMSec * 1000);
}
