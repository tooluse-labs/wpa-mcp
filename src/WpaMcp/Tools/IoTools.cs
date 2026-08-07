using System.ComponentModel;
using ModelContextProtocol.Server;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Output;

namespace WpaMcp.Tools;

[McpServerToolType]
public sealed class IoTools
{
    private readonly TraceCache _cache;
    private readonly IPrivacyLogSink _privacyLog;
    public IoTools(TraceCache cache, IPrivacyLogSink? privacyLog = null)
    {
        _cache = cache;
        _privacyLog = privacyLog ?? PassThroughPrivacyLogSink.Instance;
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top N files by total IO bytes (read + write). Supports pid/processStartUs/startUs/endUs filters " +
        "so a noisy trace can be narrowed to an exact process lifetime or startup window. A PID-only " +
        "query may explicitly aggregate reused-PID lifetimes; inspect ScopeMode and IncludedProcesses.")]
    public FileIoResponse FileIoTopFiles(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Exact process lifetime start in microseconds since trace start; requires pid")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return FileIoAnalysis.TopFiles(
            trace: trace,
            top: top,
            pid: pid,
            startUs: window.StartUs,
            endUs: window.EndUs,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by file-IO bytes — answers 'which call chain is doing all " +
        "the file IO'. PerfView equivalent: 'File I/O Stacks' view. Pairs with file_io_top_files " +
        "(per-file bucket); this one is per-stack so you can tell streaming-of-one-big-file apart " +
        "from open-read-close-of-thousands-of-small-files. Each row carries both Bytes (metric=IoSize) " +
        "and OpCount. Requires the FileIO keyword in the capture profile. StackCoverage is FileIO-only " +
        "and identifies any bytes represented by the synthetic ?!? frame.")]
    public FileIoStacksResponse FileIoTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of IO bytes over this many buckets " +
                     "across the filter window. Default 0 = histogram off.")]
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
        return FileIoStackAnalysis.TopIoStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the file-IO-stack data. Metric is " +
        "IO bytes (read+write); top-N callers ranked by inclusive bytes flowing INTO focus, " +
        "callees by bytes OUT. PerfView equivalent: 'Callers' / 'Callees' tabs of File I/O Stacks.")]
    public CallerCalleeResponse FileIoCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Focus frame name, exactly as it appears in file_io_top_stacks output.")]
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
        return FileIoStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Stack-independent PHYSICAL Disk I/O aggregation from DiskIO Read/Write events. Returns " +
        "exact read/write counts and bytes, disk service-time statistics, per-disk busy time, " +
        "and bounded top process/file/disk rows. Set bucketUs > 0 for a complete timeline; the " +
        "bucket width is widened automatically when needed to stay within 512 buckets. This is " +
        "a different layer from File I/O and does not require event stacks or symbols. Counts and " +
        "bytes use completion timestamps; busy percentages use the union of matched service intervals.")]
    public DiskIoAnalysisResponse DiskIoAnalysis(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N process and file rows per section (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("Requested timeline bucket width in microseconds; 0 disables the timeline. The effective width may be increased to cap output at 512 buckets.")]
        long bucketUs = 0,
        [Description("Return only summary and scope evidence, omitting process/file/disk/timeline rows.")]
        bool summaryOnly = false,
        [Description("Exact process lifetime start in microseconds since trace start; requires pid. PID-only queries explicitly aggregate reused lifetimes.")]
        long? processStartUs = null)
    {
        var requestedWindow = Validation.RequireWindowInput(startUs, endUs);
        Validation.RequireThreadSelector(pid, tid: null, processStartUs, threadStartUs: null);
        Validation.RequireTop(top);
        if (bucketUs < 0)
            throw ToolFailureCaptureContext.Capture(
                new ArgumentOutOfRangeException(nameof(bucketUs), "must be non-negative"));
        using var traceLease = _cache.Acquire(traceId);
        var trace = traceLease.Trace;
        var window = requestedWindow.Resolve(
            TraceTime.FromMilliseconds(trace.SessionDuration.TotalMilliseconds), maxDurationUs: null);
        return Analyzers.DiskIoAnalysis.Analyze(
            trace,
            top,
            pid,
            window.StartUs,
            window.EndUs,
            bucketUs,
            summaryOnly,
            processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top-N call stacks ranked by PHYSICAL disk-IO bytes — answers 'which call chain " +
        "actually hit the disk'. Different layer from file_io_top_stacks: file IO captures " +
        "all syscalls (cache-served included), disk IO only events that hit physical media. " +
        "Diff the two to identify cache-served reads. PerfView equivalent: 'Disk I/O Stacks' " +
        "view. Requires the DiskIO keyword in the capture profile. StackCoverage is DiskIO-only " +
        "and identifies any bytes represented by the synthetic ?!? frame.")]
    public DiskIoStacksResponse DiskIoTopStacks(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Top N rows (default 30, max 1000)")] int top = 30,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description("If > 0, also return a When-histogram of disk bytes over this many " +
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
        return DiskIoStackAnalysis.TopIoStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid,
            window.StartUs, window.EndUs, symbolLog: _privacyLog.Writer, whenBuckets: whenBuckets,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue,
            processStartUs: processStartUs);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the disk-IO-stack data. Metric is " +
        "physical disk bytes (TransferSize); top-N callers ranked by inclusive disk bytes " +
        "flowing INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse DiskIoCallerCallee(
        [Description("Canonical TraceId returned by load_trace")] string traceId,
        [Description("Focus frame name, exactly as it appears in disk_io_top_stacks output.")]
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
        return DiskIoStackAnalysis.CallerCallee(
            trace, function, top, pid, window.StartUs, window.EndUs, _privacyLog.Writer,
            processStartUs,
            filterSpecified: pid.HasValue || processStartUs.HasValue || startUs.HasValue || endUs.HasValue);
    }
}
