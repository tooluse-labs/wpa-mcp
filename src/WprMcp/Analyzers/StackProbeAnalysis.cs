using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;
using WprMcp.Output;

namespace WprMcp.Analyzers;

// Debug-only cross-check for stack availability. This intentionally bypasses
// TraceCapabilitiesDetector so fixture refreshes can catch detector mistakes.
public static class StackProbeAnalysis
{
    public static StackProbeResponse Analyze(TraceLog trace, string path)
    {
        long explicitStackWalkEvents = 0;
        long cswitchEvents = 0;
        long cswitchEventsWithCallStacks = 0;
        long readyThreadEvents = 0;
        long readyThreadEventsWithCallStacks = 0;

        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);

        kernel.StackWalkStack += _ => explicitStackWalkEvents++;
        kernel.ThreadCSwitch += data =>
        {
            cswitchEvents++;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
                cswitchEventsWithCallStacks++;
        };
        kernel.DispatcherReadyThread += data =>
        {
            readyThreadEvents++;
            if (data.CallStackIndex() != CallStackIndex.Invalid)
                readyThreadEventsWithCallStacks++;
        };

        source.Process();

        long eventsWithCallStacks = 0;
        foreach (var ev in trace.Events)
        {
            if (trace.GetCallStackIndexForEvent(ev) != CallStackIndex.Invalid)
                eventsWithCallStacks++;
        }

        var notes = new List<string>();
        if (explicitStackWalkEvents == 0 && eventsWithCallStacks > 0)
        {
            notes.Add(
                "event_attached_stacks_without_explicit_stackwalk; TraceEvent can expose usable CallStackIndex values even when KernelTraceEventParser.StackWalkStack never fires");
        }

        if (cswitchEvents > 0 && cswitchEventsWithCallStacks == 0)
            notes.Add("cswitch_events_without_callstacks");

        if (readyThreadEvents > 0 && readyThreadEventsWithCallStacks == 0)
            notes.Add("ready_thread_events_without_callstacks");

        return new StackProbeResponse(
            Path: path,
            EventCount: trace.EventCount,
            ExplicitStackWalkEvents: explicitStackWalkEvents,
            EventsWithCallStacks: eventsWithCallStacks,
            EventStackCoveragePct: RatioOrNull(eventsWithCallStacks, trace.EventCount),
            CSwitchEvents: cswitchEvents,
            CSwitchEventsWithCallStacks: cswitchEventsWithCallStacks,
            CSwitchStackCoveragePct: RatioOrNull(cswitchEventsWithCallStacks, cswitchEvents),
            ReadyThreadEvents: readyThreadEvents,
            ReadyThreadEventsWithCallStacks: readyThreadEventsWithCallStacks,
            ReadyThreadStackCoveragePct: RatioOrNull(readyThreadEventsWithCallStacks, readyThreadEvents),
            HasExplicitStackWalkEvents: explicitStackWalkEvents > 0,
            HasUsableEventStacks: eventsWithCallStacks > 0,
            Notes: notes);
    }

    private static double? RatioOrNull(long numerator, long denominator) =>
        denominator == 0 ? null : numerator / (double)denominator;
}
