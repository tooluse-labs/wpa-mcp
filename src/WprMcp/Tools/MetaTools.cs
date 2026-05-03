using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Analyzers;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class MetaTools
{
    private readonly TraceCache _cache;
    public MetaTools(TraceCache cache) => _cache = cache;

    // Pattern → recommended server URL mapping. Patterns are case-insensitive substrings on
    // module name (without .dll/.exe extension). Order matters: first match wins per module,
    // so put more-specific patterns before generic ones.
    private static readonly (string Reason, string Url, string[] Patterns)[] SymbolServerHints =
    {
        ("Chromium-based browser (Chrome / Edge / Electron / CEF / etc.)",
         "https://chromium-browser-symsrv.commondatastorage.googleapis.com",
         // Pattern list captures Chromium-family modules across vendors; matching is done
         // by substring against module name. Add additional browser-specific tokens here if
         // your trace uses a renamed Chromium fork that isn't picked up.
         new[] { "chrome", "chromium", "msedge", "electron", "cef" }),

        ("Microsoft public symbols",
         "https://msdl.microsoft.com/download/symbols",
         new[]
         {
             "ntoskrnl", "ntdll", "kernel32", "kernelbase", "win32k", "user32", "gdi32",
             "advapi32", "rpcrt4", "combase", "ole32", "oleaut32", "shell32", "shlwapi",
             "msvcrt", "ucrtbase", "vcruntime", "msvcp",
             "fltmgr", "mssecflt", "wdf01000", "wdfldr",
             "mpengine", "mpsvc",            // Windows Defender
             "msedgewebview2",
             "dxgi", "d3d11", "d3d12", "d2d1", "dwrite", "windows.ui", "wininet", "winhttp",
             "afd.sys", "netio.sys", "tcpip.sys", "http.sys",
             "win32u", "ntdll", "dwmapi", "dwmcore",
         }),
    };

    [McpServerTool, Description(
        "Loads (or returns cached) a Windows ETW .etl trace. First load can take 30s-3min; subsequent calls are instant. " +
        "Response includes symbol-server recommendations based on the modules referenced by the trace.")]
    public LoadTraceResponse LoadTrace(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var processes = trace.Processes;
        var meta = new TraceMeta(
            Path: path,
            DurationUs: (long)trace.SessionDuration.TotalMicroseconds,
            EventCount: trace.EventCount,
            EventsLost: trace.EventsLost,
            ProcessCount: processes.Count);

        var ntPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH");
        var cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WprMcp", "Symbols");
        var warning = string.IsNullOrEmpty(ntPath)
            ? "_NT_SYMBOL_PATH is not set. OS module frames will not resolve. " +
              "Call set_symbol_path or add_symbol_server, or configure env in MCP config."
            : null;

        var recommendations = BuildSymbolRecommendations(trace);
        var capabilities = _cache.GetCapabilities(path);

        return new LoadTraceResponse(
            meta,
            new SymbolStatus(ntPath, cacheDir, warning, recommendations),
            capabilities);
    }

    private static IReadOnlyList<SymbolRecommendation> BuildSymbolRecommendations(
        Microsoft.Diagnostics.Tracing.Etlx.TraceLog trace)
    {
        var hits = SymbolServerHints
            .Select(h => (h.Reason, h.Url, Modules: new SortedSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var module in trace.ModuleFiles)
        {
            // Already-resolved modules don't need a recommendation.
            if (!string.IsNullOrEmpty(module.PdbName)) continue;

            var name = module.Name ?? string.Empty;
            for (var i = 0; i < hits.Count; i++)
            {
                var patterns = SymbolServerHints[i].Patterns;
                if (patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    hits[i].Modules.Add(name);
                    break;
                }
            }
        }

        return hits
            .Where(h => h.Modules.Count > 0)
            .Select(h => new SymbolRecommendation(
                Reason: h.Reason,
                ServerUrl: h.Url,
                MatchedModuleCount: h.Modules.Count,
                SampleModules: h.Modules.Take(5).ToList()))
            .ToList();
    }

    [McpServerTool, Description(
        "Lists processes in the loaded trace. Default order is CPU time descending. " +
        "WaitRatio = WallUs/CpuUs surfaces 'high wall, low CPU' processes (blocked on minifilter, IPC, etc.). " +
        "PID 0 (Idle) and PID 4 (System) hidden by default — pass includeSystem=true to surface them. " +
        "When orderBy='wait_ratio', trace-resident processes (alive before trace start AND survived past " +
        "trace end) are pushed to the bottom because their ratio is denominator-saturated.")]
    public ProcessListResponse ListProcesses(
        [Description("Absolute path to .etl file")] string path,
        [Description("Sort order: 'cpu' (default), 'wall', or 'wait_ratio'")] string orderBy = "cpu",
        [Description("Top N rows (default 50, max 1000)")] int top = 50,
        [Description("Include PID 0 (Idle) and PID 4 (System); default false")] bool includeSystem = false)
    {
        Validation.RequireTop(top);
        var trace = _cache.Get(path);
        var rows = ProcessProjection.Rows(trace, includeSystem).ToList();
        var totalCount = rows.Count;
        var hidden = includeSystem
            ? 0
            : trace.Processes.Count(p => p.ProcessID == 0 || p.ProcessID == 4);

        rows = orderBy.ToLowerInvariant() switch
        {
            "cpu" => rows.OrderByDescending(r => r.CpuUs).ToList(),
            "wall" => rows.OrderByDescending(r => r.WallUs).ToList(),
            "wait_ratio" => rows
                .OrderByDescending(WaitRatioSortKey)
                .ThenByDescending(r => r.WallUs)
                .ToList(),
            _ => throw new ArgumentException(
                $"orderBy must be 'cpu', 'wall', or 'wait_ratio'; got '{orderBy}'", nameof(orderBy)),
        };

        rows = rows.Take(top).ToList();
        return new ProcessListResponse(rows, hidden, totalCount);
    }

    [McpServerTool, Description(
        "Per-fork timing for a parent process — given a PID, returns every child the kernel " +
        "reported as having that parent, with FirstImageLoadOffsetUs (the kernel-side window " +
        "between ProcessStart and the first DLL load: where AV / process-create callbacks " +
        "burn time invisibly to the child) and GapFromPreviousSpawnUs (lets you spot fork " +
        "bursts vs steady-state). Median/p95/max aggregates across kernel gaps surface " +
        "worst-case in a single number.")]
    public ProcessCreateTimingResponse ProcessCreateTiming(
        [Description("Absolute path to .etl file")] string path,
        [Description("Parent process ID — the process whose CreateProcess calls you want timed.")]
        int parentPid,
        [Description("Top N children by spawn order (default 50, max 1000). Children are " +
                     "sorted chronologically; 'top' caps response size on prolific spawners.")]
        int top = 50)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(parentPid);
        var trace = _cache.Get(path);
        return ProcessCreateTimingAnalysis.Analyze(trace, parentPid, top);
    }

    [McpServerTool, Description(
        "Per-process thread-lifecycle list — every ThreadStart / ThreadStop in chronological " +
        "order for one PID, with start time, end time, and lifetime in microseconds.  Useful " +
        "for 'did the thread pool spawn 200 threads in the startup window' / 'is something " +
        "thrashing thread creation'.  Threads still alive at trace end are flagged " +
        "TraceResidentEnd; threads alive when capture started are flagged TraceResidentStart " +
        "(their StartTimeUs is 0 = trace start, not the real spawn).  ConcurrentPeak gives " +
        "the maximum number of simultaneously-live threads for the PID.  Requires the Thread " +
        "keyword in the capture profile (in default kernel profiles).")]
    public ThreadLifetimeResponse ThreadLifetime(
        [Description("Absolute path to .etl file")] string path,
        [Description("Process ID")] int pid,
        [Description("Top N threads, ordered by start time (default 200, max 1000)")] int top = 200)
    {
        Validation.RequireTop(top);
        Validation.RequirePositivePid(pid);
        var trace = _cache.Get(path);
        return ThreadLifetimeAnalysis.Analyze(trace, pid, top);
    }

    // Trace-resident processes (alive across the whole trace) get ratios like 247638× from
    // wallUs ≈ trace duration / cpuUs ≈ 0 — pure noise. Sort them to the bottom alongside
    // null ratios so genuinely-blocked processes with bounded WallUs land at the top.
    private static double WaitRatioSortKey(ProcessRow r)
        => r.TraceResident ? double.NegativeInfinity : r.WaitRatio ?? double.NegativeInfinity;
}
