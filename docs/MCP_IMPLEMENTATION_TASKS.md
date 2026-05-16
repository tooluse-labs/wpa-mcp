# wpa-mcp Implementation Task List

> This document turns design conclusions into executable work items.
>
> **Place in the document set:**
>
> - `docs/archive/OPTIMIZATION.md` — archived brainstorm + candidate directions
> - `CAPABILITY_GAPS.md` — decides **what to add** (four-tier punchlist, A/B/C/D buckets)
> - `MCP_SURFACE_DESIGN.md` — decides **how to add it** (Tool / Resource / Prompt), three-layer architecture, annotation tiers
> - `MCP_IMPLEMENTATION_TASKS.md` (this) — decides **concrete tasks**, in P0/P1/P2/P3 priority
>
> Logical flow: **brainstorm → what → how → do**. The goal is not to clone WPA / PerfView feature by feature, but to close the gaps that most affect analysis correctness and LLM usability without increasing tool-selection confusion.

## Principles

- **Build the navigation layer before expanding the capability surface.** The server already exposes 55 tools; adding more directly increases the chance that an LLM picks the wrong one.
- **Critical capabilities must be Tools.** Anything an LLM must invoke autonomously cannot live only in a Resource or Prompt.
- **Resources and Prompts are enhancement layers.** Resources hold stable reference material; Prompts hold human-started investigation templates.
- **Avoid universal catch-all tools.** Do not introduce an `analyze_trace(mode=...)` entry point.
- **Prefer parameter expansion and composites for new capabilities.** Add a full tool family only when the data shape genuinely requires it.
- **Measure before expanding the routing surface.** Routing helpers and composites should prove lower wrong-tool selection or fewer investigation calls before they become the default path.
- **Deprecation is allowed, but evidence-gated.** Layer-1 tools stay structurally stable by default; sustained low usage plus an equivalent composite or replacement path can justify merge / removal review.

## P0: MCP Surface Foundation

### T0.1 Correct the Tool Annotation Classification

- **Status:** ✅ Completed 2026-05-15 (`MCP_SURFACE_DESIGN.md` v4 + `.zh-CN.md` v4).
- **Scope:** Update `docs/MCP_SURFACE_DESIGN*.md`.
- **Work:** Move `diagnose_symbols` from the Tier C (environment configuration) bucket containing `set_symbol_path` / `add_symbol_server` into Tier A (pure query).
- **Reason:** `diagnose_symbols` does not mutate `_NT_SYMBOL_PATH` and does not actively download symbols (verified against `SymbolTools.cs:61-90` — it only reads `module.PdbName` from already-loaded image events). It does trigger trace loading / `.etlx` generation through `_cache.Get(path)`, but that's the universal Tier-A behavior.
- **Acceptance:** The docs accurately distinguish environment mutation, cache generation, and pure query behavior. ✅ Tier C now contains only the two genuinely env-mutating tools.

### T0.2 Run an MCP SDK Surface Spike

- **Status:** ✅ Completed 2026-05-15 (`MCP_SDK_SURFACE_SPIKE.md`, `McpSdkSurfaceTests`).
- **Scope:** Try one low-risk Tier-A tool only.
- **Goal:** Verify how `ModelContextProtocol 1.2.0` declares `readOnlyHint`, `idempotentHint`, `openWorldHint`, tool `outputSchema`, structured result content, and resource links.
- **Result:** No SDK upgrade is required. The attributed-tool path supports annotation properties directly on `[McpServerTool]`; structured output is enabled with `UseStructuredContent=true`; explicit output schema for `CallToolResult` uses `OutputSchemaType`; resource links use `ResourceLinkBlock`.
- **Acceptance:**
  - ✅ Decide whether an SDK upgrade is required.
  - ✅ Determine whether annotations / output schema are declared through attributes or programmatic registration.
  - ✅ Determine whether `structuredContent` and `resource_link` can be returned by attributed tools.
  - ✅ Do not mass-annotate tools or implement `inspect_trace` response wiring until the spike is resolved.

### T0.3 Implement `inspect_trace(path)`

- **Status:** ✅ Completed 2026-05-15 (`MetaTools.InspectTrace`, `InspectTraceResponse`).
- **Dependency:** T0.2 resolves the SDK support pattern for `outputSchema`, `structuredContent`, and tool annotations.
- **Scope:** Add or extend `MetaTools`; add response records.
- **Return shape:** Structured tool output with an MCP `outputSchema` when the SDK supports it. If `ModelContextProtocol 1.2.0` cannot express that shape, document the fallback explicitly before implementation.
- **Implementation:** Added an attributed tool with `UseStructuredContent=true`, `ReadOnly=true`, `Idempotent=true`, `OpenWorld=false`, and `Destructive=false`. The response includes trace basics, capability flags, module-level symbol quality, structured quality warnings, orientation tools, and capability-supported tool hints.
- **Return fields:**
  - Trace basics: duration, event count, lost events, process count
  - Capability flags: CPU, CSwitch, FileIO, DiskIO, CLR, ALPC, Network, and related signals
  - Symbol quality: symbol path, resolution rate, unresolved-module hints
  - Capture quality warnings: missing keywords, missing stackwalks, lost events
  - Orientation tools and capability-supported tools: `tool_name` + `reason` records without a single global rank; avoid embedding long workflow prose that belongs in `workflow-catalog`
- **Boundary:** `inspect_trace` returns raw signals and recommendations. The opinionated yes/no verdict belongs to `diagnose_trace_quality` (T1.2), so the two do not drift into duplicate tools.
- **Acceptance:**
  - ✅ One call tells an LLM what the trace can and cannot answer, and which tools to use next.
  - ✅ Machine-readable orientation and capability-supported tool hints are stable enough for `list_applicable_tools` and composite tools to consume.
  - ✅ Existing tool behavior is unchanged; the surface adds only the new navigation tool.

### T0.4 Add Tests for `inspect_trace`

- **Status:** ✅ Completed 2026-05-15 (`InspectTraceTests`, `MetaToolsTests`, `TraceCapabilitiesDetector` event-family alignment).
- **Dependency:** T0.3.
- **Scope:** `tests/WprMcp.Tests`.
- **Coverage:**
  - ✅ Capability projection
  - ✅ Lost events produce a warning
  - ✅ Missing symbol path produces guidance
  - ✅ Missing key providers produce recapture guidance
  - ✅ Capability projection agrees with downstream analyzer behavior on fixture traces; detector event subscriptions now match analyzer event families for read/write, send/recv, alloc/free, DPC/ISR, ALPC send/receive, and registry operation variants.
- **Acceptance:** ✅ Tests lock the response shape and core diagnostic rules.

### T0.5 Establish Measurement Baseline

- **Scope:** Server-side observability, synthetic evaluation, and CI guardrails. Do not log raw arguments, trace paths, payload contents, or private trace metadata.
- **Work:**
  - ✅ Add structured per-call telemetry: tool name, salted argument hash or session trace id, latency, response byte count, error flag, cache-hit flag.
  - Telemetry implementation constraints:
    - ✅ Runtime persisted telemetry is opt-in via `WPRMCP_TELEMETRY=1`; fresh installs emit no telemetry. CI benchmarks and local measurement commands are explicit-run verification paths, not ambient runtime telemetry.
    - ✅ Generate a per-session salt at server startup and never write it to disk or logs. If hashing arguments, use `HMAC(session_salt, args_json)`; deterministic or process-lifetime path hashes are not allowed.
    - ✅ Write telemetry only to stderr or a dedicated file under `%LocalAppData%\WprMcp\Logs\`. Stdout is reserved for MCP JSON-RPC framing and must stay clean.
  - ✅ Record `tools/list` payload size at startup and add a CI guard that fails when it grows beyond the approved baseline threshold.
  - ✅ Define 10 canonical synthetic investigation scenarios with acceptable tool-call sequences, including tools-only mode with Resources and Prompts disabled. See `MCP_MEASUREMENT_BASELINE.md`.
  - ✅ Track the six success metrics from `MCP_SURFACE_DESIGN.md`: wrong-tool selection, mean tool calls per investigation, `tools/list` size, human Prompt invocation, agent Prompt invocation, and `inspect_trace` adoption.
- **Acceptance:**
  - ✅ Every P0/P1 change can quote a delta against the baseline.
  - ✅ Benchmark and telemetry outputs are privacy-safe and do not corrupt MCP stdio.
  - ✅ Privacy review passes: no raw paths, no deterministic path hashes, no payload contents, and per-session salt uniqueness is verified.
  - ✅ Transport review passes: stdout contains only MCP JSON-RPC frames; logs are verified on stderr or a dedicated file.
  - ✅ Default-off runtime telemetry is verified with no `WPRMCP_TELEMETRY` environment variable set.

### T0.6 Add Token-Compact Stack Responses

- **Scope:** The `*_top_stacks` family and any composite that embeds stack rows.
- **Work:**
  - ✅ Add `compactStacks=true` to request lossy compact stack output for token-constrained clients. Current `*_top_stacks` rows are already frame-level summaries with no full stack arrays; compact mode therefore caps rows at the documented limit of 25. See `MCP_STACK_RESPONSE_COMPACTNESS.md`.
  - ✅ Add `summaryOnly=true` to return a lossy smaller leaf / metric summary by applying the same row cap. Rerun without compact flags when long-tail detail matters.
  - ✅ Preserve existing detailed output as the default unless measurement shows the compact form should become the preferred composite path.
- **Acceptance:**
  - ✅ Compact-mode defaults are anchored to documented Claude Code MCP output behavior: warn above 10,000 tokens and default maximum 25,000 tokens. Representative default stack responses are guarded below the 10,000-token warning threshold approximation.
  - ✅ Sizing tests cover representative committed stack fixtures and a structural guard that prevents accidental full stack arrays in row DTOs.
  - ✅ Truncation is explicit and actionable; callers can raise `top` or use caller/callee drill-down for a specific focus frame.

## P1: LLM Routing and Workflow Compression

### T1.1 Implement `list_applicable_tools(path, goal?)`

- **Dependency:** `inspect_trace` (T0.3) and T0.5 measurement. Implement only after data shows `inspect_trace` orientation / capability-supported tool hints are insufficient for goal-directed routing.
- **Input:** Trace path and optional goal: `cpu`, `startup`, `memory`, `gc`, `io`, `symbols`, or `wait`.
- **Return:** Ranked tool recommendations, applicability reasons, and non-applicability reasons.
- **Acceptance:** The tool returns recommendations without dynamically mutating `tools/list`.

### T1.2 Add High-Frequency Composite Tools

- **Priority order:**
  1. `diagnose_high_wait(path, focus="general|lock|io|sync")`
  2. `diagnose_image_load_blocker`
  3. `diagnose_gc_pressure`
  4. `diagnose_trace_quality` — returns a structured verdict per dimension: capture coverage, symbol resolution, lost events, and stackwalk completeness. Each dimension carries `status: "ok|warn|fail"`, reason, and actionable next step. The overall verdict is derived from the dimension statuses, not free text.
  5. Defer a separate `diagnose_lock_contention` unless data shows the `focus="lock"` path is insufficient. If implemented separately, scope it to CLR managed locks (`clr_contention_top_stacks`) so it does not duplicate `diagnose_high_wait`.
- **Principle:** Each composite internally orchestrates 3-5 existing Layer-1 tools. Any embedded stack section should default to `summaryOnly=true` or `compactStacks=true`; detailed drill-down remains available through the underlying Layer-1 tools.
- **Acceptance:** Common investigations take fewer tool-call rounds while the low-level tools remain available.

### T1.3 Add Resources and Prompts

- **Resources:**
  - `capability-matrix`
  - `tool-catalog`: long-form usage guidance, anti-patterns, related tools, and examples; not a duplicate of `tools/list`
  - `workflow-catalog`: reusable workflow prose that `inspect_trace` and composite tools reference by structured pointer instead of copying
- **Prompts:**
  - `slow_startup`
  - `missing_symbols`
  - `high_wait`
  - `gc_pressure`
  - `baseline_regression`
- **Acceptance:**
  - Tools-only clients can still complete core investigations.
  - Each Prompt and its sibling composite Tool are derived from a single source-of-truth workflow artifact, for example `workflows/<name>.json` or `workflows/<name>.md`. Prompt messages are generated from or anchored to this file; the composite Tool argument schema and step list reference the same artifact.
  - CI fails when a Prompt or composite Tool diverges from its source workflow artifact. If a generator is deferred, both sides must include auditable metadata pointing at the source artifact until CI enforcement lands.
  - Tools-only clients reach the same outcome by calling the composite Tool with the arguments named by the source artifact.
  - Agent-only Prompt invocation near zero is expected and is not treated as a failure.
  - Resources and Prompts improve only clients that support them.

## P2: Low-Risk, High-Value Capability Gaps

### T2.1 Trace Quality and System Configuration

- **Status:** ✅ Completed 2026-05-15 (`TraceMetadataAnalysis`, `InspectTraceResponse.Metadata`).
- **Work:**
  - ✅ Expose trace system metadata through `inspect_trace`: machine name, OS name/build/version, processor count, CPU speed, boot time, UTC offset, and metadata source.
  - ✅ Expose driver modules from the trace module table, bounded to the top 50 `.sys` entries with version/product metadata when available.
  - ✅ Expose provider event counts, total provider count, top providers, uncaptured "other" count, per-provider stack coverage, and total event stack coverage.
  - ✅ Expose stackwalk completeness: whether StackWalk capability exists, observed StackWalk event count, events with attached call stacks, and coverage ratio.
  - ✅ Keep `CpuModel` nullable and report `cpu_model_not_available_from_trace_metadata` instead of falling back to the host machine. TraceEvent reliably exposes CPU count/speed here, but the committed fixture traces do not carry a CPU model string.
- **Relationship:** These fields now feed `inspect_trace` first; `load_trace` remains the lightweight cache/orientation call.
- **Acceptance:** ✅ An LLM can judge whether a trace is trustworthy and whether analysis conclusions are limited by capture quality.

### T2.2 Unify ROI / Time-Window Semantics

- **Status:** ✅ Completed 2026-05-16 (`StackAnalysisRequest` half-open filter, boundary tests, `cpu_precise_analysis`, and non-windowed tool scope descriptions).
- **Work:**
  - ✅ Audit tools that still lack `startUs` / `endUs`.
  - ✅ Standardize boundary semantics.
  - ✅ Add clip-boundary correctness tests.
  - ✅ Design shared ROI context without relying on dynamic tool lists: keep ROI explicit in `startUs` / `endUs` parameters and composite `ExecutedToolCalls` provenance rather than ambient mutable state.
- **Acceptance:**
  - ✅ Boundary semantics are defined as half-open intervals: include an event iff `startUs <= timestamp < endUs`.
  - ✅ A conformance fixture covers events exactly on the boundary.
  - ✅ Every time-windowed analyzer follows the same boundary rule; trace-global tools document why they do not accept a window.

### T2.3 CPU Usage Precise and Scheduler Analysis

- **Status:** ✅ Completed 2026-05-16 (`cpu_precise_analysis`, `CpuPreciseAnalysis`, `CpuPreciseAnalysisTests`).
- **Work:** Use CSwitch data to compute on-CPU microseconds, ready latency, per-core attribution, and priority / quantum signals.
- **Acceptance:** The server can answer questions sampled CPU cannot: how long a thread actually ran, how long it waited after becoming ready, and which cores it ran on.

### T2.4 Memory Resource Views

- **Status:** ✅ Completed (2026-05-16). `MemoryCapture.wprp` and `small_memory.etl` cover positive `Memory/ProcessMemInfo`, handle-event, and observed pool allocation/free paths. `memory_resource_analysis` handles both named `Pool/...` events and clean-conversion raw classic Pool task GUID/opcode records, so the committed fixture proves pool rows after deleting `.etlx` caches. Pool rows are explicitly captured-window deltas, not absolute current counters, because the available pool snapshot events do not expose usable counter fields.
- **Verify first (risk gate):** Confirm whether existing wpr profiles actually capture working-set, commit, paged / non-paged pool, and handle counters. If a new wpr keyword is required, this entry drops to **P2.5** and the keyword work precedes the analyzer work. See `CAPABILITY_GAPS.md` v4 A-4 risk note — an analyzer cannot recover events that were never recorded.
- **Fallback if verification fails:** Author a `MemoryCapture.wprp` beside `MmapCapture.wprp`, capture a passing fixture, and document keyword requirements in `docs/WPR_PROFILE.md` before analyzer implementation.
- **Work (assuming data source confirmed):** Expose working set, commit, private bytes, paged / non-paged pool, and handle count.
- **Acceptance:** The server can answer resident-footprint, pool-exhaustion, and handle-leak questions instead of only allocation-event questions.

## P3: High-Value but Higher-Risk Capabilities

### T3.1 Cross-Trace Diff

- **Work:** Add baseline-vs-regression diffs for CPU, wait, and image-load gaps.
- **Prerequisite:** Define process identity matching and metric schema first. Reuse the archived `docs/archive/OPTIMIZATION.md` O2 analysis for identity alternatives such as image name + spawn order + parent PID versus a new stable identity field.
- **Acceptance:** Output uses `MetricName`, `DeltaMetric`, `DeltaPct`, and appeared / disappeared markers. Do not use an incorrect universal `DeltaUs`.

### T3.2 `.gcdump` and Retention Paths

- **Work:** Load `.gcdump` files, build object reference graphs, and expose retention paths.
- **Acceptance:** The server can answer "who still holds these objects?", closing the memory-leak gap that ETW GC events cannot cover.

### T3.3 Async / Task Chain Stitching

- **Work:** Reassemble CLR Task continuations and recover async call chains across threads.
- **Acceptance:** Async workflows split across threads can be presented as explainable chains.

### T3.4 Generic Event `group_by` / Pivot

- **Engineering risk:** Low (parameter expansion on `generic_event_top_stacks`, not a new tool).
- **Value uncertainty:** Whether LLMs can drive a constrained pivot DSL effectively is unproven. See `CAPABILITY_GAPS.md` v4 B-5 risk note.
- **Work — two phases:**
  1. **Minimum viable (2-axis):** task + opcode `group_by` on the existing tool. No new tool, no widened DSL surface.
  2. **Validate against 1–2 real scenarios** before widening the axis set (event_id, payload field). If phase-1 sees zero LLM usage, do not widen.
- **Acceptance:** Phase-1 captures core WPA pivot value without unbounded query surface; widening happens only with validation data showing usage.

## Not Planned

- Pure UI rendering: flamegraph images, timelines, heatmaps, bar charts, themes, screenshots, HTML reports.
- Capture execution: starting / stopping ETW sessions, live provider management, `wpr -start`.
- Dynamic `tools/list` filtering: it breaks prompt-prefix caching and has uneven client support.
- Merging the current 55 tools into one universal entry point: this would be a breaking change and would move decision fatigue into parameters.
- Long-term migration to one tool per domain with `view=top|stacks|caller_callee` and `metric=` parameters: this is the archived O6 breaking variant. Consolidation happens through Layer-3 composites unless usage data justifies reopening the design.

## Recommended Implementation Order

1. ✅ T0.1 correct the annotation classification in the docs.
2. ✅ T0.2 complete the SDK surface spike.
3. ✅ T0.3 and T0.4 implement and test `inspect_trace`.
4. ✅ T0.5 establish the measurement baseline.
5. ✅ T0.6 add token-compact stack responses.
6. ✅ T2.1 add trace quality / system metadata.
7. T1.2 add 2-3 composite tools, starting with `diagnose_high_wait`. Composites ship as "preview" routing targets until the T0.5 benchmark shows lower wrong-tool selection or fewer mean calls per investigation versus the Layer-1-only baseline. `inspect_trace` capability-supported tool hints should include composites alongside the relevant Layer-1 tools only after that threshold is met.
8. T1.1 implement `list_applicable_tools` only if T0.5 shows `inspect_trace` is insufficient.
9. ✅ T2.2 unify ROI / time-window semantics.
10. T2.3 and T2.4 begin CPU Precise and memory resource views (T2.4 contingent on the verify-first gate).
11. Start P3 items only after usage data and correctness risks are understood.

## Completion Criteria

- Synthetic benchmark wrong-tool selection stays within the v1 baseline plus 2 percentage points across the 10 canonical scenarios.
- After T1.2 ships, mean tool calls per investigation trends down by at least 10% versus the v1 baseline, or the composite rollout is revisited.
- Tools-only MCP clients can still complete the core investigations, verified with Resources and Prompts disabled in the test harness.
- Every new tool clearly describes when it should not be used.
- High-risk analysis features have a metric schema and test fixtures before implementation.
- Documentation, tool descriptions, and tests stay consistent.

---

## Revision history

- **v13 (2026-05-16)**: marked T2.3 complete after `cpu_precise_analysis` landed with CSwitch/ReadyThread scheduler evidence, boundary clipping tests, and capture-boundary accumulator fixes.
- **v12 (2026-05-16)**: completed T2.4 by parsing clean-conversion raw classic Pool task GUID/opcode payloads, making the committed `small_memory.etl` prove the pool-positive analyzer path without stale `.etlx` caches.
- **v11 (2026-05-16)**: corrected the T2.4 fixture status after clean conversion showed the committed `small_memory.etl` does not expose named Pool events; restored the documented pool-positive fixture as a remaining limitation.
- **v10 (2026-05-16)**: added `small_memory.etl` fixture coverage for `Memory/ProcessMemInfo` and handle events plus analyzer support for observed pool allocation/free deltas.
- **v9 (2026-05-16)**: completed T2.2 by documenting half-open window semantics, locking boundary tests, and requiring non-windowed tools to explain their whole-trace or lifecycle scope.
- **v8 (2026-05-15)**: aligned `inspect_trace` wording with the final P0 schema split: orientation tools and capability-supported tool hints replace the old single ranked recommendation field.
- **v7 (2026-05-15)**: completed T2.1 trace quality / system metadata: `inspect_trace` now includes system metadata, driver module summary, provider event counts, and stackwalk completeness. CPU model remains nullable with an explicit limitation instead of host fallback.
- **v6 (2026-05-15)**: completed T0.6 stack response compactness: `compactStacks` / `summaryOnly` options across `*TopStacks`, compact row cap, and sizing/shape tests.
- **v5 (2026-05-15)**: completed T0.5 measurement baseline: default-off privacy-safe telemetry, startup `tools/list` payload logging and guard, and ten canonical synthetic investigation scenarios.
- **v4 (2026-05-15)**: renumbered P0 tasks into dependency order: SDK surface spike (`T0.2`) now precedes `inspect_trace` implementation (`T0.3`) and tests (`T0.4`); updated implementation order and task references accordingly.
- **v3 (2026-05-15)**: incorporated round-2 review refinements: explicit telemetry privacy / transport constraints, workflow source-of-truth and drift checks for Prompts and composites, Claude Code output-limit anchors for compact stacks, compact defaults for composite stack sections, structured `diagnose_trace_quality` verdicts, and benchmark-gated composite promotion.
- **v2 (2026-05-15)**: incorporated `MCP_IMPLEMENTATION_TASKS_REVIEW.md` recommendations that were grounded in repo facts: SDK surface spike before `inspect_trace`, structured output requirements, measurement baseline, token-compact stack responses, composite priority changes, precise time-window semantics, memory-capture fallback, and falsifiable completion criteria.
- **v1 (2026-05-15)**: initial task list extracted from `CAPABILITY_GAPS.md` v4 + `MCP_SURFACE_DESIGN.md` v3. T0.1 fed a correction back into `MCP_SURFACE_DESIGN.md` (now v4). T2.4 and T3.4 carry the risk notes from `CAPABILITY_GAPS.md` v4 A-4 and B-5 respectively (verify-first gate; two-phase DSL rollout). Doc-set logic in the header references all four documents.
