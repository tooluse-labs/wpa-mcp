using System.Text.Json;
using WpaMcp.Analyzers;
using WpaMcp.Core;
using WpaMcp.Tools;

namespace WpaMcp.Cli;

// Test/debug CLI surface. NOT a stable public API — the MCP stdio transport is the
// canonical way clients invoke these tools. The CLI exists for:
//   1. End-to-end validation runs without spinning up an MCP client (see
//      tests/manual/diagnose_slow_startup_validation.md, perfview_compare.md)
//   2. CI smoke tests that exercise the analyzer code path
//   3. Quick local debugging on a real .etl
//
// Most verbs here correspond to a single MCP tool method; debug probes are allowed
// when they are explicitly for validating the analyzers or fixture capture pipeline.
// If you find yourself adding interactive prompts, multi-step wizards, or stable user
// workflows here, that work probably belongs in the MCP layer instead.
public static class CliRunner
{
    private static readonly Dictionary<string, Func<string[], int>> Verbs = new(StringComparer.Ordinal)
    {
        ["--help"] = _ => PrintHelp(toError: false),
        ["-h"] = _ => PrintHelp(toError: false),
        ["--list-processes"] = RunListProcesses,
        ["--process-create-timing"] = RunProcessCreateTiming,
        ["--cpu-top"] = RunCpuTop,
        ["--cpu-caller-callee"] = RunCpuCallerCallee,
        ["--wait-analysis"] = RunWaitAnalysis,
        ["--wait-top-stacks"] = RunWaitTopStacks,
        ["--wait-caller-callee"] = RunWaitCallerCallee,
        ["--image-load-caller-callee"] = RunImageLoadCallerCallee,
        ["--hard-fault-caller-callee"] = RunHardFaultCallerCallee,
        ["--file-io-caller-callee"] = RunFileIoCallerCallee,
        ["--image-load-timing"] = RunImageLoadTiming,
        ["--image-load-top-stacks"] = RunImageLoadTopStacks,
        ["--image-load-top-gaps"] = RunImageLoadTopGaps,
        ["--hard-fault-by-file"] = RunHardFaultByFile,
        ["--hard-fault-top-stacks"] = RunHardFaultTopStacks,
        ["--memory-resource-analysis"] = RunMemoryResourceAnalysis,
        ["--file-io-top-stacks"] = RunFileIoTopStacks,
        ["--disk-io-top-stacks"] = RunDiskIoTopStacks,
        ["--interrupt-top-stacks"] = RunInterruptTopStacks,
        ["--disk-io-caller-callee"] = RunDiskIoCallerCallee,
        ["--diagnose-slow-startup"] = RunDiagnoseSlowStartup,
        ["--security-scan-analysis"] = RunSecurityScanAnalysis,
        ["--find-marker"] = RunFindMarker,
        ["--probe-stacks"] = RunProbeStacks,
    };

    public static int Run(string[] args)
    {
        if (args.Length == 0) return PrintHelp(toError: false);

        if (!Verbs.TryGetValue(args[0], out var handler))
            return PrintHelp(toError: true);

        try
        {
            return handler(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
    }

    private static int RunListProcesses(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: --list-processes <trace.etl> [orderBy]"); return 2; }
        var orderBy = args.Length >= 3 ? args[2] : "cpu";
        var meta = new MetaTools(new TraceCache(capacity: 1));
        Emit(meta.ListProcesses(args[1], orderBy: orderBy));
        return 0;
    }

    private static int RunProcessCreateTiming(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --process-create-timing <trace.etl> <parentPid> [top]");
            return 2;
        }
        var parentPid = int.Parse(args[2]);
        var top = args.Length >= 4 ? int.Parse(args[3]) : 100;
        var meta = new MetaTools(new TraceCache(capacity: 1));
        Emit(meta.ProcessCreateTiming(args[1], parentPid: parentPid, top: top));
        return 0;
    }

    private static int RunCpuTop(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: --cpu-top <trace.etl> [pid] [top]"); return 2; }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var tools = new CpuTools(new TraceCache(capacity: 1));
        Emit(tools.CpuTopFunctions(args[1], top: top, pid: pid));
        return 0;
    }

    private static int RunCpuCallerCallee(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --cpu-caller-callee <trace.etl> <function> [pid] [top]");
            return 2;
        }
        var function = args[2];
        int? pid = args.Length >= 4 ? int.Parse(args[3]) : (int?)null;
        var top = args.Length >= 5 ? int.Parse(args[4]) : 20;
        var tools = new CpuTools(new TraceCache(capacity: 1));
        Emit(tools.CpuCallerCallee(args[1], function: function, top: top, pid: pid));
        return 0;
    }

    private static int RunWaitAnalysis(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: --wait-analysis <trace.etl> [pid] [top]"); return 2; }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var tools = new WaitTools(new TraceCache(capacity: 1));
        Emit(tools.WaitAnalysis(args[1], top: top, pid: pid));
        return 0;
    }

    private static int RunWaitTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --wait-top-stacks <trace.etl> [pid] [top] [startUs] [endUs]");
            return 2;
        }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        long? startUs = args.Length >= 5 ? long.Parse(args[4]) : (long?)null;
        long? endUs = args.Length >= 6 ? long.Parse(args[5]) : (long?)null;
        var tools = new WaitTools(new TraceCache(capacity: 1));
        Emit(tools.WaitTopStacks(args[1], top: top, pid: pid, startUs: startUs, endUs: endUs));
        return 0;
    }

    private static int RunImageLoadTiming(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: --image-load-timing <trace.etl> <pid> [top]"); return 2; }
        var pid = int.Parse(args[2]);
        var top = args.Length >= 4 ? int.Parse(args[3]) : 100;
        var tools = new ImageLoadTools(new TraceCache(capacity: 1));
        Emit(tools.ImageLoadTiming(args[1], pid: pid, top: top));
        return 0;
    }

    private static int RunImageLoadTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --image-load-top-stacks <trace.etl> [pid] [top] [whenBuckets]");
            return 2;
        }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var whenBuckets = args.Length >= 5 ? int.Parse(args[4]) : 0;
        var tools = new ImageLoadTools(new TraceCache(capacity: 1));
        Emit(tools.ImageLoadTopStacks(args[1], top: top, pid: pid, whenBuckets: whenBuckets));
        return 0;
    }

    private static int RunImageLoadTopGaps(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --image-load-top-gaps <trace.etl> <pid> [top]");
            return 2;
        }
        var pid = int.Parse(args[2]);
        var top = args.Length >= 4 ? int.Parse(args[3]) : 20;
        var tools = new ImageLoadTools(new TraceCache(capacity: 1));
        Emit(tools.ImageLoadTopGaps(args[1], pid: pid, top: top));
        return 0;
    }

    private static int RunHardFaultTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --hard-fault-top-stacks <trace.etl> [pid] [top] [whenBuckets]");
            return 2;
        }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var whenBuckets = args.Length >= 5 ? int.Parse(args[4]) : 0;
        var tools = new HardFaultTools(new TraceCache(capacity: 1));
        Emit(tools.HardFaultTopStacks(args[1], top: top, pid: pid, whenBuckets: whenBuckets));
        return 0;
    }

    private static int RunHardFaultByFile(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --hard-fault-by-file <trace.etl> [top=50] [pid|-] [startUs|-] [endUs|-] [orderBy=bytes|count|max_latency]");
            return 2;
        }

        var top = args.Length >= 3 ? int.Parse(args[2]) : 50;
        int? pid = HasOptionalArg(args, 3) ? int.Parse(args[3]) : null;
        long? startUs = HasOptionalArg(args, 4) ? long.Parse(args[4]) : null;
        long? endUs = HasOptionalArg(args, 5) ? long.Parse(args[5]) : null;
        var orderBy = HasOptionalArg(args, 6) ? args[6] : "bytes";
        var tools = new HardFaultTools(new TraceCache(capacity: 1));
        Emit(tools.HardFaultByFile(args[1], top: top, pid: pid, startUs: startUs, endUs: endUs, orderBy: orderBy));
        return 0;
    }

    private static int RunMemoryResourceAnalysis(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --memory-resource-analysis <trace.etl> [top=50] [pid|-] [startUs|-] [endUs|-]");
            return 2;
        }

        var top = args.Length >= 3 ? int.Parse(args[2]) : 50;
        int? pid = HasOptionalArg(args, 3) ? int.Parse(args[3]) : null;
        long? startUs = HasOptionalArg(args, 4) ? long.Parse(args[4]) : null;
        long? endUs = HasOptionalArg(args, 5) ? long.Parse(args[5]) : null;
        var tools = new VirtualMemoryTools(new TraceCache(capacity: 1));
        Emit(tools.MemoryResourceAnalysis(args[1], top: top, pid: pid, startUs: startUs, endUs: endUs));
        return 0;
    }

    private static int RunFileIoTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --file-io-top-stacks <trace.etl> [pid] [top] [whenBuckets]");
            return 2;
        }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var whenBuckets = args.Length >= 5 ? int.Parse(args[4]) : 0;
        var tools = new IoTools(new TraceCache(capacity: 1));
        Emit(tools.FileIoTopStacks(args[1], top: top, pid: pid, whenBuckets: whenBuckets));
        return 0;
    }

    // Caller/callee drill-down helpers — same arg shape across all 4 stack sources, just
    // different tool wiring. Each requires <function> as the focus frame name.
    private static int RunWaitCallerCallee(string[] args) =>
        RunCallerCalleeVerb(args, "--wait-caller-callee", (path, fn, pid, top) =>
            new WaitTools(new TraceCache(capacity: 1)).WaitCallerCallee(path, fn, top, pid));

    private static int RunImageLoadCallerCallee(string[] args) =>
        RunCallerCalleeVerb(args, "--image-load-caller-callee", (path, fn, pid, top) =>
            new ImageLoadTools(new TraceCache(capacity: 1)).ImageLoadCallerCallee(path, fn, top, pid));

    private static int RunHardFaultCallerCallee(string[] args) =>
        RunCallerCalleeVerb(args, "--hard-fault-caller-callee", (path, fn, pid, top) =>
            new HardFaultTools(new TraceCache(capacity: 1)).HardFaultCallerCallee(path, fn, top, pid));

    private static int RunFileIoCallerCallee(string[] args) =>
        RunCallerCalleeVerb(args, "--file-io-caller-callee", (path, fn, pid, top) =>
            new IoTools(new TraceCache(capacity: 1)).FileIoCallerCallee(path, fn, top, pid));

    private static int RunDiskIoTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --disk-io-top-stacks <trace.etl> [pid] [top] [whenBuckets]");
            return 2;
        }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var whenBuckets = args.Length >= 5 ? int.Parse(args[4]) : 0;
        var tools = new IoTools(new TraceCache(capacity: 1));
        Emit(tools.DiskIoTopStacks(args[1], top: top, pid: pid, whenBuckets: whenBuckets));
        return 0;
    }

    private static int RunInterruptTopStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --interrupt-top-stacks <trace.etl> [top] [startUs] [endUs]");
            return 2;
        }

        var top = args.Length >= 3 ? int.Parse(args[2]) : 30;
        long? startUs = args.Length >= 4 ? long.Parse(args[3]) : (long?)null;
        long? endUs = args.Length >= 5 ? long.Parse(args[4]) : (long?)null;
        var tools = new InterruptTools(new TraceCache(capacity: 1));
        Emit(tools.InterruptTopStacks(args[1], top: top, startUs: startUs, endUs: endUs));
        return 0;
    }

    private static int RunDiskIoCallerCallee(string[] args) =>
        RunCallerCalleeVerb(args, "--disk-io-caller-callee", (path, fn, pid, top) =>
            new IoTools(new TraceCache(capacity: 1)).DiskIoCallerCallee(path, fn, top, pid));

    private static int RunCallerCalleeVerb(
        string[] args, string verb,
        Func<string, string, int?, int, WpaMcp.Output.CallerCalleeResponse> invoke)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine($"usage: {verb} <trace.etl> <function> [pid] [top]");
            return 2;
        }
        var function = args[2];
        int? pid = args.Length >= 4 ? int.Parse(args[3]) : (int?)null;
        var top = args.Length >= 5 ? int.Parse(args[4]) : 20;
        Emit(invoke(args[1], function, pid, top));
        return 0;
    }

    private static int RunDiagnoseSlowStartup(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --diagnose-slow-startup <trace.etl> [nameSubstring] [minWaitRatio]");
            return 2;
        }
        var nameSubstring = args.Length >= 3 ? args[2] : null;
        var minWaitRatio = args.Length >= 4 ? double.Parse(args[3]) : 3.0;
        var tools = new DiagnoseTools(new TraceCache(capacity: 1));
        Emit(tools.DiagnoseSlowStartup(
            args[1],
            nameSubstring: nameSubstring,
            maxCandidates: 5,
            minWaitRatio: minWaitRatio,
            startupWindowUs: 5_000_000,
            topImageLoads: 20,
            topCpu: 15));
        return 0;
    }

    private static int RunSecurityScanAnalysis(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --security-scan-analysis <trace.etl> [top] [pid] [startUs] [endUs] [processSubstring] [pathSubstring] [providerSubstring]");
            return 2;
        }

        var top = args.Length >= 3 ? int.Parse(args[2]) : 50;
        int? pid = HasOptionalArg(args, 3) ? int.Parse(args[3]) : null;
        long? startUs = HasOptionalArg(args, 4) ? long.Parse(args[4]) : null;
        long? endUs = HasOptionalArg(args, 5) ? long.Parse(args[5]) : null;
        var processSubstring = HasOptionalArg(args, 6) ? args[6] : null;
        var pathSubstring = HasOptionalArg(args, 7) ? args[7] : null;
        var providerSubstring = HasOptionalArg(args, 8) ? args[8] : null;

        var tools = new SecurityTools(new TraceCache(capacity: 1));
        Emit(tools.SecurityScanAnalysis(
            args[1],
            top: top,
            pid: pid,
            startUs: startUs,
            endUs: endUs,
            processSubstring: processSubstring,
            pathSubstring: pathSubstring,
            providerSubstring: providerSubstring));
        return 0;
    }

    private static int RunFindMarker(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: --find-marker <trace.etl> <substring> [mode] [top]");
            return 2;
        }
        var mode = args.Length >= 4 ? args[3] : "count_by_event";
        var top = args.Length >= 5 ? int.Parse(args[4]) : 50;
        var tools = new MarkerTools(new TraceCache(capacity: 1));
        Emit(tools.FindMarker(args[1], args[2], top: top, mode: mode));
        return 0;
    }

    private static int RunProbeStacks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: --probe-stacks <trace.etl>");
            return 2;
        }

        var cache = new TraceCache(capacity: 1);
        var trace = cache.Get(args[1]);
        Emit(StackProbeAnalysis.Analyze(trace, args[1]));
        return 0;
    }

    private static void Emit<T>(T value)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(value, opts));
    }

    private static int PrintHelp(bool toError)
    {
        var w = toError ? Console.Error : Console.Out;
        w.WriteLine("WpaMcp CLI (test/debug only — use the MCP stdio server in production).");
        w.WriteLine();
        w.WriteLine("Usage: WpaMcp.dll <verb> [args]");
        w.WriteLine();
        w.WriteLine("Verbs:");
        w.WriteLine("  --list-processes        <trace.etl> [orderBy=cpu|wall|wait_ratio]");
        w.WriteLine("  --process-create-timing <trace.etl> <parentPid> [top=100]");
        w.WriteLine("  --cpu-top               <trace.etl> [pid] [top=30]");
        w.WriteLine("  --cpu-caller-callee     <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --wait-analysis         <trace.etl> [pid] [top=30]");
        w.WriteLine("  --wait-top-stacks       <trace.etl> [pid] [top=30] [startUs] [endUs]");
        w.WriteLine("  --image-load-timing     <trace.etl> <pid> [top=100]");
        w.WriteLine("  --image-load-top-stacks <trace.etl> [pid] [top=30] [whenBuckets=0]");
        w.WriteLine("  --image-load-top-gaps   <trace.etl> <pid> [top=20]");
        w.WriteLine("  --hard-fault-by-file    <trace.etl> [top=50] [pid|-] [startUs|-] [endUs|-] [orderBy=bytes|count|max_latency]");
        w.WriteLine("  --hard-fault-top-stacks <trace.etl> [pid] [top=30] [whenBuckets=0]");
        w.WriteLine("  --memory-resource-analysis <trace.etl> [top=50] [pid|-] [startUs|-] [endUs|-]");
        w.WriteLine("  --file-io-top-stacks    <trace.etl> [pid] [top=30] [whenBuckets=0]");
        w.WriteLine("  --disk-io-top-stacks    <trace.etl> [pid] [top=30] [whenBuckets=0]");
        w.WriteLine("  --interrupt-top-stacks  <trace.etl> [top=30] [startUs] [endUs]");
        w.WriteLine("  --disk-io-caller-callee <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --wait-caller-callee    <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --image-load-caller-callee <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --hard-fault-caller-callee <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --file-io-caller-callee <trace.etl> <function> [pid] [top=20]");
        w.WriteLine("  --diagnose-slow-startup <trace.etl> [nameSubstring] [minWaitRatio=3.0]");
        w.WriteLine("  --security-scan-analysis <trace.etl> [top=50] [pid|-] [startUs|-] [endUs|-] [processSubstring|-] [pathSubstring|-] [providerSubstring|-]");
        w.WriteLine("  --find-marker           <trace.etl> <substring> [mode=count_by_event|count_by_process|rows] [top=50]");
        w.WriteLine("  --probe-stacks          <trace.etl> (debug: explicit StackWalk vs event CallStackIndex)");
        w.WriteLine();
        w.WriteLine("All verbs emit JSON to stdout, log progress to stderr, and exit 0 on success.");
        w.WriteLine("Run with no args (or no recognized verb) to see this help. --version for build info.");
        return toError ? 2 : 0;
    }

    /// <summary>
    /// Returns true iff the first arg is a CLI verb prefix ("--" + recognized name) — so
    /// Program.Main can dispatch to the CLI instead of starting the MCP stdio host.
    /// </summary>
    public static bool IsCliInvocation(string[] args)
        => args.Length > 0 && Verbs.ContainsKey(args[0]);

    private static bool HasOptionalArg(string[] args, int index) =>
        args.Length > index && !string.IsNullOrWhiteSpace(args[index]) && args[index] != "-";
}
