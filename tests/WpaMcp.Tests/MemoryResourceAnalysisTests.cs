using System.ComponentModel;
using System.Reflection;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;
using Xunit;

namespace WpaMcp.Tests;

public sealed class MemoryResourceAnalysisTests
{
    private const string MmapFixture = "fixtures/small_mmap.etl";
    private const string MemoryFixture = "fixtures/small_memory.etl";
    private const string MemoryFixturePathEnv = "WPAMCP_MEMORY_FIXTURE_PATH";

    [Fact]
    public void MemoryResourceAnalysis_ReturnsProcessSnapshotsAndHandleDeltas()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        var resp = tools.MemoryResourceAnalysis(MemoryFixturePath());

        Assert.True(resp.ProcessSampleCount > 0);
        Assert.NotEmpty(resp.Processes);
        Assert.True(resp.Pressure.ProcessSnapshotBatchCount > 0);
        Assert.NotEmpty(resp.Pressure.TopPeakWorkingSetProcesses);
        Assert.NotEmpty(resp.Pressure.TopPeakCommitProcesses);
        Assert.True(resp.Pressure.MaxObservedTotalWorkingSetBytes > 0);
        Assert.True(resp.Pressure.MaxObservedTotalCommitBytes > 0);
        Assert.True(resp.HandleEventCount > 0);
        Assert.NotEmpty(resp.Handles);
        Assert.Contains(resp.Handles, row => row.Created > 0 || row.Closed > 0 || row.DuplicatedIn > 0);
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("No Memory/ProcessMemInfo"));
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("No Object handle events"));
        Assert.True(resp.PoolEventCount > 0);
        Assert.NotEmpty(resp.PoolProcesses);
        Assert.NotEmpty(resp.PoolTags);
        Assert.DoesNotContain(resp.Warnings, warning => warning.Contains("No PoolAllocation/PoolFree"));
        Assert.Contains(resp.Warnings, warning => warning.Contains("not absolute current"));

        Assert.Contains(resp.Warnings, warning => warning.Contains("4096-byte pages"));
        Assert.Equal("all_processes", resp.ScopeMode);
        Assert.Equal("ok", resp.ScopeStatus);
        Assert.Equal("observed", resp.CapabilityStatus);
        Assert.Equal(
            resp.ProcessSampleCount + resp.HandleEventCount + resp.PoolEventCount,
            resp.MatchedEventCount);
        Assert.Null(resp.NoDataReason);
        Assert.Equal("window_global", resp.SystemMemoryScope);
        Assert.Null(resp.SelectedProcess);
        Assert.NotNull(resp.IncludedProcesses);
        Assert.Contains(resp.Warnings, warning => warning.Contains("window-global"));
        Assert.All(
            resp.Pressure.TopPeakWorkingSetProcesses,
            row => Assert.Contains(
                resp.Processes,
                process => process.Pid == row.Pid &&
                           process.ProcessStartUs == row.ProcessStartUs));
    }

    [Fact]
    public void MemoryResourceAnalysis_DeduplicatesPressureSnapshotsWithinTimeBatch()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        var resp = tools.MemoryResourceAnalysis(MemoryFixturePath(), top: 1000);

        Assert.Equal(resp.Processes.Count, resp.Pressure.TopPeakWorkingSetProcesses.Count);
        Assert.True(
            resp.Pressure.MaxObservedTotalWorkingSetBytes <=
            resp.Pressure.TopPeakWorkingSetProcesses.Sum(row => row.PeakWorkingSetBytes));
        Assert.True(
            resp.Pressure.MaxObservedTotalCommitBytes <=
            resp.Pressure.TopPeakCommitProcesses.Sum(row => row.PeakCommitBytes));
        Assert.True(
            resp.Pressure.MaxObservedTotalPrivateBytes <=
            resp.Pressure.TopPeakCommitProcesses.Sum(row => row.PeakPrivateBytes));
    }

    [Fact]
    public void MemoryResourceAnalysis_ProcessStartSelectsInstanceButSystemMemoryRemainsGlobal()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));
        var all = tools.MemoryResourceAnalysis(MemoryFixturePath(), top: 1000);
        var candidate = Assert.Single(
            all.Processes.Where(row =>
                all.Processes.Count(other => other.Pid == row.Pid) == 1).Take(1));

        var selected = tools.MemoryResourceAnalysis(
            MemoryFixturePath(),
            top: 1000,
            pid: candidate.Pid,
            processStartUs: candidate.ProcessStartUs);

        Assert.Equal("single_process", selected.ScopeMode);
        Assert.Equal(
            new ProcessInstanceKey(candidate.Pid, candidate.ProcessStartUs),
            selected.SelectedProcess);
        Assert.True(selected.SelectedProcess.HasValue);
        Assert.Equal([selected.SelectedProcess.GetValueOrDefault()], selected.IncludedProcesses);
        Assert.All(selected.Processes, row =>
        {
            Assert.Equal(candidate.Pid, row.Pid);
            Assert.Equal(candidate.ProcessStartUs, row.ProcessStartUs);
        });
        Assert.Equal("window_global", selected.SystemMemoryScope);
        Assert.Equal(all.SystemMemory, selected.SystemMemory);
        Assert.Equal(all.Pressure.MinFreeBytes, selected.Pressure.MinFreeBytes);
    }

    [Fact]
    public void MemoryResourceAnalysis_WarnsWhenProcessSnapshotsAreMissing()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        var resp = tools.MemoryResourceAnalysis(MmapFixture);

        Assert.Empty(resp.Processes);
        Assert.Equal(0, resp.ProcessSampleCount);
        Assert.Equal(0, resp.Pressure.ProcessSnapshotBatchCount);
        Assert.True(resp.Pressure.SystemSampleCount > 0);
        Assert.True(resp.Pressure.MinFreeBytes >= 0);
        Assert.Null(resp.Pressure.MinAvailableBytes);
        Assert.Null(resp.Pressure.MinAvailableTimeUs);
        Assert.NotEmpty(resp.SystemMemory);
        Assert.Equal(0, resp.PoolEventCount);
        Assert.Empty(resp.PoolProcesses);
        Assert.Empty(resp.PoolTags);
        Assert.Contains(resp.Warnings, warning => warning.Contains("Memory/ProcessMemInfo"));
        Assert.Contains(resp.Warnings, warning => warning.Contains("MemoryInfoWS"));
        Assert.Contains(resp.Warnings, warning => warning.Contains("Pool keyword"));
        Assert.Contains(resp.Warnings, warning => warning.Contains("4096-byte pages"));
    }

    [Fact]
    public void MemoryResourceAnalysis_MissingProcessScopeReturnsStructuredEmptyResponse()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        var resp = tools.MemoryResourceAnalysis(MmapFixture, pid: int.MaxValue);

        Assert.Equal("scope_not_found", resp.ScopeStatus);
        Assert.Equal("unknown", resp.CapabilityStatus);
        Assert.Equal(0, resp.MatchedEventCount);
        Assert.Equal("scope_not_found", resp.NoDataReason);
        Assert.Equal("unresolved", resp.ScopeMode);
        Assert.Empty(resp.IncludedProcesses!);
        Assert.Empty(resp.Processes);
        Assert.Empty(resp.Handles);
        Assert.Empty(resp.PoolProcesses);
        Assert.Empty(resp.PoolTags);
        Assert.Empty(resp.SystemMemory);
        Assert.Equal(0, resp.Pressure.SystemSampleCount);
        Assert.Equal("window_global", resp.SystemMemoryScope);
        Assert.Contains(resp.Warnings, warning => warning.StartsWith("scope_not_found:"));
        Assert.DoesNotContain(
            resp.Warnings,
            warning => warning.Contains("keyword", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MemoryResourceAnalysis_RejectsBadTop()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MemoryResourceAnalysis(MmapFixture, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MemoryResourceAnalysis(MmapFixture, top: 1001));
        Assert.Throws<ArgumentException>(() =>
            tools.MemoryResourceAnalysis(MmapFixture, processStartUs: 1));
    }

    [Fact]
    public void MemoryResourceAnalysis_DescriptionWarnsRankIsNotSeverity()
    {
        var description = typeof(VirtualMemoryTools)
            .GetMethod(nameof(VirtualMemoryTools.MemoryResourceAnalysis))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        Assert.Contains("Neither order implies severity or causality", description);
        Assert.Contains("absence does not prove a keyword was disabled", description);

        var matchedCountDescription = typeof(WpaMcp.Output.MemoryResourceResponse)
            .GetProperty(nameof(WpaMcp.Output.MemoryResourceResponse.MatchedEventCount))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;
        Assert.Contains("ProcessMemInfo", matchedCountDescription);
        Assert.Contains("handle events", matchedCountDescription);
        Assert.Contains("pool events", matchedCountDescription);
        Assert.Contains("System-memory samples", matchedCountDescription);
    }

    [Fact]
    public void CalculateHandleNetDelta_IgnoresDuplicatedOut()
    {
        var delta = MemoryResourceAnalysis.CalculateHandleNetDelta(
            created: 5,
            closed: 2,
            duplicatedIn: 3,
            duplicatedOut: 7);

        Assert.Equal(6, delta);
    }

    [Theory]
    [InlineData(0, "nonpaged")]
    [InlineData(1, "paged")]
    [InlineData(512, "nonpaged")]
    [InlineData(33, "paged")]
    [InlineData(268435457, "paged")]
    [InlineData(268435968, "nonpaged")]
    public void ClassifyPoolKind_UsesPoolTypeLowBit(long type, string expected)
    {
        Assert.Equal(expected, MemoryResourceAnalysis.ClassifyPoolKind(type));
    }

    [Theory]
    [InlineData(0x20202041UL, "A   ")]
    [InlineData(0x67615450UL, "PTag")]
    [InlineData(0UL, "0x00000000")]
    public void DecodePoolTag_UsesLittleEndianAscii(ulong rawTag, string expected)
    {
        Assert.Equal(expected, MemoryResourceAnalysis.DecodePoolTag(rawTag));
    }

    [Fact]
    public void ResolveScope_ReusedPidWithoutStartIsExplicitAggregate()
    {
        var oldProcess = new ProcessInstanceKey(42, 100);
        var newProcess = new ProcessInstanceKey(42, 300);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            processes:
            [
                new ProcessLifetime(oldProcess, 200, true, true),
                new ProcessLifetime(newProcess, 500, true, false),
            ],
            threads: Array.Empty<ThreadLifecycleEvent>());

        var scope = MemoryResourceAnalysis.ResolveScope(
            new TimeWindow(0, 500), pid: 42, processStartUs: null, identities);

        Assert.Equal("pid_aggregate", scope.ScopeMode);
        Assert.Null(scope.SelectedProcess);
        Assert.True(scope.PidReuseObserved);
        Assert.Equal([oldProcess, newProcess], scope.IncludedProcesses);
    }

    [Fact]
    public void ResolveScope_ProcessStartSelectsSingleLifetimeAndReturnsMissingStatus()
    {
        var oldProcess = new ProcessInstanceKey(42, 100);
        var newProcess = new ProcessInstanceKey(42, 300);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            processes:
            [
                new ProcessLifetime(oldProcess, 200, true, true),
                new ProcessLifetime(newProcess, 500, true, false),
            ],
            threads: Array.Empty<ThreadLifecycleEvent>());

        var scope = MemoryResourceAnalysis.ResolveScope(
            new TimeWindow(0, 500), pid: 42, processStartUs: 300, identities);

        Assert.Equal("single_process", scope.ScopeMode);
        Assert.Equal(newProcess, scope.SelectedProcess);
        Assert.True(scope.PidReuseObserved);
        Assert.Equal([newProcess], scope.IncludedProcesses);

        var missing = MemoryResourceAnalysis.ResolveScope(
            new TimeWindow(0, 500), pid: 42, processStartUs: 999, identities);
        Assert.Equal("scope_not_found", missing.ScopeStatus);
        Assert.Equal("unresolved", missing.ScopeMode);
        Assert.True(missing.PidReuseObserved);
        Assert.Null(missing.SelectedProcess);
        Assert.Empty(missing.IncludedProcesses);
    }

    [Fact]
    public void ResolveScope_WindowWithOneLifetimeStillReportsTraceWidePidReuse()
    {
        var oldProcess = new ProcessInstanceKey(42, 100);
        var newProcess = new ProcessInstanceKey(42, 300);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            processes:
            [
                new ProcessLifetime(oldProcess, 200, true, true),
                new ProcessLifetime(newProcess, 500, true, false),
            ],
            threads: Array.Empty<ThreadLifecycleEvent>());

        var scope = MemoryResourceAnalysis.ResolveScope(
            new TimeWindow(250, 500), pid: 42, processStartUs: null, identities);

        Assert.Equal("single_process", scope.ScopeMode);
        Assert.Equal(newProcess, scope.SelectedProcess);
        Assert.True(scope.PidReuseObserved);
        Assert.Equal([newProcess], scope.IncludedProcesses);
    }

    [Fact]
    public void DataContract_GlobalEventsOutsideScopeAreUnknownNotMissingCapability()
    {
        var process = new ProcessInstanceKey(42, 100);
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs: 500,
            processes: [new ProcessLifetime(process, 500, true, false)],
            threads: Array.Empty<ThreadLifecycleEvent>());
        var scope = MemoryResourceAnalysis.ResolveScope(
            new TimeWindow(200, 300), pid: 42, processStartUs: 100, identities);

        var contract = MemoryResourceAnalysis.ClassifyDataContract(
            scope, eventClassObserved: true, matchedEventCount: 0);

        Assert.Equal("unknown", contract.CapabilityStatus);
        Assert.Equal("no_events_in_scope", contract.NoDataReason);

        var absent = MemoryResourceAnalysis.ClassifyDataContract(
            scope, eventClassObserved: false, matchedEventCount: 0);
        Assert.Equal("not_observed", absent.CapabilityStatus);
        Assert.Equal("event_class_not_observed", absent.NoDataReason);

        var matched = MemoryResourceAnalysis.ClassifyDataContract(
            scope, eventClassObserved: true, matchedEventCount: 1);
        Assert.Equal("observed", matched.CapabilityStatus);
        Assert.Null(matched.NoDataReason);
    }

    [Fact]
    public void InstanceAccumulator_ReusedPidDoesNotMergeSnapshotsHandlesOrPoolEntries()
    {
        var oldProcess = new ProcessInstanceKey(42, 100);
        var newProcess = new ProcessInstanceKey(42, 300);
        var accumulator = new MemoryResourceAnalysis.InstanceAccumulator(
            process => process == oldProcess ? "old.exe" : "new.exe");

        accumulator.AddSnapshot(oldProcess, 150, workingSetBytes: 10, commitBytes: 20, privateBytes: 15);
        accumulator.AddSnapshot(newProcess, 350, workingSetBytes: 30, commitBytes: 40, privateBytes: 35);
        accumulator.AddHandle(oldProcess, MemoryResourceAnalysis.HandleEventKind.Create);
        accumulator.AddHandle(newProcess, MemoryResourceAnalysis.HandleEventKind.Close);
        accumulator.AddPool(oldProcess, isAllocation: true, entry: 7, bytes: 64, tag: "TEST", poolKind: "paged");
        accumulator.AddPool(oldProcess, isAllocation: false, entry: 7, bytes: 64, tag: "TEST", poolKind: "paged");
        accumulator.AddPool(newProcess, isAllocation: true, entry: 7, bytes: 96, tag: "TEST", poolKind: "paged");
        accumulator.AddPool(newProcess, isAllocation: false, entry: 7, bytes: 64, tag: "TEST", poolKind: "paged");
        // A free is attributed to the allocation owner, not to a later process instance
        // that happens to emit the free with the same PID.
        accumulator.AddPool(oldProcess, isAllocation: true, entry: 9, bytes: 32, tag: "TEST", poolKind: "paged");
        accumulator.AddPool(newProcess, isAllocation: false, entry: 9, bytes: 32, tag: "TEST", poolKind: "paged");

        Assert.Collection(
            accumulator.ProcessRows(10).OrderBy(row => row.ProcessStartUs),
            row =>
            {
                Assert.Equal(oldProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(10, row.WorkingSetBytes);
            },
            row =>
            {
                Assert.Equal(newProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(30, row.WorkingSetBytes);
            });
        Assert.Collection(
            accumulator.HandleRows(10).OrderBy(row => row.ProcessStartUs),
            row =>
            {
                Assert.Equal(oldProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(1, row.Created);
                Assert.Equal(0, row.Closed);
            },
            row =>
            {
                Assert.Equal(newProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(0, row.Created);
                Assert.Equal(1, row.Closed);
            });
        Assert.Collection(
            accumulator.PoolProcessRows(10).OrderBy(row => row.ProcessStartUs),
            row =>
            {
                Assert.Equal(oldProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(0, row.PagedOutstandingBytes);
                Assert.Equal(0, row.UnknownFreeCount);
                Assert.Equal(2, row.AllocationCount);
                Assert.Equal(2, row.FreeCount);
            },
            row =>
            {
                Assert.Equal(newProcess.StartUs, row.ProcessStartUs);
                Assert.Equal(0, row.PagedOutstandingBytes);
                Assert.Equal(0, row.UnknownFreeCount);
                Assert.Equal(1, row.AllocationCount);
                Assert.Equal(1, row.FreeCount);
            });
        Assert.Equal(
            [oldProcess.StartUs, newProcess.StartUs],
            accumulator.PressureSummary(10).TopPeakWorkingSetProcesses
                .OrderBy(row => row.ProcessStartUs)
                .Select(row => row.ProcessStartUs));
    }

    private static string MemoryFixturePath()
        => Environment.GetEnvironmentVariable(MemoryFixturePathEnv) is { Length: > 0 } path
            ? path
            : MemoryFixture;
}
