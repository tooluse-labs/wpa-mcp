using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using WprMcp.Output;

namespace WprMcp.Analyzers;

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
        var processes = trace.Processes;
        var process = processes.LastProcessWithID(pid);
        var processName = process?.Name ?? $"Process({pid})";
        var traceEndUs = (long)trace.SessionDuration.TotalMicroseconds;

        // Map: tid → (startUs, traceResident)
        var starts = new Dictionary<int, (long startUs, bool traceResident)>();
        var threads = new List<ThreadLifetimeRow>();

        KernelEventWalker.Walk(trace, kernel =>
        {
            kernel.ThreadStart += data =>
            {
                if (data.ProcessID != pid) return;
                var t = (long)(data.TimeStampRelativeMSec * 1000);
                starts[data.ThreadID] = (t, traceResident: false);
            };
            kernel.ThreadDCStart += data =>
            {
                if (data.ProcessID != pid) return;
                // DC events fire at trace start for already-alive threads; mark them
                // traceResident so callers don't read "0us start time" as a real spawn.
                starts[data.ThreadID] = (0L, traceResident: true);
            };
            kernel.ThreadStop += data =>
            {
                if (data.ProcessID != pid) return;
                var endUs = (long)(data.TimeStampRelativeMSec * 1000);
                if (starts.TryGetValue(data.ThreadID, out var s))
                {
                    threads.Add(new ThreadLifetimeRow(
                        Tid: data.ThreadID,
                        StartTimeUs: s.startUs,
                        EndTimeUs: endUs,
                        LifetimeUs: endUs - s.startUs,
                        TraceResidentStart: s.traceResident,
                        TraceResidentEnd: false));
                    starts.Remove(data.ThreadID);
                }
                else
                {
                    // Stop without observed Start — thread was alive before capture and we
                    // missed the DCStart (rare, possible if the event was lost).
                    threads.Add(new ThreadLifetimeRow(
                        Tid: data.ThreadID,
                        StartTimeUs: 0,
                        EndTimeUs: endUs,
                        LifetimeUs: endUs,
                        TraceResidentStart: true,
                        TraceResidentEnd: false));
                }
            };
        });

        // Threads still alive at trace end — every entry left in `starts`.
        foreach (var (tid, s) in starts)
        {
            threads.Add(new ThreadLifetimeRow(
                Tid: tid,
                StartTimeUs: s.startUs,
                EndTimeUs: traceEndUs,
                LifetimeUs: traceEndUs - s.startUs,
                TraceResidentStart: s.traceResident,
                TraceResidentEnd: true));
        }

        threads.Sort((a, b) => a.StartTimeUs.CompareTo(b.StartTimeUs));
        var topRows = threads.Take(top).ToList();

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

    private static int ComputePeakConcurrentThreads(List<ThreadLifetimeRow> threads)
    {
        var events = new List<(long t, int delta)>(threads.Count * 2);
        foreach (var t in threads)
        {
            events.Add((t.StartTimeUs, +1));
            events.Add((t.EndTimeUs, -1));
        }
        events.Sort((a, b) => a.t.CompareTo(b.t));

        int cur = 0, peak = 0;
        foreach (var (_, delta) in events)
        {
            cur += delta;
            if (cur > peak) peak = cur;
        }
        return peak;
    }
}
