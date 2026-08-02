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
        var traceIdentityUnresolvedEndpointCount = 0;
        var scopedIdentityUnresolvedEndpointCount = 0;
        long sourceEventCount = 0;
        long matchedSourceEventCount = 0;
        long scopedUsableSourceEventCount = 0;

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
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(
                            scope, identities, data.ProcessID, timestampUs))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }
                if (matchesScope)
                    scopedUsableSourceEventCount++;

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
                    traceIdentityUnresolvedEndpointCount++;
                    if (MatchesRawScope(
                            scope, identities, data.ProcessID, timestampUs,
                            atEndpoint: true))
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }

                var matchesScope = scope.IsResolved &&
                                   scope.IncludedProcesses.Contains(process.Value.Value) &&
                                   window.ContainsPoint(timestampUs);
                if (matchesScope)
                    matchedSourceEventCount++;
                if (!clrInstanceId.HasValue)
                {
                    traceIdentityUnresolvedEndpointCount++;
                    if (matchesScope)
                        scopedIdentityUnresolvedEndpointCount++;
                    return;
                }
                if (matchesScope)
                    scopedUsableSourceEventCount++;

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
                result.UnmatchedStarts.Count + result.UnmatchedStops.Count,
            invalidIntervalCount: result.InvalidIntervals.Count,
            scopedUnmatchedIntervalCount:
                result.UnmatchedStarts.Count(item =>
                    scope.IsResolved &&
                    scope.IncludedProcesses.Contains(item.Key.Process) &&
                    window.ContainsPoint(item.TimeUs)) +
                result.UnmatchedStops.Count(item =>
                    scope.IsResolved &&
                    scope.IncludedProcesses.Contains(item.Key.Process) &&
                    window.ContainsPoint(item.TimeUs)),
            scopedInvalidIntervalCount: result.InvalidIntervals.Count(item =>
                scope.IsResolved &&
                scope.IncludedProcesses.Contains(item.Key.Process) &&
                (window.ContainsPoint(item.StartUs) ||
                 window.ContainsPoint(item.EndUs))),
            scopedUsableSourceEventCount: scopedUsableSourceEventCount,
            traceIdentityUnresolvedEndpointCount:
                traceIdentityUnresolvedEndpointCount,
            scopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount,
            traceUnmatchedStartCount: result.UnmatchedStarts.Count,
            traceUnmatchedStopCount: result.UnmatchedStops.Count,
            scopedUnmatchedStartCount: result.UnmatchedStarts.Count(item =>
                scope.IsResolved &&
                scope.IncludedProcesses.Contains(item.Key.Process) &&
                window.ContainsPoint(item.TimeUs)),
            scopedUnmatchedStopCount: result.UnmatchedStops.Count(item =>
                scope.IsResolved &&
                scope.IncludedProcesses.Contains(item.Key.Process) &&
                window.ContainsPoint(item.TimeUs)));

        return response;
    }

    internal static JitAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        int? pid,
        int top,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0,
        int? scopedUnmatchedIntervalCount = null,
        int? scopedInvalidIntervalCount = null,
        long traceIdentityUnresolvedEndpointCount = 0,
        long scopedIdentityUnresolvedEndpointCount = 0,
        long? scopedUsableSourceEventCount = null,
        int? traceUnmatchedStartCount = null,
        int? traceUnmatchedStopCount = null,
        int? scopedUnmatchedStartCount = null,
        int? scopedUnmatchedStopCount = null)
    {
        var inferredSourceEventCount = checked(
            (long)pairs.Count * 2 +
            unmatchedIntervalCount +
            (long)invalidIntervalCount * 2 +
            traceIdentityUnresolvedEndpointCount);
        bool MatchesProcess(ProcessInstanceKey process) =>
            !pid.HasValue || process.Pid == pid.Value;
        var matchedPairEndpointCount = CountMatchedSourceEndpoints(
            pairs, window, MatchesProcess);
        var inferredScopedUsableEndpointCount = checked(
            matchedPairEndpointCount +
            (scopedUnmatchedIntervalCount ?? unmatchedIntervalCount) +
            (long)(scopedInvalidIntervalCount ?? invalidIntervalCount) * 2);
        return ProjectPairsCore(
            pairs,
            window,
            pid,
            MatchesProcess,
            top,
            inferredSourceEventCount,
            unmatchedIntervalCount,
            invalidIntervalCount,
            scopedUnmatchedIntervalCount ?? unmatchedIntervalCount,
            scopedInvalidIntervalCount ?? invalidIntervalCount,
            selectedProcess: null,
            scopeMode: pid.HasValue ? "pid_aggregate" : "all_processes",
            pidReuseObserved: false,
            includedProcesses: Array.Empty<ProcessInstanceKey>(),
            scopeStatus: ProcessAnalysisScope.ResolvedStatus,
            matchedSourceEventCount: inferredScopedUsableEndpointCount,
            scopedUsableSourceEventCount:
                scopedUsableSourceEventCount ??
                inferredScopedUsableEndpointCount,
            traceIdentityUnresolvedEndpointCount:
                traceIdentityUnresolvedEndpointCount,
            scopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount,
            traceUnmatchedStartCount: traceUnmatchedStartCount,
            traceUnmatchedStopCount: traceUnmatchedStopCount,
            scopedUnmatchedStartCount: scopedUnmatchedStartCount,
            scopedUnmatchedStopCount: scopedUnmatchedStopCount);
    }

    internal static JitAnalysisResponse ProjectPairs(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        ProcessAnalysisScope scope,
        int top,
        long sourceEventCount,
        long? matchedSourceEventCount = null,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0,
        int? scopedUnmatchedIntervalCount = null,
        int? scopedInvalidIntervalCount = null,
        long traceIdentityUnresolvedEndpointCount = 0,
        long scopedIdentityUnresolvedEndpointCount = 0,
        long? scopedUsableSourceEventCount = null,
        int? traceUnmatchedStartCount = null,
        int? traceUnmatchedStopCount = null,
        int? scopedUnmatchedStartCount = null,
        int? scopedUnmatchedStopCount = null)
    {
        var matchedPairEndpointCount = CountMatchedSourceEndpoints(
            pairs,
            window,
            process => scope.IsResolved && scope.IncludedProcesses.Contains(process));
        var inferredScopedUsableEndpointCount = checked(
            matchedPairEndpointCount +
            (scopedUnmatchedIntervalCount ?? unmatchedIntervalCount) +
            (long)(scopedInvalidIntervalCount ?? invalidIntervalCount) * 2);
        var matched = matchedSourceEventCount ??
            inferredScopedUsableEndpointCount;
        return ProjectPairsCore(
            pairs,
            window,
            scope.Pid,
            process => scope.IsResolved && scope.IncludedProcesses.Contains(process),
            top,
            sourceEventCount,
            unmatchedIntervalCount,
            invalidIntervalCount,
            scopedUnmatchedIntervalCount ?? unmatchedIntervalCount,
            scopedInvalidIntervalCount ?? invalidIntervalCount,
            scope.SelectedProcess,
            scope.ScopeMode,
            scope.PidReuseObserved,
            scope.IncludedProcesses,
            scope.ScopeStatus,
            matched,
            scopedUsableSourceEventCount ??
                (matchedSourceEventCount.HasValue
                    ? Math.Max(
                        0, matched - scopedIdentityUnresolvedEndpointCount)
                    : inferredScopedUsableEndpointCount),
            traceIdentityUnresolvedEndpointCount,
            scopedIdentityUnresolvedEndpointCount,
            traceUnmatchedStartCount,
            traceUnmatchedStopCount,
            scopedUnmatchedStartCount,
            scopedUnmatchedStopCount);
    }

    private static JitAnalysisResponse ProjectPairsCore(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        int? pid,
        Func<ProcessInstanceKey, bool> matchesProcess,
        int top,
        long sourceEventCount,
        int unmatchedIntervalCount,
        int invalidIntervalCount,
        int scopedUnmatchedIntervalCount,
        int scopedInvalidIntervalCount,
        ProcessInstanceKey? selectedProcess,
        string scopeMode,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey> includedProcesses,
        string scopeStatus,
        long matchedSourceEventCount,
        long scopedUsableSourceEventCount,
        long traceIdentityUnresolvedEndpointCount,
        long scopedIdentityUnresolvedEndpointCount,
        int? traceUnmatchedStartCount,
        int? traceUnmatchedStopCount,
        int? scopedUnmatchedStartCount,
        int? scopedUnmatchedStopCount)
    {
        ArgumentNullException.ThrowIfNull(pairs);
        if (top < 1)
            throw new ArgumentOutOfRangeException(nameof(top), top, "Top must be positive.");

        var completed = new List<JitMethodRow>();
        long totalFullJitUs = 0;
        long totalAccountedJitUs = 0;

        foreach (var pair in pairs)
        {
            AnalysisEvents.ThrowIfCancellationRequested();
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
            .ThenBy(row => row.Pid)
            .ThenBy(row => row.ProcessStartUs)
            .ThenBy(row => row.Method, StringComparer.Ordinal)
            .ThenBy(row => row.EndUs)
            .ThenBy(row => row.MethodIlSize)
            .Take(top)
            .ToArray();

        var warnings = new List<string>();
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : scopedUsableSourceEventCount > 0 || completed.Count > 0
                ? "observed"
                : sourceEventCount == 0
                    ? "not_observed"
                    : "unknown";
        if (scopeStatus != ProcessAnalysisScope.ResolvedStatus)
        {
            warnings.Add(ProcessAnalysisScope.ResolutionFailureWarning(scopeStatus));
        }
        else if (completed.Count == 0 && sourceEventCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "JIT",
                "JIT",
                "or all executed code was R2R/NGen pre-compiled, or filtered out"));
        }
        else if (completed.Count == 0 &&
                 scopedIdentityUnresolvedEndpointCount > 0 &&
                 scopedUsableSourceEventCount == 0)
        {
            warnings.Add(
                "source_events_unattributed: JIT endpoints matched the lifetime-aware raw process selector and half-open query window, but process or CLR instance identity was unresolved; no method interval attribution was guessed.");
        }
        else if (completed.Count == 0)
        {
            warnings.Add(
                "no_matching_jit_intervals: JIT endpoint events were observed, but no completed method interval matched the selected scope and window.");
        }
        if (scopeMode == "pid_aggregate")
        {
            warnings.Add(
                "pid_aggregate: pid-only scope explicitly aggregates multiple process lifetimes; rows remain separated by ProcessStartUs.");
        }
        if (traceIdentityUnresolvedEndpointCount > 0)
        {
            warnings.Add(
                $"identity_unresolved: {traceIdentityUnresolvedEndpointCount} JIT endpoint event(s) were dropped because process or CLR instance identity was unresolved or ambiguous.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);

        var noDataReason = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? scopeStatus
            : sourceEventCount == 0
                ? "event_class_not_observed"
                : completed.Count == 0
                    ? scopedUsableSourceEventCount > 0
                        ? "no_completed_intervals_in_scope"
                        : scopedIdentityUnresolvedEndpointCount > 0
                            ? "source_events_unattributed"
                            : "no_events_in_scope"
                    : null;

        var traceCompatibilityUnmatchedCount = checked(
            unmatchedIntervalCount +
            checked((int)traceIdentityUnresolvedEndpointCount));
        var scopedCompatibilityUnmatchedCount = checked(
            scopedUnmatchedIntervalCount +
            checked((int)scopedIdentityUnresolvedEndpointCount));

        return new JitAnalysisResponse(
            Pid: pid,
            TotalMethodsJitted: completed.Count,
            TotalJitUs: totalAccountedJitUs,
            TopMethods: rows,
            Warnings: warnings,
            TotalFullJitUs: totalFullJitUs,
            TotalAccountedJitUs: totalAccountedJitUs,
            HasMore: completed.Count > rows.Length,
            UnmatchedIntervalCount: traceCompatibilityUnmatchedCount,
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
            MatchedIntervalCount: completed.Count,
            TraceUnmatchedIntervalCount: traceCompatibilityUnmatchedCount,
            ScopedUnmatchedIntervalCount: scopedCompatibilityUnmatchedCount,
            TraceInvalidIntervalCount: invalidIntervalCount,
            ScopedInvalidIntervalCount: scopedInvalidIntervalCount,
            TraceIdentityUnresolvedEndpointCount:
                traceIdentityUnresolvedEndpointCount,
            ScopedIdentityUnresolvedEndpointCount:
                scopedIdentityUnresolvedEndpointCount,
            TraceUnmatchedStartCount: traceUnmatchedStartCount,
            TraceUnmatchedStopCount: traceUnmatchedStopCount,
            ScopedUnmatchedStartCount: scopedUnmatchedStartCount,
            ScopedUnmatchedStopCount: scopedUnmatchedStopCount);
    }

    private static long CountMatchedSourceEndpoints(
        IReadOnlyList<PairedInterval<JitPairKey, JitStartData, JitStopData>> pairs,
        TimeWindow window,
        Func<ProcessInstanceKey, bool> matchesProcess)
    {
        long count = 0;
        foreach (var pair in pairs)
        {
            AnalysisEvents.ThrowIfCancellationRequested();
            if (!matchesProcess(pair.Key.Process))
                continue;
            if (window.ContainsPoint(pair.StartUs)) count++;
            if (window.ContainsPoint(pair.EndUs)) count++;
        }
        return count;
    }

    internal static bool MatchesRawScope(
        ProcessAnalysisScope scope,
        TraceIdentityIndex identities,
        int pid,
        long timestampUs,
        bool atEndpoint = false) =>
        scope.MatchesRawUnresolvedCandidate(
            identities, pid, timestampUs, atEndpoint);
}
