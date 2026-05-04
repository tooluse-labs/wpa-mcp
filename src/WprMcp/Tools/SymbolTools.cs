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
        "Sets the entire _NT_SYMBOL_PATH for symbol resolution in the running server (replaces " +
        "or appends).  Use this when you want to drop in a curated path string (multiple " +
        "servers + caches separated by `;`); for incremental setup of one server at a time, " +
        "prefer add_symbol_server.  PerfView equivalent: File → Set Symbol Path… dialog.  " +
        "Affects all subsequent stack-resolving tool calls until the server restarts or this " +
        "is called again.  Returns the resulting path so callers can verify what was applied.")]
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
        "Appends a symbol server URL (with optional local cache directory) to the existing " +
        "_NT_SYMBOL_PATH.  Cache defaults to `%LocalAppData%\\WprMcp\\Symbols`.  Use this for " +
        "incremental setup ('add msdl.microsoft.com, then Chromium's symbol server'); for a " +
        "full replacement string, use set_symbol_path.  PerfView equivalent: a single entry in " +
        "the File → Set Symbol Path dialog.  Idempotent — re-adding the same URL is a no-op.  " +
        "Returns the path actually in effect after the change.")]
    public string AddSymbolServer(
        [Description("Symbol server URL (e.g. https://msdl.microsoft.com/download/symbols)")]
        string url,
        [Description("Local cache directory (optional)")] string? cacheDir = null)
    {
        _symbols.AddServer(url, cacheDir);
        return _symbols.CurrentPath ?? "";
    }

    [McpServerTool, Description(
        "Per-module symbol-resolution status for an already-loaded trace, with auto-suggested " +
        "fixes for unresolved modules (which symbol server to add for which module — e.g., " +
        "msdl.microsoft.com for ntdll/kernelbase, Chromium symbol server for chrome.exe / cef.dll).  " +
        "The first sanity check to run when cpu_top_functions shows lots of `module!?` frames " +
        "or `Stats.ResolutionRate < 0.8`.  PerfView equivalent: Modules tab + Set Symbol Path " +
        "dialog (this tool composes both, plus auto-recommends which server to add per module).  " +
        "Returns top 50 modules sorted unresolved-first; if any are unresolved, includes a " +
        "'after fixing, re-run cpu_top_functions to verify' suggestion.")]
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
                : SuggestServerForModule(module.FilePath);
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

    // Per-module symbol-server suggestion. Same pattern table as MetaTools.SymbolServerHints
    // — keep them consistent so `load_trace` recommendations and `diagnose_symbols` hints
    // never disagree on which server to add for a given module.
    private static readonly (string ServerHint, string[] Patterns)[] ServerHints =
    {
        ("Add Microsoft symbol server: add_symbol_server('https://msdl.microsoft.com/download/symbols')",
         new[] { "Microsoft", "Windows" }),
        ("Add Chromium symbol server: add_symbol_server('https://chromium-browser-symsrv.commondatastorage.googleapis.com')",
         new[] { "chrome", "chromium", "msedge", "electron", "cef" }),
    };

    internal static string SuggestServerForModule(string filePath)
    {
        foreach (var (hint, patterns) in ServerHints)
            if (patterns.Any(p => filePath.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return hint;
        return "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";
    }
}
