# Contract and trace-reference migration

This document describes the Contract 2.0 surface after ADR 0006. Contract mode
is selected before stdin and remains immutable for the server process. Trace
queries no longer have a selectable reference mode: `load_trace.tracePath` is
the only raw path input and every subsequent tool uses `traceId`.

## Current active surface

The validated development catalog advertises its current tool, capability,
goal, and workflow totals at runtime; clients must not hard-code a snapshot.
Every active tool has one closed Contract 2.0 output schema in the full contract
registry and returns the same finalized envelope in `structuredContent` and
text JSON. `tools/list` carries a lean discovery descriptor with the complete
input schema plus the full contract URI/version/hash; it does not need to inline
the deep output schema. The historical 61-tool/5-structured-tool snapshot
remains migration evidence only; it is not the current runtime.

Capability and evidence discovery is available through `list_capabilities` and
`inspect_trace.TraceEvidenceMap`. Same-source Resources provide byte-budgeted
indexes at `wpa://capabilities/server`, `wpa://tools/server`, and
`wpa://workflows/server`. Each active tool links to
`wpa://contracts/tools/{toolName}/{sha256}` for its immutable full output
contract page index; clients concatenate its byte-range fragments and verify
the advertised size/hash. Tools-only clients retrieve the same canonical bytes
in deterministic `get_tool_contract(toolName, page)` pages. Both lookup paths
share fixed 8,192-UTF-8-byte boundaries and stable page identities. Startup
measures every actual Resource/Tool frame with the maximum legal request ID and
rejects a cap below the active catalog's measured minimum before stdin (35,858
bytes for the reviewed current catalog). Each active tool
also links to
`wpa://tools/{toolName}/sections`; follow its numbered pages to obtain the
complete per-section ordering, completeness proof, evidence, measurement,
relationship, and conclusion contract. Resources reduce repeated discovery
cost but do not replace the Tool-only path.

## Release matrix

| Release line | Required default | Allowed explicit compatibility | Removal/gate |
|---|---|---|---|
| 0.4.x–0.5.x | Contract `2.0` + `id_only` | historical explicit raw-path mode | Closed compatibility window |
| 0.6.x | Contract `2.0` + canonical `traceId` queries | none | Input schema, documentation, and reviewed baseline closure |
| 1.0.0+ | Contract `2.0` + canonical `traceId` queries | none | Separate 1.0 release-governance gate |

No released wpa-mcp version established the Phase 0 snapshot as a supported
public result wire contract. The snapshot remains historical regression
evidence, not an executable compatibility floor. A request for `legacy` fails
startup with
`unsupported:no_released_legacy_result_contract_exists;contract_2.0_is_the_only_runtime_shape`.
This prevents the Contract 2.0 envelope from being mislabeled, but the absence
of a legacy adapter does not block a Contract 2.0-native `0.4.x` release.

## Configuration and precedence

Environment form:

```json
{
  "env": {
    "WPAMCP_CONTRACT_MODE": "2.0"
  }
}
```

Command form:

```text
wpa-mcp.exe --contract-mode 2.0
```

Command-line values override environment values. Contract values are closed
and case-sensitive (`legacy`, `2.0`). The removed `compatibility` trace mode
fails before the MCP transport begins serving.

## Client migration

1. Read `wpa://runtime/profile` after initialization and retain its
   `contractMode`, `traceReferenceMode`, warnings, and blockers with diagnostic
   evidence.
2. For Contract 2.0, consume `structuredContent`. Use the contract URI/hash in
   the lean descriptor to fetch the full schema only when deep client-side
   validation is needed. Follow the Resource page index or call
   `get_tool_contract(toolName, page)`, concatenate fragments in byte order, and
   verify the advertised UTF-8 size/hash. The two paths must expose the same
   fixed page boundaries. Treat text as a synchronized
   rendering, not a second source of facts.
3. Call `load_trace` with `tracePath`, then pass the returned opaque value as
   `traceId` to every query. Do not send `path` to query tools and do not
   reconstruct or persist server-internal paths.
4. The MCP client or host, not the LLM, traverses every `tools/list` cursor page.
   A cursor is bound to the server instance, catalog, ordering, and contract
   mode; never replay it after a restart or profile change. The host may then
   use the capability map to inject only task-relevant lean descriptors without
   changing the server catalog.
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

There is no permanent `*_v2` tool family and no unshipped legacy result adapter.
Contract 2.0 is the only result shape. Query-side raw-path compatibility was
removed in `0.6.0`; there is no legacy input alias.

## Operator checks

`wpa-mcp.exe --runtime-profile` prints the default profile without starting
MCP. `wpa-mcp.exe --validate-release-profile` exits 78 when the version line,
default pair, correctness evidence, or deprecation-history gate is not
releasable. The release workflow runs these commands against the exact packaged
executable and
then verifies package stdio evidence, version, commit, schemas, and snapshots.
`externalKnownBlockers` no longer contains the opaque-converter transient peak;
the non-hard-limited peak is an explicitly accepted 0.4.x residual risk exposed
through `warnings`. The workflow separately validates the reviewed
catalog/contract baselines and the truthful risk record, including
`opaqueConverterTransientPeakProven=false`, so risk acceptance cannot be
misrepresented as a hard-bound proof.

Named third-party client/version runs may measure catalog aggregation, injected
descriptor tokens, and prompt-cache behavior. They are non-blocking
compatibility observations unless a future ADR explicitly declares a support
guarantee for that named client/version.

The corrected active snapshots, lean measurements, pagination evidence, and
full-contract registry are regenerated and reviewed together in this change.
Installation/configuration must also pass packaged stdio startup; legacy
`--symbol-path` configuration is rejected by secure-default and must be migrated
to approved local roots/store plus `prepare_symbols`.
