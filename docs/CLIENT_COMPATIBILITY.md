# MCP client compatibility

wpa-mcp exposes its full capability surface and full evidence boundaries. A
compatible client must reduce discovery cost without silently discarding tools,
schemas, continuations, or uncertainty fields.

The current validated development surface is 60 active tools mapped to 51
declared capabilities, 15 goals, and 15 workflows. Those counts are a snapshot for
cross-checking full traversal; clients must use the advertised catalog version
and totals rather than hard-code them.

## Required client behavior

- Negotiate the repository-pinned MCP protocol profile over stateful stdio.
- Follow `tools/list.nextCursor` until absent and combine pages without
  omission, duplication, or reordering. A page-one-only client is incompatible.
- Preserve each tool's complete input and output schema. A tool/schema is an
  indivisible paging unit.
- Resolve every output schema according to its declared JSON Schema 2020-12
  dialect. The server uses only one-segment same-document references of the
  form `#/$defs/<safe-id>` and rejects dangling, cyclic, multi-segment,
  anchored, or external references. A client that ignores or cannot resolve
  these local references is incompatible; it must not silently treat a
  referenced schema as permissive. External references must never trigger a
  network fetch.
- Consume Contract 2.0 `structuredContent`; verify or treat `content` text as
  its synchronized rendering. Do not prefer text when the structured result is
  present.
- Preserve JSON string identifiers exactly. Do not coerce TraceId, SymbolContextId,
  connection/file/handle/address identifiers, or 64-bit quantities through a
  JavaScript `number`.
- Treat `prepare_symbols` as verified local-readiness evidence only. In the
  current build, `resolveSymbols=true` fails closed with
  `symbol_resolution_unavailable` / `context_bound_frame_resolution_unavailable`;
  do not reinterpret preparation, unsymbolized frames, or an external/offline
  resolution statistic as MCP-observed frame resolution.
- Follow tool-level cursors only with the same principal/session, trace
  generation, contract, query, scope, symbol context, and privacy profile.
- Use structured scope, capability, completeness, precision, no-data,
  provenance, and conclusion-boundary fields. Empty rows, synthetic unknown
  stacks, PDB identity, and heuristic association are not conclusions.
- Interpret `sections[]` per section. Preserve its role, exact ordering/tie
  breakers, total/more state, proof mode, continuation, evidence IDs,
  measurement basis, relationship, and conclusion status. Do not apply one
  tool-wide ordering or evidence claim to a heterogeneous composite.
- Treat `response_too_large` as a terminal delivery failure. Its truthful
  compact shape has `data=null`, `scope=null`, empty sections, and
  `hasMore=false`; it neither proves an empty requested scope nor offers a
  continuation. Follow a cursor only when the section explicitly publishes it.
- Read `wpa://runtime/profile` to learn the immutable contract/trace-reference
  modes, output-schema dialect/reference requirements, and deprecation/release
  boundaries for this server instance.

## Capability and contract resources

Resource-capable clients should start with the small indexes at
`wpa://capabilities/server`, `wpa://tools/server`, and
`wpa://workflows/server`, then follow every listed page. For a selected tool,
read `wpa://tools/{toolName}/sections` and all listed pages before relying on
section ordering or evidence semantics. These resources are same-source
projections of the Active Catalog and are frame-budgeted; they do not authorize
a client to skip `tools/list` pages or hide a capability from a Tools-only
model.

## Evidence status

The in-repository package harness exercises the exact published executable over
raw stdio. It initializes with the maximum accepted serialized request-ID size,
walks all tool and capability pages, verifies schemas and synchronized
structured/text output, reads capability and runtime resources, checks complete
frame budgets, and verifies that the executable is unchanged. It separately
proves hostile first-frame rejection occurs before mutable telemetry/trace/
symbol side effects.

This is protocol/package evidence, not proof for every named third-party
client. Prompt-schema token cost, prefix-cache behavior, and complete page
aggregation remain `release_blocked:supported_client_matrix_incomplete` until
recorded for each supported client/version. The server will not hide later
tools to accommodate a client that ignores cursors.

The release workflow requires a passed
`eng/contract-baselines/supported-client-matrix.v1.json` whose every client row
records full-page consumption, measured schema tokens, and measured prompt-cache
behavior. The file is currently absent, so both the runtime profile and workflow
remain blocked rather than treating the raw stdio harness as client evidence.

Corrected active snapshot files can exist before they are release-approved.
The runtime deliberately keeps
`release_blocked:corrected_active_contract_baselines_not_release_approved`
until the snapshots, manifests, profile, package executable, and commit have
been reviewed as one evidence set.

## Profile support

| Runtime profile | Client expectation | Current implementation |
|---|---|---|
| Contract 2.0 + ID-only | Closed envelope schemas; `load_trace`/TraceId lifecycle | Runnable development profile |
| Contract 2.0 + raw compatibility | Same envelope; raw paths deprecated and may create a canonical handle | Explicit startup compatibility only; removed in 1.0 |
| Legacy + either trace mode | Phase 0 legacy goldens, not Contract 2.0 relabeled | Not implemented; startup fails closed |

See `CONTRACT_MIGRATION.md` and ADR 0005 for version defaults and removal dates.
