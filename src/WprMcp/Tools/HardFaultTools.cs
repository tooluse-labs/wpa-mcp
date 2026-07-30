using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class HardFaultTools
{
    private readonly TraceCache _cache;
    public HardFaultTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N files by hard-fault paging-in bytes — answers 'which file caused the page-in " +
        "load' (e.g., a network-share PDF, an oversized DLL).  PerfView equivalent: " +
        "'Memory Hard Fault → ByFile'.  Most hard faults are mmap'd files being touched for " +
        "the first time; some also come from paged-out heap/stack pages and the page file.  " +
        "Requires the HardFaults kernel keyword in the capture profile (NOT in default WPR " +
        "'CPU' / 'CPU.light' profiles). Set orderBy='max_latency' to surface one-off stalls " +
        "that do not dominate total bytes; each row includes MaxLatencyTimeUs so follow-up " +
        "analysis can zoom into the exact stall window. Supports startUs/endUs filters to " +
        "isolate a startup or interaction window before ranking files.")]
    public HardFaultByFileResponse HardFaultByFile(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Sort key: bytes (default), count, or max_latency")]
        string orderBy = "bytes")
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireText(orderBy);
        orderBy = HardFaultByFileAnalysis.NormalizeOrderBy(orderBy);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return HardFaultByFileAnalysis.Analyze(trace, top, pid, orderBy, window.StartUs, window.EndUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by hard-fault paging-in bytes — answers 'which call chain " +
        "is paging in cold pages from disk'.  PerfView equivalent: 'Memory Hard Fault Stacks'.  " +
        "Pairs with hard_fault_by_file (per-file bucket); this one is per-stack so you can " +
        "tell eager loader resolution apart from lazy use of a constructor or scanner-induced " +
        "page-in.  Each row carries both PageInBytes (metric=ByteCount) and FaultCount.  " +
        "Requires the HardFaults kernel keyword in the capture profile.")]
    public HardFaultStacksResponse HardFaultTopStacks(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of paging-in bytes over this many " +
                     "buckets across the filter window. Default 0 = histogram off.")]
        int whenBuckets = 0,
        [Description(StackResponseOptions.CompactStacksDescription)]
        bool compactStacks = false,
        [Description(StackResponseOptions.SummaryOnlyDescription)]
        bool summaryOnly = false,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return PageFaultStackAnalysis.TopFaultStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: Console.Error, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the hard-fault-stack data.  Metric " +
        "is page-in bytes; top-N callers ranked by inclusive bytes paged in for focus, callees " +
        "by bytes flowing through.  Requires HardFaults keyword in the capture profile.")]
    public CallerCalleeResponse HardFaultCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in hard_fault_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequirePidTid(pid, tid: null);
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return PageFaultStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, Console.Error);
    }
}
