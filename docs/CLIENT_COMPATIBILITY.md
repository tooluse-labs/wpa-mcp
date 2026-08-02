# MCP client compatibility

wpa-mcp exposes its full capability surface and full evidence boundaries. A
compatible client must reduce discovery cost without silently discarding tool
descriptors, continuations, or uncertainty fields. Full result contracts are
available on demand and are not broadcast inline with every descriptor.

The validated development surface publishes its current tool, capability,
goal, and workflow totals with the catalog. Clients must use the advertised
catalog version and totals rather than hard-code a snapshot.

## Required client behavior

- Negotiate the repository-pinned MCP protocol profile over stateful stdio.
- Follow `tools/list.nextCursor` until absent and combine pages without
  omission, duplication, or reordering. This is MCP client/host work, not an
  LLM reasoning task.
- Preserve each complete lean discovery descriptor: name, description, input
  schema, annotations, and Contract 2.0 URI/version/hash. A descriptor is an
  indivisible paging unit; the full output schema is not required inline.
- Fetch `wpa://contracts/tools/{toolName}/{sha256}` only when deep
  client-side result validation is needed. Follow its immutable byte-range page
  index, concatenate fragments in order, and verify the advertised UTF-8 size
  and SHA-256. A Tools-only client uses
  `get_tool_contract(toolName, page)` to retrieve the same canonical bytes in
  deterministic pages. Both paths use identical fixed 8,192-UTF-8-byte page
  boundaries. The server fails startup if the configured response cap cannot
  deliver every Resource page and mirrored Contract 2.0 Tool page; the reviewed
  current catalog requires 35,858 bytes even though `tools/list` alone can page
  at a lower cap.
- Resolve a fetched output schema according to its declared JSON Schema 2020-12
  dialect. The server uses only one-segment same-document references of the
  form `#/$defs/<safe-id>` and rejects dangling, cyclic, multi-segment,
  anchored, or external references. A validator must not silently treat a
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
its discovery descriptor names the immutable full-contract resource at
`wpa://contracts/tools/{toolName}/{sha256}`. Read it only when a UI,
validator, code generator, or diagnostic needs the deep schema, then follow its
page index and verify the reassembled bytes. Also read
`wpa://tools/{toolName}/sections` and all listed pages before relying on section
ordering or evidence semantics. These resources are same-source projections of
the Active Catalog and are frame-budgeted.

The host should assemble and cache the complete lean discovery catalog, then
use the capability map and current task to inject only relevant descriptors
into the LLM context. That progressive injection is a host responsibility; it
does not change the server catalog. A host that omits a descriptor must not
report the corresponding capability as absent from wpa-mcp.

## Evidence status

For release evidence, the in-repository package harness must exercise the exact
published executable over raw stdio. It must initialize with the maximum
accepted serialized request-ID size, walk all tool and capability pages, verify
the lean descriptors, resolve every advertised full-contract URI/hash through
the Resource and Tools-only paths, validate synchronized structured/text
output, read capability and runtime resources, check complete frame budgets,
and verify that the executable is unchanged. A separate case must prove hostile
first-frame rejection occurs before mutable telemetry/trace/symbol side effects.

This is protocol/package evidence, not proof for every named third-party
client. Named client/version runs may record page aggregation, which descriptors
the host injects, prompt-schema token cost, and prefix-cache behavior. Those
observations update the compatibility table and host guidance; they are not a
global release blocker unless a future ADR explicitly guarantees that named
client/version and defines its acceptance criteria. The server will not hide
later descriptors to accommodate a host that ignores cursors.

The corrected active-tool, DTO/stdio, lean-payload, pagination, historical-hash,
and full-contract registry baselines are reviewed and automatically bound to
the active manifests/profile. That closes the former corrected-active-baseline
blocker. The independent opaque-converter transient physical-peak boundary is
now an explicitly accepted 0.4.x residual risk. It remains disclosed in the
runtime warnings and risk-acceptance evidence; catalog gates do not turn it
into a proven hard bound.

## Profile support

| Runtime profile | Client expectation | Current implementation |
|---|---|---|
| Contract 2.0 + ID-only | Lean discovery plus on-demand closed envelope contracts; `load_trace`/TraceId lifecycle | 0.4.x release profile |
| Contract 2.0 + raw compatibility | Same discovery/contract projections; raw paths deprecated and may create a canonical handle | Explicit startup compatibility only; removed in 1.0 |
| Legacy + either trace mode | No released compatibility contract; Phase 0 goldens are regression evidence only | Unsupported; startup fails closed without blocking Contract 2.0 releases |

See `CONTRACT_MIGRATION.md` and ADR 0005 for version defaults and removal dates.
