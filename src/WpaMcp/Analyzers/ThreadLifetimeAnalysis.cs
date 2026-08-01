using Microsoft.Diagnostics.Tracing.Etlx;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Analyzers;

// Per-process thread-lifecycle list: every ThreadStart / ThreadStop in chronological order
// for one PID, with start time, end time, and lifetime.  Useful for "did thread-pool spawn
// 200 threads in the startup window" / "is something thrashing thread creation".
//
// PerfView equivalent: events filter on Thread/Start + Thread/Stop, but no dedicated
// per-thread lifetime view — this composite is wpa-mcp specific.
//
// Subscribes to ThreadStart, ThreadStop (alias ThreadEnd), and the corresponding ThreadDC
// (data-collection / rundown) events.  ThreadDCStart / ThreadDCStop are emitted at the
// start of a trace for threads already alive when capture begins — including them gives
// a complete picture, but the StartTimeUs for those will be 0 (trace start) since the
// real spawn moment is before the trace.  TraceResident=true flags those.
//
// Requires the Thread keyword in the capture profile (in default kernel profiles).
public static class ThreadLifetimeAnalysis
{
    public static ThreadLifetimeResponse Analyze(
        TraceLog trace,
        int pid,
        int top,
        long? processStartUs = null)
    {
        var identities = TraceIdentityIndex.For(trace);
        var scope = ResolveSingleProcessScope(
            identities, pid, processStartUs);
        var selected = scope.SelectedProcess.HasValue
            ? identities.Processes.FindExact(scope.SelectedProcess.Value)
                .OrderByDescending(candidate => candidate.EndUs)
                .FirstOrDefault()
            : null;
        var processName = selected is null
            ? $"Process({pid})"
            : ResolveProcessName(trace, selected.Key);
        return BuildResponse(identities, pid, top, selected, processName, scope);
    }

    private static ThreadLifetimeResponse BuildResponse(
        TraceIdentityIndex identities,
        int pid,
        int top,
        ProcessLifetime? selected,
        string processName,
        ProcessAnalysisScope scope)
    {
        var threads = selected is null
            ? Array.Empty<ThreadLifetimeRow>()
            : ProjectLifetimes(identities.Threads.Lifetimes.Where(
                lifetime => lifetime.Key.Process == selected.Key));
        var topRows = threads.Take(top).ToArray();
        var globalEventCount = identities.ThreadLifecycleEventCount;
        var matchedEventCount = scope.IsResolved
            ? scope.IncludedProcesses.Sum(process =>
                identities.ThreadLifecycleEventCountsByProcess.GetValueOrDefault(process))
            : 0;

        var warnings = new List<string>();
        if (threads.Count == 0)
        {
            warnings.Add(!scope.IsResolved
                ? ProcessAnalysisScope.ResolutionFailureWarning(scope.ScopeStatus)
                : globalEventCount == 0
                    ? "event_class_not_observed: no ThreadStart/Stop or thread-rundown records were observed in the materialized trace. This does not prove that Thread capture was disabled."
                    : "no_events_in_scope: thread lifecycles were materialized in the trace, but none matched the selected process lifetime.");
        }
        // ThreadLifetimeRow ≈ 40 B; 100k = ~4 MB. Anything north of that suggests a
        // thread-pool thrasher / fork bomb pattern and the consumer should know that
        // the in-memory list is large (TopN truncates the result, not the working set).
        if (threads.Count > 100_000)
        {
            warnings.Add(
                $"Trace has {threads.Count:N0} thread events for PID {pid} — unusually high. " +
                "Consider narrowing the time window or investigating thread-creation thrash.");
        }

        return new ThreadLifetimeResponse(
            Pid: pid,
            ProcessName: processName,
            TotalThreads: threads.Count,
            PeakConcurrentThreads: ComputePeakConcurrentThreads(threads),
            Threads: topRows,
            Warnings: warnings,
            SelectedProcess: scope.SelectedProcess,
            ScopeMode: scope.ScopeMode,
            PidReuseObserved: scope.PidReuseObserved,
            IncludedProcesses: scope.IncludedProcesses,
            ScopeStatus: scope.ScopeStatus,
            CapabilityStatus: scope.IsResolved
                ? matchedEventCount > 0
                    ? "observed"
                    : globalEventCount == 0
                        ? "not_observed"
                        : "unknown"
                : "unknown",
            MatchedEventCount: matchedEventCount,
            NoDataReason: !scope.IsResolved
                ? scope.ScopeStatus
                : globalEventCount == 0
                    ? "event_class_not_observed"
                    : threads.Count == 0
                        ? "no_events_in_scope"
                        : null);
    }

    internal static ThreadLifetimeResponse AnalyzeEventsResponse(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<ThreadLifecycleEvent> events,
        int pid,
        int top,
        long? processStartUs,
        string? processName = null)
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs,
            processLifetimes,
            events);
        var scope = ResolveSingleProcessScope(
            identities, pid, processStartUs);
        var selected = scope.SelectedProcess.HasValue
            ? identities.Processes.FindExact(scope.SelectedProcess.Value)
                .OrderByDescending(candidate => candidate.EndUs)
                .FirstOrDefault()
            : null;
        return BuildResponse(
            identities,
            pid,
            top,
            selected,
            processName ?? $"Process({pid})",
            scope);
    }

    internal static IReadOnlyList<ThreadLifetimeRow> AnalyzeEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<ThreadLifecycleEvent> events,
        ProcessInstanceKey selector)
    {
        return AnalyzeEventsResponse(
            traceEndUs,
            processLifetimes,
            events,
            selector.Pid,
            top: int.MaxValue,
            processStartUs: selector.StartUs).Threads;
    }

    private static ProcessAnalysisScope ResolveSingleProcessScope(
        TraceIdentityIndex identities,
        int pid,
        long? processStartUs)
    {
        return ProcessAnalysisScope.Resolve(
            new TimeWindow(0, identities.TraceEndUs),
            pid,
            processStartUs,
            identities).RequireSingleProcess();
    }

    private static IReadOnlyList<ThreadLifetimeRow> ProjectLifetimes(
        IEnumerable<ThreadLifetime> lifetimes) =>
        lifetimes
            .Where(lifetime => lifetime.EndUs > lifetime.StartUs)
            .OrderBy(lifetime => lifetime.StartUs)
            .ThenBy(lifetime => lifetime.Key.Tid)
            .ThenBy(lifetime => lifetime.Key.Generation)
            .Select(lifetime => new ThreadLifetimeRow(
                Tid: lifetime.Key.Tid,
                StartTimeUs: lifetime.StartUs,
                EndTimeUs: lifetime.EndUs,
                LifetimeUs: checked(lifetime.EndUs - lifetime.StartUs),
                TraceResidentStart: !lifetime.StartObserved,
                TraceResidentEnd: !lifetime.EndObserved,
                ProcessStartUs: lifetime.Key.Process.StartUs,
                ThreadGeneration: lifetime.Key.Generation))
            .ToArray();

    private static string ResolveProcessName(TraceLog trace, ProcessInstanceKey process)
    {
        var exact = trace.Processes.FirstOrDefault(candidate =>
            candidate.ProcessID == process.Pid &&
            TraceTime.FromMilliseconds(candidate.StartTimeRelativeMsec) == process.StartUs);
        if (!string.IsNullOrWhiteSpace(exact?.Name))
            return exact.Name;

        var names = trace.Processes
            .Where(candidate => candidate.ProcessID == process.Pid)
            .Select(candidate => candidate.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return names.Length == 1 ? names[0]! : $"Process({process.Pid})";
    }

    private static int ComputePeakConcurrentThreads(IReadOnlyList<ThreadLifetimeRow> threads)
    {
        var events = new List<(long t, int delta)>(threads.Count * 2);
        foreach (var t in threads)
        {
            events.Add((t.StartTimeUs, +1));
            events.Add((t.EndTimeUs, -1));
        }
        events.Sort((a, b) =>
        {
            var timeComparison = a.t.CompareTo(b.t);
            return timeComparison != 0 ? timeComparison : a.delta.CompareTo(b.delta);
        });

        int cur = 0, peak = 0;
        foreach (var (_, delta) in events)
        {
            cur += delta;
            if (cur > peak) peak = cur;
        }
        return peak;
    }
}
