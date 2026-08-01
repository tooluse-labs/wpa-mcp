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

1. Each query holds `using var lease = TraceCache.Acquire(path)`. The first acquisition runs `TraceLog.OpenOrConvert` (slow); subsequent acquisitions reuse the cached trace.
2. Cache entries retire on LRU eviction, mtime change, explicit unload, or cache shutdown. Retirement never invalidates an active query; the final lease release disposes the retired `TraceLog`.
3. Each resident `TraceLog` holds 200 MB-1.5 GB of mmap'd `.etlx`. `TraceCache.Unload(path)` exists internally; a future MCP `unload_trace(path)` may expose explicit retirement to clients.

## Symbol resolution

See `SYMBOL_RECIPES.md`.

Read-only analyzers use a query-local effective symbol-path snapshot. The ETL directory is added to that reader only; it is never written into `_NT_SYMBOL_PATH`. PDB identity/local readiness and observed frame-name resolution are separate contracts: orientation can report the former, while only a stack query that executed lookup can measure the latter.

## Analysis truthfulness contracts

- Process identity is `(Pid, ProcessStartUs)`, not PID alone. `ProcessAnalysisScope` makes reused-PID aggregation explicit and exact selection stable across analyzers. Thread-sensitive CPU/Wait paths additionally resolve `(Tid, ThreadStartUs/Generation)` through `ThreadAnalysisScope`; missing or ambiguous selectors are structured results with replayable candidates, never PID-only fallback.
- Capability flags mean a supported event class was observed in the ETLX-materialized `TraceLog`; absence does not prove which capture keyword was disabled.
- Stack capability is reported per target event domain with exact total/stacked counts, a coverage state, and `StackSemantics`. The global `HasStackWalks` union is compatibility information only. Scheduler coverage used by wait analysis measures the switch-out `BlockingStack`; it is intentionally distinct from the ordinary CSwitch event `CallStackIndex` exposed by the debug probe.
- Empty results carry scope, capability, matched-event, no-data-reason, and warning fields so callers can distinguish missing scope, absent event class, no events in the selected interval, and unavailable stacks. `CapabilityStatus=observed` requires matching evidence in the resolved requested scope; trace-wide absence is required for `not_observed`; uncertain filtered cases remain `unknown`.
- `TraceMeta.EventCount` and provider counts are materialized logical TraceEvent counts. Raw ETW record count and parser coverage remain not measured unless an independent raw counter is available; comparing external raw counts to materialized counts does not prove parser loss.
