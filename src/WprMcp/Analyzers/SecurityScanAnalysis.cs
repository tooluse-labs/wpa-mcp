using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Analyzers;

internal readonly record struct SecurityScanPairKey(
    ProcessInstanceKey EmitterProcess,
    string ProviderName,
    string Id);

internal sealed record SecurityScanStartData(IReadOnlyDictionary<string, string> Fields);

internal sealed record SecurityScanStopData(IReadOnlyDictionary<string, string> Fields);

public static class SecurityScanAnalysis
{
    private const string DefenderSource = "Microsoft Defender";
    private const string DefenderEngineProvider = "Microsoft-Antimalware-Engine";
    private const string DefenderAmFilterProvider = "Microsoft-Antimalware-AMFilter";
    private const string DefenderRtpProvider = "Microsoft-Antimalware-RTP";
    private const string StreamStartEvent = "StreamScanRequestTask/Start";
    private const string StreamStopEvent = "StreamScanRequestTask/Stop";

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
        "PID",
        "Pid",
        "ProcessID",
        "ProcessId",
        "TargetProcessId"
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
        string? providerSubstring)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var pairer = new IntervalPairAccumulator<
            SecurityScanPairKey,
            SecurityScanStartData,
            SecurityScanStopData>();
        var pointEvents = new List<SecurityScanPointEvent>();
        var unresolvedStartCount = 0;
        var unresolvedStopCount = 0;

        foreach (var ev in trace.Events)
        {
            var nowUs = TraceTime.FromMilliseconds(ev.TimeStampRelativeMSec);
            var providerName = ev.ProviderName ?? string.Empty;
            var eventName = ev.EventName ?? string.Empty;

            if (IsDefenderStreamEvent(providerName, eventName))
            {
                var fields = SelectedFields(ev, "Id", "Path", "Process", "PID", "Reason");
                if (!fields.TryGetValue("Id", out var id))
                    continue;

                fields["__Source"] = DefenderSource;
                fields["__ProviderName"] = providerName;
                fields["__Id"] = id;

                var streamTarget = TargetFromFields(
                    ev,
                    DefenderSource,
                    providerName,
                    fields);
                var endpointInWindow = window.ContainsPoint(nowUs);
                var passesFilters = PassesFilters(
                    streamTarget,
                    pid,
                    processSubstring,
                    pathSubstring,
                    providerSubstring);
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
                        Statuses: []));
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
                    pairer.AddStart(key, nowUs, new SecurityScanStartData(fields));
                else
                    pairer.AddStop(key, nowUs, new SecurityScanStopData(fields));

                continue;
            }

            if (!window.ContainsPoint(nowUs) ||
                !TryCreateSecurityEvent(ev, out var source, out var target, out var reasons, out var statuses))
            {
                continue;
            }

            if (!PassesFilters(target, pid, processSubstring, pathSubstring, providerSubstring))
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
                Statuses: statuses));
        }

        var pairResult = pairer.Complete();
        var unmatchedStarts = pairResult.UnmatchedStarts.Count(start =>
            window.ContainsPoint(start.TimeUs) &&
            PassesFilters(
                TargetFromFields(start.Data.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring));
        var unmatchedStops = pairResult.UnmatchedStops.Count(stop =>
            window.ContainsPoint(stop.TimeUs) &&
            PassesFilters(
                TargetFromFields(stop.Data.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring));
        var invalidIntervals = pairResult.InvalidIntervals.Count(interval =>
            (window.ContainsPoint(interval.StartUs) || window.ContainsPoint(interval.EndUs)) &&
            PassesFilters(
                TargetFromFields(interval.StartData.Fields),
                pid,
                processSubstring,
                pathSubstring,
                providerSubstring));

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
            invalidIntervals);

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
        string? providerSubstring) =>
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
            invalidIntervalCount: 0);

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
        int invalidIntervalCount)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        ArgumentNullException.ThrowIfNull(pointEvents);
        if (top < 1)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be positive.");

        var rowsByKey = new Dictionary<RowKey, RowStats>();
        var providersByKey = new Dictionary<ProviderKey, ProviderStats>();

        foreach (var pointEvent in pointEvents)
        {
            AddProviderEvent(
                providersByKey,
                pointEvent.Source,
                pointEvent.ProviderName,
                pointEvent.EventName);
            var row = AddRowEvent(
                rowsByKey,
                pointEvent.Target,
                pointEvent.EventName,
                pointEvent.IsStart,
                pointEvent.IsStop,
                pointEvent.IsResult);
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

            var target = TargetFromPair(pair);
            if (!PassesFilters(
                    target,
                    pid,
                    processSubstring,
                    pathSubstring,
                    providerSubstring))
            {
                continue;
            }

            pairedScanCount++;
            totalFullDurationUs += projected.Value.FullDurationUs;
            totalAccountedDurationUs += projected.Value.AccountedDurationUs;

            var row = GetRowStats(rowsByKey, target);
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
                AccountingMode: DurationAccounting.ClippedOverlapMode));
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
                    .ToArray()))
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
            pointEvents.Count,
            pairedScanCount,
            unmatchedStartCount,
            unmatchedStopCount);

        return new SecurityScanAnalysisResponse(
            Rows: rows,
            SlowScans: slowRows,
            Providers: providers,
            MatchedEventCount: pointEvents.Count,
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
            AccountingMode: DurationAccounting.ClippedOverlapMode);
    }

    private static IReadOnlyList<string> BuildWarnings(
        IReadOnlyList<SecurityScanTargetRow> rows,
        long matchedEventCount,
        long pairedScanCount,
        long unmatchedStarts,
        long unmatchedStops)
    {
        var warnings = new List<string>();
        if (rows.Count == 0)
        {
            warnings.Add("No security scan ETW events matched the requested filters. Third-party security products may not emit public scan events; cross-check CPU, wait, file IO, image-load, and minifilter driver evidence.");
        }
        else if (matchedEventCount > 0 && pairedScanCount == 0)
            warnings.Add("Matched security-related events, but none exposed paired start/stop timing. For many third-party products this tool can show activity presence/counts, not exact scan duration.");

        if (unmatchedStarts > 0 || unmatchedStops > 0)
            warnings.Add($"Unmatched scan start/stop events in window: starts={unmatchedStarts}, stops={unmatchedStops}. Time-window boundaries or dropped events can prevent duration pairing.");

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
            AccountingMode: DurationAccounting.ClippedOverlapMode);

    private static RowStats AddRowEvent(
        Dictionary<RowKey, RowStats> rowsByKey,
        ScanTarget target,
        string eventName,
        bool isStart,
        bool isStop,
        bool isResult)
    {
        var row = GetRowStats(rowsByKey, target);
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

    private static RowStats GetRowStats(Dictionary<RowKey, RowStats> rowsByKey, ScanTarget target)
    {
        var key = new RowKey(target.Source, target.ProviderName, target.Process, target.Pid, target.Path);
        if (!rowsByKey.TryGetValue(key, out var row))
            rowsByKey[key] = row = new RowStats();
        return row;
    }

    private static void AddProviderEvent(Dictionary<ProviderKey, ProviderStats> providersByKey, string source, string providerName, string eventName)
    {
        var key = new ProviderKey(source, providerName);
        if (!providersByKey.TryGetValue(key, out var stats))
            providersByKey[key] = stats = new ProviderStats();
        stats.EventCount++;
        Increment(stats.EventNames, eventName);
    }

    private static bool TryCreateSecurityEvent(
        TraceEvent ev,
        out string source,
        out ScanTarget target,
        out IReadOnlyList<string> reasons,
        out IReadOnlyList<string> statuses)
    {
        var providerName = ev.ProviderName ?? string.Empty;
        var eventName = ev.EventName ?? string.Empty;
        if (!IsDefenderResultEvent(providerName, eventName) &&
            !LooksSecurityRelevant(providerName, eventName))
        {
            source = string.Empty;
            target = default;
            reasons = [];
            statuses = [];
            return false;
        }

        var fields = SelectedAvailableFields(ev);
        source = ClassifySource(providerName, eventName, fields);
        reasons = ValuesFromFields(fields, ReasonFields);
        statuses = ValuesFromFields(fields, StatusFields);
        target = TargetFromFields(ev, source, providerName, fields);

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

    private static bool LooksSecurityRelevant(string providerName, string eventName)
    {
        return ContainsAny(providerName, "Scan") ||
            ContainsAny(eventName, "Scan", "Malware", "Virus", "Threat", "Quarantine");
    }

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

    private static ScanTarget TargetFromFields(
        TraceEvent ev,
        string source,
        string providerName,
        IReadOnlyDictionary<string, string> fields)
    {
        var process = FirstField(fields, CommonProcessFields);
        if (string.IsNullOrEmpty(process))
            process = ev.ProcessName ?? string.Empty;

        var targetPid = ParseFirstInt(fields, CommonPidFields);
        if (!targetPid.HasValue && ev.ProcessID > 0)
            targetPid = ev.ProcessID;

        return new ScanTarget(
            Source: source,
            ProviderName: providerName,
            Process: process,
            Pid: targetPid,
            Path: FirstField(fields, CommonPathFields));
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

    private readonly record struct ScanTarget(
        string Source,
        string ProviderName,
        string Process,
        int? Pid,
        string Path);

    private readonly record struct RowKey(
        string Source,
        string ProviderName,
        string Process,
        int? Pid,
        string Path);

    private readonly record struct ProviderKey(string Source, string ProviderName);

    private sealed record SecurityScanPointEvent(
        ScanTarget Target,
        string Source,
        string ProviderName,
        string EventName,
        bool IsStart,
        bool IsStop,
        bool IsResult,
        IReadOnlyList<string> Reasons,
        IReadOnlyList<string> Statuses);

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
