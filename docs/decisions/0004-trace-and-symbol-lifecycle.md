# ADR 0004: Principal-scoped trace and immutable symbol lifecycle

- Status: Accepted
- Decision date: 2026-08-01
- Amended: 2026-08-02 — explicit 0.4.x acceptance of the opaque converter
  transient physical-peak residual risk
- Decision source: implementation authorization through `/goal`
- Amends: ADR 0002 and the approved trace access/lifecycle plans
- Implementation status: not complete

## Context

Raw-path query tools currently combine selection, trace conversion, backend caching, symbol policy, and analysis. That makes a nominal query capable of filesystem writes, network access, or process-global mutation, and makes concurrent calls capable of duplicate conversion. A secure result contract also needs a stable, privacy-safe reference to the exact trace generation and symbol assessment used.

## Decision

### 1. Four independent identities

The runtime keeps these identities separate:

- `TraceGenerationKey`: internal immutable source/artifact/backend identity; never public.
- `TraceId`: principal/session-scoped public handle bound to exactly one generation.
- `SymbolContextId`: principal/session-scoped immutable symbol policy/readiness snapshot.
- `ArtifactKey`: internal content-addressed derived artifact identity with independent retention.

A public trace result uses `traceId` as its replay identity. No separate public generation alias is emitted in contract 2.0. This avoids exposing a content/path fingerprint or creating two replay authorities.

### 2. Token grammars and isolation

`TraceId` is `trc_` followed by 32 lowercase hexadecimal digits generated from 128 CSPRNG bits. `SymbolContextId` is `sym_` followed by 32 lowercase hexadecimal digits generated the same way. Tokens are bearer-like sensitive locators but not authorization credentials.

Registry lookup keys are `(principal/session, token)`. The stdio host creates one explicit session principal scoped to that server process. A future shared host must bind to its authenticated principal. Cross-principal lookup is externally indistinguishable from a random unknown token. Tokens are redacted from ordinary logs/telemetry and governed by per-principal count, creation-rate, idle/absolute TTL, and tombstone quotas.

Malformed reserved-prefix input is `invalid_argument`. A canonical but unknown/expired/unloaded token never falls back to path parsing. Same-principal lifecycle detail may distinguish `unknown`, `expired`, and `unloaded` in a stable error detail; cross-principal lookup may not reveal existence.

### 3. Canonical load and generation single-flight

Within one principal and server process, repeated `load_trace` of the same unchanged source generation returns the same active canonical `TraceId`. `load_trace` is therefore idempotent after normalization. A replaced source creates a new generation and trace ID; an existing ID never silently rebinds.

Before identifying a generation, the loader performs:

```text
policy validation -> safe source handle -> same-object/source snapshot identity
-> TraceGenerationKey single-flight -> immutable artifact/backend publication
```

All concurrent loads, compatibility path queries, and composite subcalls for one generation share exactly one conversion and backend construction. Each caller holds a lease. Failure or cancellation publishes neither a half-initialized registry entry nor an incomplete artifact and releases every reservation.

Compatibility raw-path queries route through the same loader and return the canonical trace reference in their result. They do not create hidden permanent handles. A composite resolves its trace once and shares one generation lease and facts snapshot across its logical analyzers.

The production registry and the `load_trace`/`unload_trace` handle operations are implemented. The declared `lifecycle.trace.handle` capability nevertheless remains a product gap because there is no independent callable handle-status/inspection surface with a capability-keyed runtime outcome. A load or unload result proves only that operation's scoped outcome; it does not expose a principal's active-handle inventory, lease state, expiry state, or generation-status view.

### 4. Unload and artifact retention

`unload_trace(traceId)` retires only the caller's handle binding, prevents new leases, drains existing leases, and releases that handle's backend reference. A repeated call returns stable `already_unloaded`, so unload is idempotent. It is mutating/destructive with respect to server state.

Retiring the last handle does not delete the immutable ETLX or symbol artifacts. `ArtifactStore` owns independent byte/file quotas, LRU/TTL retention, leases/pins, atomic publication, and eviction. The implemented trace-artifact TTL is measured from the last store access, defaults to seven days, and is configured at startup with `WPAMCP_TRACE_ARTIFACT_RETENTION_MINUTES` or `--trace-artifact-retention-minutes` (1 minute through 365 days). TTL never invalidates a pinned live-handle object; after its last pin drains, an expired object is evicted and a later load materializes a new generation and mints a new trace ID. Permanent artifact purge, if exposed, is a separate administrator operation.

#### Physical artifact peak bound (accepted residual risk)

`MaxArtifactStoreBytes` is a retained-object quota, not a hard upper bound for the physical bytes used by the whole artifact root during materialization. The implementation serializes materializations, checks the current operation directory after the immutable input snapshot and after conversion/publication staging, cleans failed temporary operations, and enforces retained byte/object quotas with pin-aware eviction. Those are tested guarantees.

The `TraceLog.OpenOrConvert` converter may create and remove internal transient files between those checkpoints. Their maximum physical size is opaque to this process, so the converter-time peak and the combined retained-plus-temporary root peak are not hard-proven. For 0.4.x, the product owner explicitly accepts this residual risk. The server exposes `accepted_residual_risk:retained_quota_enforced;single_materialization_checkpoint_budget;opaque_converter_transient_peak_not_hard_limited` as a warning, and `artifact-materialization-budget.v1.json` records that `opaqueConverterTransientPeakProven=false` and `wholeRootPhysicalPeakHardLimited=false`. This acceptance makes the risk non-blocking; it does not permit documentation or runtime metadata to describe `MaxArtifactStoreBytes` as a whole-operation or whole-root physical peak cap. An independently quota-enforced conversion boundary remains the future hardening path.

### 5. Explicit immutable symbol contexts

`prepare_symbols(traceId, symbolPolicyRef)` is the only public operation that may probe approved local roots, contact approved symbol origins, or populate the symbol cache. It creates or returns a canonical immutable context keyed at least by:

```text
principal, TraceGenerationKey, normalized policy, resolver version,
verified PDB/module identities, verified symbol artifact content identities,
privacy/profile and contract version
```

Equivalent inputs return the same active `SymbolContextId`, so `prepare_symbols` is idempotent. Any policy, resolver, module, PDB, or verified artifact change creates a new context. It never mutates the trace ID or process-global `_NT_SYMBOL_PATH`.

An analysis with `symbolContextId=null` performs no disk/network symbol readiness probe and no symbol lookup. It may report only trace-native PDB identity and `unmeasured` local/frame state. An analysis with a context uses only its pinned/leased verified artifacts. If those artifacts can no longer satisfy the immutable promise, it fails with `symbol_context_expired`; it never silently upgrades or downgrades to current cache contents. A stronger new context invalidates only its own negative cache keys and never changes old-context results.

### 6. Query and cache keys

Symbol lookup, readiness, negative cache, and symbol-dependent analysis keys include the immutable trace generation, SymbolContextId/revision, resolver version, PDB and image identity/architecture, module base/RVA or normalized address, symbol artifact content identity, privacy/profile, contract version, and normalized query/scope where applicable.

Trace facts exclude local symbol readiness and frame resolution. Those measurements are context-bound evidence. Generation facts contain only trace metadata, provider/event observations, process/thread identities, per-domain stack coverage, and trace-native PDB identities.

### 7. Secure-default surface and annotations

The staged input modes are `compatibility` and `id_only`. In compatibility, query parameter name `path` temporarily accepts an allowed raw path or TraceId and all query annotations describe the worst reachable path behavior. In secure-default `id_only`, analysis queries accept only loaded TraceIds; the next breaking surface may rename the parameter to `traceId`.

Final annotation invariants are:

| Operation | readOnly | idempotent | openWorld | destructive |
| --- | ---: | ---: | ---: | ---: |
| `load_trace` | false | true | false | false |
| `prepare_symbols` | false | true | true | false |
| ID-only analysis/query | true | true | false | false |
| `unload_trace` | false | true | false | true |

`load_trace` may write only inside approved source/artifact boundaries and does not contact symbol origins. `prepare_symbols` may access only policy-approved roots/origins. Query never converts, downloads, or changes global environment state. Annotations remain hints; policy, quotas, handles, and egress are enforced in code.

`set_symbol_path` and arbitrary `add_symbol_server` are deprecated in contract 2.0 compatibility mode and absent from secure-default. Startup policy and `prepare_symbols` replace process-global mutation.

### 8. Retention and error behavior

Unknown, unloaded, expired, access-denied, conversion-failed, and symbol-context-expired states are failed contract results, never empty rows. Registry status exposes only the caller's aggregate and redacted handle states by default. Paths, internal generation keys, artifact keys, other principals, and global registry contents require a separate administrator capability and are not part of the normal MCP surface.

## Consequences

Secure queries become genuinely closed-world and read-only, and a result can identify the exact trace and symbol evidence context without disclosing source identity. The implementation must add registries, quotas, leases, immutable artifact publication, single-flight construction, and compatibility adapters before changing default mode.
