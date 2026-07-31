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
    public static ThreadLifetimeResponse Analyze(TraceLog trace, int pid, int top)
    {
        var identities = TraceIdentityIndex.For(trace);
        var threads = ProjectLifetimes(
            identities.Threads.Lifetimes.Where(lifetime => lifetime.Key.Process.Pid == pid));
        var topRows = threads.Take(top).ToArray();
        var processNames = trace.Processes
            .Where(process => process.ProcessID == pid)
            .Select(process => process.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var processName = processNames.Length == 1
            ? processNames[0]!
            : $"Process({pid})";

        var warnings = new List<string>();
        if (threads.Count == 0)
        {
            warnings.Add(
                $"No ThreadStart / ThreadStop events for PID {pid}. The process may not exist " +
                "in this trace, or the capture omits the Thread keyword (in default kernel profiles).");
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
            Warnings: warnings);
    }

    internal static IReadOnlyList<ThreadLifetimeRow> AnalyzeEvents(
        long traceEndUs,
        IReadOnlyList<ProcessLifetime> processLifetimes,
        IReadOnlyList<ThreadLifecycleEvent> events,
        ProcessInstanceKey selector)
    {
        var identities = TraceIdentityIndex.BuildFromEvents(
            traceEndUs,
            processLifetimes,
            events);
        return ProjectLifetimes(
            identities.Threads.Lifetimes.Where(lifetime => lifetime.Key.Process == selector));
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
                TraceResidentEnd: !lifetime.EndObserved))
            .ToArray();

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
