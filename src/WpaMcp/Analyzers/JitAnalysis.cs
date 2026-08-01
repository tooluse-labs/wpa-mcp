using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal readonly record struct JitPairKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId,
    long MethodId);

internal readonly record struct JitStartData(string Method, int MethodIlSize);

internal readonly record struct JitStopData;

// Pair every JIT start/load event over the complete trace, then charge only the overlap with
// the requested window. This preserves methods that begin before or finish after the window.
public static class JitAnalysis
{
    public static JitAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        int top,
        long? startUs,
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var pairer = new IntervalPairAccumulator<JitPairKey, JitStartData, JitStopData>();
        var incompleteIdentityCount = 0;
        long sourceEventCount = 0;
        long matchedSourceEventCount = 0;

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.MethodJittingStarted += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.Resolve(
                    data.ProcessID,
                    timestampUs,
                    processStartUs: null);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;
                if (!clrInstanceId.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                var fullName =
                    $"{data.MethodNamespace}.{data.MethodName}{data.MethodSignature}";
                pairer.AddStart(
                    new JitPairKey(process.Value.Value, clrInstanceId.Value, data.MethodID),
                    timestampUs,
                    new JitStartData(fullName, (int)data.MethodILSize));
            };

            clr.MethodLoadVerbose += data =>
            {
                sourceEventCount++;
                var timestampUs = TraceTime.FromMilliseconds(data.TimeStampRelativeMSec);
                var process = identities.Processes.ResolveAtEndpoint(
                    data.ProcessID,
                    timestampUs);
                var clrInstanceId = GcAnalysis.TryReadClrInstanceId(data);
                if (process.Status != InstanceResolutionStatus.Resolved ||
                    !process.Value.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                if (scope.IncludedProcesses.Contains(process.Value.Value) &&
                    window.ContainsPoint(timestampUs))
                    matchedSourceEventCount++;
                if (!clrInstanceId.HasValue)
                {
                    incompleteIdentityCount++;
                    return;
                }

                pairer.AddStop(
                    new JitPairKey(process.Value.Value, clrInstanceId.Value, data.MethodID),
                    timestampUs,
                    new JitStopData());
            };
        });

        var result = pairer.Complete();
        var response = ProjectPairs(
            result.Pairs,
            window,
            scope,
            top,
            sourceEventCount,
            matchedSourceEventCount,
            unmatchedIntervalCount:
                result.UnmatchedStarts.Count +
                result.UnmatchedStops.Count +
                incompleteIdentityCount,
            invalidIntervalCount: result.InvalidIntervals.Count);

        if (incompleteIdentityCount == 0)
            return response;

        return response with
        {
            Warnings = response.Warnings
                .Concat(
                [
                    $"identity_incomplete: skipped {incompleteIdentityCount} JIT endpoint events because their process or CLR instance identity was unresolved or ambiguous.",
                ])
                .ToArray(),
        };
    }

    internal static JitAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        int? pid,
        int top,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0)
    {
        var inferredSourceEventCount = checked((long)pairs.Count * 2);
        bool MatchesProcess(ProcessInstanceKey process) =>
            !pid.HasValue || process.Pid == pid.Value;
        return ProjectPairsCore(
            pairs,
            window,
            pid,
            MatchesProcess,
            top,
            inferredSourceEventCount,
            unmatchedIntervalCount,
            invalidIntervalCount,
            selectedProcess: null,
            scopeMode: pid.HasValue ? "pid_aggregate" : "all_processes",
            pidReuseObserved: false,
            includedProcesses: Array.Empty<ProcessInstanceKey>(),
            scopeStatus: ProcessAnalysisScope.ResolvedStatus,
            matchedSourceEventCount: CountMatchedSourceEndpoints(
                pairs, window, MatchesProcess));
    }

    internal static JitAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        ProcessAnalysisScope scope,
        int top,
        long sourceEventCount,
        long? matchedSourceEventCount = null,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0) =>
        ProjectPairsCore(
            pairs,
            window,
            scope.Pid,
            process => scope.IncludedProcesses.Contains(process),
            top,
            sourceEventCount,
            unmatchedIntervalCount,
            invalidIntervalCount,
            scope.SelectedProcess,
            scope.ScopeMode,
            scope.PidReuseObserved,
            scope.IncludedProcesses,
            scope.ScopeStatus,
            matchedSourceEventCount ?? CountMatchedSourceEndpoints(
                pairs,
                window,
                process => scope.IncludedProcesses.Contains(process)));

    private static JitAnalysisResponse ProjectPairsCore(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        int? pid,
        Func<ProcessInstanceKey, bool> matchesProcess,
        int top,
        long sourceEventCount,
        int unmatchedIntervalCount,
        int invalidIntervalCount,
        ProcessInstanceKey? selectedProcess,
        string scopeMode,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey> includedProcesses,
        string scopeStatus,
        long matchedSourceEventCount)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (top < 1)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be positive.");

        var completed = new List<JitMethodRow>();
        long totalFullJitUs = 0;
        long totalAccountedJitUs = 0;

        foreach (var pair in pairs)
        {
            if (!matchesProcess(pair.Key.Process))
                continue;

            var projected = DurationAccounting.Project(pair, window);
            if (!projected.HasValue)
                continue;

            totalFullJitUs += projected.Value.FullDurationUs;
            totalAccountedJitUs += projected.Value.AccountedDurationUs;
            completed.Add(new JitMethodRow(
                Method: pair.StartData.Method,
                JitDurationUs: projected.Value.AccountedDurationUs,
                MethodIlSize: pair.StartData.MethodIlSize,
                Pid: pair.Key.Process.Pid,
                StartUs: pair.StartUs,
                EndUs: pair.EndUs,
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: pair.Key.Process.StartUs));
        }

        var rows = completed
            .OrderByDescending(row => row.AccountedDurationUs)
            .ThenBy(row => row.StartUs)
            .Take(top)
            .ToArray();

        var warnings = new List<string>();
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : matchedSourceEventCount > 0 || completed.Count > 0
                ? "observed"
                : sourceEventCount == 0
                    ? "not_observed"
                    : "unknown";
        if (completed.Count == 0 && sourceEventCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "JIT",
                "JIT",
                "or all executed code was R2R/NGen pre-compiled, or filtered out"));
        }
        else if (completed.Count == 0)
        {
            warnings.Add(
                "no_matching_jit_intervals: JIT endpoint events were observed, but no completed method interval matched the selected scope and window.");
        }
        if (scopeStatus != ProcessAnalysisScope.ResolvedStatus)
            warnings.Add("scope_not_found: no process lifetime matched the requested selector and window.");
        if (scopeMode == "pid_aggregate")
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope explicitly aggregates multiple process lifetimes; rows remain separated by ProcessStartUs.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        var noDataReason = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "scope_not_found"
            : sourceEventCount == 0
                ? "event_class_not_observed"
                : completed.Count == 0
                    ? matchedSourceEventCount > 0
                        ? "no_completed_intervals_in_scope"
                        : "no_events_in_scope"
                    : null;

        return new JitAnalysisResponse(
            Pid: pid,
            TotalMethodsJitted: completed.Count,
            TotalJitUs: totalAccountedJitUs,
            TopMethods: rows,
            Warnings: warnings,
            TotalFullJitUs: totalFullJitUs,
            TotalAccountedJitUs: totalAccountedJitUs,
            HasMore: completed.Count > rows.Length,
            UnmatchedIntervalCount: unmatchedIntervalCount,
            InvalidIntervalCount: invalidIntervalCount,
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            SelectedProcess: selectedProcess,
            ScopeMode: scopeMode,
            PidReuseObserved: pidReuseObserved,
            IncludedProcesses: includedProcesses,
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedSourceEventCount,
            NoDataReason: noDataReason,
            MatchedIntervalCount: completed.Count);
    }

    private static long CountMatchedSourceEndpoints(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess)
    {
        long count = 0;
        foreach (var pair in pairs)
        {
            if (!matchesProcess(pair.Key.Process))
                continue;
            if (window.ContainsPoint(pair.StartUs)) count++;
            if (window.ContainsPoint(pair.EndUs)) count++;
        }
        return count;
    }
}
