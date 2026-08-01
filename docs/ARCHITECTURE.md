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

1. Each query holds `using var lease = TraceCache.Acquire(path)`. The first acquisition runs `TraceLog.OpenOrConvert` (slow) and may create or refresh an adjacent `.etlx`; subsequent acquisitions reuse the cached trace.
2. Cache keys use `Path.GetFullPath`. Windows uses an ordinal-ignore-case comparer, so path-casing aliases converge on one entry and invalidate the same entry through `TraceCache.Unload`.
3. `FileStamp` combines last-write time, creation time, length, and—when available on Windows—volume serial plus file ID. A changed stamp, explicit unload, LRU eviction, or shutdown retires the entry. Retirement never invalidates an active query; the final lease release disposes the retired `TraceLog`. Failed native/mmap disposal remains in a central retry registry (or the LRU callback retry set) until a later `TraceCache.Dispose` succeeds.
4. The residual freshness boundary is an in-place rewrite that preserves the same file identity, length, and timestamps. Callers that perform such rewrites must invoke `unload_trace` before re-querying it. A raw-ETL refresh request lives only in the current server process; restarting alone neither preserves that request nor invalidates a newer stale ETLX, so call `unload_trace` again after restart and before loading the rewritten path.
5. Each resident `TraceLog` holds 200 MB-1.5 GB of mmap'd `.etlx`. MCP clients can call `unload_trace(path)` for explicit retirement; for a raw ETL, the next successful load attempts the requested adjacent-ETLX refresh while active leases remain valid. The response reports request registration, not proof that regeneration already occurred.

MCP risk annotations describe observable filesystem/process effects, not only logical ETL mutation. All tools are `ReadOnly=false` in 0.3.0 because cache misses can run `OpenOrConvert`, unload can retire resident state, symbol configuration is process-wide, and stack tools with `resolveSymbols=true` can download/write PDBs. Caller-supplied trace/cache paths may be UNC, mapped, or reparse-point targets, so raw-path tools are conservatively `OpenWorld=true`; only `set_symbol_path` is `OpenWorld=false`. `Destructive=true` conservatively covers adjacent-ETLX replacement/refresh, cache retirement, and symbol-path replacement; incremental `add_symbol_server` is the sole `Destructive=false` tool. All tools are idempotent except `set_symbol_path`. The original ETL event stream remains analytically read-only.

## Symbol resolution

See `SYMBOL_RECIPES.md`.

Analyzers use a query-local effective symbol-path snapshot. The ETL directory is added to that reader only; it is never written into `_NT_SYMBOL_PATH`. Symbol truth has four layers that must not be collapsed:

1. **PDB identity** — trace metadata contains a PDB name + GUID + age. This is a lookup key, not a successful lookup.
2. **Local candidate** — a file with the expected name/container was discovered. Bare roots contribute only `<root>\<pdbName>`; filesystem roots explicitly carried by `SRV`/`SYMSRV`/`CACHE` contribute only symbol-store layouts. Every candidate is evaluated before the response caps its displayed paths at 10; total/truncation fields preserve that fact, configured discovery order is retained, and exact matches are displayed first. Name, header, and directory placement alone do not prove GUID/age.
3. **Local readiness** — `diagnose_symbols` directly calls TraceEvent `OpenSymbolFile` on each discovered path, compares the PDB's actual GUID/age, and requires the format-appropriate reader to succeed. It uses an empty search path and does not actively access remote SRV/UNC entries or download symbols, although the OS may redirect a configured local-looking root through a mapped drive or reparse point. The aggregate state distinguishes exact match, mismatch, invalid data, and `candidate_identity_unverified`; ambiguous Windows candidate/DIA failures stay unverified instead of being labeled corrupt. Any exact candidate wins over invalid or unavailable siblings.
4. **Observed frame resolution** — a stack query executed lookup and measured names across the real code frames it reached. Synthetic frames are excluded; null means no eligible frame was measured.

## Analysis truthfulness contracts

- Process identity is `(Pid, ProcessStartUs)`, not PID alone. `ProcessAnalysisScope` makes reused-PID aggregation explicit and exact selection stable across analyzers. `pid_aggregate` retains candidate keys but tool-specific rows/totals may combine their evidence. Exact-only tools return structured `process_start_required` for clean PID reuse; `ambiguous_process_instance` is reserved for unsafe/conflicting lifetime evidence. The canonical `ThreadInstanceKey` is `(ProcessInstanceKey, Tid, Generation)`; thread-sensitive CPU/Wait paths expose both `ThreadStartUs` and `threadGeneration`, because capture-boundary inference can give distinct generations an equal start timestamp. Missing or ambiguous selectors are structured results with candidates, never PID-only fallback.
- Capability flags mean a supported event class was observed in the ETLX-materialized `TraceLog`; absence does not prove which capture keyword was disabled.
- Stack capability is reported per target event domain with event and metric-weighted total/stacked coverage, a coverage state, and `StackSemantics`. The global `HasStackWalks` union is compatibility information only. Scheduler coverage used by wait analysis measures the switch-out `BlockingStack`; it is intentionally distinct from the ordinary CSwitch event `CallStackIndex` exposed by the debug probe. `?!?` is synthetic accounting for an unstacked event, never a captured call chain.
- Empty results carry `ScopeStatus`, `CapabilityStatus`, `MatchedEventCount`, `MatchedIntervalCount` where applicable, `NoDataReason`, and warnings. Scope status is evaluated first. `observed` requires attributable source evidence in the resolved requested scope; established whole-trace absence is required for `not_observed`; filtered or identity-unresolved evidence remains `unknown`. `MatchedEventCount` is scoped raw source events/endpoints, while `MatchedIntervalCount` is completed projected intervals; neither is silently a trace-wide denominator or row count.
- `Trace*` fields are whole-trace evidence; `Scoped*` fields are selected process/thread/window evidence. Completeness and unmatched counters retain that prefix so an LLM does not attribute trace-global loss to one process. Ratios must not mix the two unless their DTO description explicitly defines the denominator.
- Stack rows produced through TraceEvent call-tree samples can carry `MetricPrecision` / `RowMetricAccounting=float32_per_sample_approximate` even when exposed as `long`. Exact source totals and coverage counters use `ExactTotalAccounting=exact_long`; approximate rows are not required to sum exactly to those totals.
- `inspect_trace.AnalysisContract` repeats these interpretation rules in a compact structured object whose descriptions are verified in the actual MCP output schema. Large response schemas are not blindly duplicated across every catalog entry; the measured `tools/list` payload remains guarded.
- `TraceMeta.EventCount` and provider counts are materialized logical TraceEvent counts. Raw ETW record count and parser coverage remain not measured unless an independent raw counter is available; comparing external raw counts to materialized counts does not prove parser loss.
