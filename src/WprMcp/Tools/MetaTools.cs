using System.ComponentModel;
using ModelContextProtocol.Server;
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
        ("Chromium / Edge / Quark / Electron",
         "https://chromium-browser-symsrv.commondatastorage.googleapis.com",
         new[] { "chrome", "chromium", "msedge", "quark", "electron", "cef", "uc_crash" }),

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

        return new LoadTraceResponse(meta, new SymbolStatus(ntPath, cacheDir, warning, recommendations));
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
        "PID 0 (Idle) and PID 4 (System) hidden by default — pass includeSystem=true to surface them.")]
    public ProcessListResponse ListProcesses(
        [Description("Absolute path to .etl file")] string path,
        [Description("Sort order: 'cpu' (default), 'wall', or 'wait_ratio'")] string orderBy = "cpu",
        [Description("Include PID 0 (Idle) and PID 4 (System); default false")] bool includeSystem = false)
    {
        var trace = _cache.Get(path);
        var hidden = 0;
        var rows = new List<ProcessRow>();
        foreach (var p in trace.Processes)
        {
            if (!includeSystem && (p.ProcessID == 0 || p.ProcessID == 4))
            {
                hidden++;
                continue;
            }

            var startUs = (long)(p.StartTimeRelativeMsec * 1000);
            var endUs = (long)(p.EndTimeRelativeMsec * 1000);
            var wallUs = Math.Max(0, endUs - startUs);
            var cpuUs = (long)(p.CPUMSec * 1000);
            // PerfView convention: ratio undefined for processes that never ran on CPU
            // (data noise — short-lived processes whose threads were never scheduled
            // during the trace window). Surface as null rather than +inf to keep JSON sane.
            double? ratio = cpuUs > 0 ? (double)wallUs / cpuUs : (double?)null;

            rows.Add(new ProcessRow(
                Pid: p.ProcessID,
                ParentPid: p.ParentID,
                Name: p.Name ?? string.Empty,
                StartUs: startUs,
                EndUs: endUs,
                WallUs: wallUs,
                CpuUs: cpuUs,
                WaitRatio: ratio,
                ImageLoadCount: p.LoadedModules.Count()));
        }

        rows = (orderBy?.ToLowerInvariant()) switch
        {
            "wall" => rows.OrderByDescending(r => r.WallUs).ToList(),
            "wait_ratio" => rows
                // null ratios sort to the end; tie-break by WallUs so we don't bury slow-but-zero-CPU processes
                .OrderByDescending(r => r.WaitRatio ?? double.NegativeInfinity)
                .ThenByDescending(r => r.WallUs)
                .ToList(),
            _ => rows.OrderByDescending(r => r.CpuUs).ToList(),
        };

        return new ProcessListResponse(rows, hidden);
    }
}
