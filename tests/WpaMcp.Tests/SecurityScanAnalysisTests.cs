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

        var compositeEvidence = DiagnoseTools.BuildSecurityDurationEvidence(
            response,
            pid: null);
        Assert.Equal(response.TotalDurationUs, compositeEvidence.MetricValue);
        Assert.Null(compositeEvidence.ProcessName);
        Assert.Null(compositeEvidence.ProcessStartUs);
        Assert.Null(compositeEvidence.File);
        Assert.Null(compositeEvidence.TimeUs);
        Assert.DoesNotContain(compositeEvidence.Details, detail =>
            detail.Contains("Sample", StringComparison.OrdinalIgnoreCase));
        var sample = Assert.Single(compositeEvidence.Samples);
        Assert.False(sample.Representative);
        Assert.False(sample.MetricAttributable);
        Assert.Equal("returned_rows_only", sample.SampleScope);
        Assert.Null(compositeEvidence.DetailsBoundary);
        Assert.Equal(compositeEvidence.Samples.Count, compositeEvidence.SamplesBoundary!.Returned);
    }

    [Fact]
    public void SecurityDurationEvidence_TopDoesNotAttributeAggregateToOneTarget()
    {
        var emitter = new ProcessInstanceKey(4, 0);
        var firstTarget = new ProcessInstanceKey(42, 0);
        var secondTarget = new ProcessInstanceKey(43, 0);

        PairedInterval<SecurityScanPairKey, SecurityScanStartData, SecurityScanStopData>
            Pair(string id, ProcessInstanceKey targetProcess, string process, string path,
                long startUs, long stopUs)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["__Source"] = "Microsoft Defender",
                ["__ProviderName"] = "Microsoft-Antimalware-Engine",
                ["__Id"] = id,
                ["Path"] = path,
                ["Process"] = process,
                ["PID"] = targetProcess.Pid.ToString(),
            };
            var target = new SecurityScanAnalysis.ScanTarget(
                "Microsoft Defender",
                "Microsoft-Antimalware-Engine",
                process,
                targetProcess.Pid,
                path);
            return new(
                new SecurityScanPairKey(emitter, "Microsoft-Antimalware-Engine", id),
                startUs,
                stopUs,
                new SecurityScanStartData(
                    fields, targetProcess, "payload_target_pid", target),
                new SecurityScanStopData(
                    fields, targetProcess, "payload_target_pid", target));
        }

        var pairs = new[]
        {
            Pair("scan-1", firstTarget, "first.exe", "c:\\first.dll", 10, 30),
            Pair("scan-2", secondTarget, "second.exe", "c:\\second.dll", 40, 70),
        };
        var topOne = SecurityScanAnalysis.ProjectPairs(
            pairs, new TimeWindow(0, 100), top: 1, pid: null,
            processSubstring: null, pathSubstring: null, providerSubstring: null);
        var topAll = SecurityScanAnalysis.ProjectPairs(
            pairs, new TimeWindow(0, 100), top: 2, pid: null,
            processSubstring: null, pathSubstring: null, providerSubstring: null);

        var evidenceAtTopOne = DiagnoseTools.BuildSecurityDurationEvidence(
            topOne, pid: 999);
        var evidenceAtTopAll = DiagnoseTools.BuildSecurityDurationEvidence(
            topAll, pid: 999);

        Assert.Equal(50, evidenceAtTopOne.MetricValue);
        Assert.Equal(evidenceAtTopOne.MetricValue, evidenceAtTopAll.MetricValue);
        Assert.Null(evidenceAtTopOne.Pid);
        Assert.Null(evidenceAtTopOne.ProcessStartUs);
        Assert.Null(evidenceAtTopOne.ProcessName);
        Assert.Null(evidenceAtTopOne.File);
        Assert.Null(evidenceAtTopOne.TimeUs);
        Assert.Null(evidenceAtTopAll.ProcessName);
        Assert.Null(evidenceAtTopAll.File);
        Assert.DoesNotContain(evidenceAtTopOne.Details, detail =>
            detail.Contains("sample", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ToolSectionTotalState.LowerBound,
            evidenceAtTopOne.SamplesBoundary!.TotalState);
        Assert.Equal(ToolSectionMoreState.Present,
            evidenceAtTopOne.SamplesBoundary.MoreState);
        Assert.True(evidenceAtTopOne.SamplesBoundary.HasMore);
        Assert.False(evidenceAtTopOne.SamplesBoundary.ContinuationAvailable);
        Assert.Equal(ToolSectionTotalState.Exact,
            evidenceAtTopAll.SamplesBoundary!.TotalState);
        Assert.True(evidenceAtTopAll.SamplesBoundary.HasMore);
        Assert.All(evidenceAtTopOne.Samples, sample =>
        {
            Assert.False(sample.Representative);
            Assert.False(sample.MetricAttributable);
            Assert.Equal("returned_rows_only", sample.SampleScope);
        });
        Assert.Null(evidenceAtTopOne.DetailsBoundary);
        Assert.Null(evidenceAtTopAll.DetailsBoundary);
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
    public void SecurityPresenceEvidence_UsesExactPrePaginationClassTotals()
    {
        var process = new ProcessInstanceKey(42, 0);
        var pointEvents = Enumerable.Range(1, 3)
            .Select(index => PointEvent(
                pid: 42,
                process,
                identitySource: "payload_target_pid",
                path: $"c:\\sample-{index}.dll"))
            .ToArray();
        var scope = ProcessAnalysisScope.Resolve(
            new TimeWindow(0, 100),
            pid: 42,
            processStartUs: null,
            Lifetimes((process, 100)));

        var topOne = SecurityScanAnalysis.ProjectPointEventsDetailed(
            pointEvents,
            top: 1,
            scope: scope);
        var topAll = SecurityScanAnalysis.ProjectPointEventsDetailed(
            pointEvents,
            top: 3,
            scope: scope);

        Assert.True(topOne.Response.RowsHasMore);
        Assert.Single(topOne.Response.Rows);
        Assert.Equal(3, topOne.Response.MatchedEventCount);
        var exactSummary = Assert.Single(topOne.EvidenceClassSummaries);
        Assert.Equal(3, exactSummary.EventCount);
        Assert.Equal("exact", exactSummary.TotalState);
        Assert.Equal(
            Assert.Single(topAll.EvidenceClassSummaries).EventCount,
            exactSummary.EventCount);

        var evidenceAtTopOne = Assert.Single(
            DiagnoseTools.BuildSecurityPresenceEvidence(
                topOne,
                pid: 42));
        var evidenceAtTopAll = Assert.Single(
            DiagnoseTools.BuildSecurityPresenceEvidence(
                topAll,
                pid: 42));
        Assert.Equal(3, evidenceAtTopOne.MetricValue);
        Assert.Equal(evidenceAtTopAll.MetricValue, evidenceAtTopOne.MetricValue);
        Assert.Equal("app.exe", evidenceAtTopOne.ProcessName);
        Assert.Equal(process.StartUs, evidenceAtTopOne.ProcessStartUs);
        Assert.Null(evidenceAtTopOne.File);
        Assert.Null(evidenceAtTopOne.TimeUs);
        Assert.Contains(evidenceAtTopOne.Details, detail =>
            detail.Contains("eventCountTotalState=exact", StringComparison.Ordinal));
        Assert.DoesNotContain(evidenceAtTopOne.Details, detail =>
            detail.Contains("sampleScope", StringComparison.Ordinal));
        var returnedSample = Assert.Single(evidenceAtTopOne.Samples);
        Assert.False(returnedSample.Representative);
        Assert.False(returnedSample.MetricAttributable);
        Assert.Equal("returned_rows_only", returnedSample.SampleScope);
        Assert.Equal(ToolSectionTotalState.Unknown,
            evidenceAtTopOne.SamplesBoundary!.TotalState);
        Assert.Equal(ToolSectionMoreState.Unknown,
            evidenceAtTopOne.SamplesBoundary.MoreState);
        Assert.False(evidenceAtTopOne.SamplesBoundary.HasMore);
        Assert.False(evidenceAtTopOne.SamplesBoundary.ContinuationAvailable);
        Assert.Equal(ToolSectionTotalState.Exact,
            evidenceAtTopAll.SamplesBoundary!.TotalState);
        Assert.Null(evidenceAtTopOne.DetailsBoundary);
        Assert.Null(evidenceAtTopAll.DetailsBoundary);
    }

    [Fact]
    public void SecurityPresenceEvidence_TopDoesNotTurnAggregateTotalIntoSampleAttribution()
    {
        var first = new ProcessInstanceKey(42, 0);
        var second = new ProcessInstanceKey(43, 0);
        var pointEvents = new[]
        {
            PointEvent(42, first, "payload_target_pid", "c:\\first.dll"),
            PointEvent(43, second, "payload_target_pid", "c:\\second.dll"),
        };
        var topOne = SecurityScanAnalysis.ProjectPointEventsDetailed(
            pointEvents,
            top: 1);
        var topAll = SecurityScanAnalysis.ProjectPointEventsDetailed(
            pointEvents,
            top: 2);

        var evidenceAtTopOne = Assert.Single(
            DiagnoseTools.BuildSecurityPresenceEvidence(
                topOne,
                // Hostile caller input: all_processes from the child response
                // must prevent aggregate ownership attribution.
                pid: 999));
        var evidenceAtTopAll = Assert.Single(
            DiagnoseTools.BuildSecurityPresenceEvidence(
                topAll,
                pid: 999));

        Assert.Equal(2, evidenceAtTopOne.MetricValue);
        Assert.Equal(evidenceAtTopOne.MetricValue, evidenceAtTopAll.MetricValue);
        Assert.Null(evidenceAtTopOne.Pid);
        Assert.Null(evidenceAtTopOne.ProcessStartUs);
        Assert.Null(evidenceAtTopOne.ProcessName);
        Assert.Null(evidenceAtTopOne.File);
        Assert.Null(evidenceAtTopOne.TimeUs);
        Assert.Null(evidenceAtTopAll.ProcessName);
        Assert.Null(evidenceAtTopAll.File);
        Assert.DoesNotContain(evidenceAtTopOne.Details, detail =>
            detail.Contains("sample", StringComparison.OrdinalIgnoreCase));
        Assert.All(evidenceAtTopOne.Samples, sample =>
        {
            Assert.False(sample.Representative);
            Assert.False(sample.MetricAttributable);
            Assert.Equal("returned_rows_only", sample.SampleScope);
        });
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
    public void SecurityRows_UseTheCompleteRowKeyForEqualMetrics()
    {
        var baseline = RankedTargetRow();
        var cases = new (SecurityScanTargetRow Earlier, SecurityScanTargetRow Later)[]
        {
            (baseline with { Source = "a" }, baseline with { Source = "b" }),
            (baseline with { ProviderName = "a" }, baseline with { ProviderName = "b" }),
            (baseline with { Process = "a" }, baseline with { Process = "b" }),
            (baseline with { Pid = 1 }, baseline with { Pid = 2 }),
            (baseline with { Path = "a" }, baseline with { Path = "b" }),
            (baseline with { ProcessStartUs = 1 }, baseline with { ProcessStartUs = 2 }),
            (baseline with { TargetIdentitySource = "a" }, baseline with { TargetIdentitySource = "b" }),
            (baseline with { EvidenceKind = "a" }, baseline with { EvidenceKind = "b" }),
            (baseline with { Provenance = "a" }, baseline with { Provenance = "b" }),
            (baseline with { Confidence = "a" }, baseline with { Confidence = "b" }),
        };

        foreach (var (earlier, later) in cases)
        {
            Assert.Equal(
                [earlier, later],
                SecurityScanAnalysis.OrderTargetRows([later, earlier]));
        }
    }

    [Fact]
    public void SecuritySlowScans_UseCompleteStableTieBreakersForEqualDuration()
    {
        var baseline = RankedRequestRow();
        var cases = new (SecurityScanRequestRow Earlier, SecurityScanRequestRow Later)[]
        {
            (baseline with { StartUs = 1 }, baseline with { StartUs = 2 }),
            (baseline with { Source = "a" }, baseline with { Source = "b" }),
            (baseline with { ProviderName = "a" }, baseline with { ProviderName = "b" }),
            (baseline with { Id = "a" }, baseline with { Id = "b" }),
            (baseline with { Pid = 1 }, baseline with { Pid = 2 }),
            (baseline with { ProcessStartUs = 1 }, baseline with { ProcessStartUs = 2 }),
            (baseline with { Process = "a" }, baseline with { Process = "b" }),
            (baseline with { Path = "a" }, baseline with { Path = "b" }),
            (baseline with { StopUs = 20 }, baseline with { StopUs = 30 }),
            (baseline with { EvidenceKind = "a" }, baseline with { EvidenceKind = "b" }),
            (baseline with { Provenance = "a" }, baseline with { Provenance = "b" }),
            (baseline with { Confidence = "a" }, baseline with { Confidence = "b" }),
            (baseline with { TargetIdentitySource = "a" }, baseline with { TargetIdentitySource = "b" }),
            (baseline with { Reason = "a" }, baseline with { Reason = "b" }),
        };

        foreach (var (earlier, later) in cases)
        {
            Assert.Equal(
                [earlier, later],
                SecurityScanAnalysis.OrderSlowScans([later, earlier]));
        }
    }

    [Fact]
    public void SecurityScanAnalysis_DescriptionExplainsThirdPartyDegradationWithoutClaimingExternalAccess()
    {
        var method = typeof(SecurityTools).GetMethod(nameof(SecurityTools.SecurityScanAnalysis));
        var tool = method?.GetCustomAttribute<McpServerToolAttribute>();
        var description = method?.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Assert.NotNull(tool);
        Assert.False(tool!.OpenWorld);
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

    private static SecurityScanTargetRow RankedTargetRow() => new(
        Source: "source",
        ProviderName: "provider",
        Process: "process",
        Pid: 42,
        Path: "path",
        PairedScanCount: 1,
        TotalDurationUs: 100,
        AvgDurationUs: 100,
        MaxDurationUs: 100,
        EventCount: 2,
        StartEventCount: 1,
        StopEventCount: 1,
        ResultEventCount: 0,
        EventNames: ["event:2"],
        Reasons: [],
        Statuses: [],
        TotalFullDurationUs: 100,
        TotalAccountedDurationUs: 100,
        AvgAccountedDurationUs: 100,
        MaxAccountedDurationUs: 100,
        AccountingMode: DurationAccounting.ClippedOverlapMode,
        EvidenceKind: "evidence",
        Provenance: "provenance",
        Confidence: "confidence",
        ProcessStartUs: 10,
        TargetIdentitySource: "identity");

    private static SecurityScanRequestRow RankedRequestRow() => new(
        Source: "source",
        ProviderName: "provider",
        Id: "id",
        StartUs: 10,
        StopUs: 110,
        DurationUs: 100,
        Process: "process",
        Pid: 42,
        Path: "path",
        Reason: "reason",
        FullDurationUs: 100,
        AccountedDurationUs: 100,
        AccountingMode: DurationAccounting.ClippedOverlapMode,
        EvidenceKind: "evidence",
        Provenance: "provenance",
        Confidence: "confidence",
        ProcessStartUs: 1,
        TargetIdentitySource: "identity");

    private static IReadOnlyList<ProcessLifetime> Lifetimes(
        params (ProcessInstanceKey Key, long EndUs)[] values) =>
        values.Select(value => new ProcessLifetime(
            value.Key,
            value.EndUs,
            StartObserved: true,
            EndObserved: true)).ToArray();
}
