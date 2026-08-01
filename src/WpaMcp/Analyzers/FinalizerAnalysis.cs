using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

internal readonly record struct FinalizerPairKey(
    ProcessInstanceKey Process,
    ushort ClrInstanceId);

internal readonly record struct FinalizerStartData;

internal readonly record struct FinalizerStopData(int FinalizersRun);

// Finalizer batches are intervals; finalized objects are point events. Pair all batch
// endpoints first, then scope both streams by exact process lifetime.
public static class FinalizerAnalysis
{
    public static FinalizerAnalysisResponse Analyze(
        TraceLog trace,
        int? pid,
        long? startUs,
        long? endUs,
        long? processStartUs = null)
    {
        var traceEndUs = TraceTime.FromMilliseconds(
            trace.SessionDuration.TotalMilliseconds);
        var window = TimeWindowInput.Validate(startUs, endUs, maxDurationUs: null)
            .Resolve(traceEndUs, maxDurationUs: null);
        var identities = TraceIdentityIndex.For(trace);
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        var events = new List<FinalizerEvent>();

        ClrEventWalker.Walk(trace, clr =>
        {
            clr.GCFinalizeObject += data => events.Add(FinalizerEvent.Object(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                data.TypeName));
            clr.GCFinalizersStart += data => events.Add(FinalizerEvent.BatchStart(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data)));
            clr.GCFinalizersStop += data => events.Add(FinalizerEvent.BatchStop(
                data.ProcessID,
                TraceTime.FromMilliseconds(data.TimeStampRelativeMSec),
                GcAnalysis.TryReadClrInstanceId(data),
                data.Count));
        });

        return AnalyzeResolved(identities, scope, events, pid);
    }

    internal static FinalizerAnalysisResponse AnalyzeEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<FinalizerEvent> events,
        int? pid,
        TimeWindow window,
        long? processStartUs)
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs,
            processLifetimes,
            Array.Empty<ThreadLifecycleEvent>());
        var scope = ProcessAnalysisScope.Resolve(
            window, pid, processStartUs, identities);
        return AnalyzeResolved(identities, scope, events, pid);
    }

    private static FinalizerAnalysisResponse AnalyzeResolved(
        TraceIdentityIndex identities,
        ProcessAnalysisScope scope,
        IReadOnlyList<FinalizerEvent> events,
        int? pid)
    {
        var pairer = new IntervalPairAccumulator<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>();
        var incompleteEvents = new List<IncompleteFinalizerEvent>();
        var countByType = new Dictionary<string, long>(StringComparer.Ordinal);
        long totalObjects = 0;
        long matchedObjectEventCount = 0;
        long matchedBatchEndpointEventCount = 0;

        foreach (var observation in events
                     .Select((value, index) => (value, index))
                     .OrderBy(item => item.value.TimeUs)
                     .ThenBy(item => item.index)
                     .Select(item => item.value))
        {
            var processResolution = observation.Kind == FinalizerEventKind.BatchStop
                ? identities.Processes.ResolveAtEndpoint(
                    observation.Pid, observation.TimeUs)
                : identities.Processes.Resolve(
                    observation.Pid,
                    observation.TimeUs,
                    processStartUs: null);
            if (processResolution.Status != InstanceResolutionStatus.Resolved ||
                !processResolution.Value.HasValue)
            {
                incompleteEvents.Add(new IncompleteFinalizerEvent(
                    observation.Pid,
                    observation.TimeUs));
                continue;
            }

            var process = processResolution.Value.Value;
            if (observation.Kind == FinalizerEventKind.Object)
            {
                if (!scope.Window.ContainsPoint(observation.TimeUs) ||
                    !scope.IncludedProcesses.Contains(process))
                {
                    continue;
                }

                totalObjects++;
                matchedObjectEventCount++;
                var typeName = string.IsNullOrEmpty(observation.TypeName)
                    ? "(unknown)"
                    : observation.TypeName;
                countByType[typeName] = countByType.GetValueOrDefault(typeName) + 1;
                continue;
            }

            if (scope.IncludedProcesses.Contains(process) &&
                scope.Window.ContainsPoint(observation.TimeUs))
            {
                matchedBatchEndpointEventCount++;
            }

            if (!observation.ClrInstanceId.HasValue)
            {
                incompleteEvents.Add(new IncompleteFinalizerEvent(
                    observation.Pid,
                    observation.TimeUs));
                continue;
            }

            var key = new FinalizerPairKey(
                process,
                observation.ClrInstanceId.Value);
            if (observation.Kind == FinalizerEventKind.BatchStart)
            {
                pairer.AddStart(
                    key,
                    observation.TimeUs,
                    new FinalizerStartData());
            }
            else
            {
                pairer.AddStop(
                    key,
                    observation.TimeUs,
                    new FinalizerStopData(observation.Count));
            }
        }

        var result = pairer.Complete();
        bool MatchesProcess(ProcessInstanceKey process) =>
            scope.IncludedProcesses.Contains(process);
        var unmatchedIntervalCount =
            result.UnmatchedStarts.Count(endpoint =>
                MatchesProcess(endpoint.Key.Process) &&
                scope.Window.ContainsPoint(endpoint.TimeUs)) +
            result.UnmatchedStops.Count(endpoint =>
                MatchesProcess(endpoint.Key.Process) &&
                scope.Window.ContainsPoint(endpoint.TimeUs));
        var invalidIntervalCount = result.InvalidIntervals.Count(interval =>
            MatchesProcess(interval.Key.Process) &&
            (scope.Window.ContainsPoint(interval.StartUs) ||
             scope.Window.ContainsPoint(interval.EndUs)));
        var incompleteIdentityCount = incompleteEvents.Count(endpoint =>
            (!pid.HasValue || endpoint.Pid == pid.Value) &&
            scope.Window.ContainsPoint(endpoint.TimeUs));

        var response = ProjectBatches(
            result.Pairs,
            scope.Window,
            scope,
            events.Count,
            matchedBatchEndpointEventCount,
            unmatchedIntervalCount + incompleteIdentityCount,
            invalidIntervalCount);
        var topTypes = StackSourceTopN.TopByValue(
            countByType,
            20,
            (typeName, count) => new FinalizedTypeRow(typeName, count));
        var matchedEventCount = checked(
            matchedObjectEventCount + matchedBatchEndpointEventCount);

        return response with
        {
            TotalObjectsFinalized = totalObjects,
            TopTypes = topTypes,
            Warnings = BuildWarnings(
                totalObjects,
                response.Batches.Count,
                incompleteIdentityCount,
                events.Count,
                scope.ScopeStatus,
                scope.ScopeMode,
                matchedEventCount),
            MatchedEventCount = matchedEventCount,
            MatchedObjectEventCount = matchedObjectEventCount,
            MatchedBatchEndpointEventCount = matchedBatchEndpointEventCount,
            MatchedBatchCount = response.Batches.Count,
            CapabilityStatus = scope.ScopeStatus != ProcessAnalysisScope.ResolvedStatus
                ? "unknown"
                : matchedEventCount > 0 || response.Batches.Count > 0
                    ? "observed"
                    : events.Count == 0
                        ? "not_observed"
                        : "unknown",
            NoDataReason = NoDataReason(
                scope.ScopeStatus,
                events.Count,
                matchedEventCount,
                hasOutput: totalObjects > 0 || response.Batches.Count > 0),
        };
    }

    internal static FinalizerAnalysisResponse ProjectBatches(
        IReadOnlyList<PairedInterval<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>> pairs,
        TimeWindow window,
        int? pid,
        int unmatchedIntervalCount = 0,
        int invalidIntervalCount = 0)
    {
        var inferredSourceEventCount = checked(pairs.Count * 2);
        bool MatchesProcess(ProcessInstanceKey process) =>
            !pid.HasValue || process.Pid == pid.Value;
        return ProjectBatchesCore(
            pairs,
            window,
            pid,
            MatchesProcess,
            inferredSourceEventCount,
            CountMatchedBatchEndpoints(pairs, window, MatchesProcess),
            unmatchedIntervalCount,
            invalidIntervalCount,
            selectedProcess: null,
            scopeMode: pid.HasValue ? "pid_aggregate" : "all_processes",
            pidReuseObserved: false,
            includedProcesses: Array.Empty<ProcessInstanceKey>(),
            scopeStatus: ProcessAnalysisScope.ResolvedStatus);
    }

    private static FinalizerAnalysisResponse ProjectBatches(
        IReadOnlyList<PairedInterval<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>> pairs,
        TimeWindow window,
        ProcessAnalysisScope scope,
        int sourceEventCount,
        long matchedBatchEndpointEventCount,
        int unmatchedIntervalCount,
        int invalidIntervalCount) =>
        ProjectBatchesCore(
            pairs,
            window,
            scope.Pid,
            process => scope.IncludedProcesses.Contains(process),
            sourceEventCount,
            matchedBatchEndpointEventCount,
            unmatchedIntervalCount,
            invalidIntervalCount,
            scope.SelectedProcess,
            scope.ScopeMode,
            scope.PidReuseObserved,
            scope.IncludedProcesses,
            scope.ScopeStatus);

    private static FinalizerAnalysisResponse ProjectBatchesCore(
        IReadOnlyList<PairedInterval<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>> pairs,
        TimeWindow window,
        int? pid,
        Func<ProcessInstanceKey, bool> matchesProcess,
        int sourceEventCount,
        long matchedBatchEndpointEventCount,
        int unmatchedIntervalCount,
        int invalidIntervalCount,
        ProcessInstanceKey? selectedProcess,
        string scopeMode,
        bool pidReuseObserved,
        IReadOnlyList<ProcessInstanceKey> includedProcesses,
        string scopeStatus)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        var rows = new List<FinalizerBatchRow>();
        long totalFullBatchUs = 0;
        long totalAccountedBatchUs = 0;

        foreach (var pair in pairs)
        {
            if (!matchesProcess(pair.Key.Process))
                continue;

            var projected = DurationAccounting.Project(pair, window);
            if (!projected.HasValue)
                continue;

            totalFullBatchUs += projected.Value.FullDurationUs;
            totalAccountedBatchUs += projected.Value.AccountedDurationUs;
            rows.Add(new FinalizerBatchRow(
                Pid: pair.Key.Process.Pid,
                StartUs: pair.StartUs,
                DurationUs: projected.Value.AccountedDurationUs,
                FinalizersRun: pair.StopData.FinalizersRun,
                EndUs: pair.EndUs,
                FullDurationUs: projected.Value.FullDurationUs,
                AccountedDurationUs: projected.Value.AccountedDurationUs,
                AccountingMode: DurationAccounting.ClippedOverlapMode,
                ProcessStartUs: pair.Key.Process.StartUs));
        }

        rows.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));
        var capabilityStatus = scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "unknown"
            : matchedBatchEndpointEventCount > 0 || rows.Count > 0
                ? "observed"
                : sourceEventCount == 0
                    ? "not_observed"
                    : "unknown";
        return new FinalizerAnalysisResponse(
            Pid: pid,
            TotalObjectsFinalized: 0,
            TotalBatchUs: totalAccountedBatchUs,
            Batches: rows,
            TopTypes: [],
            Warnings: BuildWarnings(
                0,
                rows.Count,
                incompleteIdentityCount: 0,
                sourceEventCount,
                scopeStatus,
                scopeMode,
                matchedBatchEndpointEventCount),
            TotalFullBatchUs: totalFullBatchUs,
            TotalAccountedBatchUs: totalAccountedBatchUs,
            UnmatchedIntervalCount: unmatchedIntervalCount,
            InvalidIntervalCount: invalidIntervalCount,
            AccountingMode: DurationAccounting.ClippedOverlapMode,
            SelectedProcess: selectedProcess,
            ScopeMode: scopeMode,
            PidReuseObserved: pidReuseObserved,
            IncludedProcesses: includedProcesses,
            ScopeStatus: scopeStatus,
            CapabilityStatus: capabilityStatus,
            MatchedEventCount: matchedBatchEndpointEventCount,
            NoDataReason: NoDataReason(
                scopeStatus,
                sourceEventCount,
                matchedBatchEndpointEventCount,
                hasOutput: rows.Count > 0),
            MatchedObjectEventCount: 0,
            MatchedBatchEndpointEventCount: matchedBatchEndpointEventCount,
            MatchedBatchCount: rows.Count);
    }

    private static IReadOnlyList<string> BuildWarnings(
        long totalObjects,
        int batchCount,
        int incompleteIdentityCount,
        int sourceEventCount,
        string scopeStatus,
        string scopeMode,
        long matchedEventCount)
    {
        var warnings = new List<string>();
        if (totalObjects == 0 && batchCount == 0 && sourceEventCount == 0)
        {
            warnings.Add(WarningBuilder.MissingClrKeyword(
                "finalizer",
                "GC",
                "or no finalizers ran in the trace"));
        }
        else if (totalObjects == 0 && batchCount == 0)
        {
            warnings.Add(matchedEventCount > 0
                ? "no_completed_intervals_in_scope: finalizer batch endpoint events matched the selected scope and window, but did not form a completed batch interval."
                : "no_events_in_scope: finalizer events were observed, but none matched the selected scope and window.");
        }
        if (scopeStatus != ProcessAnalysisScope.ResolvedStatus)
            warnings.Add("scope_not_found: no process lifetime matched the requested selector and window.");
        if (scopeMode == "pid_aggregate")
        {
            warnings.Add(
                "ambiguous_process_instance: pid-only scope explicitly aggregates multiple process lifetimes; batches remain separated by ProcessStartUs and TopTypes covers the aggregate scope.");
        }
        warnings.Add(WarningBuilder.LegacyAccountedDurationWarning);
        if (incompleteIdentityCount > 0)
        {
            warnings.Add(
                $"identity_incomplete: skipped {incompleteIdentityCount} finalizer event(s) because their process or CLR instance identity was unresolved or ambiguous.");
        }
        return warnings;
    }

    private static string? NoDataReason(
        string scopeStatus,
        long sourceEventCount,
        long matchedEventCount,
        bool hasOutput) =>
        scopeStatus != ProcessAnalysisScope.ResolvedStatus
            ? "scope_not_found"
            : sourceEventCount == 0
                ? "event_class_not_observed"
                : !hasOutput
                    ? matchedEventCount > 0
                        ? "no_completed_intervals_in_scope"
                        : "no_events_in_scope"
                    : null;

    private static long CountMatchedBatchEndpoints(
        IReadOnlyList<PairedInterval<
            FinalizerPairKey,
            FinalizerStartData,
            FinalizerStopData>> pairs,
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

    private readonly record struct IncompleteFinalizerEvent(int Pid, long TimeUs);
}

internal enum FinalizerEventKind
{
    Object,
    BatchStart,
    BatchStop,
}

internal readonly record struct FinalizerEvent(
    int Pid,
    long TimeUs,
    FinalizerEventKind Kind,
    ushort? ClrInstanceId,
    string TypeName,
    int Count)
{
    public static FinalizerEvent Object(int pid, long timeUs, string? typeName) =>
        new(pid, timeUs, FinalizerEventKind.Object, null, typeName ?? string.Empty, 0);

    public static FinalizerEvent BatchStart(
        int pid,
        long timeUs,
        ushort? clrInstanceId) =>
        new(pid, timeUs, FinalizerEventKind.BatchStart, clrInstanceId, string.Empty, 0);

    public static FinalizerEvent BatchStop(
        int pid,
        long timeUs,
        ushort? clrInstanceId,
        int count) =>
        new(pid, timeUs, FinalizerEventKind.BatchStop, clrInstanceId, string.Empty, count);
}
