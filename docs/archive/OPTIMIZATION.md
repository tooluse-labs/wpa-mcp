# wpa-mcp — optimization directions (review draft)

> Working notes. Not a roadmap, not an RFC. A snapshot of candidate work surfaced from reading `README.md`, `CONTRIBUTING.md`, `docs/ARCHITECTURE.md`, and `src/`, framed for prioritization discussion.

## Where we stand

- One `WprMcp` csproj, 54 MCP tools, Windows-only (kernel `TraceEvent` parsers aren't portable).
- 90 %+ of the tool surface is the same `top-N + stacks + caller-callee` triplet replicated per domain. Only `diagnose_slow_startup` is composite.
- `Core/TraceCache.cs`: default LRU capacity 2 (`WPRMCP_CACHE_SIZE` overrides). Each entry mmaps 200 MB – 1.5 GB of `.etlx`. The cache already has `TraceCache.Unload(path)`, but no MCP tool exposes it yet.
- Every analyzer in `Analyzers/*.cs` calls `source.Process()` on its own. Three tool calls against the same trace = three full ETLX passes.
- `Analyzers/CpuAnalysis.cs` warms exactly the first 50 modules via `LookupWarmSymbols(50, …)`. Anything outside that set silently falls back to `module!?` the moment an agent drills into caller-callee.
- The symbol path lives in the **process-global** `_NT_SYMBOL_PATH`. `SymbolService` mutates it through `Environment.SetEnvironmentVariable`, so changing the path after a trace is loaded currently requires a server restart; exposing `unload_trace` would make this an `unload_trace` + `load_trace` flow.

---

## P0 — highest leverage, recommended first

### O1. Expose `unload_trace` + design fused single-pass analysis

**Today.** Long MCP sessions still rely on LRU auto-eviction because the internal `TraceCache.Unload(path)` method is not surfaced as a tool. Meanwhile each analyzer drives its own `source.Process()`, so a four-step investigation on one trace means four full passes over the ETLX.

**Proposal.**
1. Add `MetaTools.UnloadTrace(path)` as MCP `unload_trace`, delegating to the existing `TraceCache.Unload(path)` and returning whether an entry was removed.
2. Separately design a batched-analysis path that fuses several analyzers' kernel callbacks into a single `source.Process()`. `cpu_top_functions_batch` already validates the "one pass, many PIDs" pattern — lift that idea up one layer so it works across analyzers, not just across PIDs.

**Effort.** The MCP wrapper is hours of work. The fused pass is week-scale because it touches the analyzer contract.

**Risk.** Keep these as two work items. `unload_trace` is additive and low risk; fused execution doesn't fit MCP's strictly per-call shape, so it wants a new multi-tool entry point (e.g. `analyze_trace_multi`) rather than a retrofit of the 54 existing tools.

### O2. Baseline-vs-regression diff tools

**Today.** Every tool consumes one trace. The 50× fork-slow case study in `docs/CASE_STUDIES.md` is fundamentally a *comparison* — and the agent has to run each tool twice and reconcile the rows by hand.

**Proposal.** Ship the three diffs that cover the bulk of real investigations:

- `cpu_top_functions_diff(traceA, traceB, pid, top)`
- `image_load_top_gaps_diff(traceA, traceB, pid)`
- `wait_analysis_diff(traceA, traceB, pid)`

Output should carry a generic `MetricName` / `DeltaMetric` plus `DeltaPct` / `NewlyAppeared` / `Disappeared`, with domain aliases where useful (`DeltaSamples`, `DeltaBlockedUs`, `DeltaGapUs`). CPU is sample-based, while wait and image-load gaps are time-based; a universal `DeltaUs` field would be wrong for CPU.

**Effort.** 0.5 – 1 day each — analyzer reuse is straightforward.

**Risk.** PIDs aren't stable across captures. The current process projection exposes name, parent PID, start/end times, CPU, and image-load count, but not a main-module hash. Either match on the existing identity fields (image name + spawn order + parent PID) or add a stable process-identity field before promising hash-based matching.

### O3. Token-compact mode for the `*_top_stacks` family

**Today.** `Tools/MarkerTools.cs` already heads off token blowup with `mode=count_by_event`. The `*_top_stacks` family never got the same treatment. Most stacks have a long, near-identical tail of CRT / loader frames that adds no information but is expensive in tokens.

**Proposal.** Add two optional knobs to every stack-shaped tool:

- `compactStacks=true` — truncate each stack at depth N (default 8); fold the tail into `[+K more]`.
- `summaryOnly=true` — drop stacks entirely; return leaf functions + counts only.

**Effort.** 1 – 2 days; the work is concentrated in `Analyzers/StackSourceTopN.cs`.

**Risk.** Additive optional parameters are low risk, but response-shape changes need care. Avoid inserting required positional-record constructor fields that can break existing clients; use nullable compatibility fields or a new compact response wrapper.

---

## P1 — medium-term

### O4. More composite diagnostics

Same template as `diagnose_slow_startup`: pick candidates → run a small bundle of analyzers in the relevant window → return one response.

| Tool | Composes |
| --- | --- |
| `diagnose_high_wait` | `wait_analysis` + `ready_thread_top_stacks` + caller-callee on the top wait frame |
| `diagnose_gc_pressure` | `clr_gc_analysis` + `clr_gc_heap_stats` + `clr_alloc_top_stacks` |
| `diagnose_lock_contention` | `clr_contention_top_stacks` + `wait_analysis` on the contended threads |
| `diagnose_image_load_blocker` | `image_load_timing` + `image_load_top_gaps` + `wait_top_stacks` over the largest gaps |

These are the multi-step PerfView workflows that benefit most from one-shot packaging — same value proposition the README already makes for `diagnose_slow_startup`.

**Effort.** 1 – 2 days per tool.

### O5. Progressive symbol resolution

**Today.** `CpuAnalysis.TopFunctions` calls `LookupWarmSymbols(50, …)` with a hard-coded 50. Caller-callee drill-downs that walk past those 50 modules render `module!?`, which looks like a symbol-config problem but is actually a fixed budget.

**Proposal.** Make the warm set a per-`TraceLog` incremental cache. Each caller-callee call pushes the unresolved modules that actually appeared in its response into the warm set, then re-renders. At minimum, expose the 50 as a tunable.

**Effort.** 1 – 2 days. Mind `SymbolReader`'s thread-safety story — `CONTRIBUTING.md` flags PDB-lock contention against PerfView's shared `C:\Symbols`, which is why the wpa-mcp default cache is `%LocalAppData%\WprMcp\Symbols`.

### O6. Tool-surface compression

**Today.** 54 tools, each domain shipping top + stacks + caller-callee in near-identical shape. The repetition is decision-fatigue fuel for LLM clients and bloats the `tools/list` payload.

**Proposal.**
- **Short term (low risk).** Keep `tools/list` static and add `list_applicable_tools(path)` or a `load_trace` recommendation block that maps `Capabilities` to relevant tools. This reduces decision noise without relying on dynamic MCP tool registration.
- **Medium term (compatibility experiment).** If the MCP SDK and common clients support dynamic tool lists without schema caching surprises, filter `tools/list` by the active trace's `Capabilities`.
- **Long term (breaking).** Collapse each domain into a single tool with `view=top_functions|top_stacks|caller_callee` and a `metric=` enum.

**Effort / risk.** The recommendation helper is a clean win. Dynamic `tools/list` is not guaranteed to be client-safe, and the long-term variant is a breaking change — client configs (`claude_desktop_config.json` and friends) reference tool names directly, so it has to ship behind a deprecation window.

---

## P2 — long-term / architectural

### O7. Cross-platform analysis subset

The kernel `TraceEvent` parsers are Windows-only, but the generated `.etlx` files are platform-neutral. Splitting the csproj into `WprMcp.Analyzers` (ETLX-only, cross-platform) and `WprMcp.Capture` (Windows-only) — the split already on the wishlist in `docs/ARCHITECTURE.md` — would let a Linux CI runner replay regressions against captured traces. Don't do this without an explicit Linux-CI ask: the architectural cost is real and the immediate payoff is thin.

### O8. Refactor `Analyzers/StackSourceTopN.cs`

22 KB in one file; every `*StackAnalysis.cs` reimplements the same skeleton (attach parser, build a `CallTree`, take top-N, optional when-buckets histogram). A `StackAnalysisHarness<TEvent>` would cut ~30 % of analyzer code and make new metric dimensions (the O2 diffs in particular) cheap to add. Pairs naturally with O6 — do them together or not at all.

### O9. Performance + token-budget benchmark harness

The PerfView-parity invariant on `CpuAnalysis` is currently validated by hand against the "7/10 top-N overlap, ±10 % counts, ±15 pp percentages, ±1 % grand total" rubric in `CONTRIBUTING.md`. Stand up `tests/WprMcp.Bench/` with BenchmarkDotNet and snapshot comparison against `small_cpu.etl`; gate every PR on latency and response-size regressions. **Effort:** 2 – 3 days to bootstrap; ongoing maintenance is non-trivial because fixture refreshes ripple into snapshots.

### O10. Expose `tools/etlshrink/` as an MCP tool

The standalone `tools/etlshrink/` project already knows how to trim an `.etl` down to a PID list. Wrapping it as `shrink_trace(path, pid_list)` would let an agent shrink the working set *before* `load_trace`, which directly attacks the 200 MB – 1.5 GB mmap-per-trace cost. Mostly an attribute-plumbing job; the only real design call is what to do on a read-only target path.

---

## Recommendation

If three slots are all we get:

1. **O1a** — expose MCP `unload_trace`. Smallest correctness gap; fixes memory control and symbol-path re-resolution guidance.
2. **O3** — token-compact stacks. Buys agents more questions per session before they hit the context wall.
3. **O2** — baseline-vs-regression diff family. High user-visible value, but only after the diff schema and process matching story are nailed down.

Defer:

- **O7** (cross-platform). Reopen if Linux CI becomes a stated requirement; until then the architecture cost is too high.
- **O1b fused pass**. Keep designing it, but don't block the low-risk `unload_trace` wrapper on a week-scale analyzer-contract change.
- **O6 dynamic / long-term variants**. The recommendation helper captures most of the win. Wait on dynamic tool filtering or breaking domain merges until we have evidence the tool count is actually hurting clients.
