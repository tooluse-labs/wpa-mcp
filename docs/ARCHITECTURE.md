# Architecture

```
Claude Code / Codex / Cursor (any MCP client)
        │ stdio JSON-RPC (MCP)
WpaMcp.Server (single .NET 10 console)
  ├── Program.cs                — Hosting + DI + stdio transport
  ├── Tools/*Tools.cs           — [McpServerTool] entry points
  ├── Analyzers/*.cs            — TraceEvent-based analysis logic
  ├── Core/{LruCache,TraceCache,SymbolService}.cs
  └── Output/{Records,Warnings}.cs   — JSON DTOs

Microsoft.Diagnostics.Tracing.TraceEvent (NuGet)
  ├── TraceLog (.etl → .etlx index, mmap'd .etlx)
  ├── KernelTraceEventParser (FileIO, PageFault, ...)
  └── SymbolReader (PDB resolution via dbghelp)
```

Single csproj for PoC. Module split (`WpaMcp.Analyzers`, `WpaMcp.Core`) is deferred to Phase 1 when team contributors join.

## Trace lifecycle

1. `load_trace(path)` → `TraceCache.Get(path)` → first call: `TraceLog.OpenOrConvert` (slow); subsequent: cached (fast).
2. Cache evicts on LRU + mtime change. Each `TraceLog` holds 200 MB-1.5 GB of mmap'd `.etlx`.
3. `TraceCache.Unload(path)` exists internally; a future MCP `unload_trace(path)` should expose it so clients can free memory mid-session.

## Symbol resolution

See `SYMBOL_RECIPES.md`.
