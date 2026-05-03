using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class ClrTools
{
    private readonly TraceCache _cache;
    public ClrTools(TraceCache cache) => _cache = cache;

    [McpServerTool, Description(
        ".NET CLR GC analysis — list of garbage collections in the trace, with wall " +
        "duration AND 'stop the world' pause time per GC.  PerfView equivalent: 'GCStats'.  " +
        "Each row carries Generation (0/1/2), Reason (Induced / AllocSmall / AllocLarge / etc.), " +
        "DurationUs (GCStart→GCStop wall interval), and PauseUs (covering GCSuspendEEStart→ " +
        "GCRestartEEStop interval — the time mutator threads were halted).  Aggregate fields " +
        "TotalGcUs and TotalPauseUs make it easy to see 'is this app GC-bound'.  Requires " +
        "Microsoft-Windows-DotNETRuntime ETW provider with the GC keyword in the capture profile.")]
    public GcAnalysisResponse ClrGcAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID (recommended — without it, all PIDs share rows)")]
        int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        var trace = _cache.Get(path);
        return Analyzers.GcAnalysis.Analyze(trace, pid, startUs, endUs);
    }

    [McpServerTool, Description(
        ".NET CLR JIT compilation analysis — top-N methods ranked by JIT duration.  PerfView " +
        "equivalent: 'JIT Stats'.  Matches MethodJittingStarted→MethodLoadVerbose by " +
        "(ProcessID, MethodID) to compute per-method JIT μs.  Each row gives full method name " +
        "(namespace.method + signature), JitDurationUs, and the resulting native code size.  " +
        "R2R / NGen / pre-jitted methods don't fire MethodJittingStarted, so they're invisible " +
        "here — which is correct for 'what's the JIT cost in this trace'.  Requires " +
        "Microsoft-Windows-DotNETRuntime ETW provider with the JIT keyword.")]
    public JitAnalysisResponse ClrJitAnalysis(
        [Description("Absolute path to .etl file")] string path,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Top N methods by JIT duration (default 50, max 1000)")] int top = 50,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start")] long? endUs = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return Analyzers.JitAnalysis.Analyze(trace, pid, top, startUs, endUs);
    }
}
