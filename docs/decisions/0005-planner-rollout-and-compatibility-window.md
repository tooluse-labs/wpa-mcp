# ADR 0005: Planner scope, rollout, and compatibility window

- Status: Accepted
- Decision date: 2026-08-01
- Amended: 2026-08-02 — Contract 2.0-only rollout, lean discovery,
  non-blocking client observations, and explicit acceptance of the opaque
  converter transient-peak residual risk for 0.4.x
- Amended: 2026-08-07 — define the additive `0.7.x` release line (disk IO
  analysis tool, trace-access escape hatch, and denial diagnostics) with
  unchanged contract 2.0 + canonical `traceId` defaults
- Superseded in part: ADR 0006 closes the raw-path query window in `0.6.0`
  and replaces ambiguous query `path` inputs with canonical `traceId`
- Decision source: implementation authorization through `/goal`
- Depends on: ADR 0001, ADR 0003, ADR 0004
- Implementation status: partially implemented; Contract 2.0 is the only result
  runtime, while reviewed 1.0 raw-path deprecation-window evidence does not yet exist

## Context

The capability/evidence refactor needs an explicit rollout that does not publish a half-structured contract, invent compatibility obligations for an unpublished legacy snapshot, or use planner optimization to change evidence. It must also remain on the already selected stable .NET/MCP protocol profile until a new platform matrix approves an upgrade.

## Decision

### 1. Platform boundary

ADR 0001 remains authoritative: .NET SDK 10.0.302, `net10.0`, ModelContextProtocol 1.4.1, protocol revision 2025-11-25, stateful profile, and Windows x64. The catalog and cursor implementation uses the stable SDK's custom list/call handlers and stateful server services. A 2026-07-28/stateless or MCP 2.x transition requires a new platform ADR and the full host-mode/protocol matrix; this refactor does not smuggle in that upgrade.

### 2. Release stages

The implementation and compatibility window are:

| Release | Default | Optional compatibility | Required gate |
| --- | --- | --- | --- |
| `0.4.x` | contract `2.0` + ID-only secure default | explicit raw-path switch | Phase 0–4 schemas, lean catalog, full-contract lookup, stdio and lifecycle security pass |
| `0.5.x` | contract `2.0` + ID-only secure default | explicit raw-path switch | capability map, migration docs, and deprecation telemetry pass |
| `0.6.x` | contract `2.0` + canonical `traceId` queries | none | breaking input-schema, documentation, and reviewed-baseline closure |
| `0.7.x` | contract `2.0` + canonical `traceId` queries (unchanged) | none | additive tool/startup-option evidence, README and changelog closure |
| `1.0.0` | contract `2.0` + ID-only only | none | one full `0.5.x` deprecation window, usage telemetry review, release gate |
| contract `3.0` | exact string identifiers only | none | deprecated numeric projections removed with a separate breaking-contract changelog |

The repository may implement later-stage code before publishing it, but defaults and removal happen only at their release gates. A tool call cannot select contract or trace-reference mode. Startup rejects incompatible combinations and profiles that hide `list_capabilities`, the Tools-only `get_tool_contract` fallback, or, while analysis is enabled, `inspect_trace`.

Implementation note (2026-08-01): `RuntimeCompatibilityPolicy` now applies this
matrix once at startup. `WPAMCP_CONTRACT_MODE` / `--contract-mode` and
`WPAMCP_TRACE_REFERENCE_MODE` / `--trace-reference-mode` are explicit inputs;
CLI wins, and the resulting pair is exposed at `wpa://runtime/profile`, bound
into `tools/list` cursors, and recorded in privacy-safe telemetry. Version 0.4.0
activates the already implemented Contract 2.0 + ID-only release profile.
Versions before 0.4.0 remain release-blocked.

The runtime does **not** claim a legacy adapter exists. No released wpa-mcp
version established the Phase 0 snapshot as a supported public result wire
contract, so that snapshot is regression evidence, not a compatibility floor.
`legacy` selection fails closed with an explicit unsupported status; missing
legacy projection does not block a Contract 2.0-native `0.4.x` release. ADR
0006 removes raw-path query compatibility in `0.6.0`. The separate 1.0
historical gate remains relevant to the 1.0 release decision, but no longer
keeps the unsafe query syntax callable in the `0.6.x` runtime.

`releaseStatus` is not a vague "eligible subject to external gates" value.
Known repository-wide blockers are listed separately as
`externalKnownBlockers` and also participate in the terminal release decision.
At this implementation checkpoint the corrected active baselines are reviewed
and closed. The product owner explicitly accepts that the opaque converter's
transient physical disk peak is not hard-limited for 0.4.x. It is therefore a
non-blocking runtime warning, not an `externalKnownBlocker`. The workflow
independently validates the catalog and contract baselines and requires the
versioned risk record to state both `opaqueConverterTransientPeakProven=false`
and `wholeRootPhysicalPeakHardLimited=false`; risk acceptance cannot masquerade
as technical proof.

Phase 0 per-tool snapshots remain historical regression inputs for explicitly
reviewed correctness changes. They are not executable wire modes and are never
treated as an eternal compatibility requirement.

### 3. Atomic contract activation

Contract 2.0 is considered activatable only when every enabled tool has:

- one validated manifest and at least one CapabilityId;
- a closed input schema and closed output schema;
- schema-valid structured success;
- applicable partial/failed/no-data/truncated/privacy goldens;
- exact identifier and section-completeness treatment;
- truthful worst-case annotations and side-effect tests.

The server does not advertise a mixture where some active tools use the new envelope and others silently return unstructured legacy data.

### 4. Query planner admission

`QueryPlanner` is an internal execution layer, never a mega-tool and never a reason to remove direct tools. An analyzer operation is admitted to shared dispatch only after tests prove identical scope, event eligibility, counts, ordering, precision, completeness, evidence boundary, cancellation, and errors between standalone and planned execution.

Planner candidates require before/after evidence on representative and large traces. Admission needs:

- a named operation key/version in the active manifest;
- a compatible dispatcher/event stream and explicit accumulator dependencies;
- golden/cross-tool semantic equivalence;
- measured physical-pass, wall-time, peak-memory, and cancellation improvement;
- no reliance on `unknown` capability state as proof that scanning can be skipped.

The initial benchmark candidates are `inspect_trace`, `diagnose_window`, `diagnose_high_wait`, and `diagnose_slow_startup`; this is a measurement queue, not automatic approval. Each composite is approved individually in `benchmarks/capability-matrix.v1.json`. Direct single-tool execution remains available and shares the same operation implementation.

### 5. Facts snapshot and scan accounting

One generation-bound `TraceFactsSnapshot` owns metadata, parsed provider/event observations, process/thread identity, per-domain stack coverage, capture/parser integrity, and trace-native PDB identity. `inspect_trace` may trigger at most one physical scan for those common facts. Symbol readiness/frame resolution remains SymbolContext-bound and is not cached as a generation fact.

Every planned composite exposes logical analyzers, physical pass count, scanned/matched event counts, phase durations, and budget termination. Performance claims use those measurements and never infer MCP self-performance unless the selected trace scope contains the exact MCP server/worker instance.

### 6. Wire and client gates

Before a Contract 2.0-native `0.4.x` artifact is released, production stdio
tests must traverse every
`tools/list` page, combine discovery descriptors without omission or
duplication, and prove that every advertised contract URI/hash resolves to the
same closed output schema through the Resource path and the Tools-only
`get_tool_contract` fallback. The gate records page bytes, aggregate lean
discovery bytes, full-contract-registry bytes, and the hash closure between the
two projections. The server does not hide later tools.

Named third-party client/version runs may additionally record page aggregation,
the descriptors actually injected into the model, prompt-schema tokens, and
cache behavior. Those observations inform the compatibility table and host
guidance; absence or failure of such an observation does not block the global
release unless a later ADR explicitly makes that named client/version a support
guarantee. MCP pagination is performed by the client or host, not by the LLM.

Every outbound result uses exact complete-frame fitting after redaction and after text regeneration. Cancellation, hostile request IDs, UTF-8 boundaries, minimum legal failure, cursor expiry/tamper, and large-trace budget behavior are release gates. The release tag, assembly/package version, generated catalog hash, schemas, capability documentation, and uploaded artifacts must come from the same gated commit.

### 7. Documentation and removal

Each default change and removal updates README, architecture, capability gaps, changelog, startup examples, client compatibility, and migration examples. Raw-path deprecation warnings name the replacement and removal release. No permanent duplicate `*_v2` tool family or unshipped legacy result adapter is introduced.

## Consequences

The project carries only the explicitly implemented raw-path compatibility window; Contract 2.0 is the single result shape. The secure default and planner optimization are released only after complete discovery, contract-registry, lifecycle, package-transport, and semantic-equivalence evidence exists. Named-client observations remain useful support evidence without becoming an implicit release veto.

The release workflow now asks the exact published executable for its immutable
default runtime profile, rejects any blocked/default-explicit mismatch, runs the
package stdio gate, and binds profile, project version, commit, manifests,
active snapshots, package evidence, and release artifacts by hash. Missing
raw-path deprecation-history evidence remains a 1.0 removal blocker rather than
a documentation-only warning.
