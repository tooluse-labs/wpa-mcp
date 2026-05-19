using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using WprMcp.Core;
using WprMcp.Output;
using WprMcp.Tools;

namespace WprMcp.Tests;

public sealed class SecurityScanAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void SecurityScanAnalysis_NoMatchingProviders_ReturnsActionableWarning()
    {
        var tools = new SecurityTools(new TraceCache(capacity: 2));

        var response = tools.SecurityScanAnalysis(FixturePath, top: 5);

        Assert.Empty(response.Rows);
        Assert.Empty(response.SlowScans);
        Assert.Equal(0, response.MatchedEventCount);
        Assert.Contains(response.Warnings, warning => warning.Contains("No security scan ETW events", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityScanAnalysis_RejectsBadTopBeforeLoadingTrace()
    {
        var tools = new SecurityTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentOutOfRangeException>(() => tools.SecurityScanAnalysis("nonexistent.etl", top: 0));
    }

    [Fact]
    public void SecurityScanAnalysis_ResponseShapeIncludesGenericSourceAndProvider()
    {
        var row = new SecurityScanTargetRow(
            Source: "Alibaba Aliedr",
            ProviderName: "Aliedr-Provider",
            Process: "C:\\app.exe",
            Pid: 123,
            Path: "C:\\data.bin",
            PairedScanCount: 0,
            TotalDurationUs: 0,
            AvgDurationUs: null,
            MaxDurationUs: null,
            EventCount: 2,
            StartEventCount: 0,
            StopEventCount: 0,
            ResultEventCount: 2,
            EventNames: ["ScanResult:2"],
            Reasons: ["4"],
            Statuses: ["0"]);

        Assert.Equal("Alibaba Aliedr", row.Source);
        Assert.Equal("Aliedr-Provider", row.ProviderName);
        Assert.Equal(2, row.EventCount);
    }

    [Fact]
    public void SecurityScanAnalysis_DescriptionExplainsThirdPartyDegradation()
    {
        var method = typeof(SecurityTools).GetMethod(nameof(SecurityTools.SecurityScanAnalysis));
        var tool = method?.GetCustomAttribute<McpServerToolAttribute>();
        var description = method?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.NotNull(tool);
        Assert.False(tool!.OpenWorld);
        Assert.Contains("third-party", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("degrade", description, StringComparison.OrdinalIgnoreCase);
    }
}
