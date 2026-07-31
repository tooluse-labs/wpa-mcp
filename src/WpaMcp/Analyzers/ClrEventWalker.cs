using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace WpaMcp.Analyzers;

// CLR-events analog of KernelEventWalker.  Subscribe via `configure`, then runs a single
// trace pass.  Same construction note as the kernel walker — attach to the source from
// trace.Events.GetSource(), not the TraceLog directly, because TraceLog rejects callbacks
// for synthesized events.
internal static class ClrEventWalker
{
    public static void Walk(TraceLog trace, Action<ClrTraceEventParser> configure)
    {
        var source = trace.Events.GetSource();
        var clr = new ClrTraceEventParser(source);
        configure(clr);
        source.Process();
    }
}
