# wpa-mcp Production Remediation Program Design

**Date:** 2026-07-29

**Status:** Approved design baseline, including the thread-scoped CPU/wait amendment and the accepted 2026-08-01 capability-map/evidence-contract amendment
**Target:** Move wpa-mcp from a strong ETL analysis assistant to a production-capable, bounded, evidence-driven headless WPA subset without claiming unsupported WPA parity.

## Background

The repository already has a useful architecture: MCP tool facades call analyzers, analyzers operate on TraceEvent data, and typed output records carry capabilities, evidence, warnings, not-concluded reasons, and follow-up tool suggestions. The review nevertheless found correctness errors, inconsistent interval semantics, process/thread identity ambiguity, inaccurate capability and symbol-quality claims, false-success error handling, uncontrolled trace/symbol access, non-deterministic resource release, missing cancellation, incomplete MCP response contracts, and insufficient release evidence.

The program must fix those issues without hiding the current product's useful behavior behind a rewrite. The approved approach is a staged compatibility migration. Existing tool names and raw-path calls remain available during one compatibility stage, but all new behavior is designed around a validated trace registry, opaque trace identifiers, leases, explicit accounting policies, structured completion status, bounded responses, and executable release gates.

## Goals

1. Make time-window, process/thread identity, GC, wait, and startup metrics internally consistent and reproducible.
2. Ensure every completed tool result has an explicit success, partial, truncated, cancelled, or failed state, while transport-cancelled requests obey the negotiated protocol; an analysis failure must never look like a clean empty result.
3. Prevent untrusted trace, symbol, path, URL, and cache parameters from escaping configured policy.
4. Bound trace conversion, analysis, symbol resolution, disk use, concurrency, and response size; release resources deterministically.
5. Provide versioned MCP structured output and optional analysis-result privacy modes while retaining a documented migration path for existing clients.
6. Replace documentation-only parity claims with golden traces, cross-tool invariants, true stdio MCP tests, agent benchmarks, and release gates.

## Non-goals

- Reimplement the WPA graphical timeline, arbitrary drag-and-drop pivoting, or every WPA/PerfView domain in this remediation program.
- Add dynamic `tools/list` filtering; the tool surface remains static for client and prompt-prefix compatibility.
- Treat shared use of TraceEvent as proof that analysis quality matches PerfView.
- Make an LLM benchmark the only correctness oracle. Deterministic analyzers, protocol tests, and golden invariants remain the primary CI gates.
- Preserve incorrect numeric semantics merely to keep old snapshots unchanged. Intentional accounting changes are versioned and documented.

## Accepted amendment: capability map and evidence contract

The accepted direction in `docs/decisions/0002-capability-map-evidence-contract.md` and `docs/MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md` extends this baseline with complete capability discovery and structured evidence boundaries. Unmodified path security, time/identity semantics, privacy, exact-frame budgeting, cancellation, worker isolation, and release requirements in this specification remain authoritative.

Implementation must keep three states distinct: current runtime evidence, accepted target design, and implementation completion. The open choices listed in the amendment design §19 require their named ADR and owning-plan update; implementers must not infer them from the direction-level approval.

## Program-level decisions

### 1. Staged compatibility instead of a big-bang rewrite

The migration has three contract stages:

- **Compatibility stage:** `load_trace(path)` returns an opaque `traceId`. Existing query-tool string inputs continue to accept raw paths or trace IDs. Raw-path use emits a structured deprecation warning and is conservatively annotated as potentially mutating because it may load or convert a trace. Output contract mode is selected only at server startup (`legacy` initially, or opt-in `v2`); a tool call cannot override it, and `tools/list.outputSchema` must describe the active mode.
- **Secure-default stage:** raw paths are accepted only by `load_trace`; query tools require an already loaded trace ID. The v2 contract becomes the default, legacy output requires an explicit startup switch, and read-only annotations then describe actual behavior.
- **Next major version:** query parameters are renamed to `traceId`, legacy raw-path and legacy-output modes are removed, and the v2 structured envelope is the only response contract.

The trace ID uses a reserved `trc_` prefix plus at least 128 bits from a cryptographically secure random generator. A prefixed value that is unknown or expired returns `trace_not_loaded` and is never retried as a path. Each ID is bound to one immutable source generation; replacing a file causes a later `load_trace` to return a new ID and never silently rebinds the old one. IDs expire through configured idle/absolute lifetime or eviction, and a server restart invalidates them. For stdio the scope is one server process; any future shared/network host must additionally bind IDs to the authenticated principal.

In v2 mode, structured content is the normative result and bounded text content is a compatibility representation produced from the same already-redacted envelope. The complete JSON-RPC frame, including both representations, is charged to one response budget. Switching the default contract mode is a separately reviewed compatibility event with schema snapshots and release notes.

### 2. Syntactic validation and trace-relative validation are separate

Validation that does not require trace data runs before any path lookup or cache access:

- non-negative numeric values;
- `endUs > startUs` and configured maximum duration when both bounds are supplied;
- PID, collection-count, `top`, bucket, text-length, and request-byte limits.

Validation against trace duration runs only after acquiring a validated trace descriptor or lease. One-sided windows resolve to `[0,endUs)` or `[startUs,traceDurationUs)`. The resolved window must satisfy `0 <= StartUs < EndUs <= TraceDurationUs` and the configured maximum duration; an explicit out-of-range bound or an empty resolved window is rejected rather than silently clamped.

Shared invariants must use shared validators. Domain-specific validation remains local where its rule is not common to the MCP surface.

### 3. Point events and duration metrics use explicit half-open semantics

The common time model is a half-open window `[startUs,endUs)`:

- a point event contributes when `startUs <= timestampUs < endUs`;
- an interval contributes `max(0, min(intervalEndUs,endUs) - max(intervalStartUs,startUs))`;
- touching the window at only one endpoint contributes zero.

Duration rows retain both the complete event duration and the window contribution:

- `FullDurationUs` describes the complete paired event;
- `AccountedDurationUs` is the clipped overlap used by window summaries;
- `AccountingMode` is `ClippedOverlap` for the remediated duration analyzers.

This avoids relabeling a clipped portion as the full GC, JIT, contention, wait, or scan duration.

All TraceEvent millisecond timestamps and any nanosecond durations pass through one checked conversion function before pairing or arithmetic: non-negative fractional values are floored to integer microseconds. Tests therefore compare exact integer microseconds rather than using analyzer-specific tolerances. Missing start/stop endpoints are never invented; affected rows are excluded from exact totals, counted as unmatched intervals, and make the relevant section partial when completeness is affected.

### 4. PID and TID are selectors, not stable identities

Internally, a process lifetime is the half-open interval `[StartUs,EndUs)` and is identified by `ProcessInstanceKey(Pid, StartUs)`. Resolution returns `Resolved`, `Unresolved`, or `Ambiguous`; missing/overlapping lifecycle evidence never falls back to `FirstOrDefault`, `LastProcessWithID`, or another arbitrary instance. Thread state uses `ThreadInstanceKey(ProcessInstanceKey,Tid,Generation)`: generation advances on thread start or detected reuse, and thread stop clears the active generation.

Legacy PID-only queries retain aggregate behavior across matching instances but return an `ambiguous_process_instance` warning when reuse is observed. New optional selectors use `pid + processStartUs`; both values must be provided and match normalized microseconds exactly. Zero matches return `process_instance_not_found`, multiple matches return `ambiguous_process_instance`, and every v2 instance-level row carries the process key. Composite evidence IDs and provenance include the process instance, preventing collisions such as repeated `pid-1234` evidence from different lifetimes.

Thread-scoped tools share one `ThreadSelector`: `tid` is optional but requires `pid`; `processStartUs` and `threadStartUs` optionally select a specific lifetime. The minimum `(pid,tid,startUs,endUs)` form succeeds when the window resolves to one thread instance. Multiple matching generations return `ambiguous_thread_instance` rather than aggregating unrelated lifetimes; zero matches return `thread_instance_not_found`. Specifying a TID changes row selection, not just presentation: the requested thread cannot disappear because a process-wide TopN was applied first.

### 5. Deterministic CI and probabilistic agent evaluation are different gates

Per-PR gates are deterministic: build, unit tests, contract/schema tests, stdio E2E, hostile-input tests, golden traces, and cross-tool invariants. Agent benchmarks run on a pinned model/prompt/scenario configuration in scheduled and release workflows until their variance is low enough to make a per-PR hard gate reliable.

## Target architecture

```text
MCP request
  -> transport-frame limit, pre-trace validation, and request budget
  -> trace reference resolver
       -> load_trace(raw path) -> TraceAccessPolicy -> ArtifactStore/worker -> TraceRegistry
       -> query(traceId)        -> TraceRegistry.AcquireAsync -> TraceLease
  -> AnalysisOperationContext(cancellation, deadline, progress, budgets)
  -> analyzer using TimeWindow + ProcessInstanceKey + explicit accounting mode
  -> versioned ToolEnvelope + per-section completeness
  -> privacy redaction and privacy-aware logging
  -> final UTF-8 response budget
  -> MCP transport / telemetry byte accounting
```

### Core runtime interfaces

The detailed implementation plans may adapt names to existing conventions, but the responsibilities and boundaries are fixed.

```csharp
internal readonly record struct TimeWindow(long StartUs, long EndUs)
{
    public bool ContainsPoint(long timestampUs);
    public long IntersectDurationUs(long intervalStartUs, long intervalEndUs);
}

internal readonly record struct ProcessInstanceKey(int Pid, long StartUs);

internal readonly record struct ThreadInstanceKey(
    ProcessInstanceKey Process,
    int Tid,
    long Generation);

internal sealed record AnalysisOperationContext(
    CancellationToken CancellationToken,
    TimeProvider TimeProvider,
    long DeadlineTimestamp,
    IProgress<TraceProgress>? Progress,
    AnalysisBudget Budget);

internal interface ITraceRegistry
{
    ValueTask<TraceDescriptor> RegisterAsync(
        ValidatedTraceSource source,
        AnalysisOperationContext context);
    ValueTask<TraceLease> AcquireAsync(string traceId, AnalysisOperationContext context);
    ValueTask<UnloadTraceResult> UnloadAsync(string traceId, CancellationToken cancellationToken);
}
```

Only `TraceAccessPolicy` can create a `ValidatedTraceSource`; the registry never receives an unvalidated raw path. `TraceLease` owns one reference to an analysis backend. In trusted in-process mode that backend owns an evaluated `TraceLog`; in isolated mode it owns a worker-side trace handle. Eviction and unload stop new leases, wait for active leases to drain, then dispose the owning backend and any evaluated `TraceLog` exactly once.

### Versioned MCP envelope

Only malformed JSON-RPC, unknown methods/tools, messages that cannot bind to `tools/call`, and unrecoverable server protocol faults use JSON-RPC protocol errors. Failures originating from a valid tool call—including value validation, access denial, unavailable trace IDs, load/analysis failure, and cancellation with no usable evidence—return `CallToolResult.IsError=true` with a `Status=Failed` v2 envelope. A composite or batch that produced usable evidence but could not finish every requested section returns `Status=Partial` with `IsError=false`.

```csharp
public enum ToolCompletionStatus
{
    Succeeded,
    Partial,
    Failed
}

public sealed record ToolSectionFailure(
    string Section,
    string Code,
    string Message,
    bool Retryable);

public sealed record ToolError(
    string Code,
    string Message,
    bool Retryable);

public sealed record ToolSectionPage(
    string Section,
    bool HasMore,
    long? TotalAvailable,
    long Returned);

public sealed record ToolEnvelope<T>(
    string ContractVersion,
    ToolCompletionStatus Status,
    T? Data,
    ToolError? Error,
    IReadOnlyList<ToolSectionFailure> FailedSections,
    IReadOnlyList<ToolSectionPage> Sections,
    IReadOnlyList<string> Warnings,
    bool HasMore);
```

`ContractVersion` is the literal `"2.0"`; statuses and error codes serialize as stable lowercase strings, not enum ordinals. `Succeeded` requires non-null data, null error, and no failed sections. `Partial` requires usable data and at least one failed section. `Failed` requires null data and a non-null error and is paired with `IsError=true`. A globally cancelled request with no evidence is `Failed/cancelled`; cancellation of only a child section may be `Partial`.

The stable error-code set includes `invalid_argument`, `process_instance_not_found`, `ambiguous_process_instance`, `thread_instance_not_found`, `ambiguous_thread_instance`, `trace_not_loaded`, `trace_access_denied`, `trace_conversion_failed`, `analysis_failed`, `cancelled`, `budget_exceeded`, `response_too_large`, and `symbol_policy_denied`. Raw exception messages never cross the MCP boundary; full diagnostic details go only to redacted local logs.

`Partial` and `HasMore` are independent:

- `Partial` means requested work failed, was skipped, cancelled, or exhausted an analysis budget.
- `HasMore` means known valid data was omitted because of `top` or the final response budget.
- `HasMore=true` requires an exact total or a `top+1` probe; `rows.Count == top` is not proof.
- Composite pagination is per stable section name. Top-level `HasMore` is exactly `Sections.Any(section => section.HasMore)`; unrelated section counts are never summed.

Together, `(Status, HasMore, Error.Code)` distinguishes succeeded, partial, truncated, cancelled, and failed outcomes. If protocol cancellation prevents a response, the server emits no late success/error frame; the stdio suite verifies that behavior.

### Privacy modes

Privacy is a startup-level policy and cannot be weakened by a tool call:

- `off`: current raw analytical output, still subject to telemetry privacy and response limits;
- `paths`: redact canonical file, registry, symbol-cache, UNC, username, and machine paths while retaining only taxonomy-approved basenames and stable server-process-scoped aliases;
- `strict`: additionally redact marker string payloads, host/IP identifiers, registry values, and other configured sensitive string classes.

Aliases use a random per-server-process HMAC key that is never persisted. The same sensitive value maps consistently inside that scope and cannot be correlated across restarts. A bounded inbound alias resolver accepts only aliases previously issued in that scope and only for explicitly alias-enabled parameter types; it resolves before analysis and then reapplies trace/symbol access policy. A future shared host must scope aliases by authenticated principal. Redaction applies to stdout, privacy-aware stderr/log sinks, structured errors, warnings, symbol paths, progress, and telemetry metadata; analyzers and tools may not write directly to `Console.Error` in `paths` or `strict` mode.

A checked-in field taxonomy defines each sensitive DTO/string class and its `off`, `paths`, and `strict` behavior. No basename is presumed safe merely because it lacks directory separators; taxonomy and sentinel tests decide whether it is retained, aliased, or removed.

### Default budgets

Defaults are configuration-backed but have hard safety ceilings:

- tool arguments: 16 KiB serialized JSON;
- one string: 4,096 characters;
- one input collection: 128 items;
- `top` and histogram buckets: existing maximum 1,000;
- final JSON-RPC response: 100,000 UTF-8 bytes;
- response warning threshold: 40,000 UTF-8 bytes.

The initial resource baseline is also configuration-backed. Child 11's platform record may lower these values but cannot raise a hard ceiling without a separately reviewed threat-model update:

| Resource | Default | Hard ceiling |
|---|---:|---:|
| input trace size | 16 GiB | 64 GiB |
| total artifact store | 64 GiB | 256 GiB |
| symbol cache | 20 GiB | 100 GiB |
| one symbol download | 512 MiB | 2 GiB |
| queued trace loads | 8 | 32 |
| concurrent conversion workers | 1 | 4 |
| concurrent analysis workers | 2 | 8 |
| operation wall time | 10 minutes | 60 minutes |
| worker committed memory | 4 GiB | 16 GiB |
| worker CPU time | 10 minutes | 60 minutes |
| one IPC frame | 1 MiB | 4 MiB |
| progress notifications | 2/second | 10/second |
| event visits per operation | 100 million | 1 billion |
| stack-node visits per operation | 10 million | 100 million |
| symbol attempts per operation | 100,000 | 1 million |

The request-frame limit is enforced before full SDK deserialization. Work counters and quota reservations are atomically charged across composite sections; deadlines use `TimeProvider.GetTimestamp()` rather than wall-clock comparisons. Truncation happens only at row or section boundaries after privacy redaction. The 100,000-byte limit covers the complete UTF-8 JSON-RPC frame, including IDs, framing, text content, and structured content. Aggregate totals, completion status, truncation metadata, ordering, and JSON/schema validity must survive truncation. If the minimum legal envelope still exceeds the limit, the server sends a fixed-size `IsError=true` `response_too_large` envelope and never emits a partial frame.

## Child goals

### Child 1: Establish input, time-window, interval, and instance-identity foundations

**Deliverables**

- Shared pre-trace validation for window shape, PIDs, collection sizes, strings, and response options.
- `TimeWindow`, the canonical timestamp-to-microsecond conversion, and reusable point/interval accounting helpers.
- `ProcessInstanceKey`, `ThreadInstanceKey`, and a resolver returning `Resolved`, `Unresolved`, or `Ambiguous` from PID plus event timestamp.
- The `pid + processStartUs` selector contract and stable instance-resolution failures.
- A shared `ThreadSelector(pid,tid,processStartUs?,threadStartUs?)`; validation rejects `tid` without `pid` before trace access.
- Reflection-based MCP surface conformance tests for every exposed `startUs`/`endUs`, PID collection, text-limit, and TopN parameter.
- A checked-in architecture-test allowlist for the few domain adapters permitted to inspect raw time fields; all other windowed analyzers must call the shared primitives.

**Acceptance**

- Negative and reversed windows fail before trace access.
- One-sided windows are resolved only after descriptor acquisition; empty, overlong, or trace-relative out-of-range results fail before analyzer traversal.
- The interval boundary matrix covers outside, left overlap, contained, right overlap, enclosing, and endpoint-touch cases.
- Millisecond/nanosecond normalization and every interval boundary case produce exact integer-microsecond assertions.
- Missing or overlapping process metadata yields unresolved/ambiguous output and never silently chooses an instance.
- A unique `(pid,tid,window)` resolves one `ThreadInstanceKey`; missing and reused generations return the stable thread-instance errors unless the optional start selectors disambiguate them.
- The architecture test fails if code outside the reviewed allowlist duplicates boundary/clipping formulas or bypasses the instance resolver.

### Child 2: Make thread-scoped CPU/wait summary, stacks, and caller/callee consistent

**Deliverables**

- A shared blocked-interval stream or accumulator consumed by `wait_analysis`, `wait_top_stacks`, and `wait_caller_callee`.
- Optional `tid` plus the Child 1 lifetime selectors on `wait_analysis`, `wait_top_stacks`, `wait_caller_callee`, `cpu_precise_analysis`, `cpu_top_functions`, and `cpu_caller_callee`.
- `StackAnalysisRequest` and CPU/CSwitch accumulators consume the same resolved `ThreadSelector`; filtering happens before TopN and stack aggregation.
- Clipped-overlap accounting for every blocked interval.
- Full-trace state-machine traversal so an interval is pairable even when switch-out or resume lies outside the query window.
- Thread state keyed by `ThreadInstanceKey`, with thread-start generation and thread-stop cleanup.
- No-stack blocked intervals assigned to the synthetic `?!?` frame rather than omitted.
- `when` buckets split duration by the exact overlap with each bucket; they are not resume-point buckets.
- An `UnmatchedBlockedIntervalCount`; open intervals at capture end are excluded from exact totals and make the wait section partial.

**Acceptance**

- For the same process instance and window, `wait_analysis`, `wait_top_stacks`, and the caller/callee root use the same `TotalBlockedUs`; every `when` bucket sums to that total.
- For one resolved thread instance and window, wait summary/stacks and CPU summary/stacks include only that thread. A requested TID is returned even when its process-wide rank would be below `top`.
- TopN truncation never changes a total or produces `Partial`; it changes only section `HasMore` metadata.
- Synthetic cases cover cross-process TID reuse, reuse across two lifetimes of the same PID, and stop-then-reuse of a TID inside one process lifetime without cross-pairing.
- Left/right/enclosing window overlaps contribute only the exact clipped duration.
- A resume after the query end still supplies the stack for the overlapping blocked duration; an unclosed interval is reported but never guessed.
- Missing symbols change frame readability and resolution statistics only; they do not change thread selection or CPU/blocked totals.

### Child 3: Correct GC pause association and duration-pair accounting

**Deliverables**

- Pair GC wall intervals by process instance, CLR instance, and GC count.
- Pair all start/stop intervals over the full trace before clipping to the query window.
- Pair suspend/restart pause intervals independently, including nested/background GC cases, then associate each pause with exactly one GC. Candidates must share process/CLR instance and positive temporal overlap; prefer a GC whose start lies in the pause (latest start wins), otherwise choose greatest overlap then latest start. Missing CLR identity is explicit incomplete evidence, never a silent PID-only fallback.
- Apply the common full/accounted duration model to SecurityScan, JIT, GC, Finalizer, and CLR contention pairs where the domain exposes a duration.
- Emit orphan rows only when no compatible GC interval exists, not because event order cleared state prematurely.
- In legacy output, `DurationUs`/`PauseUs` and their totals map to accounted window contribution and emit the versioned time-semantics warning; v2 exposes both full and accounted values.

**Acceptance**

- `SuspendStart(90) -> GCStart(100) -> GCStop(150) -> RestartStop(160)` produces one GC with `FullPauseUs=70`, `AccountedPauseUs=70`, no orphan, and `TotalAccountedPauseUs=70`.
- A nested sequence with background GC #1 `[100,200)`, its pause `[90,120)`, foreground GC #2 `[140,160)`, and pause `[130,170)` yields pause totals 30 and 40 respectively, no orphan, and no double count.
- For every remediated analyzer, the complete accumulator's row `AccountedDurationUs` sum equals the window total. Returned TopN rows sum to at most that total and equal it when the section has `HasMore=false`.
- Full duration remains available and is never mislabeled as window contribution.

### Child 4: Make slow-startup evidence genuinely startup-scoped

**Deliverables**

- A `StartupWindow` defined as `[ProcessStartUs, min(checked(ProcessStartUs + startupWindowUs), observed ProcessEndUs, TraceDurationUs))`.
- Candidate selection, wait, image load, CPU, evidence, provenance, and executed-call metadata all use the same process instance and startup window.
- Candidate ranking uses `StartupWaitRatio = ObservedStartupWallUs / StartupCpuUs`; zero CPU yields null. Ordering is ratio descending, observed wall descending, process start ascending, then PID ascending. Lifetime values never participate.
- Explicit `ProcessStartObserved` provenance from a real ProcessStart event rather than `StartUs==0` or rundown metadata; trace-resident and pre-existing processes are not treated as known starts.
- First-image-gap evidence may use the explicit child window `[ProcessStartUs,FirstImageLoadUs)`, but provenance records the parent startup window and the child cannot escape it.
- Lifetime metrics remain available only as clearly named auxiliary evidence.

**Acceptance**

- Large wait/image-load activity after the startup window cannot create or reorder slow-startup candidates.
- Every candidate-selection and primary-evidence subcall records the same half-open startup window; a first-image child call records both parent and child.
- A process present before capture is omitted from startup ranking and yields one structured not-concluded result for startup timing.
- A startup window cut short only by trace end is `startup_window_truncated`/Partial; a normally observed early process exit is a complete short lifetime.
- Only an ImageLoad from the same process instance inside the startup window can be the first load; no matching load yields not-concluded.
- Duplicate PID lifetimes produce distinct evidence identifiers.
- Wait, image-load, and CPU events from a reused PID's later lifetime cannot enter the earlier lifetime's evidence.

### Child 5: Unify failure, capability, symbol-quality, response, and privacy contracts

**Deliverables**

- Versioned `ToolEnvelope` and stable error codes.
- Batch accounting in which requested items are partitioned exactly into succeeded, failed, and skipped sets.
- Per-domain stack coverage: eligible-event count, stacked-event count, coverage ratio, and the exact states `NoEvents`, `NoStacks`, `Partial`, and `Complete`; provider/capture capability is reported separately from observed coverage.
- Thread analysis quality explicitly distinguishes: no CSwitch (no reliable CPU/off-CPU duration), CSwitch without the requested domain's stackwalk (durations available, method attribution unavailable), and stackwalk with unresolved symbols (addresses or `module!?` available, names degraded).
- `PdbIdentityCoverage` separated from actual frame-resolution statistics.
- Enforced input and wire-response budgets with deterministic `HasMore` metadata.
- Startup-level legacy/v2 contract selection, schemas matching the active mode, and privacy modes applied immediately before final serialization.
- Privacy-aware logging, checked-in field taxonomy, and bounded inbound alias resolution.
- An MCP SDK spike proving how `outputSchema` and structured content are exposed for every tool; the implementation must be verified through a real `tools/list`, not assumed from reflection alone.

**Acceptance**

- Injected per-PID failure cannot produce `Partial=false` or a clean empty success.
- For every batch, succeeded/failed/skipped are pairwise disjoint and their union equals the de-duplicated requested set.
- CPU-only stacks do not make FileIO, CLR, registry, or network stack coverage appear available.
- Thread-filtered duration results remain valid when symbols are absent; thread-filtered stack results report `NoStacks` or degraded resolution without claiming the selector failed.
- `PdbIdentityCoverage` requires complete name/GUID/Age identity; actual resolution is measured from frames or symbol-load results.
- Every v2 tool has a succeeded result validated against the schema obtained from real `tools/list`; representative tools also validate partial, failed, truncated, and privacy-redacted results. Schema snapshots specify nullability, additional-property policy, stable section names, and require contract-version notes for changes.
- `Succeeded`, `Partial`, and `Failed` results have the defined `IsError`, data, error, and failed-section invariants.
- Budget boundary tests cover limit minus one, exact limit, and limit plus one, including multibyte UTF-8 and simultaneous text/structured content.
- Privacy sentinels do not appear in stdout, stderr, errors, warnings, or telemetry in the relevant modes.

### Child 6: Establish Trace and Symbol access policy and trace-ID migration

**Deliverables**

- `TraceAccessPolicy` with fail-closed defaults. Startup in every profile requires at least one allowed trace root; compatibility preserves raw-path syntax, not unrestricted filesystem reachability. It accepts only absolute local regular `.etl`/`.etlx` files (case-insensitive extension) inside a root by path-component containment and rejects relative paths, alternate data streams, UNC, NT/device namespaces, and any reparse-point component.
- Same-object consumption after validation: conversion/parsing consumes the validated open file object or a private snapshot copied from that handle. It never validates one object and then asks TraceEvent to reopen the attacker-controlled source path.
- A controlled ETLX artifact store outside the source directory, using unique per-conversion temporary directories, startup scavenging, validation, and atomic publication.
- Single-flight conversion by immutable source generation, including cross-process coordination and retry after fault/cancellation. Its cache key includes at least content hash, length, TraceEvent version, and conversion-option version.
- `SymbolPolicy` with remote access disabled and an empty host allowlist by default. Only startup configuration can enable approved HTTPS hosts; every redirect hop revalidates scheme, host, port, credentials, and resolved destination, denying loopback/private/link-local addresses unless separately allowlisted. URL/path delimiters, UNC cache paths, and unapproved local roots are rejected.
- Immutable per-request/per-trace symbol contexts. `_NT_SYMBOL_PATH` may be read once at startup only after full validation and is never mutated at runtime.
- `load_trace` returns a trace ID; query tools resolve trace references through one compatibility component.

**Acceptance**

- Rejected paths and symbol settings perform no network access and create no files.
- Read-only query tools no longer generate sidecars in the input directory.
- Two processes loading the same trace do not race on a shared `.etlx.new` file.
- A failed or cancelled conversion can be retried after the cause is corrected.
- An unknown `trc_` token never falls back to path handling; replacing the source after validation cannot change the bytes parsed for an existing trace ID.
- Concurrent symbol contexts cannot alter each other's path, cache, host, or environment settings; redirects cannot escape the configured allowlist.
- Tool annotations conservatively match actual compatibility-stage side effects and become strictly read-only in secure-default mode.

### Child 7: Implement leases, deterministic eviction, unload, and shutdown

**Deliverables**

- Lease/ref-count ownership with the state machine `Loading -> Ready -> Retiring -> Disposed` plus terminal `Faulted`, and exactly-once disposal in the owning process.
- Eviction/unload linearize when an entry enters `Retiring`: new leases are rejected while active readers finish. Caller cancellation or timeout stops waiting but does not roll back retirement or dispose beneath a lease; the last lease performs the exactly-once release.
- Explicit unload by trace ID with drained, pending, not-found, and timed-out results, plus configured idle/absolute trace-ID expiry.
- Hard cache accounting for artifact bytes, quota reservations, worker memory/CPU, and entry count; in-process `TraceLog` resident size is reported only as an estimate.
- Cross-request/process atomic quota reservations. When all candidates are leased, a new load queues within its admission budget or fails `budget_exceeded` instead of temporarily exceeding quota.
- Host shutdown that stops admission, cancels operations/workers, and drains leases to a configured deadline. After the deadline it may terminate isolated workers, but it never force-disposes a live in-process lease.

**Acceptance**

- Eviction, mtime/source replacement, unload, and shutdown each dispose an evaluated trace exactly once.
- Active analysis is never disposed underneath a lease.
- A faulted lazy/in-flight entry is removed with compare-by-identity semantics and cannot delete a newer successful entry.
- Cache size and disk quota are observable and enforceable resource limits.
- Repeated unload is idempotent; concurrent Acquire/Unload/Evict/Shutdown tests prove the state-machine outcomes and exactly-once release.
- Production configuration fails its gate if an in-process lease cannot drain within shutdown SLA; deadline expiry is not treated as safe forced disposal.

### Child 8: Propagate cancellation, progress, budgets, and hard isolation

**Deliverables**

- `AnalysisOperationContext` passed from MCP tools through composites and analyzers.
- Kernel/CLR walkers register cancellation to stop source processing and rethrow cancellation after traversal.
- One shared budget accounts for scan work, symbol work, composite sections, output, and deadline.
- MCP progress reports monotonic phases such as validation, conversion, metadata, scan, symbols, serialization, and completion when the client supplies a progress token. Progress is privacy-redacted, rate/byte bounded, and stops after completion/cancellation.
- Versioned worker IPC with bounded frames, strict message schemas, explicit inherited handles, unique artifact ownership, timeout, and concurrency limits. Windows Job Objects (or an equivalent enforced mechanism) limit memory, CPU time, child processes, and termination scope.
- Secure-default treats trace parsing and symbol resolution as untrusted-parser work: ETL/ETLX parsing, conversion, every analyzer that consumes the parsed trace, and symbol resolution run in a restricted worker with no network except policy-approved symbol egress. Parsed `TraceLog` objects never cross the process boundary. A p95 cancellation measurement decides only whether explicitly trusted-local analysis may remain in process; it cannot waive untrusted-parser isolation.
- If trusted-local in-process analysis cannot stop representative large-trace work within two seconds at p95, full analysis isolation is mandatory for that profile as well.

**Acceptance**

- Cancellation while queued, converting, scanning, resolving symbols, or serializing returns `Failed/cancelled` when a tool result is still permitted, or `Partial` when completed sections remain usable; transport cancellation never permits a later success frame. Reservations and temporary files are released by a bounded cleanup deadline.
- The server does not continue unbounded work after client cancellation or deadline expiry.
- `diagnose_high_wait` and CPU batch charge their prerequisite full scans to the same budget as later stack work.
- Worker crash, timeout, malformed IPC, and forced termination leave the server able to handle a subsequent request.
- Limit-minus-one/exact/plus-one tests cover every configured hard resource and prove that cancellation leaves no background CPU, network, artifact, or quota reservation.

### Child 9: Add real MCP, concurrency, and hostile-input integration tests

**Deliverables**

- A real child-process stdio harness for the pinned protocol matrix. It covers the repository's legacy `initialize -> notifications/initialized -> tools/list -> tools/call -> notifications/cancelled -> close stdin/host termination` flow and, if Child 11 selects the 2026-07-28 protocol, its discovery/per-request-metadata flow. It never invents a non-standard `shutdown` method.
- Tests for corrupt, truncated, wrong-extension, inaccessible, oversized, UNC, reparse, and replaced-after-validation traces.
- Same-trace concurrent loads within one server and across two server processes.
- Cancellation, deadline, client-disconnect, worker-crash, disk-full/quota, and oversized-response scenarios.
- Fixture preconditions that fail when repository-required fixtures are missing; dynamic skip is reserved for explicitly optional external environments.
- Contract/profile smoke for legacy, v2, `paths`, and `strict`, including real advertised output schemas and annotation matrices.
- Schema and execution cases for the six thread-filtered CPU/wait tools, including `tid` without `pid`, a unique thread, an ambiguous reused TID, and a target ranked below process-wide TopN.

**Acceptance**

- Stdout contains only valid JSON-RPC frames.
- Protocol negotiation/capabilities and request-response IDs are correct; stdout has no BOM, log, or non-frame bytes. All steps have deterministic timeouts and the server exits cleanly after stdin closes or the harness terminates it.
- Succeeded, partial, failed, invalid-argument, unknown-tool, and cancellation cases have the specified JSON-RPC versus `IsError` behavior; cancellation is never followed by a success for the same request.
- Concurrent conversion performs exactly one published conversion per source identity and produces no `.new` collision.
- The global xUnit parallel-disable switch is removed only after the concurrency suite proves isolated fixture/artifact behavior.
- Packaged self-contained executable passes the same stdio smoke and native-DLL layout checks, using the exact immutable artifact later offered to release upload.

### Child 10: Establish golden parity, cross-tool invariants, and agent benchmarks

**Deliverables**

- A versioned golden manifest containing fixture SHA-256, provenance, capture recipe, expected capabilities, oracle source/version, normalization rules, and numeric tolerances.
- Cross-tool invariants for PID/process instance, windows, units, totals, stack coverage, symbol identity, and completion state.
- A thread-window comparison case inside the canonical S01-S10 harness: the same `(pid,tid)` is analyzed over fast and slow windows, and the evidence verifier checks CPU, blocked duration, wait reasons, and readable-or-degraded stack attribution without confusing symbol quality with selector correctness.
- Executable S01-S10 investigation harness in full and tools-only modes.
- Pinned model snapshot, prompt hash, tool-schema hash, fixture hashes, runner version, temperature/seed where supported, exact commit, and at least five saved trials per scenario/mode for agent metrics.
- Deterministic evidence verifiers that decide task success and supported/not-concluded/root-cause labels independently of the model's self-report.
- A supported/preview/gap parity matrix; unsupported WPA domains are never scored as implemented parity.
- A release benchmark policy for unavailable external-model service: fail closed by default; any waiver records reason, approver, expiry, and the exact omitted artifact. Only public or synthetic fixtures in the required privacy mode may be sent to an external model.

**Acceptance**

- Fixture hashes and normalized deterministic snapshots match in CI.
- Structured parse success is 100% for the deterministic harness.
- Wrong-tool rate is irrelevant calls divided by all tool calls and does not exceed the comparable baseline by more than two percentage points.
- Mean tool calls is computed only over verifier-confirmed successful runs. Composite promotion requires at least a ten-percent reduction without reducing task success or conclusion accuracy, or an explicitly approved exception backed by better accuracy.
- Unsupported/insufficient-evidence scenarios never pass by asserting an unproven root cause; supported-scenario conclusion thresholds and overclaim ceilings are frozen in a versioned policy that cannot be relaxed in the same change as a failing run.
- Unknown-trace scenarios call `inspect_trace` within the first three tool calls.
- Every trial is retained, not only its mean. Baselines are comparable only when commit-independent model/prompt/schema/fixture/runner dimensions match; LLM variance is recorded and probabilistic gates remain scheduled/release-only until their false-failure rate is acceptable.

### Child 11: Complete dependency, platform, protocol, release, and documentation governance

This workstream has an early `11A` decision gate and a final `11B` release gate; it is not deferred wholesale until the other children finish.

**Deliverables**

- `11A` produces a versioned platform/protocol decision record before analyzer/runtime implementation. Because [.NET 8 support ends on 2026-11-10 while .NET 10 LTS is active through 2028-11-14](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core), .NET 10 is the default candidate, not an automatic choice. The record proves normal and `win-x64` locked restore, Release build/test, all golden TraceEvent reads, Windows PDB/DIA resolution, self-contained stdio, native layout, and the supported Windows/architecture matrix for each viable TFM.
- The same record pins the MCP SDK and protocol revision. It explicitly decides whether to remain on the current stateful protocol or support the [2026-07-28 protocol](https://modelcontextprotocol.io/specification/2026-07-28/server/tools), and defines the E2E matrix before implementation relies on handshake, cancellation, task, or output-schema behavior.
- Exact NuGet versions, `global.json`, and lock files for every project involved in CI, fixture generation, or release, including normal solution and `win-x64` release restore graphs. NuGet wildcard/range versions are forbidden; `global.json` and workflow SDK setup use the same exact SDK.
- GitHub Actions are pinned by full commit SHA.
- One reusable quality workflow used by CI and release; a tag cannot publish a commit that did not pass build, tests, stdio E2E, golden/invariant, packaging, and locked-restore gates.
- `11B` produces one immutable publish artifact. Packaged smoke, native checks, SHA-256/attestation, and final upload all consume that artifact; release never republishes after the gate. Tag, assembly version, commit, and provenance SHA agree.
- README, architecture, contributing, capability-gap, time-semantics, privacy, compatibility, and changelog updates.
- External review/cleanup tools are advisory unless their version, command, input scope, and deterministic pass criteria are checked into the repository.

**Acceptance**

- `restore --locked-mode`, Release build/test, stdio E2E, golden/invariants, and packaged smoke all pass on the same commit.
- Release cannot be triggered successfully by a tag whose commit lacks the quality-gate result.
- The selected TFM, MCP SDK, and protocol have a checked-in decision record satisfying every `11A` proof; neither EOL dates nor dependency restore alone count as proof.
- The uploaded bytes have the same digest and attestation as the artifact tested by the reusable workflow.
- Documentation no longer claims PerfView-equivalent analysis merely from the shared TraceEvent dependency.
- Every supported/preview/gap claim links to an executable test, benchmark, or explicit capability record.

## Dependency and rollout order

The dependency graph is:

```text
Child 11A platform/protocol decision gate
        |
        v
Child 1 -----> Child 2
   |  \------> Child 3
   |   \-----> Child 4
   \---------> Child 5

Child 6 -> Child 7 -> Child 8

Children 2-8 -> Child 9 -> Child 10 -> Child 11B final release gate
```

Practical rollout:

1. Freeze the current external surface and benchmark baseline; complete Child 11A's TFM/MCP protocol record.
2. Land Child 1 foundations.
3. Run correctness Children 2-4 in parallel where files do not overlap; serialize changes to shared output records.
4. Land Child 5 contract versioning before changing all public response shapes.
5. Land Child 6 access policy and trace IDs, then Child 7 lifecycle, then Child 8 cancellation/isolation.
6. Use Child 9 to validate the assembled runtime rather than relying only on unit tests.
7. Establish deterministic and agent evidence in Child 10.
8. Enable the final quality/release gates and documentation in Child 11B.

No child is considered complete because its implementation compiles. Each child must satisfy its listed acceptance tests and preserve the already-completed child gates.

## Planned file ownership

The implementation plans will assign exact task-level changes. At design level, ownership is:

| Area | New or primary files | Existing integration points |
|---|---|---|
| Validation/time/identity | `Core/TimeWindow.cs`, `Analyzers/ProcessInstanceResolver.cs` | `Core/Validation.cs`, analyzers exposing windows/PIDs |
| Thread CPU/wait and duration correctness | shared thread selector, blocked/pair accumulators | `StackSourceTopN.cs`, `WaitAnalysis.cs`, `BlockedTimeStackAnalysis.cs`, `CpuAnalysis.cs`, `CpuPreciseAnalysis.cs`, `SecurityScanAnalysis.cs`, `GcAnalysis.cs`, `ClrContentionStackAnalysis.cs`, JIT/finalizer analyzers |
| Startup/capability | startup-window and domain-coverage records | `DiagnoseTools.cs`, `ImageLoadAnalysis.cs`, `TraceCapabilitiesDetector.cs`, `MetaTools.cs` |
| MCP contract/privacy | `Output/ToolEnvelope.cs`, `Core/McpBudgetPolicy.cs`, `Core/PrivacyRedactor.cs`, shared PDB identity helper | `Program.cs`, tool attributes/return types, `Records.cs`, telemetry filters |
| Trace runtime | `TraceAccessPolicy.cs`, `TraceRegistry.cs`, `TraceArtifactStore.cs`, conversion coordinator, quota manager | `TraceCache.cs`, `LruCache.cs`, `McpServerOptions.cs`, all tools using `_cache.Get` |
| Symbol runtime | `SymbolPolicy.cs`, per-request symbol context | `SymbolService.cs`, `SymbolPathDefaults.cs`, `StackSourceTopN.cs`, `SymbolTools.cs` |
| Cancellation/worker | operation context and worker client/host | kernel/CLR walkers, analyzers, composites, host shutdown |
| Verification | stdio harness, golden manifest, invariant and privacy tests | CI/release workflows, fixtures and provenance docs |

Large existing files such as `Records.cs`, `MetaTools.cs`, and `DiagnoseTools.cs` are modified only where required. New cohesive contracts live in focused files rather than expanding those files further.

## Verification policy

Every implementation task follows test-first execution:

1. Add the smallest synthetic or protocol test that demonstrates the defect or missing invariant.
2. Run it and record the expected failure reason.
3. Implement the narrow change.
4. Run the focused test and the affected-domain suite.
5. Run contract/invariant tests touched by public schema changes.
6. Commit one independently reviewable change.

Tests that require a full ETL should still prefer small generated or provenance-pinned fixtures. Accumulator, window, identity, pairing, and error-state behavior should use synthetic events so a missing provider in a fixture cannot turn the test green without exercising its target.

## Program completion criteria

The remediation program is complete only when all of the following are true on the same release commit:

- all 11 child acceptance sections are proven by executable evidence;
- no confirmed HIGH correctness or lifecycle finding remains open;
- secure-default trace/symbol policy and trace-ID query flow are enabled;
- active traces are leased and deterministically disposed;
- cancellation/deadline tests stop real work within the accepted SLA or full analysis worker isolation is enabled;
- every v2 tool exposes and returns schema-valid structured content with explicit completeness;
- the six CPU/wait summary and stack tools accept one consistent thread selector and cannot lose a requested TID to process-wide TopN;
- privacy modes and response budgets are enforced on the final wire representation;
- stdio, hostile-input, concurrency, golden, invariant, packaging, and locked-restore gates pass;
- agent metrics meet the documented release thresholds without replacing deterministic correctness gates;
- product documentation describes a tested headless WPA subset and accurately lists preview and unsupported domains.
