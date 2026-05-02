using System.Text.Json;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Tools;

namespace WprMcp.Cli;

// Test/debug CLI surface. NOT a stable public API — the MCP stdio transport is the
// canonical way clients invoke these tools. The CLI exists for:
//   1. End-to-end validation runs without spinning up an MCP client (see
//      tests/manual/diagnose_slow_startup_validation.md, perfview_compare.md)
//   2. CI smoke tests that exercise the analyzer code path
//   3. Quick local debugging on a real .etl
//
// Every verb here corresponds to a single MCP tool method; the JSON output is the
// MCP response shape verbatim. If you find yourself adding non-tool features here
// (interactive prompts, multi-step wizards, etc.), that work probably belongs in
// the MCP layer instead.
public static class CliRunner
{
    public static int Run(string[] args)
    {
        if (args.Length == 0) return PrintHelp(toError: false);

        var verb = args[0];

        try
        {
            return verb switch
            {
                "--help" or "-h" => PrintHelp(toError: false),
                "--list-processes" => RunListProcesses(args),
                "--cpu-top" => RunCpuTop(args),
                "--wait-analysis" => RunWaitAnalysis(args),
                "--image-load-timing" => RunImageLoadTiming(args),
                "--diagnose-slow-startup" => RunDiagnoseSlowStartup(args),
                "--find-marker" => RunFindMarker(args),
                _ => PrintHelp(toError: true),
            };
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

    private static int RunCpuTop(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: --cpu-top <trace.etl> [pid] [top]"); return 2; }
        int? pid = args.Length >= 3 ? int.Parse(args[2]) : (int?)null;
        var top = args.Length >= 4 ? int.Parse(args[3]) : 30;
        var tools = new CpuTools(new TraceCache(capacity: 1));
        Emit(tools.CpuTopFunctions(args[1], top: top, pid: pid));
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

    private static int RunImageLoadTiming(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: --image-load-timing <trace.etl> <pid> [top]"); return 2; }
        var pid = int.Parse(args[2]);
        var top = args.Length >= 4 ? int.Parse(args[3]) : 100;
        var tools = new ImageLoadTools(new TraceCache(capacity: 1));
        Emit(tools.ImageLoadTiming(args[1], pid: pid, top: top));
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

    private static void Emit<T>(T value)
    {
        var opts = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine(JsonSerializer.Serialize(value, opts));
    }

    private static int PrintHelp(bool toError)
    {
        var w = toError ? Console.Error : Console.Out;
        w.WriteLine("WprMcp CLI (test/debug only — use the MCP stdio server in production).");
        w.WriteLine();
        w.WriteLine("Usage: WprMcp.dll <verb> [args]");
        w.WriteLine();
        w.WriteLine("Verbs:");
        w.WriteLine("  --list-processes        <trace.etl> [orderBy=cpu|wall|wait_ratio]");
        w.WriteLine("  --cpu-top               <trace.etl> [pid] [top=30]");
        w.WriteLine("  --wait-analysis         <trace.etl> [pid] [top=30]");
        w.WriteLine("  --image-load-timing     <trace.etl> <pid> [top=100]");
        w.WriteLine("  --diagnose-slow-startup <trace.etl> [nameSubstring] [minWaitRatio=3.0]");
        w.WriteLine("  --find-marker           <trace.etl> <substring> [mode=count_by_event|count_by_process|rows] [top=50]");
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
    {
        if (args.Length == 0) return false;
        var v = args[0];
        return v == "--help" || v == "-h"
            || v == "--list-processes"
            || v == "--cpu-top"
            || v == "--wait-analysis"
            || v == "--image-load-timing"
            || v == "--diagnose-slow-startup"
            || v == "--find-marker";
    }
}
