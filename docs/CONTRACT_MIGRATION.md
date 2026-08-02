# Contract and trace-reference migration

This document implements the release window accepted in ADR 0005. It describes
startup behavior, not a per-call option. `WPAMCP_CONTRACT_MODE`,
`WPAMCP_TRACE_REFERENCE_MODE`, `--contract-mode`, and
`--trace-reference-mode` are read before stdin; the selected pair is immutable
for that server process.

## Current active surface

The 2026-08-01 validated development catalog contains **60 active tools,
51 declared capabilities, 15 goals, and 15 workflows**. Every active tool advertises a
closed Contract 2.0 output schema and returns the same finalized envelope in
`structuredContent` and text JSON. The historical 61-tool/5-structured-tool
snapshot remains migration evidence only; it is not the current runtime.

Capability and evidence discovery is available through `list_capabilities` and
`inspect_trace.TraceEvidenceMap`. Same-source Resources provide byte-budgeted
indexes at `wpa://capabilities/server`, `wpa://tools/server`, and
`wpa://workflows/server`. Each active tool links to
`wpa://tools/{toolName}/sections`; follow its numbered pages to obtain the
complete per-section ordering, completeness proof, evidence, measurement,
relationship, and conclusion contract. Resources reduce repeated discovery
cost but do not replace the Tool-only path.

## Release matrix

| Release line | Required default | Allowed explicit compatibility | Removal/gate |
|---|---|---|---|
| 0.3.x development | Contract `2.0` + `id_only` in the current source tree | raw-path `compatibility`; `legacy` is rejected | Not a publishable ADR 0005 release line |
| 0.4.x | `legacy` result + raw-path `compatibility` | Contract `2.0`; `id_only`; either opt-in independently | Cannot ship until the reviewed Phase 0 legacy result floor is runnable |
| 0.5.x | Contract `2.0` + `id_only` | explicit `legacy` result and/or raw-path `compatibility` | Requires supported-client paging/caching evidence and corrected active baselines |
| 1.0.0+ | Contract `2.0` + `id_only` only | none | Requires one full 0.5.x deprecation window and usage-telemetry review |

The active runtime does not currently contain a trustworthy legacy result
adapter. A request for `legacy` therefore fails startup with
`release_blocked:not_implemented;phase0_legacy_floor_is_not_projected_by_the_active_runtime`.
This deliberately prevents the Contract 2.0 envelope from being mislabeled as
legacy. It also means the current code cannot publish a conforming 0.4.x
artifact, even though an explicit Contract 2.0 development profile can run.

## Configuration and precedence

Environment form:

```json
{
  "env": {
    "WPAMCP_CONTRACT_MODE": "2.0",
    "WPAMCP_TRACE_REFERENCE_MODE": "id_only"
  }
}
```

Command form:

```text
wpa-mcp.exe --contract-mode 2.0 --trace-reference-mode id_only
```

Command-line values override environment values. Values are closed and
case-sensitive for the contract (`legacy`, `2.0`); trace-reference values are
`compatibility`, `id_only` (or the equivalent `id-only`). Unknown, removed, or
unimplemented combinations fail before the MCP transport begins serving.

## Client migration

1. Read `wpa://runtime/profile` after initialization and retain its
   `contractMode`, `traceReferenceMode`, warnings, and blockers with diagnostic
   evidence.
2. For Contract 2.0, consume `structuredContent` and its declared output schema.
   Treat text as a synchronized rendering, not a second source of facts.
3. Replace raw paths with `load_trace` followed by the returned opaque TraceId.
   Do not reconstruct or persist server-internal paths.
4. Traverse every `tools/list` cursor page. A cursor is bound to the server
   instance, catalog, ordering, and contract mode; never replay it after a
   restart or profile change.
5. Preserve exact string identifiers, evidence/completeness state, no-data
   reasons, and process/thread instance selectors. Do not infer a conclusion
   from an empty row array.
6. Interpret each `sections[]` item independently. Composite sections may have
   different ordering, proof modes, measurement bases, relationships, and
   conclusions; there is no valid synthetic tool-wide comparator for them.
7. Treat budget fitting as part of the contract. A resumable section has an
   explicit cursor and `truncationReason=response_budget`. A terminal
   `response_too_large` failure has `data=null`, `scope=null`, empty sections,
   and `hasMore=false`; it is not an empty result for the requested scope and
   cannot be continued.
8. Use `prepare_symbols` only for verified local readiness. The current build
   deliberately fails `resolveSymbols=true` with `symbol_resolution_unavailable`
   because no context-bound TraceEvent frame adapter is available. Keep frame
   resolution unmeasured rather than falling back to legacy ambient lookup.

There is no permanent `*_v2` tool family. Contract compatibility is a startup
adapter around the same active tool names and has a 1.0 removal deadline.

## Operator checks

`wpa-mcp.exe --runtime-profile` prints the default profile without starting
MCP. `wpa-mcp.exe --validate-release-profile` exits 78 when the version line,
default pair, legacy floor, or deprecation-history gate is not releasable. The
release workflow runs these commands against the exact packaged executable and
then verifies package stdio evidence, version, commit, schemas, and snapshots.
`externalKnownBlockers` currently also prevents an eligible status until the
corrected active baselines, supported-client matrix (for 0.5+), and opaque
converter transient-peak evidence are release-approved. The workflow requires
and validates those evidence artifacts independently, so changing one runtime
constant cannot bypass the gate.

The corrected active snapshot files may exist in the working tree while this
blocker remains set. File presence is not approval: they must be regenerated
from and reviewed against the same commit, profile, manifests, and packaged
executable. Installation/configuration must also pass packaged stdio startup;
legacy `--symbol-path` configuration is rejected by secure-default and must be
migrated to approved local roots/store plus `prepare_symbols`.
