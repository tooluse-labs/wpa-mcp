using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;
using WpaMcp.Tools;

namespace WpaMcp.Tests;

public sealed class SecurityScanAnalysisTests
{
    private const string FixturePath = "fixtures/small_cpu.etl";

    [Fact]
    public void SecurityProjection_UsesClippedDurationInTargetAndRequestTotals()
    {
        var emitter = new ProcessInstanceKey(4, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = "scan-1",
            ["Path"] = "c:\\sample.dll",
            ["Process"] = "app.exe",
            ["PID"] = "8",
        };
        var pair = new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
            new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", "scan-1"),
            90,
            210,
            new SecurityScanStartData(fields),
            new SecurityScanStopData(fields));

        var response = SecurityScanAnalysis.ProjectPairs(
            [pair],
            new TimeWindow(100, 200),
            top: 10,
            pid: null,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null);

        Assert.Equal(120, Assert.Single(response.SlowScans).FullDurationUs);
        Assert.Equal(100, response.SlowScans[0].AccountedDurationUs);
        Assert.Equal(100, response.SlowScans[0].DurationUs);
        Assert.Equal(100, Assert.Single(response.Rows).TotalAccountedDurationUs);
        Assert.Equal(100, response.TotalDurationUs);
    }

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
            Statuses: ["0"],
            TotalFullDurationUs: 0,
            TotalAccountedDurationUs: 0,
            AvgAccountedDurationUs: null,
            MaxAccountedDurationUs: null,
            AccountingMode: "clipped_overlap_v2");

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
