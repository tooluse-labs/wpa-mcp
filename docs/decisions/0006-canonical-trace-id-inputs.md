# ADR 0006: Canonical trace input names

- Status: Accepted
- Decision date: 2026-08-03
- Decision source: implementation authorization through `/goal`
- Supersedes: ADR 0004 and ADR 0005 raw-path query compatibility clauses

## Context

The MCP surface used `path` both for the filesystem input to `load_trace` and
for the opaque `trc_` identifier consumed by every analysis tool. This caused
models and clients to confuse loading, inspection, and query execution even
though the secure default already required an ID.

## Decision

The public contract has one name for each semantic type:

- `load_trace(tracePath)` accepts the only raw `.etl` or `.etlx` filesystem path.
- `load_trace` returns a canonical `traceId` matching `^trc_[0-9a-f]{32}$`.
- Every analysis, symbol-lifecycle, and unload operation accepts `traceId`.
- Trace metadata exposes `traceId`; it never labels an opaque ID as `path`.

Query-side raw-path compatibility and its catalog projection are removed in
the `0.6.x` release line. A noncanonical query value fails before source-path
validation, file access, conversion, or artifact creation. No legacy `path`
alias or hidden argument rewrite is provided.

The C# MCP method signatures are the source of truth for generated input
schemas. Locator overlays add grammar and opaque-locator metadata only; they
do not rename fields.

## Consequences

This is an intentional breaking input-contract change. Clients must call
`load_trace` with `tracePath`, retain the returned `traceId`, and use that exact
field name for subsequent calls. `tools/list`, `get_tool_contract`, runtime
validation, telemetry provenance, and reviewed baselines must agree on these
names.
