using System.ComponentModel;
using System.Reflection;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;
using Xunit;

namespace WprMcp.Tests;

public sealed class MemoryResourceAnalysisTests
{
    private const string MmapFixture = "fixtures/small_mmap.etl";
    private const string MemoryFixture = "fixtures/small_memory.etl";
    private const string MemoryFixturePathEnv = "WPRMCP_MEMORY_FIXTURE_PATH";

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
    public void MemoryResourceAnalysis_RejectsBadTop()
    {
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MemoryResourceAnalysis(MmapFixture, top: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => tools.MemoryResourceAnalysis(MmapFixture, top: 1001));
    }

    [Fact]
    public void MemoryResourceAnalysis_DescriptionWarnsRankIsNotSeverity()
    {
        var description = typeof(VirtualMemoryTools)
            .GetMethod(nameof(VirtualMemoryTools.MemoryResourceAnalysis))!
            .GetCustomAttribute<DescriptionAttribute>()!
            .Description;

        Assert.Contains("Neither order implies severity or causality", description);
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

    private static string MemoryFixturePath()
        => Environment.GetEnvironmentVariable(MemoryFixturePathEnv) is { Length: > 0 } path
            ? path
            : MemoryFixture;
}
