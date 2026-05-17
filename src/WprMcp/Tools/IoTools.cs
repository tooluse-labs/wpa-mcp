using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class IoTools
{
    private readonly TraceCache _cache;
    public IoTools(TraceCache cache) => _cache = cache;

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = false, Destructive = false), Description(
        "Top N files by total IO bytes (read + write). No startUs/endUs: this is a whole-trace " +
        "by-file summary; use file_io_top_stacks for windowed attribution.")]
    public FileIoResponse FileIoTopFiles(
        [Description("Absolute path to .etl file")] string path,
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Filter to a single process ID")] int? pid = null)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        return FileIoAnalysis.TopFiles(trace, top, pid);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by file-IO bytes — answers 'which call chain is doing all " +
        "the file IO'. PerfView equivalent: 'File I/O Stacks' view. Pairs with file_io_top_files " +
        "(per-file bucket); this one is per-stack so you can tell streaming-of-one-big-file apart " +
        "from open-read-close-of-thousands-of-small-files. Each row carries both Bytes (metric=IoSize) " +
        "and OpCount. Requires the FileIO keyword in the capture profile.")]
    public FileIoStacksResponse FileIoTopStacks(
        [Description("Absolute path to .etl file")] string path,
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
        bool resolveSymbols = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return FileIoStackAnalysis.TopIoStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the file-IO-stack data. Metric is " +
        "IO bytes (read+write); top-N callers ranked by inclusive bytes flowing INTO focus, " +
        "callees by bytes OUT. PerfView equivalent: 'Callers' / 'Callees' tabs of File I/O Stacks.")]
    public CallerCalleeResponse FileIoCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in file_io_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return FileIoStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Top-N call stacks ranked by PHYSICAL disk-IO bytes — answers 'which call chain " +
        "actually hit the disk'. Different layer from file_io_top_stacks: file IO captures " +
        "all syscalls (cache-served included), disk IO only events that hit physical media. " +
        "Diff the two to identify cache-served reads. PerfView equivalent: 'Disk I/O Stacks' " +
        "view. Requires the DiskIO keyword in the capture profile.")]
    public DiskIoStacksResponse DiskIoTopStacks(
        [Description("Absolute path to .etl file")] string path,
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
        bool resolveSymbols = false)
    {
        Validation.RequireTop(top);
        Validation.RequireWhenBuckets(whenBuckets);
        var trace = _cache.Get(path);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return DiskIoStackAnalysis.TopIoStacks(
            trace, StackResponseOptions.EffectiveTop(top, compactStacks, summaryOnly), pid, startUs, endUs, symbolLog: Console.Error, whenBuckets: whenBuckets);
    }

    [McpServerTool(ReadOnly = true, Idempotent = true, OpenWorld = true, Destructive = false), Description(
        "Caller/callee drill-down for a focus function in the disk-IO-stack data. Metric is " +
        "physical disk bytes (TransferSize); top-N callers ranked by inclusive disk bytes " +
        "flowing INTO focus, callees by bytes OUT.")]
    public CallerCalleeResponse DiskIoCallerCallee(
        [Description("Absolute path to .etl file")] string path,
        [Description("Focus frame name, exactly as it appears in disk_io_top_stacks output.")]
        string function,
        [Description("Top N callers / callees to return (default 20, max 1000)")] int top = 20,
        [Description("Filter to a single process ID")] int? pid = null,
        [Description("Window start in microseconds since trace start")] long? startUs = null,
        [Description("Window end in microseconds since trace start (exclusive)")] long? endUs = null,
        [Description(StackResponseOptions.ResolveSymbolsDescription)]
        bool resolveSymbols = false)
    {
        Validation.RequireTop(top);
        Validation.RequireFunctionName(function);
        var trace = _cache.Get(path);
        using var symbolResolution = StackResponseOptions.UseResolveSymbols(resolveSymbols);
        return DiskIoStackAnalysis.CallerCallee(
            trace, function, top, pid, startUs, endUs, Console.Error);
    }
}
