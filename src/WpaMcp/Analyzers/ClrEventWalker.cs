using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers;

namespace WpaMcp.Analyzers;

// CLR-events analog of KernelEventWalker.  Subscribe via `configure`, then runs a single
// trace pass.  Same construction note as the kernel walker — attach to the source from
// AnalysisEvents dispatcher, not the TraceLog directly, because TraceLog rejects callbacks
// for synthesized events.
internal static class ClrEventWalker
{
    public static void Walk(
        TraceLog trace,
        Action<ClrTraceEventParser> configure,
        CancellationToken cancellationToken = default)
    {
        var source = AnalysisEvents.CreateDispatcher(trace, cancellationToken);
        var clr = new ClrTraceEventParser(source);
        configure(clr);
        AnalysisEvents.Process(source, cancellationToken);
    }
}
