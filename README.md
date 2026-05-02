# wpa-mcp

C# MCP server for reading Windows ETW (`.etl`) traces. Answers common Chromium / Quark perf-debug questions through any MCP client (Claude Code, Codex, Cursor).

## Status

PoC. 6 main tools live: `load_trace`, `list_processes`, `cpu_top_functions`, `file_io_top_files`, `mmap_hot_files`, `find_marker`. Plus 3 symbol UX tools: `set_symbol_path`, `add_symbol_server`, `diagnose_symbols`.

Not for production. Internal use only until validated.

## Requirements

- Windows 10/11 (TraceEvent kernel APIs are Windows-only).
- .NET 8 SDK — `winget install Microsoft.DotNet.SDK.8`.
- For symbol resolution: `_NT_SYMBOL_PATH` set, or use the symbol tools at runtime.

## Build

```powershell
git clone <repo> wpa-mcp
cd wpa-mcp
dotnet build -c Release
```

## Use with Claude Code

Add to your MCP config (`%APPDATA%\Claude\claude_desktop_config.json` or per-project):

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "dotnet",
      "args": ["C:/Users/me/Dev/wpa-mcp/src/WprMcp/bin/Release/net8.0/WprMcp.dll"],
      "env": {
        "_NT_SYMBOL_PATH": "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com",
        "WPRMCP_CACHE_SIZE": "2"
      }
    }
  }
}
```

## Quickstart

```
> Load this trace: C:\path\to\crash.etl
(MCP calls load_trace; first call takes 30s-3min as .etlx is built)

> Show top 20 CPU hot functions in quark.exe
(MCP calls cpu_top_functions with pid filtered to quark.exe)

> Which mmap'd files had the most page-in load?
(MCP calls mmap_hot_files — answers the BUGFIX:81630508 class of questions)
```

## Symbol resolution

See `docs/SYMBOL_RECIPES.md`. If frame resolution is < 80%, run `diagnose_symbols` for actionable suggestions.

## Trace caching

LRU, default capacity 2 traces. Override with `WPRMCP_CACHE_SIZE=N`. First load builds `.etlx` (slow); cached calls are instant.

## Architecture

See `docs/ARCHITECTURE.md`.

## Capturing your own traces

See `docs/WPR_PROFILE.md` for a recommended `.wprp` that captures CPU + FileIO + MemoryHardFaults (required for `mmap_hot_files`).
