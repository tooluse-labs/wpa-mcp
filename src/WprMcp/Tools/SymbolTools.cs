using System.ComponentModel;
using ModelContextProtocol.Server;
using WprMcp.Core;

namespace WprMcp.Tools;

[McpServerToolType]
public sealed class SymbolTools
{
    private readonly SymbolService _symbols;
    public SymbolTools(SymbolService symbols) => _symbols = symbols;

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
}
