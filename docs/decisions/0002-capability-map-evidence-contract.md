# ADR 0002: Capability map and evidence contract direction

- Status: Accepted
- Decision date: 2026-08-01
- Decision source: the user explicitly requested implementation through `/goal`
- Detailed design: `../MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md`
- Implementation status: Phase 0 authorized; implementation is not complete

## Context

The current MCP surface exposes broad ETW analysis capability, but a model can still confuse server capability with trace evidence, scoped facts with trace-wide facts, Top-N with completeness, PDB identity with resolved frames, or a 64-bit JSON number with an exact identifier. The existing production-remediation baseline already owns security, privacy, cancellation, worker isolation, and release governance; this decision adds a single capability-and-evidence direction without creating a competing architecture.

## Decision

1. Expose every enabled public capability by default. Do not hide low-frequency tools or dynamically filter `tools/list` based on a loaded trace.
2. Use a validated active model as the shared source for capability definitions, tool mappings, schemas, routing, documentation, and evidence references.
3. Separate the Server Capability Map, generation-bound Trace Evidence Map, and per-call Tool Result Contract.
4. Treat silent truncation and unsafe opaque 64-bit JSON numbers as correctness failures, not presentation issues.
5. Express scope, capability, completeness, precision, capture integrity, and evidence boundaries in structured contracts so an LLM does not need to infer them from prose.
6. Keep measurement basis, relationship, and conclusion status orthogonal; direct observations do not automatically prove causality.
7. Reuse the approved trace security, privacy, exact-frame budget, cancellation, worker, and release mechanisms. Update their owning plans instead of introducing parallel implementations.
8. Execute the accepted design in Phase 0–7 order and preserve already-correct process/thread instance, dual stack-coverage, symbol-measurement, and replay-candidate semantics.

## Follow-up decisions

The former open choices in design §19 are now locked by three accepted follow-up decisions:

- ADR 0003: active catalog source, CapabilityId rules, contract `2.0`, exact identifiers, section/tool-list pagination, capability/trace maps, and the evidence registry.
- ADR 0004: canonical principal-scoped trace IDs, generation single-flight, artifact retention, immutable symbol contexts, secure-default queries, and final annotations.
- ADR 0005: the stable protocol boundary, planner admission, compatibility/default/removal releases, and release gates.

Those ADRs authorize implementation but do not claim it is complete.

## Consequences

- Phase 0 may immediately inventory and snapshot the current runtime, classify known correctness issues, and update approved-plan ownership.
- A legacy snapshot is evidence, not proof of correctness; `known_incorrect_must_change` behavior must not be preserved merely for compatibility.
- Implementation cannot claim completion until the detailed design §20 gates pass.
- This ADR records direction and authorization only; it does not claim that runtime behavior has changed.
