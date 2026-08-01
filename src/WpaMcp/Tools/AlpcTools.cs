using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class AlpcTools
{
    private readonly TraceCache _cache;
    public AlpcTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Top-N call stacks ranked by ALPC (Async Local Procedure Call) message count — answers " +
        "'which call chain is doing all the cross-process IPC'.  ALPC is the kernel IPC " +
        "primitive used by RPC, COM, AppContainer broker calls, lsass, the SCM, and most of the " +
        "Windows service surface.  Counts Send + Receive events; Wait/Unwait are skipped to " +
        "avoid double-counting against the CSwitch / ReadyThread paths.  Response splits " +
        "SendCount / ReceiveCount.  Requires the ALPC keyword in the capture profile (default " +
        "WPR 'CPU' / 'CPU.light' profiles do NOT enable it).")]
    public AlpcStacksResponse AlpcTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of message count over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return AlpcStackAnalysis.TopStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: Console.Error, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = false, Idempotent = true, OpenWorld = true, Destructive = true), Description(
        "Caller/callee drill-down for a focus function in the ALPC-stack data.  Metric is " +
        "message count; top-N callers ranked by inclusive messages flowing INTO focus, callees " +
        "by messages OUT.")]
    public CallerCalleeResponse AlpcCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in alpc_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false,
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        using var traceLease = _cache.Acquire(path);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return AlpcStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, Console.Error,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
