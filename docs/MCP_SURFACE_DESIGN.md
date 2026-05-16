# wpa-mcp — MCP surface design (review draft)

> Working notes, not an RFC. How to organize a broad and growing tool surface so that LLM consumers don't drown in decision fatigue, without throwing away analytical power.
>
> **Document-set logic** — three active docs in a sequential pipeline:
>
> - **`CAPABILITY_GAPS.md`** — **what to add** (the inventory)
> - **`MCP_SURFACE_DESIGN.md` (this)** — **how to add it** (Tool / Resource / Prompt, three-layer architecture, annotation tiers)
> - **`MCP_IMPLEMENTATION_TASKS.md`** — **prioritization + concrete tasks** (P0–P3, Scope / Work / Acceptance)
>
> Earlier brainstorm notes are in `docs/archive/` for historical context.

## Why this doc exists

- wpa-mcp already ships a broad MCP tool surface, and `CAPABILITY_GAPS.md` identifies more high-value additions. Even after preferring parameter expansion over new tools, the surface will keep growing.
- The surface is mostly the same "top-N + stacks + caller-callee" triplet replicated per domain (CPU, wait, file IO, disk IO, hard faults, registry, ALPC, CLR, ...).
- That density isn't fatal — broad MCP surfaces are common — but it does start to hurt LLMs through three channels:
  1. **Token cost** — `tools/list` is loaded into every session prefix.
  2. **Decision fatigue** — similar tools (`*_top_stacks` family) raise wrong-tool selection rate.
  3. **Schema repetition** — most stack tools repeat `pid`, `top`, `startUs` / `endUs` with near-identical descriptions.
- MCP protocol has three primitives — **Tools, Resources, Prompts** — but wpa-mcp uses only Tools today. The other two are obvious levers, currently unused.

## The wrong moves (don't do these)

| Anti-pattern | Why it fails |
|---|---|
| Collapse the tool surface into one `analyze_trace(mode=...)` catch-all | Migrates decision fatigue from tool layer to parameter layer. JSON schema can't express conditional-required params cleanly; LLM wrong-arg rate rises. Also a hard breaking change for every existing client config. |
| Delete low-frequency low-level tools to "simplify" | Cripples expert paths. Frequency ≠ value; the rare tool may be the only one that answers a specific incident. |
| Dynamic `tools/list` filtering per loaded trace | Breaks prompt-prefix caching on Anthropic / OpenAI / most providers. Client compatibility for `tools/list_changed` is also uneven. |
| Add a "top + stacks + caller-callee" triplet for every new capability | Multiplies headcount monotonically. Prefer parameter expansion on existing tools. |
| Put critical-path capabilities into Resources or Prompts | See "Critical-path rule" below — these primitives have weaker client coverage than Tools and aren't auto-invokable by the model. |
| Walk down the WPA / PerfView feature list and port capabilities in that order | The reference tools organize by capture / domain, not by LLM value. Sequence via `MCP_IMPLEMENTATION_TASKS.md`, not via "what does WPA have on tab 7." |

---

## Three-layer architecture

### Layer 1 — Base tools (current surface, mostly preserved)

Direct, fine-grained access for expert callers and Layer-3 composites that build on them. **Stay structurally stable** so:

- Client configs (`claude_desktop_config.json` etc.) referencing specific tool names don't break.
- Prompt-prefix caching across providers stays valid across sessions.
- Layer 3 composites can call them as building blocks.

**Evolution rules:**
1. Prefer parameter expansion over new tools.
2. New domains (memory resource views, scheduler analysis) get a triplet only if the data shape genuinely warrants it — otherwise extend an existing tool.
3. Deprecate by usage data, not by guess.

### Layer 2 — Navigation tools (HIGH priority, not yet implemented)

The missing middle. Help an LLM go from "trace loaded" to "the right Layer-1 tool for this trace" without scanning the full tool list.

| Tool | Returns | Notes |
|---|---|---|
| `inspect_trace(path)` | Capabilities + system metadata + provider counts + stackwalk completeness + symbol quality + missing-keyword guidance + recommended workflows (raw signals) | One-shot orientation. Built by T0.3 and expanded by T2.1 in `MCP_IMPLEMENTATION_TASKS.md`. |
| `list_applicable_tools(path, goal?)` | Filtered + ranked tool list for this trace | Pure routing — does **not** mutate `tools/list`. Depends on `inspect_trace` data. Task T1.1. |

**Layer 2 deliberately does NOT include** `suggest_next_steps(lastResult)`. Decide based on Layer-2 usage data, not preemptively.

### Layer 3 — Composite / workflow tools

`diagnose_slow_startup` is the existing precedent. Each composite internally orchestrates 3–5 Layer-1 calls and returns a single response. **Reduce decision rounds, not tool count.**

| Composite | Layer-1 building blocks |
|---|---|
| `diagnose_high_wait` | `wait_analysis` + `ready_thread_top_stacks` + caller-callee on top wait frame |
| `diagnose_gc_pressure` | `clr_gc_analysis` + `clr_gc_heap_stats` + `clr_alloc_top_stacks` |
| `diagnose_lock_contention` | `clr_contention_top_stacks` + `wait_analysis` on contended threads |
| `diagnose_image_load_blocker` | `image_load_timing` + `image_load_top_gaps` + `wait_top_stacks` over largest gaps |
| `diagnose_trace_quality` | Reads `inspect_trace`'s raw signals and returns an opinionated verdict — distinct from `inspect_trace`: the latter returns signals, this returns a yes/no with reasoning. |

**Composites are Tools, not Prompts.** A Prompt is user-invoked; in agent-only deployments Prompts are never triggered.

---

## Tools vs Resources vs Prompts — role separation

| Primitive | Trigger model | Best for | wpa-mcp use |
|---|---|---|---|
| **Tools** | Model-controlled, model-invoked | Actions / queries with parameters; runtime computation | The current base tools + Layer 2 navigation + Layer 3 composites |
| **Resources** | Client-driven | Stable / semi-stable knowledge; reference docs; static catalogs | Capability matrix, tool catalog, per-trace metadata snapshots |
| **Prompts** | User-invoked | Reusable workflow templates | Slow startup, missing symbols, GC pressure, baseline regression |

### Client compatibility ladder (real-world, not just protocol)

| Primitive | Protocol status | Client coverage | Practical implication |
|---|---|---|---|
| Tools | Standard, required for any tool-calling MCP client | ~Universal | **Safe for critical-path capabilities** |
| Resources | Standard | Inconsistent — many agent runtimes do NOT auto-inject into context | OK for reference content; risky for content the model must see automatically |
| Prompts | Standard | Often requires explicit user invocation; agent-only runtimes typically never trigger | OK for human-in-the-loop; **effectively dead-code in pure-agent deployments** |
| `tools/list_changed` notification | Optional | Most third-party clients ignore or cache aggressively | Don't rely on for dynamic tool surfaces |
| Tool annotations (`*Hint`) | Standard (2025-03) | Display-only | Hint, not safety boundary |

### Critical-path rule

**Any capability the LLM must be able to invoke autonomously is a Tool — never a Resource or Prompt.**

Resources are application-driven; Prompts are user-invoked. Resources and Prompts are **additive enhancement layers**, not substitutes for Tools.

**Test:** ask "if a client supports only Tools (the minimal MCP profile), does the system still work?" — the answer must be yes for every critical-path capability.

### Planned Resources

Long-lived reference content. The model may or may not see them automatically depending on the client; that's acceptable because they don't gate any analysis.

- `resource://wpa-mcp/capability-matrix`
- `resource://wpa-mcp/tool-catalog`
- `resource://wpa-mcp/workflows/{name}`

### Planned Prompts

**Scope:** intended for human-in-the-loop clients. In agent-only deployments these may never be invoked — that's **expected**. The same workflow content must be reachable by an agent via Tools without invoking the Prompt.

- `slow_startup`
- `missing_symbols`
- `high_wait`
- `gc_pressure`
- `baseline_regression`

---

## Tool annotations — side-effect classification (4 tiers)

**Not "everything is readOnly".** wpa-mcp tools split into four tiers by actual behavior:

| Tier | Tools | `readOnlyHint` | `idempotentHint` | `openWorldHint` | Rationale |
|---|---|---|---|---|---|
| **A. Pure query (post-first-access)** | `*_top_*` / `*_caller_callee` / `list_processes` / `find_marker` / `thread_lifetime` / `process_create_timing` / `diagnose_symbols`, and similar query tools | `true` | `true` | **`true`** | Drill-downs can trigger symbol fetch → network + writes to `%LocalAppData%\WprMcp\Symbols`. **`diagnose_symbols` belongs here** (corrected v4 — verified against `SymbolTools.cs:61-90`): it does NOT mutate `_NT_SYMBOL_PATH` and does NOT actively fetch; it only reads `module.PdbName` from already-loaded image events. |
| **B. Cache generation** | `load_trace` | **`false`** | `true` | `false` | `TraceLog.OpenOrConvert` produces `.etlx` next to the input `.etl` (TraceCache.cs:54). |
| **C. Environment configuration** | `set_symbol_path`, `add_symbol_server` | **`false`** | `false` | **`true`** | Mutate process-global `_NT_SYMBOL_PATH`. `openWorldHint=true` because these change which servers downstream tools will probe on symbol resolution. |
| **D. Future file-producing tools** | `shrink_trace`, `slice_trace`, `redact_trace` (when implemented) | `false` | `true` | `false` | Write new `.etl` artifacts. |

### Annotations are display hints, not safety boundaries

**Server-side enforcement is the only real boundary.** Input validation, file-path safety, environment-variable scoping, network egress containment — all must be implemented in the server regardless of annotations. Annotations exist for client UI grouping, not server behavior.

### SDK readiness — verified

`WprMcp.csproj:13` pins `ModelContextProtocol 1.2.0`. The T0.2 spike verified that no SDK upgrade is required for the P0 navigation work.

- `[McpServerTool]` exposes annotation fields directly: `ReadOnly`, `Idempotent`, `OpenWorld`, and `Destructive`.
- `McpServerToolCreateOptions` exposes the same fields for programmatic registration.
- `UseStructuredContent=true` enables `CallToolResult.StructuredContent` and `Tool.OutputSchema`.
- `OutputSchemaType` supplies the schema when a tool returns `CallToolResult`.
- `ResourceLinkBlock` is a `ContentBlock` subtype and can appear in tool result content.

See `MCP_SDK_SURFACE_SPIKE.md` and `McpSdkSurfaceTests`.

---

## Sequencing

See **`MCP_IMPLEMENTATION_TASKS.md`** for the prioritized task list (P0–P3 with Scope / Work / Acceptance per task) and the recommended order. The Week 1 / Week 2 / After-Week-2 calendar that used to live here has been replaced by the dependency-based order in the tasks doc — calendar estimates are brittle, dependencies are not.

---

## Success metrics — define before shipping

| Metric | Target | Where measured |
|---|---|---|
| Tool wrong-selection rate | ↓ noticeable | Synthetic agent benchmark across canonical scenarios |
| Avg tool calls per investigation | ↓ via composites | Session traces |
| `tools/list` payload size | Stable (don't let it grow >2× from today) | Server startup logs |
| Prompt invocation rate (human-in-the-loop) | >0 | Server logs filtered to Claude Desktop / similar |
| Prompt invocation rate (agent-only) | ≈0 is **expected**, not a failure | Server logs filtered to Claude Code / SDK agents |
| `inspect_trace` adoption | Called within first 3 tool calls of >50% of sessions | Server logs |

Without measurement, every claim that "this made things better" is a vibe.

---

Last revised: 2026-05-15.

Revision history:
- **v8 (2026-05-15)**: updated the Layer-2 `inspect_trace` return summary after T2.1 added system metadata, provider event counts, driver summary, and stackwalk completeness.
- **v7 (2026-05-15)**: replaced SDK readiness unknowns with T0.2 spike results; documented attributed-tool support for annotations, structured output, output schema, and resource links.
- **v6 (2026-05-15)**: updated task cross-reference after `MCP_IMPLEMENTATION_TASKS.md` renumbered P0 into dependency order (`T0.2` SDK surface spike, `T0.3` `inspect_trace`).
- **v5 (2026-05-15)**: removed the Implementation path section (Week 1 / Week 2 / After-Week-2 calendar + Re-evaluate-at-6-months); sequencing now lives solely in `MCP_IMPLEMENTATION_TASKS.md` as a dependency-based prioritization. Doc-set cleanup also archived `OPTIMIZATION.md` and dropped the reference to it from the header.
- **v4 (2026-05-15)**: corrected `diagnose_symbols` placement from Tier C to Tier A; Tier C now contains only the two genuinely env-mutating tools.
- **v3 (2026-05-15)**: reframed doc-set as sequential pipeline; added "walk down the WPA feature list" as an explicit anti-pattern.
- **v2 (2026-05-15)**: added Client compatibility ladder; explicit Critical-path rule; composites must be Tools; scoped Prompts to human-in-the-loop; annotations-are-display-hints clarification.
- **v1 (2026-05-15)**: initial design doc.
