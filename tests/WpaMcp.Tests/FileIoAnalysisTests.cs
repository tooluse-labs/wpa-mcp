using WpaMcp.Core;
using WpaMcp.Analyzers;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class FileIoAnalysisTests
{
    private const string FixturePath = "fixtures/small_fileio.etl"; // captured by fixtures/capture_all.ps1
    private const string CpuOnlyFixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void FileIoTopFiles_ReturnsRows()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        var resp = tools.FileIoTopFiles(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.True(resp.MatchedEventCount > 0);
        Assert.Null(resp.NoDataReason);
        Assert.NotNull(resp.Warnings);
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
            Assert.Equal("ok", afterTraceIo.ScopeStatus);
            Assert.Equal("unknown", afterTraceIo.CapabilityStatus);
            Assert.Equal(0, afterTraceIo.MatchedEventCount);
            Assert.Equal("no_events_in_scope", afterTraceIo.NoDataReason);
            Assert.Contains(afterTraceIo.Warnings!, warning =>
                warning.StartsWith("no_events_in_scope:", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void FileIoTopFiles_MissingPid_ReturnsStructuredScopeStatus()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));

        var resp = tools.FileIoTopFiles(FixturePath, top: 10, pid: int.MaxValue);

        Assert.Empty(resp.Rows);
        Assert.Null(resp.SelectedProcess);
        Assert.Equal("unresolved", resp.ScopeMode);
        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal(0, resp.MatchedEventCount);
        Assert.Equal("scope_not_found", resp.NoDataReason);
        Assert.Empty(resp.IncludedProcesses!);
        Assert.Contains(resp.Warnings!, warning =>
            warning.StartsWith("scope_not_found:", StringComparison.Ordinal));
    }

    [Fact]
    public void FileIoTopFiles_NoTraceWideEventFamily_IsNotObservedRatherThanKeywordProof()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));

        var resp = tools.FileIoTopFiles(CpuOnlyFixturePath, top: 10);

        Assert.Empty(resp.Rows);
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("not_observed", resp.CapabilityStatus);
        Assert.Equal(0, resp.MatchedEventCount);
        Assert.Equal("event_class_not_observed", resp.NoDataReason);
        var warning = Assert.Single(resp.Warnings!);
        Assert.StartsWith("event_class_not_observed:", warning, StringComparison.Ordinal);
        Assert.Contains("does not prove", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FileIoTopFiles_ExactStartSelectsOneProcessInstance()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var identities = TraceIdentityIndex.For(trace);
        ProcessInstanceKey? eventProcess = null;
        KernelEventWalker.Walk(trace, kernel =>
        {
            void Observe(int pid, double timestampMs)
            {
                if (eventProcess.HasValue || pid <= 0) return;
                var resolution = identities.Processes.Resolve(
                    pid,
                    TraceTime.FromMilliseconds(timestampMs),
                    processStartUs: null);
                if (resolution.Status == InstanceResolutionStatus.Resolved)
                    eventProcess = resolution.Value;
            }

            kernel.FileIORead += data => Observe(data.ProcessID, data.TimeStampRelativeMSec);
            kernel.FileIOWrite += data => Observe(data.ProcessID, data.TimeStampRelativeMSec);
        });
        var selected = Assert.IsType<ProcessInstanceKey>(eventProcess);

        var resp = new IoTools(cache).FileIoTopFiles(
            FixturePath,
            top: 50,
            pid: selected.Pid,
            processStartUs: selected.StartUs);

        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("single_process", resp.ScopeMode);
        Assert.Equal(selected, resp.SelectedProcess);
        Assert.Equal([selected], resp.IncludedProcesses);
        Assert.True(resp.MatchedEventCount > 0);
    }

    [Fact]
    public void FileIoTopFiles_RejectsBadTop()
    {
        var tools = new IoTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.FileIoTopFiles("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void ResolveFileName_PrefersTheNameCarriedByTheIoEvent()
    {
        var resolver = new FileObjectResolver();
        resolver.AddMapping(fileObject: 0x10, fileKey: 0x20, timestampUs: 100, "mapped.dat");

        var name = FileIoAnalysis.ResolveFileName(
            resolver,
            eventFileName: "event.dat",
            fileObject: 0x10,
            fileKey: 0x20,
            timestampUs: 200);

        Assert.Equal("event.dat", name);
    }

    [Fact]
    public void WarningBuilders_DoNotClaimUnobservedEventsProveMissingCaptureConfiguration()
    {
        var kernel = WpaMcp.Output.WarningBuilder.MissingKeyword("FileIO", "FileIO");
        var defaultProfile = WpaMcp.Output.WarningBuilder.NoEventsInDefaultProfile(
            "ReadyThread", "CSwitch / ReadyThread");
        var clr = WpaMcp.Output.WarningBuilder.MissingClrKeyword("GC allocation", "GC");

        Assert.Contains("does not prove", kernel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not prove", defaultProfile, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not prove", clr, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("trace lacks", clr, StringComparison.OrdinalIgnoreCase);
    }

    private static long ToUs(double timeStampRelativeMSec) => (long)(timeStampRelativeMSec * 1000);
}
