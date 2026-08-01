using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal readonly record struct SecurityScanPairKey(
    ProcessInstanceKey EmitterProcess,
    string ProviderName,
    string Id);

internal sealed record SecurityScanStartData(
    IReadOnlyDictionary<string, string> Fields,
    ProcessInstanceKey? TargetProcess = null,
    string TargetIdentitySource = "unresolved");

internal sealed record SecurityScanStopData(
    IReadOnlyDictionary<string, string> Fields,
    ProcessInstanceKey? TargetProcess = null,
    string TargetIdentitySource = "unresolved");

public static class SecurityScanAnalysis
{
    private const string DefenderSource = "Microsoft Defender";
    private const string DefenderEngineProvider = "Microsoft-Antimalware-Engine";
    private const string DefenderAmFilterProvider = "Microsoft-Antimalware-AMFilter";
    private const string DefenderRtpProvider = "Microsoft-Antimalware-RTP";
    private const string StreamStartEvent = "StreamScanRequestTask/Start";
    private const string StreamStopEvent = "StreamScanRequestTask/Stop";

    private static readonly EvidenceClassification DefenderPairedEvidence = new(
        EvidenceKind: "paired_interval",
        Provenance: "known_defender_schema",
        Confidence: "high");

    private static readonly EvidenceClassification DefenderResultEvidence = new(
        EvidenceKind: "result_event",
        Provenance: "known_defender_schema",
        Confidence: "high");

    private static readonly EvidenceClassification HeuristicEvidence = new(
        EvidenceKind: "scan_like_event",
        Provenance: "name_heuristic",
        Confidence: "low");

    private static readonly string[] SecurityContextTokens =
    [
        "Antimalware",
        "Defender",
        "AMFilter",
        "RTPFileScan",
        "Sense",
        "Aliedr",
        "Alibaba",
        "Aliyun",
        "Qihoo",
        "Qihu",
        "Huorong",
        "PCManager",
        "MSPCManager",
        "CrowdStrike",
        "Falcon",
        "Sentinel",
        "Symantec",
        "McAfee",
        "Trellix",
        "Sophos",
        "Kaspersky",
        "ESET",
        "TrendMicro",
        "Bitdefender",
        "Avast",
        "Avira",
        "Cylance",
        "Endpoint Protection",
        "Antivirus",
        "Security",
        "EDR",
        "XDR",
        "Hips"
    ];

    private static readonly string[] CommonPathFields =
    [
        "Path",
        "FileName",
        "FilePath",
        "TargetFileName",
        "TargetPath",
        "ImagePath",
        "SourceFileName",
        "DestinationFileName"
    ];

    private static readonly string[] CommonProcessFields =
    [
        "Process",
        "ProcessName",
        "ImageName",
        "ApplicationName",
        "TargetProcessName"
    ];

    private static readonly string[] CommonPidFields =
    [
        "TargetProcessId",
        "TargetProcessID",
        "TargetPid",
        "TargetPID",
        "PID",
        "Pid",
        "ProcessID",
        "ProcessId"
    ];

    private static readonly string[] ReasonFields =
    [
        "Reason",
        "ScanReason",
        "ThreatReason"
    ];

    private static readonly string[] StatusFields =
    [
        "ScanStatus",
        "ScanResult",
        "RtpScanResult",
        "RtpScanAction",
        "Status",
        "Result",
        "Action"
    ];

    public static SecurityScanAnalysisResponse Analyze(
        TraceLog trace,
        int top,
        int? pid,
        long? startUs,
        long? endUs,
        string? processSubstring,
        string? pathSubstring,
        string? providerSubstring,
        long? targetProcessStartUs = null)
    {
        if (targetProcessStartUs.HasValue && !pid.HasValue)
            throw new ArgumentException("targetProcessStartUs requires pid.", nameof(targetProcessStartUs));

        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(window, pid, targetProcessStartUs, identities);
        var pairer = new IntervalPairAccumulator<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData>();
        var pointEvents = new List<SecurityScanPointEvent>();
        var unresolvedStartCount = 0;
        var unresolvedStopCount = 0;
        var eventClassObserved = false;

        foreach (var ev in trace.Events)
        {
            var nowUs = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
            var providerName = ev.ProviderName ?? string.Empty;
            var eventName = ev.EventName ?? string.Empty;

            if (IsDefenderStreamEvent(providerName, eventName))
            {
                eventClassObserved = true;
                var fields = SelectedFields(ev, "Id", "Path", "Process", "PID", "Reason");
                if (!fields.TryGetValue("Id", out var id))
                    continue;

                fields["__Source"] = DefenderSource;
                fields["__ProviderName"] = providerName;
                fields["__Id"] = id;

                var streamIdentity = ResolveTargetIdentity(
                    ev.ProcessID,
                    ev.ProcessName ?? string.Empty,
                    DefenderSource,
                    providerName,
                    fields,
                    nowUs,
                    identities.Processes,
                    endpoint: IsStopEvent(eventName));
                var streamTarget = streamIdentity.Target;
                var endpointInWindow = window.ContainsPoint(nowUs);
                var passesFilters = PassesFilters(
                    streamTarget,
                    pid,
                    processSubstring,
                    pathSubstring,
                    providerSubstring) &&
                    MatchesTargetScope(scope, streamIdentity.TargetProcess);
                if (endpointInWindow && passesFilters)
                {
                    pointEvents.Add(new SecurityScanPointEvent(
                        streamTarget,
                        DefenderSource,
                        providerName,
                        eventName,
                        IsStart: IsStartEvent(eventName),
                        IsStop: IsStopEvent(eventName),
                        IsResult: false,
                        Reasons: [],
                        Statuses: [],
                        Evidence: DefenderPairedEvidence,
                        TargetProcess: streamIdentity.TargetProcess,
                        TargetIdentitySource: streamIdentity.IdentitySource));
                }

                var isStart = IsStartEvent(eventName);
                var process = isStart
                    ? identities.Processes.Resolve(
                        ev.ProcessID,
                        nowUs,
                        processStartUs: null)
                    : identities.Processes.ResolveAtEndpoint(ev.ProcessID, nowUs);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue)
                {
                    if (endpointInWindow && passesFilters)
                    {
                        if (isStart)
                            unresolvedStartCount++;
                        else
                            unresolvedStopCount++;
                    }
                    continue;
                }

                var key = new SecurityScanPairKey(
                    process.Value.Value,
                    providerName,
                    id);
                if (isStart)
                    pairer.AddStart(key, nowUs, new SecurityScanStartData(
                        fields,
                        streamIdentity.TargetProcess,
                        streamIdentity.IdentitySource));
                else
                    pairer.AddStop(key, nowUs, new SecurityScanStopData(
                        fields,
                        streamIdentity.TargetProcess,
                        streamIdentity.IdentitySource));

                continue;
            }

            if (!TryCreateSecurityEvent(
                    ev,
                    identities.Processes,
                    nowUs,
                    out var source,
                    out var targetIdentity,
                    out var reasons,
                    out var statuses,
                    out var evidence))
            {
                continue;
            }

            eventClassObserved = true;
            if (!window.ContainsPoint(nowUs))
                continue;

            var target = targetIdentity.Target;

            if (!PassesFilters(target, pid, processSubstring, pathSubstring, providerSubstring) ||
                !MatchesTargetScope(scope, targetIdentity.TargetProcess))
                continue;

            pointEvents.Add(new SecurityScanPointEvent(
                target,
                source,
                providerName,
                eventName,
                IsStart: false,
                IsStop: false,
                IsResult: true,
                Reasons: reasons,
                Statuses: statuses,
                Evidence: evidence,
                TargetProcess: targetIdentity.TargetProcess,
                TargetIdentitySource: targetIdentity.IdentitySource));
        }

        var pairResult = pairer.Complete();
        var unmatchedStarts = pairResult.UnmatchedStarts.Count(start =>
            window.ContainsPoint(start.TimeUs) &&
            PassesFilters(
                TargetFromFields(start.Data.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring) &&
            MatchesTargetScope(scope, start.Data.TargetProcess));
        var unmatchedStops = pairResult.UnmatchedStops.Count(stop =>
            window.ContainsPoint(stop.TimeUs) &&
            PassesFilters(
                TargetFromFields(stop.Data.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring) &&
            MatchesTargetScope(scope, stop.Data.TargetProcess));
        var invalidIntervals = pairResult.InvalidIntervals.Count(interval =>
            (window.ContainsPoint(interval.StartUs) || window.ContainsPoint(interval.EndUs)) &&
            PassesFilters(
                TargetFromFields(interval.StartData.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring) &&
            MatchesTargetScope(scope, interval.StartData.TargetProcess));

        var response = Project(
            pairResult.Pairs,
            window,
            top,
            pid,
            processSubstring,
            pathSubstring,
            providerSubstring,
            pointEvents,
            unmatchedStarts + unresolvedStartCount,
            unmatchedStops + unresolvedStopCount,
            invalidIntervals,
            scope,
            eventClassObserved);

        var unresolvedIdentityCount = unresolvedStartCount + unresolvedStopCount;
        if (unresolvedIdentityCount == 0)
            return response;

        return response with
        {
            Warnings = response.Warnings
                .Concat(
                [
                    $"identity_incomplete: skipped {unresolvedIdentityCount} security scan endpoint events because their emitting process instance was unresolved or ambiguous.",
                ])
                .ToArray(),
        };
    }

    internal static SecurityScanAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData>> pairs,
        TimeWindow window,
        int top,
        int? pid,
        string? processSubstring,
        string? pathSubstring,
        string? providerSubstring,
        ProcessAnalysisScope? scope = null,
        bool eventClassObserved = true) =>
        Project(
            pairs,
            window,
            top,
            pid,
            processSubstring,
            pathSubstring,
            providerSubstring,
            pointEvents: [],
            unmatchedStartCount: 0,
            unmatchedStopCount: 0,
            invalidIntervalCount: 0,
            scope,
            eventClassObserved);

    internal static SecurityScanAnalysisResponse ProjectPointEvents(
        IReadOnlyList<SecurityScanPointEvent> pointEvents,
        int top,
        ProcessAnalysisScope? scope = null,
        bool eventClassObserved = true) =>
        Project(
            pairs: [],
            window: new TimeWindow(0, 1),
            top,
            pid: null,
            processSubstring: null,
            pathSubstring: null,
            providerSubstring: null,
            pointEvents,
            unmatchedStartCount: 0,
            unmatchedStopCount: 0,
            invalidIntervalCount: 0,
            scope,
            eventClassObserved);

    private static SecurityScanAnalysisResponse Project(
        IReadOnlyList<PairedInterval<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData>> pairs,
        TimeWindow window,
        int top,
        int? pid,
        string? processSubstring,
        string? pathSubstring,
        string? providerSubstring,
        IReadOnlyList<SecurityScanPointEvent> pointEvents,
        long unmatchedStartCount,
        long unmatchedStopCount,
        int invalidIntervalCount,
        ProcessAnalysisScope? scope,
        bool eventClassObserved)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(pointEvents);
        if (top < 1)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be positive.");

        var rowsByKey = new Dictionary<RowKey, RowStats>();
        var providersByKey = new Dictionary<ProviderKey, ProviderStats>();
        var identitySources = new HashSet<string>(StringComparer.Ordinal);
        long payloadTargetIdentityCount = 0;
        long emitterFallbackIdentityCount = 0;
        long unresolvedTargetIdentityCount = 0;
        long targetIdentityMismatchCount = 0;
        long selectedPointEventCount = 0;

        foreach (var pointEvent in pointEvents)
        {
            if (!PassesFilters(
                    pointEvent.Target,
                    pid,
                    processSubstring,
                    pathSubstring,
                    providerSubstring) ||
                !MatchesTargetScope(scope, pointEvent.TargetProcess))
            {
                continue;
            }

            selectedPointEventCount++;
            ObserveTargetIdentity(
                pointEvent.TargetIdentitySource,
                pointEvent.TargetProcess,
                identitySources,
                ref payloadTargetIdentityCount,
                ref emitterFallbackIdentityCount,
                ref unresolvedTargetIdentityCount);
            AddProviderEvent(
                providersByKey,
                pointEvent.Source,
                pointEvent.ProviderName,
                pointEvent.EventName,
                pointEvent.Evidence);
            var row = AddRowEvent(
                rowsByKey,
                pointEvent.Target,
                pointEvent.EventName,
                pointEvent.IsStart,
                pointEvent.IsStop,
                pointEvent.IsResult,
                pointEvent.Evidence,
                pointEvent.TargetProcess,
                pointEvent.TargetIdentitySource);
            foreach (var reason in pointEvent.Reasons)
                row.Reasons.Add(reason);
            foreach (var status in pointEvent.Statuses)
                row.Statuses.Add(status);
        }

        var slowScans = new List<SecurityScanRequestRow>();
        long pairedScanCount = 0;
        long totalFullDurationUs = 0;
        long totalAccountedDurationUs = 0;

        foreach (var pair in pairs)
        {
            var projected = DurationAccounting.Project(pair, window);
            if (!projected.HasValue)
                continue;

            if (pair.StartData.TargetProcess.HasValue &&
                pair.StopData.TargetProcess.HasValue &&
                pair.StartData.TargetProcess.Value != pair.StopData.TargetProcess.Value)
            {
                targetIdentityMismatchCount++;
                continue;
            }

            var target = TargetFromPair(pair);
            if (!PassesFilters(
                    target,
                    pid,
                    processSubstring,
                    pathSubstring,
                    providerSubstring) ||
                !MatchesTargetScope(scope, pair.StartData.TargetProcess) ||
                (scope?.Pid.HasValue == true &&
                 !MatchesTargetScope(scope, pair.StopData.TargetProcess)))
            {
                continue;
            }

            ObserveTargetIdentity(
                pair.StartData.TargetIdentitySource,
                pair.StartData.TargetProcess,
                identitySources,
                ref payloadTargetIdentityCount,
                ref emitterFallbackIdentityCount,
                ref unresolvedTargetIdentityCount);

            pairedScanCount++;
            totalFullDurationUs += projected.Value.FullDurationUs;
            totalAccountedDurationUs += projected.Value.AccountedDurationUs;

            var row = GetRowStats(
                rowsByKey,
                target,
                DefenderPairedEvidence,
                pair.StartData.TargetProcess,
                pair.StartData.TargetIdentitySource);
            row.PairedScanCount++;
            row.TotalFullDurationUs += projected.Value.FullDurationUs;
            row.TotalAccountedDurationUs += projected.Value.AccountedDurationUs;
            row.MaxAccountedDurationUs = Math.Max(
                row.MaxAccountedDurationUs ?? 0,
                projected.Value.AccountedDurationUs);
            AddIfPresent(row.Reasons, Field(pair, "Reason"));

            slowScans.Add(new SecurityScanRequestRow(
                Source: target.Source,
                ProviderName: target.ProviderName,
                Id: Field(pair, "__Id"),
                StartUs: pair.StartUs,
                StopUs: pair.EndUs,
                DurationUs: projected.Value.AccountedDurationUs,
                Process: target.Process,
                Pid: target.Pid,
                Path: target.Path,
                Reason: NullIfEmpty(Field(pair, "Reason")),
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                EvidenceKind: DefenderPairedEvidence.EvidenceKind,
                Provenance: DefenderPairedEvidence.Provenance,
                Confidence: DefenderPairedEvidence.Confidence,
                ProcessStartUs: pair.StartData.TargetProcess?.StartUs,
                TargetIdentitySource: pair.StartData.TargetIdentitySource));
        }

        var completeRows = rowsByKey
            .Select(kv => ToRow(kv.Key, kv.Value))
            .OrderByDescending(row => row.TotalAccountedDurationUs)
            .ThenByDescending(row => row.EventCount)
            .ThenByDescending(row => row.PairedScanCount)
            .ThenBy(row => row.Source, StringComparer.Ordinal)
            .ThenBy(row => row.ProviderName, StringComparer.Ordinal)
            .ToArray();
        var rows = completeRows.Take(top).ToArray();

        var completeProviders = providersByKey
            .Select(kv => new SecurityScanProviderRow(
                Source: kv.Key.Source,
                ProviderName: kv.Key.ProviderName,
                EventCount: kv.Value.EventCount,
                EventNames: kv.Value.EventNames
                    .OrderByDescending(item => item.Value)
                    .ThenBy(item => item.Key, StringComparer.Ordinal)
                    .Take(10)
                    .Select(item => $"{item.Key}:{item.Value}")
                    .ToArray(),
                EvidenceKind: kv.Key.EvidenceKind,
                Provenance: kv.Key.Provenance,
                Confidence: kv.Key.Confidence))
            .OrderByDescending(row => row.EventCount)
            .ThenBy(row => row.Source, StringComparer.Ordinal)
            .ToArray();
        var providers = completeProviders.Take(top).ToArray();

        var completeSlowScans = slowScans
            .OrderByDescending(row => row.AccountedDurationUs)
            .ThenBy(row => row.StartUs)
            .ToArray();
        var slowRows = completeSlowScans.Take(top).ToArray();

        var warnings = BuildWarnings(
            completeRows,
            selectedPointEventCount,
            pairedScanCount,
            unmatchedStartCount,
            unmatchedStopCount,
            scope,
            pid,
            unresolvedTargetIdentityCount,
            emitterFallbackIdentityCount,
            targetIdentityMismatchCount,
            eventClassObserved);

        var noDataReason = ClassifyNoData(
            scope,
            eventClassObserved,
            selectedPointEventCount,
            pairedScanCount);
        var targetIdentitySource = identitySources.Count switch
        {
            0 => "not_observed",
            1 => identitySources.Single(),
            _ => "mixed",
        };

        return new SecurityScanAnalysisResponse(
            Rows: rows,
            SlowScans: slowRows,
            Providers: providers,
            MatchedEventCount: selectedPointEventCount,
            PairedScanCount: pairedScanCount,
            TotalDurationUs: totalAccountedDurationUs,
            UnmatchedStartCount: unmatchedStartCount,
            UnmatchedStopCount: unmatchedStopCount,
            Warnings: warnings,
            TotalFullDurationUs: totalFullDurationUs,
            TotalAccountedDurationUs: totalAccountedDurationUs,
            RowsHasMore: completeRows.Length > rows.Length,
            SlowScansHasMore: completeSlowScans.Length > slowRows.Length,
            ProvidersHasMore: completeProviders.Length > providers.Length,
            InvalidIntervalCount: invalidIntervalCount,
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            SelectedProcess: scope?.SelectedProcess,
            ScopeMode: scope?.ScopeMode ?? "all_processes",
            PidReuseObserved: scope?.PidReuseObserved ?? false,
            IncludedProcesses: scope?.IncludedProcesses ?? Array.Empty<ProcessInstanceKey>(),
            ScopeStatus: scope?.ScopeStatus ?? ProcessAnalysisScope.ResolvedStatus,
            CapabilityStatus: scope is { IsResolved: false }
                ? "unknown"
                : selectedPointEventCount > 0 || pairedScanCount > 0
                    ? "observed"
                    : eventClassObserved ? "unknown" : "not_observed",
            NoDataReason: noDataReason,
            TargetIdentitySource: targetIdentitySource,
            PayloadTargetIdentityCount: payloadTargetIdentityCount,
            EmitterFallbackIdentityCount: emitterFallbackIdentityCount,
            UnresolvedTargetIdentityCount: unresolvedTargetIdentityCount,
            TargetIdentityMismatchCount: targetIdentityMismatchCount);
    }

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<SecurityScanTargetRow> rows,
        long matchedEventCount,
        long pairedScanCount,
        long unmatchedStarts,
        long unmatchedStops,
        ProcessAnalysisScope? scope,
        int? pid,
        long unresolvedTargetIdentityCount,
        long emitterFallbackIdentityCount,
        long targetIdentityMismatchCount,
        bool eventClassObserved)
    {
        var warnings = new List<string>();
        if (scope?.ScopeMode == "pid_aggregate")
        {
            warnings.Add(
                $"pid_aggregate: target PID {pid} matched {scope.IncludedProcesses.Count} process lifetimes in the requested window; rows remain instance-separated and totals combine those lifetimes. Specify targetProcessStartUs for one lifetime.");
        }

        if (scope is { IsResolved: false })
        {
            warnings.Add(
                $"scope_not_found: no target process lifetime for PID {pid}" +
                (scope.ProcessStartUs.HasValue
                    ? $" with targetProcessStartUs={scope.ProcessStartUs.Value}"
                    : string.Empty) +
                " intersects the requested half-open window.");
        }
        else if (!eventClassObserved)
        {
            warnings.Add(
                "event_class_not_observed: No security scan ETW events were recognized in the materialized trace. This does not prove that security scanning was disabled; the provider may be absent, private, unsupported, or not captured.");
        }
        else if (rows.Count == 0)
        {
            warnings.Add("no_events_in_scope: No security scan ETW events matched the requested target process instances, half-open window, and text filters. Third-party security products may not emit public scan events; cross-check CPU, wait, file IO, image-load, and minifilter driver evidence.");
        }
        else if (matchedEventCount > 0 && pairedScanCount == 0)
            warnings.Add("Matched security-related events, but none exposed paired start/stop timing. For many third-party products this tool can show activity presence/counts, not exact scan duration.");

        if (rows.Any(row => string.Equals(row.Confidence, "low", StringComparison.Ordinal)))
            warnings.Add("name_heuristic: low-confidence scan-like event names are presence evidence only; they do not prove exact scan duration, performance impact, or root cause.");

        if (unmatchedStarts > 0 || unmatchedStops > 0)
            warnings.Add($"Unmatched scan start/stop events in window: starts={unmatchedStarts}, stops={unmatchedStops}. Time-window boundaries or dropped events can prevent duration pairing.");

        if (unresolvedTargetIdentityCount > 0)
        {
            warnings.Add(
                $"target_identity_incomplete: {unresolvedTargetIdentityCount} matched security event observations exposed a target PID (or allowed emitter fallback) that could not be resolved to one target process lifetime. They are retained only for all-process queries and are never silently assigned to a selected instance.");
        }

        if (emitterFallbackIdentityCount > 0)
        {
            warnings.Add(
                $"emitter_fallback: {emitterFallbackIdentityCount} matched security event observations had no payload target PID, so the emitting process PID was used under the legacy fallback rule. These are explicitly provenance-marked and must not be treated as provider-confirmed scan targets.");
        }

        if (targetIdentityMismatchCount > 0)
        {
            warnings.Add(
                $"target_identity_mismatch: skipped {targetIdentityMismatchCount} paired scan intervals whose start and stop resolved to different target process lifetimes; endpoint presence evidence remains available without attributing a cross-instance duration.");
        }

        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);
        return warnings;
    }

    private static SecurityScanTargetRow ToRow(RowKey key, RowStats stats) =>
        new(
            Source: key.Source,
            ProviderName: key.ProviderName,
            Process: key.Process,
            Pid: key.Pid,
            Path: key.Path,
            PairedScanCount: stats.PairedScanCount,
            TotalDurationUs: stats.TotalAccountedDurationUs,
            AvgDurationUs: stats.PairedScanCount > 0
                ? stats.TotalAccountedDurationUs / (double)stats.PairedScanCount
                : null,
            MaxDurationUs: stats.MaxAccountedDurationUs,
            EventCount: stats.EventCount,
            StartEventCount: stats.StartEventCount,
            StopEventCount: stats.StopEventCount,
            ResultEventCount: stats.ResultEventCount,
            EventNames: stats.EventNames
                .OrderByDescending(item => item.Value)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Take(10)
                .Select(item => $"{item.Key}:{item.Value}")
                .ToList(),
            Reasons: stats.Reasons.OrderBy(value => value, StringComparer.Ordinal).Take(10).ToList(),
            Statuses: stats.Statuses.OrderBy(value => value, StringComparer.Ordinal).Take(10).ToList(),
            TotalFullDurationUs: stats.TotalFullDurationUs,
            TotalAccountedDurationUs: stats.TotalAccountedDurationUs,
            AvgAccountedDurationUs: stats.PairedScanCount > 0
                ? stats.TotalAccountedDurationUs / (double)stats.PairedScanCount
                : null,
            MaxAccountedDurationUs: stats.MaxAccountedDurationUs,
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            EvidenceKind: key.EvidenceKind,
            Provenance: key.Provenance,
            Confidence: key.Confidence,
            ProcessStartUs: key.ProcessStartUs,
            TargetIdentitySource: key.TargetIdentitySource);

    private static RowStats AddRowEvent(
        Dictionary<RowKey, RowStats> rowsByKey,
        ScanTarget target,
        string eventName,
        bool isStart,
        bool isStop,
        bool isResult,
        EvidenceClassification evidence,
        ProcessInstanceKey? targetProcess,
        string targetIdentitySource)
    {
        var row = GetRowStats(
            rowsByKey,
            target,
            evidence,
            targetProcess,
            targetIdentitySource);
        row.EventCount++;
        Increment(row.EventNames, eventName);
        if (isStart)
            row.StartEventCount++;
        if (isStop)
            row.StopEventCount++;
        if (isResult)
            row.ResultEventCount++;
        return row;
    }

    private static RowStats GetRowStats(
        Dictionary<RowKey, RowStats> rowsByKey,
        ScanTarget target,
        EvidenceClassification evidence,
        ProcessInstanceKey? targetProcess,
        string targetIdentitySource)
    {
        var key = new RowKey(
            target.Source,
            target.ProviderName,
            target.Process,
            target.Pid,
            target.Path,
            targetProcess?.StartUs,
            targetIdentitySource,
            evidence.EvidenceKind,
            evidence.Provenance,
            evidence.Confidence);
        if (!rowsByKey.TryGetValue(key, out var row))
            rowsByKey[key] = row = new RowStats();
        return row;
    }

    private static void AddProviderEvent(
        Dictionary<ProviderKey, ProviderStats> providersByKey,
        string source,
        string providerName,
        string eventName,
        EvidenceClassification evidence)
    {
        var key = new ProviderKey(
            source,
            providerName,
            evidence.EvidenceKind,
            evidence.Provenance,
            evidence.Confidence);
        if (!providersByKey.TryGetValue(key, out var stats))
            providersByKey[key] = stats = new ProviderStats();
        stats.EventCount++;
        Increment(stats.EventNames, eventName);
    }

    private static bool TryCreateSecurityEvent(
        TraceEvent ev,
        ProcessInstanceResolver resolver,
        long timestampUs,
        out string source,
        out ResolvedScanTarget targetIdentity,
        out IReadOnlyList<string> reasons,
        out IReadOnlyList<string> statuses,
        out EvidenceClassification evidence)
    {
        var providerName = ev.ProviderName ?? string.Empty;
        var eventName = ev.EventName ?? string.Empty;
        if (!IsDefenderResultEvent(providerName, eventName) &&
            !HasSecurityEventNameTerm(eventName))
        {
            source = string.Empty;
            targetIdentity = default;
            reasons = [];
            statuses = [];
            evidence = default;
            return false;
        }

        var fields = SelectedAvailableFields(ev);
        var classification = ClassifyEvidence(providerName, eventName, fields);
        if (!classification.HasValue)
        {
            source = string.Empty;
            targetIdentity = default;
            reasons = [];
            statuses = [];
            evidence = default;
            return false;
        }

        evidence = classification.Value;
        source = ClassifySource(providerName, eventName, fields);
        reasons = ValuesFromFields(fields, ReasonFields);
        statuses = ValuesFromFields(fields, StatusFields);
        targetIdentity = ResolveTargetIdentity(
            ev.ProcessID,
            ev.ProcessName ?? string.Empty,
            source,
            providerName,
            fields,
            timestampUs,
            resolver,
            endpoint: false);

        return true;
    }

    private static bool IsDefenderStreamEvent(string providerName, string eventName) =>
        string.Equals(providerName, DefenderEngineProvider, StringComparison.Ordinal) &&
        (IsStartEvent(eventName) || IsStopEvent(eventName));

    private static bool IsStartEvent(string eventName) =>
        string.Equals(eventName, StreamStartEvent, StringComparison.Ordinal);

    private static bool IsStopEvent(string eventName) =>
        string.Equals(eventName, StreamStopEvent, StringComparison.Ordinal);

    private static bool IsDefenderResultEvent(string providerName, string eventName) =>
        (string.Equals(providerName, DefenderAmFilterProvider, StringComparison.Ordinal) &&
         (string.Equals(eventName, "AMFilter_FileScan", StringComparison.Ordinal) ||
          string.Equals(eventName, "AMFilter_FileScanResult", StringComparison.Ordinal))) ||
        (string.Equals(providerName, DefenderRtpProvider, StringComparison.Ordinal) &&
         string.Equals(eventName, "RTPFileScanResult", StringComparison.Ordinal));

    internal static EvidenceClassification? ClassifyEvidence(
        string providerName,
        string eventName,
        IReadOnlyDictionary<string, string> fields)
    {
        ArgumentNullException.ThrowIfNull(providerName);
        ArgumentNullException.ThrowIfNull(eventName);
        ArgumentNullException.ThrowIfNull(fields);

        if (IsDefenderStreamEvent(providerName, eventName))
            return DefenderPairedEvidence;
        if (IsDefenderResultEvent(providerName, eventName))
            return DefenderResultEvidence;

        var hasExplicitSecurityTerm = ContainsAny(
            eventName,
            "Malware",
            "Virus",
            "Threat",
            "Quarantine");
        var hasScanTerm = ContainsAny(eventName, "Scan");
        if (!hasExplicitSecurityTerm && !hasScanTerm)
            return null;

        var providerHasSecurityContext = SecurityContextTokens.Any(token =>
            providerName.Contains(token, StringComparison.OrdinalIgnoreCase));
        var fieldsHaveSecurityProductContext = fields.Values.Any(value =>
            SecurityContextTokens.Any(token =>
                !string.Equals(token, "Security", StringComparison.Ordinal) &&
                value.Contains(token, StringComparison.OrdinalIgnoreCase)));
        if (!hasExplicitSecurityTerm &&
            !providerHasSecurityContext &&
            !fieldsHaveSecurityProductContext)
        {
            return null;
        }

        return HeuristicEvidence;
    }

    private static bool HasSecurityEventNameTerm(string eventName) =>
        ContainsAny(eventName, "Scan", "Malware", "Virus", "Threat", "Quarantine");

    private static string ClassifySource(string providerName, string eventName, IReadOnlyDictionary<string, string> fields)
    {
        var haystack = providerName + " " + eventName + " " + string.Join(' ', fields.Values);
        if (ContainsAny(haystack, "Antimalware", "Defender", "AMFilter", "RTPFileScan"))
            return DefenderSource;
        if (ContainsAny(haystack, "Sense"))
            return "Microsoft Defender for Endpoint";
        if (ContainsAny(haystack, "Aliedr", "Alibaba", "Aliyun"))
            return "Alibaba Aliedr";
        if (ContainsAny(haystack, "360", "Qihoo", "Qihu"))
            return "360/Qihoo";
        if (ContainsAny(haystack, "Huorong"))
            return "Huorong";
        if (ContainsAny(haystack, "PCManager", "MSPCManager", "Huawei"))
            return "PC Manager";
        if (ContainsAny(haystack, "CrowdStrike", "Falcon"))
            return "CrowdStrike Falcon";
        if (ContainsAny(haystack, "Sentinel"))
            return "Sentinel";
        if (ContainsAny(haystack, "Symantec"))
            return "Symantec";
        if (ContainsAny(haystack, "McAfee", "Trellix"))
            return "McAfee/Trellix";
        if (ContainsAny(haystack, "Sophos"))
            return "Sophos";
        if (ContainsAny(haystack, "Kaspersky"))
            return "Kaspersky";
        if (ContainsAny(haystack, "ESET"))
            return "ESET";
        if (ContainsAny(haystack, "TrendMicro"))
            return "Trend Micro";
        if (ContainsAny(haystack, "Bitdefender"))
            return "Bitdefender";
        if (ContainsAny(haystack, "Avast"))
            return "Avast";
        if (ContainsAny(haystack, "Avira"))
            return "Avira";
        if (ContainsAny(haystack, "Cylance"))
            return "Cylance";
        if (ContainsAny(haystack, "EDR", "XDR", "Hips"))
            return "Security/EDR";

        return "Security scan event";
    }

    private static ScanTarget TargetFromPair(
        PairedInterval<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData> pair) =>
        TargetFromFields(pair.StartData.Fields);

    private static ScanTarget TargetFromFields(IReadOnlyDictionary<string, string> fields)
    {
        var source = Field(fields, "__Source");
        var providerName = Field(fields, "__ProviderName");
        return new ScanTarget(
            Source: string.IsNullOrEmpty(source) ? "Security scan event" : source,
            ProviderName: providerName,
            Process: FirstField(fields, CommonProcessFields),
            Pid: ParseFirstInt(fields, CommonPidFields),
            Path: FirstField(fields, CommonPathFields));
    }

    internal static ResolvedScanTarget ResolveTargetIdentity(
        int emitterPid,
        string emitterProcessName,
        IReadOnlyDictionary<string, string> fields,
        long timestampUs,
        ProcessInstanceResolver resolver,
        bool endpoint) =>
        ResolveTargetIdentity(
            emitterPid,
            emitterProcessName,
            source: Field(fields, "__Source"),
            providerName: Field(fields, "__ProviderName"),
            fields,
            timestampUs,
            resolver,
            endpoint);

    internal static ResolvedScanTarget ResolveTargetIdentity(
        int emitterPid,
        string emitterProcessName,
        string source,
        string providerName,
        IReadOnlyDictionary<string, string> fields,
        long timestampUs,
        ProcessInstanceResolver resolver,
        bool endpoint)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentNullException.ThrowIfNull(resolver);

        var process = FirstField(fields, CommonProcessFields);
        var targetPid = ParseFirstInt(fields, CommonPidFields);
        var identitySource = targetPid.HasValue
            ? "payload_target_pid"
            : emitterPid > 0
                ? "emitter_fallback"
                : "unresolved";
        if (!targetPid.HasValue && emitterPid > 0)
        {
            targetPid = emitterPid;
            if (string.IsNullOrEmpty(process))
                process = emitterProcessName;
        }

        ProcessInstanceKey? targetProcess = null;
        if (targetPid.HasValue)
        {
            var resolution = endpoint
                ? resolver.ResolveAtEndpoint(targetPid.Value, timestampUs)
                : resolver.Resolve(targetPid.Value, timestampUs, processStartUs: null);
            if (resolution.Status == InstanceResolutionStatus.Resolved &&
                resolution.Value.HasValue)
            {
                targetProcess = resolution.Value.Value;
            }
        }

        return new ResolvedScanTarget(
            new ScanTarget(
                Source: string.IsNullOrEmpty(source) ? "Security scan event" : source,
                ProviderName: providerName,
                Process: process,
                Pid: targetPid,
                Path: FirstField(fields, CommonPathFields)),
            targetProcess,
            identitySource);
    }

    private static bool PassesFilters(
        ScanTarget target,
        int? pid,
        string? processSubstring,
        string? pathSubstring,
        string? providerSubstring) =>
        (!pid.HasValue || target.Pid == pid.Value) &&
        (string.IsNullOrEmpty(processSubstring) || target.Process.Contains(processSubstring, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(pathSubstring) || target.Path.Contains(pathSubstring, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrEmpty(providerSubstring) || target.ProviderName.Contains(providerSubstring, StringComparison.OrdinalIgnoreCase) || target.Source.Contains(providerSubstring, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesTargetScope(
        ProcessAnalysisScope? scope,
        ProcessInstanceKey? targetProcess)
    {
        if (scope is null)
            return true;
        if (!scope.IsResolved)
            return false;
        if (!scope.Pid.HasValue)
            return true;
        return targetProcess.HasValue &&
               scope.IncludedProcesses.Contains(targetProcess.Value);
    }

    private static string? ClassifyNoData(
        ProcessAnalysisScope? scope,
        bool eventClassObserved,
        long matchedEventCount,
        long pairedScanCount)
    {
        if (scope is { IsResolved: false })
            return "scope_not_found";
        if (!eventClassObserved)
            return "event_class_not_observed";
        return matchedEventCount == 0 && pairedScanCount == 0
            ? "no_events_in_scope"
            : null;
    }

    private static void ObserveTargetIdentity(
        string identitySource,
        ProcessInstanceKey? targetProcess,
        HashSet<string> identitySources,
        ref long payloadTargetIdentityCount,
        ref long emitterFallbackIdentityCount,
        ref long unresolvedTargetIdentityCount)
    {
        identitySources.Add(identitySource);
        if (string.Equals(identitySource, "payload_target_pid", StringComparison.Ordinal))
            payloadTargetIdentityCount++;
        else if (string.Equals(identitySource, "emitter_fallback", StringComparison.Ordinal))
            emitterFallbackIdentityCount++;
        if (!targetProcess.HasValue)
            unresolvedTargetIdentityCount++;
    }

    private static Dictionary<string, string> SelectedFields(TraceEvent ev, params string[] names)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = PayloadString(ev, name);
            if (!string.IsNullOrEmpty(value))
                result[name] = value;
        }

        return result;
    }

    private static Dictionary<string, string> SelectedAvailableFields(TraceEvent ev)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < ev.PayloadNames.Length; i++)
        {
            var name = ev.PayloadNames[i];
            var value = ev.PayloadValue(i)?.ToString();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                result[name] = value;
        }

        return result;
    }

    private static string Field(
        PairedInterval<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData> pair,
        string name)
    {
        if (pair.StartData.Fields.TryGetValue(name, out var startValue))
            return startValue;
        if (pair.StopData.Fields.TryGetValue(name, out var stopValue))
            return stopValue;
        return string.Empty;
    }

    private static string Field(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var value) ? value : string.Empty;

    private static string FirstField(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                return value;
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ValuesFromFields(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> names)
    {
        var result = new List<string>();
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                result.Add(value);
        }

        return result;
    }

    private static string PayloadString(TraceEvent ev, string name)
    {
        for (var i = 0; i < ev.PayloadNames.Length; i++)
        {
            if (!string.Equals(ev.PayloadNames[i], name, StringComparison.OrdinalIgnoreCase))
                continue;
            return ev.PayloadValue(i)?.ToString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static int? ParseFirstInt(IReadOnlyDictionary<string, string> fields, IReadOnlyList<string> names)
    {
        foreach (var name in names)
        {
            if (fields.TryGetValue(name, out var value) && int.TryParse(value, out var parsed))
                return parsed;
        }

        return null;
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static void AddIfPresent(HashSet<string> values, string value)
    {
        if (!string.IsNullOrEmpty(value))
            values.Add(value);
    }

    private static void Increment(Dictionary<string, long> counts, string key)
    {
        if (string.IsNullOrEmpty(key))
            return;

        counts.TryGetValue(key, out var count);
        counts[key] = count + 1;
    }

    private static bool ContainsAny(string value, params string[] tokens) =>
        tokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));

    internal readonly record struct ScanTarget(
        string Source,
        string ProviderName,
        string Process,
        int? Pid,
        string Path);

    internal readonly record struct ResolvedScanTarget(
        ScanTarget Target,
        ProcessInstanceKey? TargetProcess,
        string IdentitySource);

    private readonly record struct RowKey(
        string Source,
        string ProviderName,
        string Process,
        int? Pid,
        string Path,
        long? ProcessStartUs,
        string TargetIdentitySource,
        string EvidenceKind,
        string Provenance,
        string Confidence);

    private readonly record struct ProviderKey(
        string Source,
        string ProviderName,
        string EvidenceKind,
        string Provenance,
        string Confidence);

    internal sealed record SecurityScanPointEvent(
        ScanTarget Target,
        string Source,
        string ProviderName,
        string EventName,
        bool IsStart,
        bool IsStop,
        bool IsResult,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Statuses,
        EvidenceClassification Evidence,
        ProcessInstanceKey? TargetProcess = null,
        string TargetIdentitySource = "unresolved");

    internal readonly record struct EvidenceClassification(
        string EvidenceKind,
        string Provenance,
        string Confidence);

    private sealed class RowStats
    {
        public long PairedScanCount { get; set; }
        public long TotalFullDurationUs { get; set; }
        public long TotalAccountedDurationUs { get; set; }
        public long? MaxAccountedDurationUs { get; set; }
        public long EventCount { get; set; }
        public long StartEventCount { get; set; }
        public long StopEventCount { get; set; }
        public long ResultEventCount { get; set; }
        public Dictionary<string, long> EventNames { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Reasons { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Statuses { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ProviderStats
    {
        public long EventCount { get; set; }
        public Dictionary<string, long> EventNames { get; } = new(StringComparer.Ordinal);
    }
}
