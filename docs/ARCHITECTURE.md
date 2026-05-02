# Architecture

```
Claude Code / Codex / Cursor (any MCP client)
        │ stdio JSON-RPC (MCP)
WprMcp.Server (single .NET 8 console)
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

Single csproj for PoC. Module split (`WprMcp.Analyzers`, `WprMcp.Core`) is deferred to Phase 1 when team contributors join.

## Trace lifecycle

1. `load_trace(path)` → `TraceCache.Get(path)` → first call: `TraceLog.OpenOrConvert` (slow); subsequent: cached (fast).
2. Cache evicts on LRU + mtime change. Each `TraceLog` holds 200 MB-1.5 GB of mmap'd `.etlx`.
3. `unload_trace(path)` (future) frees memory mid-session.

## Symbol resolution

See `SYMBOL_RECIPES.md`.
