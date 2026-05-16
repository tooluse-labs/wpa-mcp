# wpa-mcp — capability gaps vs WPA / PerfView

> Working notes, not an RFC. Inventory of analysis capabilities that WPA and PerfView expose but wpa-mcp doesn't yet, plus capabilities that are GUI-tool-specific and not worth porting for LLM consumers.
>
> **Document-set logic** — three active docs in a sequential pipeline:
>
> - **`CAPABILITY_GAPS.md` (this)** — **what to add** (the stable inventory)
> - **`MCP_SURFACE_DESIGN.md`** — **how to add it** (Tool / Resource / Prompt, three-layer architecture, annotation tiers)
> - **`MCP_IMPLEMENTATION_TASKS.md`** — **prioritization + concrete tasks** (P0–P3, Scope / Work / Acceptance)
>
> A gap identified here is **not actionable** until `MCP_SURFACE_DESIGN.md` has classified the surface and `MCP_IMPLEMENTATION_TASKS.md` has sequenced the work.
>
> Earlier brainstorm notes are in `docs/archive/` for historical context.
>
> **Revision notes:**
> - **v5 (2026-05-15):** removed the four-tier punchlist; prioritization now lives solely in `MCP_IMPLEMENTATION_TASKS.md`. This doc retains only the stable capability inventory (A/B/C/D + not-to-port + UI vs data caveats + the anti-pattern callout). Doc-set cleanup also archived `OPTIMIZATION.md`.
> - **v4 (2026-05-15):** rewrote the punchlist as a four-tier priority structure; added explicit anti-pattern warning; reframed doc set as sequential rather than companion; added risk notes to A-4 and B-5.
> - **v3 (2026-05-15):** tightened factual claims after code review; added three A-table gaps.
> - **v2 (2026-05-15):** corrected v1's time-window overstatement; split capture-side; added D. lifecycle.
> - **v1 (2026-05-15):** initial inventory.

## Framing

Strip away the UI and WPA / PerfView capabilities sort into four buckets:

- **A. Data dimensions** — what is being observed
- **B. Slicing / aggregation** — how it's cut
- **C. Trace metadata** — info about the trace itself
- **D. Trace lifecycle / preprocessing** — operations on the trace artifact before / between analyses

---

## A. Data-dimension gaps

| Gap | Value | Notes |
|---|---|---|
| **CPU Usage Precise** (on-CPU µs from CSwitch, vs the statistical sampling of Sampled) | High | Current `cpu_*` is Sampled; `wait_*` is the off-CPU dual. Neither answers "how many µs did this thread actually spend on a CPU." Adding Precise closes the thread-time triangle (Sampled CPU / Precise on-CPU / Wait off-CPU). Note: this is only half of WPA's Precise view — see "Scheduler / core / priority" below for the other half. |
| **Scheduler / core / priority analysis** (per-core attribution, CPU migration, priority inversion, ready latency, quantum end) | Medium-high | The deeper half of WPA's CPU Usage (Precise) view. Answers "which core?", "did it bounce across cores?", "did priority inversion stall it?", "how long from ready to run?". Distinct from `ready_thread_*` (which is the wake event, not the dispatch latency distribution). |
| **GC heap dump (`.gcdump`) loading + object reference graph + retention paths** | High | Required for memory-leak investigations. `clr_gc_*` is ETW-event-level only — it can't answer "who still holds this `byte[]` array." |
| **Memory resource views** (working set, commit, private bytes, paged / non-paged pool, handle count) | High | The system-memory perspective, **distinct from allocation streams**: `clr_gc_*` is managed allocations, `heap_alloc_*` is NT heap events, `virtual_alloc_*` is reservation events. None of these answer "what's the actual resident footprint right now?", "is paged pool exhausted?", or "are we leaking handles?". WPA has dedicated views; wpa-mcp has zero coverage. <br/>**Risk note (v4):** verify first that current wpr profiles actually capture working-set / commit / pool / handle counters. If a new wpr keyword is required, this slips a priority tier — analyzers can't recover events that were never recorded. |
| **Async / Task chain stitching** (CLR Task continuation reassembly across threads) | High | `clr_*` returns events disjointly; async call chains don't reconnect. PerfView has task tracing for this. |
| **UI responsiveness / input latency / frame pacing** (input-to-render latency, DWM frame pacing, compositor stalls) | Medium-high | The user-perceived performance dimension. `Window-in-focus` (below) only answers "which window was foreground"; it does not answer "how long from keystroke to pixel." Required for any desktop / UI investigation. |
| **GPU profiling** (Compute Graphics / GPU Work) | Medium | Narrow scope but no substitute for GPU-bound workloads. |
| **Power profiling** (CPU C-states, frequency, battery transitions) | Medium | Required for laptop / mobile scenarios. |
| **Window-in-focus / foreground-process events** | Medium | Anchors perf events to "which window the user was looking at." |
| **Boot trace phase analysis** (PreSession / SMSSInit / WinLogonInit, etc.) | Medium | wpa-mcp currently treats boot traces as normal traces, losing the phase-stratified view. |
| **Audio glitches** | Low | Media-specific. |

## B. Slicing / aggregation gaps

| Gap | Value | Notes |
|---|---|---|
| **Time-window filtering — unify + share ROI context + add correctness tests** | Medium-high | **Status correction over v1.** `startUs` / `endUs` parameters are *already* present on most stack-shaped tools — `grep -l "startUs\|endUs"` finds 40+ files in `src/`, with a uniform `[Description("Window start in microseconds since trace start")] long? startUs = null` signature. The real gaps: (a) coverage isn't uniform; (b) no shared ROI context between calls; (c) no systematic correctness tests around clip boundaries. Treat as **unify + share-context + test**, not "add the parameter from scratch." |
| **Cross-trace diff** (baseline vs regression) | High | Blocker is process-identity matching across captures. |
| **Flexible group-by / pivot** (regroup by module / namespace / threadpool / payload field) | Medium-high | WPA does this via column drag; PerfView has GroupPats. wpa-mcp's output schema is fixed. |
| **Aggregation mode switching** (sum / avg / max / min / count / weighted) | Medium | Most columns are locked to sum or count. |
| **Generic-event field group_by** (constrained pivot DSL over task / opcode / event_id / payload field) | Medium-high | **Status correction over v1.** `generic_event_top_stacks` already filters by provider, event-name substring, pid, and time window, with a `whenBuckets` time-histogram option. What's missing is **group / pivot by event task, opcode, event_id, or payload field**. <br/>**Risk-vs-value note (v4):** engineering risk is low (parameter expansion on an existing tool, not a new tool). The unknown is **value** — whether LLMs can drive a constrained pivot DSL effectively. Ship a minimal 2-axis version first (task + opcode), validate against 1–2 real scenarios, then widen. |
| **Storage stack stratification** (LayoutComplete / FlushComplete / Volume Flush layered latency) | Medium | Current `disk_io_*` is post-merge / coarse-grained. |
| **Multi-stack-type combined CallTree** (CPU + Wait folded into one tree) | Medium | A PerfView strength; wpa-mcp forces separate queries and manual reconciliation. |

## C. Metadata gaps

| Gap | Value | Notes |
|---|---|---|
| **System Configuration** (OS build, CPU model / topology, core count, boot config, driver list — carried by SystemConfig events) | High | `load_trace` returns `Capabilities` but no hardware / OS provenance — the LLM lacks ground-truth context. ETW carries this via SystemConfig events; the analyzer just needs to project it. AC / battery state and frequency transitions live in Kernel-Power provider events and belong to A-table "Power profiling," not here. |
| **Per-provider trace statistics** (per-provider event counts; buffer config; dropped-event signal) | Medium-high | Baseline `EventsLost` is already exposed via `TraceMeta.EventsLost`, but the per-provider breakdown isn't. ETW buffer loss is fundamentally session-level — a strict per-provider dropped-events split may not be reconstructable. What is recoverable: per-provider event counts. |
| **Capture-quality diagnostics** (profile recommendation, missing-keyword guidance, stackwalk completeness, symbol resolution rate) | Medium-high | The other half of "what should I trust in this trace?" Reads from existing `Capabilities` + `SymbolStatus` and tells the LLM what to ask the user to re-capture. **Pure analysis-side — does NOT involve starting/stopping ETW sessions.** |
| **Symbol source lookup** (return `file:line`) | Medium | Lets LLMs cross-reference source. |
| **Inline frame expansion** | Medium | Inlined functions are currently subsumed into the outer frame; deep drilldowns distort. |

---

## D. Trace lifecycle / preprocessing

Not core analysis, but the analysis end legitimately owns artifact preprocessing — `tools/etlshrink/` already establishes precedent.

| Gap | Value | Notes |
|---|---|---|
| **`shrink_trace(path, pid_list)`** | Medium-high | Wrap `tools/etlshrink/` as an MCP tool. |
| **`slice_trace(path, startUs, endUs)`** | Medium | Time-window subset export — natural pair with B-1. |
| **`redact_trace(path, ...)`** | Low-medium | Strip specific processes / images / payload fields. |
| **Folded-stack / call-tree artifact export** (`format=folded` on `*_top_stacks`) | Low-medium | Not a UI flamegraph; an export format for external tools. Optional mode on existing tools. |
| **Multi-trace merge / stitching** | Low | Capture-side concern. |

---

## Capabilities we should NOT port

### Pure UI rendering (zero LLM value)

- Flamegraph / Icicle / Heatmap / Timeline / Bar chart **images**
- Colors / themes / fonts / layout
- Screenshots, image export
- HTML report generation
- Multi-view tab / window management
- UI pagination semantics (the API still needs `top` limits / cursors / truncation metadata — that's an API question, not a UI port)

### Config / state persistence (weakly necessary)

- `.wpaProfile` / `.perfView` view-config files — tool calls are request / JSON oriented
- User preferences / session-history restoration

### Capture execution (out of scope)

Write-side operations that need elevated rights:

- Starting / stopping ETW sessions
- Live provider enable / disable
- Heap-snapshot triggering
- `wpr -start` equivalent

`wpr` / PerfView own these. Capture *quality diagnostics* (above, C-table) is a separate in-scope capability.

---

## Common misclassification: UI form vs data capability

1. **Time-range selection** is the interaction form of **time-window filtering**. Already exposed as `startUs` / `endUs` parameters (B-1).
2. **Cross-view linking** is "multiple analyzers sharing the same time-window / process-filter context" — see B-1's "shared ROI context" half.
3. **Drill-down** is "the previous query's result is an input to the next query." wpa-mcp's current model (hand-retyping `module!func` strings) is a degraded form.

---

## Prioritization

**⚠️ Anti-pattern, do not do:** add capabilities by reading this list top-to-bottom. The reference tools (WPA / PerfView) organize features by capture / domain, not by LLM value. **Sequence the work via the priority structure in `MCP_IMPLEMENTATION_TASKS.md`**, not by reading this doc in order.

This doc describes **what is missing relative to WPA / PerfView** — a slow-changing inventory. **How to sequence the work** lives in `MCP_IMPLEMENTATION_TASKS.md` (P0 navigation foundation → P1 routing & composites → P2 low-risk high-value → P3 high-value high-risk), which can evolve with each sprint without touching this doc.

---

Last revised: 2026-05-15 (v5).
