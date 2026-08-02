# ADR 0003: Active catalog, contract v2, discovery, and evidence registry

- Status: Accepted
- Decision date: 2026-08-01
- Amended: 2026-08-02 — make Contract 2.0 the sole runtime shape and separate
  lean discovery from full result contracts
- Decision source: implementation authorization through `/goal`
- Amends: ADR 0002 and the approved production-remediation contract plan
- Implementation status: not complete

## Context

ADR 0002 accepted the capability-map and evidence-contract direction but deliberately left the public catalog, result contract, cursor, identifier, and inference registries undecided. Those choices must be fixed before a programmatic catalog or public vNext adapter can become authoritative. The decision must retain the reviewed Phase 0 snapshots as regression evidence without inventing a compatibility obligation for an unpublished result shape, while preventing a model from confusing availability, scope, completeness, precision, or association with stronger facts.

## Decision

### 1. One validated active model

The repository has three declarative inputs with non-overlapping ownership:

- `eng/capabilities.v1.json` owns stable capability meaning and evidence requirements.
- `eng/tool-contracts.v2.json` owns tool-to-capability mapping and MCP contract metadata.
- `benchmarks/capability-matrix.v1.json` owns maturity and executable evidence references.

`ActiveToolCatalog` joins those files to SDK-created typed tool methods and generated schemas at startup. The joined immutable catalog is the only production source for calls, capability routing, schemas, annotations, pagination metadata, documentation projections, snapshots, and both public catalog projections described below. Assembly reflection may discover typed implementations during catalog construction, but no other component may independently infer a second tool list or result contract. Missing or duplicate tools, dangling capabilities, invalid schema pointers, evidence-reference drift, or annotation mismatch fail before the transport reads stdin.

The public surface has two projections from that one validated model:

- a lean discovery projection for `tools/list`, containing tool identity,
  description, complete input schema, annotations, and a stable Contract 2.0
  locator/version/hash; and
- a full contract registry containing each closed `ToolEnvelope<TData>` output
  schema used by server-side validation and optional deep client validation.

The projections may have different serialized sizes, but they may not disagree
about tool identity, contract version, or schema hash. The full registry is not
a second hand-maintained schema source.

The JSON files are reviewed source; generated C# projections and snapshots are derived artifacts. A generated artifact never overrides its source manifest. Runtime startup validation remains mandatory even after build-time validation.

Tool-level scope metadata is named `selectableScopes` and means only scopes a caller can select through that tool's public input schema. It does not describe result granularity, evidence completeness, or conclusion strength, and no parallel `supportedScopes` alias is exposed for tools. Catalog validation binds `thread` to `tid`, `time_window` to both `startUs` and `endUs`, `focus_frame` to `focusFunction` or `function`, `provider` to a public provider selector, and `process` to a reviewed PID-class or explicit process-name selector. A declaration without its selector fails startup. Capability-level `supportedScopes` remains collective: for an implemented capability, it equals the union of mapped tools' `selectableScopes`, so every listed scope is selectable by at least one mapped tool but not necessarily every mapped tool, and no mapped selector is hidden; gap entries describe intended scope without a callable mapping. Public Tool and Resource projections include stable semantics text for both fields so clients do not have to infer this distinction.

### 2. Capability identity and versioning

`CapabilityId` is a lowercase dotted identifier matching `^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$`. An ID names one stable question-and-evidence boundary, not a display title or implementation. Adding evidence, tools, or compatible fields does not rename the ID. Changing the question answered, maximum conclusion strength, required evidence domain, or scope semantics creates a new ID.

Each capability carries an integer `definitionVersion`, initially `1`. Compatible clarification increments the definition version while retaining the ID. A rename or semantic replacement keeps the old entry as `deprecated`, supplies `replacedBy`, and declares `removalContractVersion`; aliases are never silently resolved in runtime evidence. `capabilities.v1.json` is the manifest schema generation, while `catalogVersion` is a deterministic content version/hash of the validated active model.

### 3. Public result contract

The structured result contract is literal `"2.0"` and is the only executable
result shape. Contract selection is immutable at server startup; individual
calls cannot override it. `legacy` may be parsed only to return an explicit
unsupported status because no released wpa-mcp version established that shape
as a supported public contract.

Contract `2.0` extends the single approved `ToolEnvelope<TData>`; it does not introduce a second nested envelope. Its required top-level wire properties are:

```text
contractVersion, status, data, error, failedSections, sections, warnings, hasMore,
toolRef, traceRef, scope, capabilityEvidence, completeness, evidenceBoundary,
noData, precision
```

All names are camelCase. `toolRef`, `scope`, arrays, completeness, evidence boundary, and precision are required. `traceRef`, `data`, `error`, and `noData` are required nullable. Server/lifecycle operations use explicit `not_applicable` scope/capability states rather than omitting the header. Domain data remains a closed, strongly typed schema with `additionalProperties=false`; arbitrary object data is forbidden.

The envelope state invariants are:

- `succeeded`: non-null usable data, null top-level error, no failed sections.
- `partial`: non-null usable data and at least one explicitly incomplete/failed/skipped section.
- `failed`: null data, non-null stable error, and MCP `isError=true`.
- Top-N or cursor paging alone changes completeness, not `status`.
- A failed selector may retain authorized replay candidates in the common scope header, never in domain data.

`structuredContent` and the single text content block are two serializations of the same final redacted, paged, and fitted envelope and must be JSON-semantic equals on every success and failure. A summary, pointer, or structured-only fallback is forbidden because it creates two observable contracts. If the mirrored frame cannot fit, the server uses only a manifest-declared retrievable cursor boundary; otherwise it returns an atomic mirrored failure. An already-failed result retains its stable error code while budget-only evidence details are compacted. Phase 0 output schemas and result goldens remain historical regression inputs for explicitly reviewed correctness changes; they are not an executable compatibility floor.

### 4. No-data and error registry

Selector, scope, lifecycle, authorization, execution, cancellation-without-data, and minimum-frame failures are `failed` errors. Contract 2.0 initially registers:

```text
invalid_argument
process_instance_not_found
process_start_required
ambiguous_process_instance
thread_instance_not_found
ambiguous_thread_instance
trace_not_loaded
trace_access_denied
trace_conversion_failed
symbol_context_expired
symbol_policy_denied
invalid_cursor
analysis_failed
cancelled
budget_exceeded
response_too_large
```

A resolved and successfully evaluated scope with no domain data is `succeeded` with typed NoData. Initial reasons are:

```text
event_class_not_observed
no_events_in_scope
no_completed_intervals_in_scope
unpaired_endpoints_in_scope
source_events_unattributed
stacks_unavailable
symbols_unresolved
focus_not_found
no_candidates_in_considered_input
no_capabilities_match_filter
invalid_lifetime_boundaries
```

`no_capabilities_match_filter` is reserved for a successfully evaluated
`list_capabilities` request whose normalized domain/goal filter matched no
declared capability. It must not be used for trace-domain absence or analyzer
failure.

`invalid_lifetime_boundaries` is reserved for attributable lifecycle records
whose projected end boundary is at or before the start boundary. Those rows are
excluded; the server does not invent a positive duration.

`no_completed_intervals_in_scope` means attributable scoped endpoints were observed but no valid completed interval could be projected; it must never be normalized to `no_events_in_scope`. `unpaired_endpoints_in_scope` likewise preserves the observed-but-unpaired endpoint boundary and is not an alias for unattributed source events.

`not_concluded` is a conclusion status, not a generic NoData reason. Composite sections carry their own NoData; top-level NoData is non-null only when every requested domain-data or domain-evidence section has no usable data. Boundary, provenance, recommendation, and diagnostic rows never turn an otherwise empty result into usable domain data.

### 5. Exact identifiers

Opaque and platform identifiers have exactly one authoritative string representation:

- protocol/business unsigned IDs such as connection IDs use invariant unsigned decimal without leading zeroes;
- pointer/address/FileObject/FileKey/handle values use `0x` plus 16 lowercase hexadecimal digits;
- PID and TID remain bounded JSON integers;
- server-minted trace and symbol tokens use the grammars fixed by ADR 0004;
- cursors are random registry locators and disclose no query or trace material.

During contract 2.0 compatibility, a deprecated numeric projection may be required-nullable and populated only for values at or below `9007199254740991`. Its sibling precision/deprecation state is required. Unsafe values produce numeric `null`, never a rounded number. Contract `3.0` removes those numeric projections. A schema linter rejects public `ulong` and identifier-like unbounded integers unless explicitly allow-listed as bounded selectors.

### 6. Section completeness

Every bounded collection maps to a `ToolSectionPage` with required fields:

```text
section, mode, requested, returned, totalAvailable, totalState, hasMore,
sortKey, sortDirection, tieBreakers, nextCursor, truncationReason, noData,
role, evidenceIds
```

`mode` is `none`, `top_n`, or `cursor`; `totalState` is `exact`, `lower_bound`, or `unknown`. `role` is one of `domain_data`, `domain_evidence`, `boundary`, `provenance`, `recommendation`, or `diagnostic`. A full aggregation may publish an exact pre-truncation group total. A `top+1` probe publishes only a lower bound. Fixed internal saturation without a top+1 witness publishes `totalState=unknown` and cannot invent `hasMore=true`. `hasMore`, ordering, tie-breakers, and omission cause are always observable. A multi-phase result may have a global continuation while the current section is terminal; each section publishes `hasMore/nextCursor` only when that cursor continues that section. Phase-transition cursors are never copied into inactive or exhausted sections. Changing `top` cannot change totals, denominators, scope resolution, or focus existence.

Cross-section composite identifiers form a closed graph: every evidence or boundary call reference resolves to one unique `executedToolCalls.callId`; evidence IDs are unique; not-concluded statement IDs are named `boundaryId` and are not evidence references. Response-budget fitting may trim a composite only as a proven dependency-closed unit. Until such a typed partial pager exists, the whole validated composite is atomic and an oversized response fails with `response_too_large` rather than independently trimming JSON pointers.

### 7. Cursor domains

The selected 2025-11-25 server profile is stateful, so both cursor domains use bounded server-side registries with CSPRNG 128-bit locators, idle/absolute expiry, quota, and tombstones. Cursor contents are not client-decodable payloads.

- `tools/list` tokens use prefix `tlc_`; their registry binding includes server instance, catalog hash/version, contract mode, discovery order, and next index. Invalid, expired, cross-mode, cross-instance, or tampered cursors return the approved MCP/JSON-RPC protocol error, not a tool envelope.
- analysis query-result tokens use prefix `qrc_`; their binding additionally includes principal/session, trace ID and immutable generation, symbol context (explicit null included), privacy profile, catalog-or-tool/contract version, normalized query/scope hash, ordering, phase/index, and last key. Failure returns contract-2.0 `invalid_cursor`; registry capacity and entropy failures map to `budget_exceeded` and `analysis_failed`.

Neither cursor contains a raw path, identifier candidate list, or sensitive query field. Registry exhaustion fails closed; it never falls back to an unsigned offset cursor.

### 8. Complete tool discovery

`tools/list` has deterministic `discoveryPriority`, domain, and ordinal-name ordering. Bootstrap orientation tools are first, but later pages remain part of the complete catalog. Pages are fitted against the approved complete JSON-RPC frame limit with one complete discovery descriptor as the indivisible unit. A descriptor contains the complete input schema and annotations, but need not inline the full output schema. A single descriptor that cannot fit causes startup failure.

Each descriptor carries `_meta["wpa-mcp/outputContract"]` with the contract
version, schema dialect, media type, UTF-8 size, representation, a
content-addressed URI of the form
`wpa://contracts/tools/{toolName}/{sha256}`, and the SHA-256 of the
canonical full output schema. The URI resolves to an immutable page index; its
fixed 8,192-UTF-8-byte pages reassemble the canonical UTF-8 JSON. A client follows those
pages only when it needs deep result validation. A Tools-only client retrieves
the same canonical UTF-8 bytes through deterministic
`get_tool_contract(toolName, page)` pages.
Resource and Tool responses are generated from the same registry; concatenated
bytes must match the advertised size and hash. Local `$defs` references remain
within the reassembled schema, and external network references remain forbidden.
Page boundaries and page URIs are independent of the configured frame cap.
Before stdin, startup measures every actual Resource frame and mirrored Contract
2.0 Tool frame with the maximum accepted request ID and fails closed unless the
configured cap can deliver all of them. For the reviewed current catalog this
unified minimum is 35,858 bytes; it is measured from the active registry rather
than frozen as a permanent protocol constant.

All pages combined contain every enabled tool exactly once. The server never changes the list for a loaded trace and never hides tools to meet prompt budgets. Protocol clients and hosts are responsible for following `nextCursor` and assembling the advertised catalog. A host may then use the capability map to inject only task-relevant descriptors into an LLM context, but that host policy does not mutate the server catalog and must not be reported as server-side capability absence. CI separately gates maximum page bytes and aggregate lean-discovery bytes. Full-contract-registry bytes are measured separately; successful paging does not claim lower aggregate cost for either projection.

### 9. Capability and trace maps

`list_capabilities` is an unhideable Tool in every valid profile. It returns the complete declared universe or an explicitly filtered/paged projection with pre-filter and post-filter totals. Its header declares:

```text
catalogScope = wpa_mcp_declared_capabilities
exhaustiveForWpa = false
unlistedCapabilityMeaning = unknown_not_catalogued
```

`inspect_trace` is unhideable whenever trace analysis remains enabled. It evaluates every declared capability against one immutable trace generation and returns per-domain trace/scoped counts, capture/parser integrity, event- and metric-weighted stack coverage, symbol measurement state, normalized filter, and evaluator provenance. Its `qrc_` traversal is globally ordered capabilities-by-domain-and-ID followed by workflows-by-ID; every matching capability and workflow appears exactly once. Only the first page carries the large orientation blocks; continuation pages explicitly report `evidence_continuation` and retain the evidence boundaries. Missing parser/capture evidence yields `unknown` or `partial`, never a guessed `unavailable`. Resources are same-source projections only. Tools-only clients remain sufficient through `list_capabilities`, `inspect_trace`, and `get_tool_contract`; they are not required to support Resources to invoke a domain tool.

### 10. Evidence inference registry

The closed contract-2.0 registries are:

```text
MeasurementBasis: direct | derived | heuristic | metadata | unmeasured
Relationship: descriptive | temporal | association | attribution | causal
ConclusionStatus: observed | supported | partial | not_concluded | not_applicable
```

These dimensions are orthogonal. `direct` does not imply `causal`; a ReadyThread/readier stack is at most `association`; scan-like name matching is `heuristic + association + not_concluded`; PDB identity is `metadata`, not frame resolution. `causal` is permitted only when the capability manifest declares that maximum and a named conclusion rule verifies the required paired/mechanistic evidence with acceptable scope and completeness. Each evidence item carries stable `doesNotProve` boundary codes, and runtime adapters may maintain or weaken the manifest maximum but never strengthen it.

## Compatibility and rollout

- Contract 2.0 and paged discovery activate atomically; Phase 0 goldens remain historical regression evidence rather than a second runtime mode.
- The default switches only after every active tool has a schema-valid structured result, the lean discovery budget passes, and every advertised contract locator resolves to the matching full schema through both supported retrieval paths.
- Named third-party client paging/token/cache observations are compatibility evidence, not a global release gate. A future ADR may make a named client and version blocking only by explicitly declaring that support guarantee and its acceptance criteria.
- Contract 3.0 removes deprecated unsafe numeric projections after the release window fixed by ADR 0005; there is no legacy result envelope whose removal is deferred.
- Any registry expansion or semantic strengthening is a versioned manifest/ADR change with snapshots and adversarial tests.

## Consequences

The implementation pays for two validated projections, immutable contract lookup, and stateful cursor registries. In exchange, the startup discovery catalog remains bounded while no result schema, omission, unsafe identifier, or inference boundary is weakened. Hosts can progressively expose relevant tools to an LLM without asking the server to hide capabilities or broadcasting the full contract registry into every model context.
