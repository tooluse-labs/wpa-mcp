using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Core;
using WprMcp.Output;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private readonly SymbolService _symbols;
    private readonly TraceCache _cache;
    public SymbolTools(SymbolService symbols, TraceCache cache)
    {
        _symbols = symbols;
        _cache = cache;
    }

    [McpServerTool, Description(
        "Sets _NT_SYMBOL_PATH for symbol resolution. Call before tools that resolve stacks.")]
    public string SetSymbolPath(
        [Description("New path (e.g. 'SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols')")]
        string path,
        [Description("Append to existing path instead of replacing (default true)")]
        bool append = true)
    {
        _symbols.SetPath(path, append);
        return _symbols.CurrentPath ?? "";
    }

    [McpServerTool, Description(
        "Appends a symbol server URL with optional local cache. Cache defaults to %LocalAppData%\\WprMcp\\Symbols.")]
    public string AddSymbolServer(
        [Description("Symbol server URL (e.g. https://msdl.microsoft.com/download/symbols)")]
        string url,
        [Description("Local cache directory (optional)")] string? cacheDir = null)
    {
        _symbols.AddServer(url, cacheDir);
        return _symbols.CurrentPath ?? "";
    }

    [McpServerTool, Description(
        "Reports per-module symbol status for a loaded trace and suggests fixes for unresolved modules.")]
    public DiagnoseSymbolsResponse DiagnoseSymbols(
        [Description("Absolute path to .etl file")] string path)
    {
        var trace = _cache.Get(path);
        var rows = new List<ModuleSymbolStatus>();
        var suggestions = new List<string>();
        var path0 = _symbols.CurrentPath;

        // Walk modules listed in the trace, mark which have PDB indices loaded.
        foreach (var module in trace.ModuleFiles)
        {
            var resolved = !string.IsNullOrEmpty(module.PdbName);
            var hint = resolved
                ? "PDB resolved."
                : module.FilePath.Contains("Microsoft", StringComparison.OrdinalIgnoreCase)
                    ? "Add Microsoft symbol server: add_symbol_server('https://msdl.microsoft.com/download/symbols')"
                    : module.FilePath.Contains("chrome", StringComparison.OrdinalIgnoreCase)
                      || module.FilePath.Contains("quark", StringComparison.OrdinalIgnoreCase)
                        ? "Add Chromium symbol server: add_symbol_server('https://chromium-browser-symsrv.commondatastorage.googleapis.com')"
                        : "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";
            rows.Add(new ModuleSymbolStatus(module.Name, 0, resolved, hint));
        }

        if (rows.Any(r => !r.Resolved))
        {
            suggestions.Add(
                "After updating symbols, re-run cpu_top_functions to verify resolution_rate improved.");
        }

        return new DiagnoseSymbolsResponse(
            CurrentSymbolPath: path0 ?? "<unset>",
            CacheDir: _symbols.DefaultCacheDir,
            Modules: rows.OrderByDescending(r => r.Resolved ? 0 : 1).Take(50).ToList(),
            Suggestions: suggestions);
    }
}
