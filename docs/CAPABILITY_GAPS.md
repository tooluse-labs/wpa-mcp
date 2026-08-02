# wpa-mcp — capability gaps vs WPA / PerfView

> **Current status (2026-08-02):** This is the human-readable delta ledger,
> not the runtime catalog. The validated development model currently contains
> 61 active tools, 51 declared capabilities, 15 goals, and 15 workflows. Ten of
> those capabilities are deliberately mapped to `evaluator.declared_gap`; the
> other rows below include broader WPA/PerfView candidates that are not yet
> catalogued. `eng/capabilities.v1.json`, `eng/tool-contracts.v2.json`, the
> Active Catalog validators, and runtime resources are authoritative.

> Working notes, not an RFC. The server fully exposes its declared capability
> map to lower selection cost and fully exposes evidence gaps so an LLM cannot
> turn an unsupported or unmeasured capability into a conclusion. The map is
> exhaustive for wpa-mcp's declared surface, never for the complete WPA/ETW
> universe; unlisted means `unknown_not_catalogued`, not proven absent.
>
> **Document-set logic as of May 2026** — three historical planning docs in a sequential pipeline:
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
> - **v8 (2026-08-02):** aligned discovery and release gaps with the lean/full-contract projections: a static 61-tool catalog, a 250,000-byte lean `tools/list`, content-addressed contract lookup, and Contract 2.0-only result compatibility. Removed the nonexistent legacy-adapter and named-client-matrix release blockers and closed the corrected-active-baseline blocker through reviewed, automated artifacts.
> - **v7 (2026-08-01):** reconciled the historical inventory with the validated 60-tool/51-capability catalog, recorded the ten manifest-declared gaps, and converted CPU Precise, memory resources, system metadata, provider counts, and capture diagnostics from false "missing" claims into covered-core/remaining-boundary rows.
> - **v6 (2026-08-01):** added contract-rollout and client-evidence release gaps with the exact machine-readable blocker codes; the historical analyzer inventory remains unchanged.
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

### Runtime authority and discovery path

- Tools-only clients call `list_capabilities`; Resource-capable clients follow
  every page linked from `wpa://capabilities/server`, `wpa://tools/server`, and
  `wpa://workflows/server`.
- The server publishes a static, complete active tool set. `tools/list` is a
  lean projection capped at 250,000 aggregate bytes: it retains complete input
  schemas and content-addressed Contract 2.0 URI/hash metadata but does not
  inline deep output schemas. The historical approximately 2.5 MB inline
  catalog is a before-measurement, not the current discovery cost.
- Resource-capable clients resolve a selected schema through
  `wpa://contracts/tools/{toolName}/{sha256}` and its pages. Tools-only clients
  use `get_tool_contract(toolName, page)`. Both paths reassemble the same
  canonical bytes the server uses for result validation.
- The MCP host/client follows all protocol pages and may progressively inject
  task-relevant descriptors into the LLM. This does not dynamically activate
  tools on the server, and there is no universal dispatcher tool.
- A chosen tool's complete evidence and result-section semantics live at
  `wpa://tools/{toolName}/sections` and its linked pages. A gap ledger cannot
  substitute for those runtime ordering, truncation, precision, and conclusion
  boundaries.
- `inspect_trace` supplies the Trace Evidence Map for one loaded generation.
  Server declaration, trace evidence availability, and actual query outcome are
  separate states.
- The ten current manifest-declared gaps are:
  `symbols.configuration.path`, `symbols.configuration.server`,
  `symbols.diagnostics.metadata`, `scheduler.ready.causality`,
  `security.scanner.attribution`, `symbols.frame_resolution.measured`,
  `trace.raw_event_count.external`, `attribution.cross_domain.causal`,
  `lifecycle.trace.handle`, and `lifecycle.trace.artifact_peak_bound`.

Several of these deliberately name an unavailable standalone capability even
though a narrower fact exists elsewhere. For example, `prepare_symbols` reports
verified readiness and load/query/unload implement handle lifecycle, but the
current context-bound frame resolver is unavailable and there is no independent
handle-status/inventory tool. The catalog keeps those distinctions visible.

---

## A. Data-dimension gaps

| Gap | Value | Notes |
|---|---|---|
| **CPU Usage Precise** (on-CPU µs from CSwitch, vs the statistical sampling of Sampled) | Covered core | `cpu_precise_analysis` now reports exact CSwitch-derived on-CPU time, ready-to-run latency, per-core attribution, and quantum/preemption counters by replayable process/thread instance. This row remains as closure evidence, not an open capability. |
| **Scheduler / core / priority analysis** (CPU migration, priority inversion, richer priority timelines) | Medium-high, partial | Per-core attribution, ready latency, and quantum/preemption counts are covered by `cpu_precise_analysis`; `ready_thread_*` is explicitly association evidence. Dedicated migration summaries, priority timelines, and mechanistic priority-inversion proof remain gaps. `scheduler.ready.causality` stays a declared gap so a readier stack cannot be overclaimed as "who unblocked". |
| **GC heap dump (`.gcdump`) loading + object reference graph + retention paths** | High | Required for memory-leak investigations. `clr_gc_*` is ETW-event-level only — it can't answer "who still holds this `byte[]` array." |
| **Memory resource views** (working set, commit, private bytes, pool and handle activity) | Covered core, retention gap remains | `memory_resource_analysis` now projects `Memory/ProcessMemInfo` snapshots plus observed handle create/close and pool allocation/free deltas with explicit capture requirements and scope. These deltas are not absolute current counters, and a trend does not prove a leak or retention path. `.gcdump` object graphs and authoritative retained-object attribution remain open. |
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
| **Shared ROI context across calls** | Medium-high, partial | Per-call half-open `startUs <= t < endUs` semantics and boundary tests are broadly implemented; lifecycle-relative tools intentionally use different scopes, and admitted timeline/inventory results can publish bound cursors. There is still no first-class immutable ROI object shared across independent calls, so clients must replay the exact window and identity selectors. |
| **Cross-trace diff** (baseline vs regression) | High | Blocker is process-identity matching across captures. |
| **Flexible group-by / pivot** (regroup by module / namespace / threadpool / payload field) | Medium-high | WPA does this via column drag; PerfView has GroupPats. wpa-mcp's output schema is fixed. |
| **Aggregation mode switching** (sum / avg / max / min / count / weighted) | Medium | Most columns are locked to sum or count. |
| **Generic-event field group_by** (constrained pivot DSL over task / opcode / event_id / payload field) | Medium-high | **Status correction over v1.** `generic_event_top_stacks` already filters by provider, event-name substring, pid, and time window, with a `whenBuckets` time-histogram option. What's missing is **group / pivot by event task, opcode, event_id, or payload field**. <br/>**Risk-vs-value note (v4):** engineering risk is low (parameter expansion on an existing tool, not a new tool). The unknown is **value** — whether LLMs can drive a constrained pivot DSL effectively. Ship a minimal 2-axis version first (task + opcode), validate against 1–2 real scenarios, then widen. |
| **Storage stack stratification** (LayoutComplete / FlushComplete / Volume Flush layered latency) | Medium | Current `disk_io_*` is post-merge / coarse-grained. |
| **Multi-stack-type combined CallTree** (CPU + Wait folded into one tree) | Medium | A PerfView strength; wpa-mcp forces separate queries and manual reconciliation. |

## C. Metadata gaps

| Gap | Value | Notes |
|---|---|---|
| **System Configuration** (OS build, CPU model/topology, core count, boot config, driver list) | Covered core, nullable-source gaps remain | `inspect_trace` now projects trace-derived system metadata and driver summaries. Fields remain nullable when the trace did not carry them; host-machine values are never substituted. Power-state timelines remain a separate gap. |
| **Per-provider trace statistics** (event counts, buffer config, dropped-event provenance) | Covered counts, raw/session details remain | `inspect_trace` reports parser-materialized provider event counts and trace-level loss metadata with explicit provenance. Raw external record count, provider-specific loss attribution, and complete ETW buffer configuration are not inferred; `trace.raw_event_count.external` is a declared gap. |
| **Capture-quality diagnostics** (capability evidence, stack completeness, symbol boundaries) | Covered core, resolution/recapture gaps remain | `inspect_trace` now exposes the Trace Evidence Map, same-domain stack coverage, quality warnings, PDB-identity state, and next-step boundaries. `prepare_symbols` measures verified local readiness, but the current context-bound frame resolver fails closed and `symbols.frame_resolution.measured` remains a declared gap. None of these prove the original keyword configuration or automate recapture. |
| **Symbol source lookup** (return `file:line`) | Medium | Lets LLMs cross-reference source. |
| **Inline frame expansion** | Medium | Inlined functions are currently subsumed into the outer frame; deep drilldowns distort. |

---

## D. Trace lifecycle / preprocessing

The secure core lifecycle is implemented: `load_trace` is the only raw source
entry, returns a principal-scoped immutable TraceId, and `unload_trace` retires
the handle without claiming artifact deletion. The remaining rows are new
artifact-producing transformations, not missing parts of that core lifecycle.

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

## Contract rollout and client-evidence gaps

These are rollout gates and accepted residual risks, not hidden analyzer capabilities:

| Gap | Machine-readable status | Consequence |
|---|---|---|
| Full 0.5.x deprecation window and usage review | `release_blocked:no_reviewed_full_0.5.x_window_or_usage_telemetry_evidence` | 1.0 cannot remove raw-path compatibility until this evidence is reviewed in-repository. An environment variable cannot waive it. |
| Physical artifact materialization peak | `accepted_residual_risk:retained_quota_enforced;single_materialization_checkpoint_budget;opaque_converter_transient_peak_not_hard_limited` | Retained-store quota and checkpoints do not hard-limit the opaque converter's transient disk peak. The 0.4.x owner explicitly accepts this non-blocking risk; `artifact-materialization-budget.v1.json` records `opaqueConverterTransientPeakProven=false` rather than inventing a hard cap. |

There is no reviewed-legacy-projection gap: no released version established the
Phase 0 snapshot as a supported result wire contract, so Contract 2.0 is the
only 0.4.x result contract. `legacy` fails closed to prevent mislabeling, and
the absence of an unshipped adapter is not a release blocker. Named-client
paging/token/cache measurements remain useful compatibility observations, not
a global release gate; page-one-only hosts are still incompatible because host
pagination is required protocol behavior.

The corrected active-tool, DTO/stdio, lean-payload, pagination, and full-contract
registry baselines are generated and reviewed in this change, and automated
tests bind them to the active manifests/profile. That closes the former
`corrected_active_contract_baselines_not_release_approved` blocker; it does not
by itself claim that every unrelated full-suite/package gate has passed.

The selected runtime profile is exposed at `wpa://runtime/profile`; absence of a
release gate is never reported as analyzer support. See `CLIENT_COMPATIBILITY.md`
and `CONTRACT_MIGRATION.md`.

---

## Prioritization

**Anti-pattern:** do not add capabilities by reading this table top-to-bottom,
and do not hide an existing specialist tool to reduce prompt size. A candidate
must first receive a stable CapabilityId, questions answered/not answered,
required evidence/stacks, scope/cost/symbol requirements, maximum relationship,
runtime evaluator or explicit gap evaluator, tool/section contracts, benchmark,
and compatibility decision. Only then can an approved task or ADR sequence it.

The historical `MCP_IMPLEMENTATION_TASKS.md` is not the current backlog. The
accepted architecture and Phase 0–7 implementation/release gates live in
`MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md` and ADRs 0002–0005.

---

Last revised: 2026-08-02 (v8).
