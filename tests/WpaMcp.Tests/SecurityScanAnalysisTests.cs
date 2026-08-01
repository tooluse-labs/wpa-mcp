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
        Assert.All(response.Rows, row =>
        {
            Assert.Equal("paired_interval", row.EvidenceKind);
            Assert.Equal("known_defender_schema", row.Provenance);
            Assert.Equal("high", row.Confidence);
        });
        Assert.All(response.SlowScans, row =>
        {
            Assert.Equal("paired_interval", row.EvidenceKind);
            Assert.Equal("known_defender_schema", row.Provenance);
            Assert.Equal("high", row.Confidence);
        });
    }

    [Fact]
    public void ClassifyEvidence_KnownDefenderSchemasAreHighConfidence()
    {
        var paired = SecurityScanAnalysis.ClassifyEvidence(
            "Microsoft-Antimalware-Engine",
            "StreamScanRequestTask/Start",
            new Dictionary<string, string>());
        var result = SecurityScanAnalysis.ClassifyEvidence(
            "Microsoft-Antimalware-AMFilter",
            "AMFilter_FileScanResult",
            new Dictionary<string, string>());

        Assert.NotNull(paired);
        Assert.Equal("paired_interval", paired.Value.EvidenceKind);
        Assert.Equal("known_defender_schema", paired.Value.Provenance);
        Assert.Equal("high", paired.Value.Confidence);
        Assert.NotNull(result);
        Assert.Equal("result_event", result.Value.EvidenceKind);
        Assert.Equal("known_defender_schema", result.Value.Provenance);
        Assert.Equal("high", result.Value.Confidence);
    }

    [Fact]
    public void ClassifyEvidence_ThirdPartySecurityNameMatchIsLowConfidence()
    {
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            "Aliedr-Provider",
            "ScanResult",
            new Dictionary<string, string>());

        Assert.NotNull(evidence);
        Assert.Equal("scan_like_event", evidence.Value.EvidenceKind);
        Assert.Equal("name_heuristic", evidence.Value.Provenance);
        Assert.Equal("low", evidence.Value.Confidence);
    }

    [Theory]
    [InlineData("MalwareDetected")]
    [InlineData("VirusFound")]
    [InlineData("ThreatBlocked")]
    [InlineData("QuarantineResult")]
    [InlineData("ScanResult")]
    public void ClassifyEvidence_SecurityContextTermsRemainRecognized(string eventName)
    {
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            "Contoso-Security",
            eventName,
            new Dictionary<string, string>());

        Assert.NotNull(evidence);
        Assert.Equal("scan_like_event", evidence.Value.EvidenceKind);
        Assert.Equal("low", evidence.Value.Confidence);
    }

    [Fact]
    public void ClassifyEvidence_SecurityProductFieldCanEstablishHeuristicContext()
    {
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            "Contoso-Telemetry",
            "ScanResult",
            new Dictionary<string, string> { ["Product"] = "CrowdStrike Falcon" });

        Assert.NotNull(evidence);
        Assert.Equal("name_heuristic", evidence.Value.Provenance);
        Assert.Equal("low", evidence.Value.Confidence);
    }

    [Theory]
    [InlineData("Contoso-Database", "DatabaseScan")]
    [InlineData("Search-Indexer", "IndexScan")]
    [InlineData("Storage", "ScanCompleted")]
    public void ClassifyEvidence_GenericScanNamesAreNotSecurityEvidence(
        string providerName,
        string eventName)
    {
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            providerName,
            eventName,
            new Dictionary<string, string>());

        Assert.Null(evidence);
    }

    [Fact]
    public void ClassifyEvidence_ScannedPathDoesNotCreateSecurityContext()
    {
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            "Storage",
            "ScanCompleted",
            new Dictionary<string, string>
            {
                ["Path"] = "c:\\security\\database.bin",
            });

        Assert.Null(evidence);
    }

    [Fact]
    public void SecurityProjection_DoesNotMergeExactAndHeuristicEvidence()
    {
        var target = new SecurityScanAnalysis.ScanTarget(
            Source: "Microsoft Defender",
            ProviderName: "Microsoft-Antimalware-AMFilter",
            Process: "app.exe",
            Pid: 42,
            Path: "c:\\sample.dll");
        var exact = SecurityScanAnalysis.ClassifyEvidence(
            target.ProviderName,
            "AMFilter_FileScanResult",
            new Dictionary<string, string>()) ?? throw new InvalidOperationException();
        var heuristic = SecurityScanAnalysis.ClassifyEvidence(
            target.ProviderName,
            "ThreatTelemetry",
            new Dictionary<string, string>()) ?? throw new InvalidOperationException();

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [
                new SecurityScanAnalysis.SecurityScanPointEvent(
                    target, target.Source, target.ProviderName, "AMFilter_FileScanResult",
                    IsStart: false, IsStop: false, IsResult: true, Reasons: [], Statuses: [], exact),
                new SecurityScanAnalysis.SecurityScanPointEvent(
                    target, target.Source, target.ProviderName, "ThreatTelemetry",
                    IsStart: false, IsStop: false, IsResult: true, Reasons: [], Statuses: [], heuristic),
            ],
            top: 10);

        Assert.Equal(2, response.Rows.Count);
        Assert.Contains(response.Rows, row => row.EvidenceKind == "result_event" && row.Confidence == "high");
        Assert.Contains(response.Rows, row => row.EvidenceKind == "scan_like_event" && row.Confidence == "low");
        Assert.Equal(2, response.Providers.Count);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("name_heuristic:", StringComparison.Ordinal));
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
    public void SecurityScanAnalysis_TargetProcessStartRequiresTargetPidBeforeLoadingTrace()
    {
        var tools = new SecurityTools(new TraceCache(capacity: 2));

        Assert.Throws<ArgumentException>(() => tools.SecurityScanAnalysis(
            "nonexistent.etl",
            targetProcessStartUs: 10));
    }

    [Fact]
    public void SecurityProjection_PidReuseKeepsTargetInstancesSeparateAndReportsAggregateScope()
    {
        var first = new ProcessInstanceKey(42, 0);
        var second = new ProcessInstanceKey(42, 100);
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 200),
            pid: 42,
            processStartUs: null,
            Lifetimes(
                (first, 100),
                (second, 200)));

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [
                PointEvent(42, first, "payload_target_pid", "a.bin"),
                PointEvent(42, second, "payload_target_pid", "b.bin"),
            ],
            top: 10,
            scope,
            eventClassObserved: true);

        Assert.Equal("pid_aggregate", response.ScopeMode);
        Assert.True(response.PidReuseObserved);
        Assert.Equal([first, second], response.IncludedProcesses);
        Assert.Equal(2, response.MatchedEventCount);
        Assert.Equal([0L, 100L], response.Rows.Select(row => row.ProcessStartUs).Order().ToArray());
        Assert.Contains(response.Warnings, warning => warning.StartsWith("pid_aggregate:", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityProjection_ExactTargetInstanceExcludesOtherPidLifetime()
    {
        var first = new ProcessInstanceKey(42, 0);
        var second = new ProcessInstanceKey(42, 100);
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 200),
            pid: 42,
            processStartUs: 100,
            Lifetimes(
                (first, 100),
                (second, 200)));

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [
                PointEvent(42, first, "payload_target_pid", "a.bin"),
                PointEvent(42, second, "payload_target_pid", "b.bin"),
            ],
            top: 10,
            scope,
            eventClassObserved: true);

        Assert.Equal("single_process", response.ScopeMode);
        Assert.Equal(second, response.SelectedProcess);
        Assert.Equal(1, response.MatchedEventCount);
        Assert.Equal(100, Assert.Single(response.Rows).ProcessStartUs);
        Assert.Null(response.NoDataReason);
    }

    [Fact]
    public void SecurityProjection_DoesNotAttributePairedDurationAcrossTargetPidReuse()
    {
        var first = new ProcessInstanceKey(42, 0);
        var second = new ProcessInstanceKey(42, 100);
        var emitter = new ProcessInstanceKey(7, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = "scan-1",
            ["PID"] = "42",
        };
        var pair = new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
            new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", "scan-1"),
            90,
            110,
            new SecurityScanStartData(fields, first, "payload_target_pid"),
            new SecurityScanStopData(fields, second, "payload_target_pid"));
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 200),
            pid: 42,
            processStartUs: null,
            Lifetimes((first, 100), (second, 200), (emitter, 200)));

        var response = SecurityScanAnalysis.ProjectPairs(
            [pair],
            new TimeWindow(0, 200),
            top: 10,
            pid: 42,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null,
            scope,
            eventClassObserved: true);

        Assert.Equal(0, response.PairedScanCount);
        Assert.Equal(1, response.TargetIdentityMismatchCount);
        Assert.Equal(1, response.ScopedUnattributedEventCount);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Contains(response.Warnings, warning => warning.StartsWith("target_identity_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityProjection_MissingTargetInstanceReturnsStableScopeStatus()
    {
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 200),
            pid: 42,
            processStartUs: 999,
            Lifetimes((new ProcessInstanceKey(42, 0), 200)));

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [],
            top: 10,
            scope,
            eventClassObserved: true);

        Assert.Equal("scope_not_found", response.ScopeStatus);
        Assert.Equal("scope_not_found", response.NoDataReason);
        Assert.Empty(response.Rows);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Fact]
    public void ResolveTargetIdentity_PayloadPidWinsAndEmitterFallbackIsExplicit()
    {
        var target = new ProcessInstanceKey(42, 0);
        var emitter = new ProcessInstanceKey(7, 0);
        var resolver = new ProcessInstanceResolver(Lifetimes((target, 200), (emitter, 200)));

        var fromPayload = SecurityScanAnalysis.ResolveTargetIdentity(
            emitterPid: 7,
            emitterProcessName: "scanner.exe",
            new Dictionary<string, string> { ["TargetProcessId"] = "42" },
            timestampUs: 50,
            resolver,
            endpoint: false);
        var fromEmitter = SecurityScanAnalysis.ResolveTargetIdentity(
            emitterPid: 7,
            emitterProcessName: "scanner.exe",
            new Dictionary<string, string>(),
            timestampUs: 50,
            resolver,
            endpoint: false);

        Assert.Equal(42, fromPayload.Target.Pid);
        Assert.Equal(target, fromPayload.TargetProcess);
        Assert.Equal("payload_target_pid", fromPayload.IdentitySource);
        Assert.Equal(string.Empty, fromPayload.Target.Process);
        Assert.Equal(7, fromEmitter.Target.Pid);
        Assert.Equal(emitter, fromEmitter.TargetProcess);
        Assert.Equal("emitter_fallback", fromEmitter.IdentitySource);
        Assert.Equal("scanner.exe", fromEmitter.Target.Process);
    }

    [Fact]
    public void SecurityProjection_PairedEmitterFallbackRetainsResolvedTarget()
    {
        var emitter = new ProcessInstanceKey(7, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = "scan-1",
            ["Path"] = "c:\\sample.dll",
        };
        var target = new SecurityScanAnalysis.ScanTarget(
            "Microsoft Defender",
            "Microsoft-Antimalware-Engine",
            "scanner.exe",
            7,
            "c:\\sample.dll");
        var pair = new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
            new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", "scan-1"),
            10,
            20,
            new SecurityScanStartData(fields, emitter, "emitter_fallback", target),
            new SecurityScanStopData(fields, emitter, "emitter_fallback", target));

        var response = SecurityScanAnalysis.ProjectPairs(
            [pair],
            new TimeWindow(0, 100),
            top: 10,
            pid: 7,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null);

        var row = Assert.Single(response.Rows);
        Assert.Equal(7, row.Pid);
        Assert.Equal("scanner.exe", row.Process);
        Assert.Equal(0, row.ProcessStartUs);
        Assert.Equal("emitter_fallback", row.TargetIdentitySource);
        Assert.Equal(1, response.PairedScanCount);
        Assert.Equal(1, response.EmitterFallbackIdentityCount);
    }

    [Fact]
    public void SecurityProjection_OneUnresolvedEndpointDoesNotAttributePairToResolvedTarget()
    {
        var targetProcess = new ProcessInstanceKey(42, 0);
        var emitter = new ProcessInstanceKey(7, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = "scan-1",
            ["PID"] = "42",
        };
        var target = new SecurityScanAnalysis.ScanTarget(
            "Microsoft Defender",
            "Microsoft-Antimalware-Engine",
            "target.exe",
            42,
            string.Empty);
        var pair = new PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>(
            new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", "scan-1"),
            10,
            60,
            new SecurityScanStartData(
                fields, targetProcess, "payload_target_pid", target),
            new SecurityScanStopData(
                fields,
                TargetProcess: null,
                TargetIdentitySource: "payload_target_pid",
                Target: target));

        var response = SecurityScanAnalysis.ProjectPairs(
            [pair],
            new TimeWindow(0, 100),
            top: 10,
            pid: null,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null);

        Assert.Equal(0, response.PairedScanCount);
        Assert.Equal(0, response.TotalDurationUs);
        Assert.Empty(response.Rows);
        Assert.Equal(1, response.TargetIdentityMismatchCount);
        Assert.Equal(1, response.ScopedUnattributedEventCount);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Contains(response.Warnings, warning =>
            warning.StartsWith("target_identity_mismatch:", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityUnmatchedData_UsesSavedEmitterFallbackTarget()
    {
        var emitter = new ProcessInstanceKey(7, 0);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__Source"] = "Microsoft Defender",
            ["__ProviderName"] = "Microsoft-Antimalware-Engine",
            ["__Id"] = "scan-1",
        };
        var fallback = new SecurityScanAnalysis.ScanTarget(
            "Microsoft Defender",
            "Microsoft-Antimalware-Engine",
            "scanner.exe",
            7,
            string.Empty);
        var start = new SecurityScanStartData(
            fields, emitter, "emitter_fallback", fallback);
        var stop = new SecurityScanStopData(
            fields, emitter, "emitter_fallback", fallback);

        Assert.Equal(fallback, SecurityScanAnalysis.TargetFromData(start));
        Assert.Equal(fallback, SecurityScanAnalysis.TargetFromData(stop));
    }

    [Fact]
    public void SecurityProjection_UnresolvedPayloadTargetIsReportedInsteadOfRelabeledAsEmitter()
    {
        var target = new SecurityScanAnalysis.ScanTarget(
            "Security/EDR", "Contoso-Security", string.Empty, 999, "sample.bin");
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            target.ProviderName,
            "ScanResult",
            new Dictionary<string, string>())!.Value;

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [new SecurityScanAnalysis.SecurityScanPointEvent(
                target,
                target.Source,
                target.ProviderName,
                "ScanResult",
                IsStart: false,
                IsStop: false,
                IsResult: true,
                Reasons: [],
                Statuses: [],
                evidence,
                TargetProcess: null,
                TargetIdentitySource: "payload_target_pid")],
            top: 10,
            scope: null,
            eventClassObserved: true);

        Assert.Equal("payload_target_pid", response.TargetIdentitySource);
        Assert.Equal(1, response.UnresolvedTargetIdentityCount);
        Assert.Equal(1, response.PayloadTargetIdentityCount);
        Assert.Equal(0, response.EmitterFallbackIdentityCount);
        Assert.Contains(response.Warnings, warning => warning.StartsWith("target_identity_incomplete:", StringComparison.Ordinal));
    }

    [Fact]
    public void SecurityProjection_SelectedRawTargetWithUnresolvedLifetimeIsUnattributed()
    {
        var targetKey = new ProcessInstanceKey(42, 0);
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 200),
            pid: 42,
            processStartUs: 0,
            Lifetimes((targetKey, 200)));
        var target = new SecurityScanAnalysis.ScanTarget(
            "Security/EDR", "Contoso-Security", string.Empty, 42, "sample.bin");
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            target.ProviderName,
            "ScanResult",
            new Dictionary<string, string>())!.Value;

        var response = SecurityScanAnalysis.ProjectPointEvents(
            [new SecurityScanAnalysis.SecurityScanPointEvent(
                target,
                target.Source,
                target.ProviderName,
                "ScanResult",
                IsStart: false,
                IsStop: false,
                IsResult: true,
                Reasons: [],
                Statuses: [],
                evidence,
                TargetProcess: null,
                TargetIdentitySource: "payload_target_pid")],
            top: 10,
            scope,
            eventClassObserved: true);

        Assert.Empty(response.Rows);
        Assert.Equal("source_events_unattributed", response.NoDataReason);
        Assert.Equal(1, response.ScopedUnattributedEventCount);
        Assert.Equal(0, response.MatchedEventCount);
    }

    [Theory]
    [InlineData(false, "event_class_not_observed")]
    [InlineData(true, "no_events_in_scope")]
    public void SecurityProjection_EmptyResultDistinguishesCapabilityFromScope(
        bool eventClassObserved,
        string expectedReason)
    {
        var response = SecurityScanAnalysis.ProjectPointEvents(
            [],
            top: 10,
            scope: null,
            eventClassObserved);

        Assert.Equal(expectedReason, response.NoDataReason);
        Assert.Equal(eventClassObserved ? "unknown" : "not_observed", response.CapabilityStatus);
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
        Assert.True(tool!.OpenWorld);
        Assert.Contains("third-party", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("degrade", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confidence", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("target PID", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("emitter", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnoseWindowEvidenceCanCarrySecurityConfidence()
    {
        Assert.NotNull(typeof(WindowEvidenceRow).GetProperty(nameof(WindowEvidenceRow.EvidenceKind)));
        Assert.NotNull(typeof(WindowEvidenceRow).GetProperty(nameof(WindowEvidenceRow.Provenance)));
        Assert.NotNull(typeof(WindowEvidenceRow).GetProperty(nameof(WindowEvidenceRow.Confidence)));
    }

    private static SecurityScanAnalysis.SecurityScanPointEvent PointEvent(
        int pid,
        ProcessInstanceKey process,
        string identitySource,
        string path)
    {
        var target = new SecurityScanAnalysis.ScanTarget(
            "Microsoft Defender",
            "Microsoft-Antimalware-AMFilter",
            "app.exe",
            pid,
            path);
        var evidence = SecurityScanAnalysis.ClassifyEvidence(
            target.ProviderName,
            "AMFilter_FileScanResult",
            new Dictionary<string, string>())!.Value;
        return new SecurityScanAnalysis.SecurityScanPointEvent(
            target,
            target.Source,
            target.ProviderName,
            "AMFilter_FileScanResult",
            IsStart: false,
            IsStop: false,
            IsResult: true,
            Reasons: [],
            Statuses: [],
            evidence,
            TargetProcess: process,
            TargetIdentitySource: identitySource);
    }

    private static IReadOnlyList<ProcessLifetime> Lifetimes(
        params (ProcessInstanceKey Key, long EndUs)[] values) =>
        values.Select(value => new ProcessLifetime(
            value.Key,
            value.EndUs,
            StartObserved: true,
            EndObserved: true)).ToArray();
}
