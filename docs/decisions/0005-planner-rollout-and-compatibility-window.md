# ADR 0005: Planner scope, rollout, and compatibility window

- Status: Accepted
- Decision date: 2026-08-01
- Decision source: implementation authorization through `/goal`
- Depends on: ADR 0001, ADR 0003, ADR 0004
- Implementation status: partially implemented; rollout selection and release
  enforcement are active, but no legacy result adapter or reviewed 1.0
  deprecation-window evidence exists

## Context

The capability/evidence refactor needs an explicit rollout that does not publish a half-structured contract, silently remove legacy behavior, or use planner optimization to change evidence. It must also remain on the already selected stable .NET/MCP protocol profile until a new platform matrix approves an upgrade.

## Decision

### 1. Platform boundary

ADR 0001 remains authoritative: .NET SDK 10.0.302, `net10.0`, ModelContextProtocol 1.4.1, protocol revision 2025-11-25, stateful profile, and Windows x64. The catalog and cursor implementation uses the stable SDK's custom list/call handlers and stateful server services. A 2026-07-28/stateless or MCP 2.x transition requires a new platform ADR and the full host-mode/protocol matrix; this refactor does not smuggle in that upgrade.

### 2. Release stages

The implementation and compatibility window are:

| Release | Default | Optional compatibility | Required gate |
| --- | --- | --- | --- |
| `0.4.x` | legacy result + raw-path compatibility | opt-in contract `2.0`; opt-in ID-only | Phase 0–4 schemas, stdio and lifecycle security pass |
| `0.5.x` | contract `2.0` + ID-only secure default | explicit legacy/result and raw-path switches | supported-client catalog paging, capability map and migration docs pass |
| `1.0.0` | contract `2.0` + ID-only only | none | one full `0.5.x` deprecation window, usage telemetry review, release gate |
| contract `3.0` | exact string identifiers only | none | deprecated numeric projections removed with a separate breaking-contract changelog |

The repository may implement later-stage code before publishing it, but defaults and removal happen only at their release gates. A tool call cannot select contract or trace-reference mode. Startup rejects incompatible combinations and profiles that hide `list_capabilities` or, while analysis is enabled, `inspect_trace`.

Implementation note (2026-08-01): `RuntimeCompatibilityPolicy` now applies this
matrix once at startup. `WPAMCP_CONTRACT_MODE` / `--contract-mode` and
`WPAMCP_TRACE_REFERENCE_MODE` / `--trace-reference-mode` are explicit inputs;
CLI wins, and the resulting pair is exposed at `wpa://runtime/profile`, bound
into `tools/list` cursors, and recorded in privacy-safe telemetry. The current
0.3.0 development line retains its already implemented Contract 2.0 + ID-only
default but is release-blocked because it precedes 0.4.0.

The runtime does **not** claim a legacy adapter exists. `legacy` selection fails
closed with
`release_blocked:not_implemented;phase0_legacy_floor_is_not_projected_by_the_active_runtime`.
Consequently a 0.4.x artifact remains release-blocked even if its explicit
Contract 2.0 profile can start: its required legacy default is unavailable.
The 1.0 gate likewise remains
`release_blocked:no_reviewed_full_0.5.x_window_or_usage_telemetry_evidence`;
it cannot be waived by environment configuration.

`releaseStatus` is not a vague "eligible subject to external gates" value.
Known repository-wide blockers are listed separately as
`externalKnownBlockers` and also participate in the terminal release decision.
At this implementation checkpoint they include corrected active baselines,
the unproven opaque-converter transient artifact peak, and for 0.5+ the
supported-client paging/token/cache matrix. The workflow independently requires
passed evidence documents for these gates before artifact creation.

Legacy mode preserves the Phase 0 per-tool catalog/schema and success/failure/boundary goldens except for explicitly approved correctness fixes. Known incorrect behavior receives a dedicated migration golden and is never protected as an eternal compatibility requirement.

### 3. Atomic contract activation

Contract 2.0 is considered activatable only when every enabled tool has:

- one validated manifest and at least one CapabilityId;
- a closed input schema and closed output schema;
- schema-valid structured success;
- applicable partial/failed/no-data/truncated/privacy goldens;
- exact identifier and section-completeness treatment;
- truthful worst-case annotations and side-effect tests.

Until then, contract 2.0 remains opt-in development mode. The server does not advertise a mixture where some active tools use the new envelope and others silently return unstructured legacy data.

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

Before `0.5.x` becomes default, production stdio tests must traverse every `tools/list` page with each supported client behavior, combine pages without omission/duplication, and record page bytes, aggregate bytes, schema tokens, and prompt-cache behavior. Clients that only consume page one are declared incompatible; the server does not hide later tools.

Every outbound result uses exact complete-frame fitting after redaction and after text regeneration. Cancellation, hostile request IDs, UTF-8 boundaries, minimum legal failure, cursor expiry/tamper, and large-trace budget behavior are release gates. The release tag, assembly/package version, generated catalog hash, schemas, capability documentation, and uploaded artifacts must come from the same gated commit.

### 7. Documentation and removal

Each default change and removal updates README, architecture, capability gaps, changelog, startup examples, client compatibility, and migration examples. Deprecation warnings name the replacement and removal release. No permanent duplicate `*_v2` tool family is introduced; compatibility is an adapter around the same active implementation and has an explicit end.

## Consequences

The project carries compatibility adapters through one minor-release window and must maintain two startup contract modes temporarily. In return, the secure default and planner optimization are released only after complete catalog, schema, lifecycle, client, and semantic-equivalence evidence exists.

The release workflow now asks the exact published executable for its immutable
default runtime profile, rejects any blocked/default-explicit mismatch, runs the
package stdio gate, and binds profile, project version, commit, manifests,
active snapshots, package evidence, and release artifacts by hash. This makes
the missing legacy and deprecation-history evidence release blockers rather
than documentation-only warnings.
