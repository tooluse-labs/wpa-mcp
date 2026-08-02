using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public class HardFaultByFileAnalysisTests
{
    private const string FixturePath = "fixtures/small_mmap.etl";
    private const string CpuFixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void HardFaultByFile_ReturnsAtLeastOneRow()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.NotEmpty(resp.Rows);
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.True(resp.MatchedEventCount > 0);
        Assert.Null(resp.NoDataReason);
        Assert.All(resp.Rows, row =>
        {
            var counts = row.MappingStateEventCounts;
            Assert.Equal(
                row.PageInCount,
                counts.EventNameEventCount +
                counts.TemporalFileKeyEventCount +
                counts.TemporalFileObjectEventCount +
                counts.AmbiguousTemporalMappingEventCount +
                counts.UnresolvedFileIdentityEventCount);
        });
    }

    [Fact]
    public void HardFaultByFile_FilteredTraceWithoutHardFaultsReportsEventClassNotObserved()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));

        var response = tools.HardFaultByFile(
            CpuFixturePath,
            top: 10,
            startUs: 0,
            endUs: 100_000);

        Assert.Empty(response.Rows);
        Assert.Equal("ok", response.ScopeStatus);
        Assert.Equal("not_observed", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("event_class_not_observed", response.NoDataReason);
    }

    [Fact]
    public void HardFaultByFile_GlobalEventsOutsideWindowReportNoEventsInScope()
    {
        var cache = new TraceCache(capacity: 2);
        var trace = cache.Get(FixturePath);
        var occupiedTimes = new HashSet<long>();
        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.MemoryHardFault += data =>
                occupiedTimes.Add(TraceTime.FromMilliseconds(data.TimeStampRelativeMSec));
        });
        Assert.NotEmpty(occupiedTimes);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var emptyStartUs = Enumerable.Range(0, checked((int)Math.Min(traceEndUs, 10_000)))
            .Select(value => (long)value)
            .First(timestampUs => !occupiedTimes.Contains(timestampUs));
        var tools = new HardFaultTools(cache);

        var response = tools.HardFaultByFile(
            FixturePath,
            top: 10,
            startUs: emptyStartUs,
            endUs: emptyStartUs + 1);

        Assert.Empty(response.Rows);
        Assert.Equal("ok", response.ScopeStatus);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("no_events_in_scope", response.NoDataReason);
    }

    [Fact]
    public void HardFaultByFile_MissingProcessScopeTakesPrecedenceOverTraceCapability()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));

        var response = tools.HardFaultByFile(
            FixturePath,
            top: 10,
            pid: int.MaxValue,
            startUs: 0,
            endUs: 100_000,
            processStartUs: 123);

        Assert.Empty(response.Rows);
        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("unknown", response.CapabilityStatus);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Equal("scope_not_found", response.NoDataReason);
    }

    [Fact]
    public void HardFaultByFile_AlwaysIncludesKeywordHint()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 10);
        Assert.Contains(resp.Warnings, w => w.Contains("MemoryHardFaults"));
    }

    [Fact]
    public void HardFaultByFile_CanSortByMaxLatency()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var resp = tools.HardFaultByFile(FixturePath, top: 100, orderBy: "max_latency");

        Assert.NotEmpty(resp.Rows);
        Assert.True(resp.Rows.Zip(resp.Rows.Skip(1), (a, b) => a.MaxLatencyUs >= b.MaxLatencyUs).All(v => v));
        Assert.All(resp.Rows, row => Assert.True(row.MaxLatencyTimeUs >= 0));
    }

    [Fact]
    public void HardFaultByFile_ReportsMaxLatencyTimestamp()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        var fullTrace = tools.HardFaultByFile(FixturePath, top: 100, orderBy: "max_latency");
        var slowest = fullTrace.Rows[0];

        var singleTimestampWindow = tools.HardFaultByFile(
            FixturePath,
            top: 100,
            startUs: slowest.MaxLatencyTimeUs,
            endUs: slowest.MaxLatencyTimeUs + 1,
            orderBy: "max_latency");

        var row = Assert.Single(singleTimestampWindow.Rows.Where(row => row.File == slowest.File));
        Assert.Equal(slowest.MaxLatencyUs, row.MaxLatencyUs);
        Assert.Equal(slowest.MaxLatencyTimeUs, row.MaxLatencyTimeUs);
    }

    [Fact]
    public void HardFaultByFile_RejectsWindowBeyondTrace()
    {
        var cache = new TraceCache(capacity: 2);
        var tools = new HardFaultTools(cache);
        var fullTrace = tools.HardFaultByFile(FixturePath, top: 100);
        Assert.NotEmpty(fullTrace.Rows);
        var trace = cache.Get(FixturePath);
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile(
            FixturePath, top: 100, startUs: traceEndUs, endUs: traceEndUs + 1));
    }

    [Fact]
    public void HardFaultByFile_RejectsBadTop()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.HardFaultByFile("nonexistent.etl", top: 1001));
    }

    [Fact]
    public void HardFaultByFile_RejectsBadOrderBy()
    {
        var tools = new HardFaultTools(new TraceCache(capacity: 2));
        Assert.Throws<ArgumentException>(() => tools.HardFaultByFile("nonexistent.etl", orderBy: "duration"));
    }

    [Fact]
    public void ResolveFileName_UsesTheFileKeyNameValidAtTheFaultTime()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "early.dat");
        names.Add(0x20, timestampUs: 300, "later.dat");

        Assert.Equal(
            "early.dat",
            HardFaultByFileAnalysis.ResolveFileName(
                eventFileName: null,
                fileKey: 0x20,
                timestampUs: 200,
                names));
    }

    [Fact]
    public void ResolveFileMapping_ReportsTemporalFileKeyBasis()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "mapped.dat");

        var resolution = HardFaultByFileAnalysis.ResolveFileMapping(
            eventFileName: null,
            fileKey: 0x20,
            timestampUs: 200,
            names);

        Assert.Equal("mapped.dat", resolution.File);
        Assert.Equal(FileMappingStates.TemporalFileKey, resolution.MappingState);
    }

    [Fact]
    public void ResolveFileName_PrefersTheNameCarriedByTheFaultEvent()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "mapped.dat");

        Assert.Equal(
            "event.dat",
            HardFaultByFileAnalysis.ResolveFileName(
                eventFileName: "event.dat",
                fileKey: 0x20,
                timestampUs: 200,
                names));
    }

    [Fact]
    public void ResolveFileMapping_EventNameWinsAndReportsItsBasis()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "mapped.dat");

        var resolution = HardFaultByFileAnalysis.ResolveFileMapping(
            eventFileName: "event.dat",
            fileKey: 0x20,
            timestampUs: 200,
            names);

        Assert.Equal("event.dat", resolution.File);
        Assert.Equal(FileMappingStates.EventName, resolution.MappingState);
    }

    [Fact]
    public void ResolveFileName_DoesNotCarryADeletedFileKeyForward()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "deleted.dat");
        names.End(0x20, timestampUs: 200);

        var name = HardFaultByFileAnalysis.ResolveFileName(
            eventFileName: null,
            fileKey: 0x20,
            timestampUs: 300,
            names);

        Assert.StartsWith("<unmapped:0x", name);
    }

    [Fact]
    public void ResolveFileMapping_DeletedKeyHasTypedUnresolvedState()
    {
        var names = new TemporalFileNameMap<ulong>();
        names.Add(0x20, timestampUs: 100, "deleted.dat");
        names.End(0x20, timestampUs: 200);

        var resolution = HardFaultByFileAnalysis.ResolveFileMapping(
            eventFileName: null,
            fileKey: 0x20,
            timestampUs: 300,
            names);

        Assert.Equal(FileMappingStates.UnresolvedFileIdentity, resolution.MappingState);
        Assert.StartsWith("<unmapped:0x", resolution.File);
    }

    [Fact]
    public void HardFaultAggregate_ExposesMixedMappingStateAndExactStateCounts()
    {
        var aggregate = new HardFaultByFileAnalysis.HardFaultFileAggregate();
        aggregate.Add(bytes: 4_096, latencyUs: 50, timestampUs: 100, FileMappingStates.EventName);
        aggregate.Add(bytes: 8_192, latencyUs: 75, timestampUs: 200, FileMappingStates.TemporalFileKey);
        aggregate.Add(bytes: 4_096, latencyUs: 25, timestampUs: 300, FileMappingStates.UnresolvedFileIdentity);

        var row = aggregate.ToRow("same.dat");

        Assert.Equal(FileMappingStates.Mixed, row.MappingState);
        Assert.Equal(3, row.PageInCount);
        Assert.Equal(75, row.MaxLatencyUs);
        Assert.Equal(200, row.MaxLatencyTimeUs);
        Assert.Equal(1, row.MappingStateEventCounts.EventNameEventCount);
        Assert.Equal(1, row.MappingStateEventCounts.TemporalFileKeyEventCount);
        Assert.Equal(0, row.MappingStateEventCounts.TemporalFileObjectEventCount);
        Assert.Equal(0, row.MappingStateEventCounts.AmbiguousTemporalMappingEventCount);
        Assert.Equal(1, row.MappingStateEventCounts.UnresolvedFileIdentityEventCount);
    }

    [Fact]
    public void HardFaultRows_UseFileNameAsStableFinalTieBreaker()
    {
        static WpaMcp.Output.HardFaultFileRow Row(string file)
        {
            var aggregate = new HardFaultByFileAnalysis.HardFaultFileAggregate();
            aggregate.Add(
                bytes: 4_096,
                latencyUs: 50,
                timestampUs: 100,
                FileMappingStates.EventName);
            return aggregate.ToRow(file);
        }

        foreach (var orderBy in new[] { "bytes", "count", "max_latency" })
        {
            var rows = HardFaultByFileAnalysis.RankRows(
                [Row("z.dat"), Row("a.dat")],
                orderBy,
                top: 10);

            Assert.Equal(["a.dat", "z.dat"], rows.Select(row => row.File));
        }
    }
}
