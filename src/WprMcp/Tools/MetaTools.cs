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

    [McpServerTool, Description(
        "Loads (or returns cached) a Windows ETW .etl trace. First load can take 30s-3min; subsequent calls are instant.")]
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

        return new LoadTraceResponse(meta, new SymbolStatus(ntPath, cacheDir, warning));
    }

    [McpServerTool, Description("Lists processes in the loaded trace, sorted by CPU time descending.")]
    public ProcessListResponse ListProcesses(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var rows = trace.Processes
            .Select(p => new ProcessRow(
                Pid: p.ProcessID,
                Name: p.Name ?? string.Empty,
                StartUs: (long)(p.StartTimeRelativeMsec * 1000),
                EndUs: (long)(p.EndTimeRelativeMsec * 1000),
                CpuUs: (long)(p.CPUMSec * 1000)))
            .OrderByDescending(r => r.CpuUs)
            .ToList();
        return new ProcessListResponse(rows);
    }
}
