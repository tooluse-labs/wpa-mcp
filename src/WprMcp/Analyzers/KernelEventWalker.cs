using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace WprMcp.Analyzers;

internal static class KernelEventWalker
{
    /// <summary>
    /// Subscribe via <paramref name="configure"/>, then run a single Process() pass.
    ///
    /// Always attaches the parser to <c>trace.Events.GetSource()</c>, never to the TraceLog
    /// directly. TraceLog overrides <c>ITraceParserServices.RegisterEventTemplate</c> and throws
    /// <c>ApplicationException</c> ("You may not register callbacks in TraceEventParsers that
    /// you attach directly to a TraceLog") for events it synthesizes (CSwitch, ImageLoad, and
    /// at least the FileIO events). The source-based pattern works for every event type, so
    /// every analyzer should funnel through this helper rather than repeating the dance.
    /// </summary>
    public static void Walk(TraceLog trace, Action<KernelTraceEventParser> configure)
    {
        var source = trace.Events.GetSource();
        var kernel = new KernelTraceEventParser(source);
        configure(kernel);
        source.Process();
    }
}
