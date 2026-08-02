# Architecture

The current source tree is a single .NET 10 executable project with explicit
runtime boundaries. The single project is a packaging choice; it does not mean
catalog, lifecycle, analysis, and wire-contract state are interchangeable.

```text
MCP client (stateful stdio JSON-RPC)
  │
  ├─ tools/list cursor pages ───────────────┐
  ├─ list_capabilities / inspect_trace      │ capability selection
  └─ wpa://... resources                    │ and evidence boundaries
                                            ▼
Validated Active Catalog
  ├─ eng/capabilities.v1.json
  ├─ eng/tool-contracts.v2.json
  ├─ benchmarks/capability-matrix.v1.json
  └─ executable SDK tool/schema bindings
                                            │
                                            ▼
ContractMcpServerTool
  ├─ input and exact-integer validation
  ├─ trace/symbol-context resolution
  ├─ reviewed outcome adapter
  ├─ ToolEnvelope<T> + per-section contract
  └─ complete-frame fitting and text/structured mirroring
                                            │
                                            ▼
Tools/*Tools.cs → Analyzers/*.cs → TraceEvent / TraceLog
```

As of 2026-08-01 the validated model contains **60 active tools, 51 declared
capabilities, 15 goals, and 15 workflows**. These are a reviewed snapshot, not
constants to copy into code. Startup fails closed when manifests, attributed
methods, schemas, capability links, evidence references, planner admissions,
or selectable scopes do not join exactly.

## Capability discovery without capability hiding

The server follows one design rule:

> Expose the complete capability surface, and use a capability map to reduce
> selection cost. Expose complete evidence boundaries, and use structured
> contracts to prevent an LLM from over-interpreting results.

`tools/list` always exposes every active tool in deterministic order. It is
byte-budgeted and cursor-paged; a tool and its complete input/output schemas are
an indivisible unit. A client must consume every page. The server never removes
later tools or schemas to make page one smaller.

The same Active Catalog supplies:

- `list_capabilities`, the standard Tool-only discovery path;
- `inspect_trace.TraceEvidenceMap`, which evaluates every declared capability
  and workflow against one immutable trace generation;
- `wpa://capabilities/server`, `wpa://tools/server`, and
  `wpa://workflows/server`, which are small page indexes;
- domain/workflow resource pages; and
- `wpa://tools/{toolName}/sections` plus numbered pages, which expose every
  section's JSON pointer, role, ordering and tie breakers, completeness proof,
  limit source, evidence IDs, measurement basis, relationship, and declared
  conclusion.

Resources lower repeated discovery cost but are not the only route to critical
facts. A Tools-only client can still discover the full surface and inspect a
trace. Capabilities absent from a capture remain discoverable with structured
`partial`, `unavailable`, or `unknown` evidence; they are not hidden.

## Trace and artifact lifecycle

Secure-default analysis separates source-path access from trace queries:

```text
allowed local ETL/ETLX path
  └─ load_trace
       ├─ reject UNC/device/ADS/reparse/out-of-root input
       ├─ snapshot the opened handle into the owned artifact store
       ├─ materialize/reuse the immutable TraceLog generation
       └─ return principal-scoped canonical trc_... TraceId

TraceId
  ├─ inspect/list/domain analysis (leases generation)
  ├─ prepare_symbols (optional immutable symbol context)
  └─ unload_trace (retires handle; does not claim artifact deletion)
```

`TraceHandleRegistry` binds opaque IDs to a principal and immutable generation.
Queries acquire leases; retirement rejects new acquisitions but active leases
finish normally. Repeated loads of the same observed generation return the same
canonical handle. Generation construction and trace-facts construction are
single-flight, so concurrent callers do not intentionally open/convert the
same large generation multiple times.

Artifact retention is independent from handle retirement. Unpinned trace
artifacts have a startup-configured last-access TTL (seven days by default;
`WPAMCP_TRACE_ARTIFACT_RETENTION_MINUTES` or
`--trace-artifact-retention-minutes`, bounded to 1 minute through 365 days).
A live handle pins its object, so TTL cannot invalidate an active generation;
expiry is enforced after the final pin drains and before reuse. Quotas and
materialization checkpoints bound retained state, but the opaque converter's
transient physical disk peak is not yet proven. That is an explicit release
blocker, not an inferred hard cap.

The optional `compatibility` trace-reference profile accepts approved raw paths
through query adapters and therefore advertises the worst reachable path and
conversion effects. It is deprecated and removed in 1.0. The default `id_only`
profile never falls back from an unknown TraceId to a filesystem path.

## Symbol lifecycle

Symbols also use an explicit lifecycle. Startup may approve local symbol roots
and a disjoint private verified-symbol store. `prepare_symbols` is the sole
operation allowed to inspect those roots and populate that store. It accepts an
already-loaded TraceId and named policy, then returns an immutable,
principal/generation/policy-bound `SymbolContextId`.

The intended lookup boundary requires a caller-supplied context plus an explicit
resolution request. It never falls back to `_NT_SYMBOL_PATH`, the trace
directory, arbitrary filesystem search, or a remote symbol server. The current
secure policy is local-only and records `NetworkAccessed=false`.

The current build does **not** yet contain the context-bound TraceEvent adapter
needed to turn pinned artifacts into function names. A request with
`resolveSymbols=true` therefore fails closed with
`symbol_resolution_unavailable` and detail
`context_bound_frame_resolution_unavailable`; it does not run the legacy
ambient resolver or return an unsymbolized result labeled as resolved.

Keep four facts separate:

1. **PDB identity** — name/GUID/age carried by trace metadata.
2. **Local candidate** — a file discovered under an approved root.
3. **Verified readiness** — the candidate's actual identity matched and it was
   pinned in the verified store.
4. **Observed frame resolution** — a real stack lookup attempted and resolved
   code-frame names.

Preparation never claims frame resolution. A null/unmeasured frame rate is not
zero percent and PDB identity is not readiness. The declared
`symbols.frame_resolution.measured` gap remains open until a context-bound
lookup implementation and real-trace correctness evidence are admitted.

## Contract 2.0 result pipeline

All 60 active tools advertise a closed Contract 2.0 output schema and return a
`ToolEnvelope<T>`. The exact same finalized object is serialized into
`structuredContent` and the text JSON block. The shared header makes these
dimensions machine-readable:

- tool and trace reference;
- resolved scope and exact process/thread identities;
- trace and scoped capability evidence;
- completeness and failed sections;
- per-section ordering, totals, continuation and no-data state;
- identifier/metric precision;
- evidence provenance, measurement basis, relationship, conclusion status,
  and `doesNotProve` boundaries; and
- stable error and warning codes.

Section semantics are authoritative. Heterogeneous composite sections do not
inherit one fake tool-wide ordering or evidence claim. Boundary, provenance,
and recommendation sections are explicitly unmeasured/descriptive and carry no
domain conclusion. Empty domain sections require structured `noData`; a bare
empty array has no stable meaning.

The frame fitter trims only manifest-reviewed section boundaries. A resumable
section exposes `hasMore`, `moreState`, `continuationAvailable`, `nextCursor`,
and `truncationReason=response_budget`. If the minimum truthful success result
cannot fit, fitting is atomic: the server returns terminal
`response_too_large` with `data=null`, `scope=null`, empty sections,
`hasMore=false`, and unmeasured/not-concluded budget evidence. That failure is
not an empty analysis of the requested scope and has no continuation.

## Analysis truthfulness invariants

- A single process-instance identity is `(Pid, ProcessStartUs)`. PID-only input
  is valid only where the tool explicitly returns `ScopeMode=pid_aggregate` and
  the included lifetime identities; it must never masquerade as one instance.
  Exact thread replay adds `(Tid, ThreadStartUs, ThreadGeneration)`.
- `Trace*` fields are whole-generation evidence; `Scoped*` fields use the
  resolved identity and half-open request window. They are not interchangeable
  numerator/denominator sources.
- Capability status describes source evidence and evaluator boundaries, not
  whether a tool method returned normally. Single endpoints, unmatched pairs,
  inferred capture boundaries, and unresolved identities cannot become
  completed observed intervals.
- Stack availability and coverage are evaluated for the target event domain and
  selected scope. Global stack presence cannot enable another domain. `?!?` is
  a synthetic accounting bucket, not a captured call chain.
- Public stack metrics use the checked exact accumulator; TraceEvent's float
  sample metric is not round-tripped into an apparently exact integer.
- Caller/callee and ReadyThread stacks establish association, not causality.
  Security-event name matching remains heuristic with provenance/confidence.
- Materialized TraceEvent counts are not raw ETW record counts. Unknown raw
  counts and parser coverage remain explicitly unmeasured.
- A workload trace that does not contain an exactly selected wpa-mcp process
  cannot support conclusions about the MCP server's own performance.

## Query planning and physical-pass truth

`TraceFactsSnapshot` combines metadata, identities, capability facts, stack
coverage, provider counts, and PDB identities in one generation-level dispatcher
pass. Its build is single-flight and cancellation is waiter-local until the last
waiter leaves. `inspect_trace` is admitted through the typed `QueryPlanner` and
reports whether the call started, joined, or reused that snapshot.

Physical-pass counts describe the current call's participation; scanned-event
counts describe the generation snapshot. They are not cumulative timing or
work claims. Composites not yet admitted by the manifest continue through their
direct implementations and explicitly report that no single-dispatch claim is
available. Large-trace wall time, memory, cancellation, and selected composite
pass limits remain release gates.

## Runtime profile and release boundary

Contract and trace-reference modes are immutable startup choices. CLI values
override environment values; tool calls cannot switch modes. The selected
profile, warnings, and blockers are exposed at `wpa://runtime/profile` and bind
directory/query cursors.

The current 0.3.0 development source runs Contract 2.0 + ID-only. It is not a
publishable ADR 0005 release profile. `legacy` fails closed because no reviewed
legacy adapter exists. Release eligibility also remains blocked until corrected
active baselines are explicitly approved, the supported-client paging/token/
cache matrix exists where required, and opaque converter transient-peak evidence
passes. See `CONTRACT_MIGRATION.md` and `CLIENT_COMPATIBILITY.md`.
