using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class HardFaultTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public HardFaultTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N file mappings associated with observed hard-fault paging-in bytes (for example, " +
        "a network-share PDF or DLL). PerfView equivalent: " +
        "'Memory Hard Fault → ByFile'.  Most hard faults are mmap'd files being touched for " +
        "the first time; some also come from paged-out heap/stack pages and the page file.  " +
        "Requires the HardFaults kernel keyword in the capture profile (NOT in default WPR " +
        "'CPU' / 'CPU.light' profiles). Set orderBy='max_latency' to surface one-off stalls " +
        "that do not dominate total bytes; each row includes MaxLatencyTimeUs so follow-up " +
        "analysis can zoom into the exact stall window. Supports startUs/endUs filters to " +
        "isolate a startup or interaction window before ranking files.")]
    public HardFaultByFileResponse HardFaultByFile(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Sort key: bytes (default), count, or max_latency")]
        string orderBy = "bytes",
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireText(orderBy);
        orderBy = HardFaultByFileAnalysis.NormalizeOrderBy(orderBy);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return HardFaultByFileAnalysis.Analyze(
            trace, top, pid, orderBy, window.StartUs, window.EndUs, processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N event-attached call stacks ranked by hard-fault paging-in bytes. PerfView " +
        "equivalent: 'Memory Hard Fault Stacks'. " +
        "Pairs with hard_fault_by_file (per-file bucket); this one is per-stack so you can " +
        "form hypotheses about eager loader resolution, lazy access, or concurrent scanning; " +
        "the stack alone does not establish higher-level causality. Each row carries both PageInBytes (metric=ByteCount) and FaultCount. " +
        "Requires the HardFaults kernel keyword in the capture profile. StackCoverage is " +
        "HardFault-only and identifies any bytes represented by the synthetic ?!? frame.")]
    public HardFaultStacksResponse HardFaultTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
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
        bool resolveSymbols = false,
        [Description("Optional process lifetime start in microseconds; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return PageFaultStackAnalysis.TopFaultStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the hard-fault-stack data.  Metric " +
        "is page-in bytes; top-N callers ranked by inclusive bytes paged in for focus, callees " +
        "by bytes flowing through.  Requires HardFaults keyword in the capture profile.")]
    public CallerCalleeResponse HardFaultCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Focus frame name, exactly as it appears in hard_fault_top_stacks output.")]
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
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return PageFaultStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
