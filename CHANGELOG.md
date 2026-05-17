# Changelog

All notable user-facing changes to `wpa-mcp` are tracked here.

This changelog starts with `v0.2.15`. Older releases remain available from
GitHub Releases and the git tag history.

## Unreleased

No user-facing changes yet.

## v0.2.16 - 2026-05-17

### Added

- Added `cpu_top_functions_batch` shared-pass execution for analyzing multiple
  PIDs from one trace walk.
- Added a soft budget and partial-evidence reporting to `diagnose_high_wait` so
  broad traces can return bounded diagnostic evidence instead of timing out.
- Added a local interrupt-stack validation helper under `tools/interruptfixture`
  and documented the real-event missing-stack validation workflow.

### Changed

- Added MCP risk annotations for all tools, including accurate `OpenWorld`
  hints for stack-resolving tools that may download symbols through
  `_NT_SYMBOL_PATH`.
- Kept symbol-path configuration tools closed-world but documented that their
  configured symbol servers are trusted by subsequent stack-resolving tools.

### Fixed

- Warn when missing DPC/ISR stacks dominate interrupt time, even if they are a
  minority of interrupt events.
- Made `cpu_top_functions_batch` isolate per-PID failures and report warnings
  instead of dropping the whole batch.
- Tightened `list_processes orderBy=wait_ratio` so tiny CPU denominators do not
  dominate high-wait rankings.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- `dotnet build tools/interruptfixture/interruptfixture.csproj -c Release --no-restore`
- Local real-event validation with ignored manual ETL:
  `noStackCount=4/11`, `noStackUs=1958/1969us`, warning emitted.

## v0.2.15 - 2026-05-17

### Added

- Added a real wait-bound ETW fixture, `small_wait_bound.etl`, that contains
  CSwitch and ReadyThread events with event-attached call stacks.
- Added `--probe-stacks <trace.etl>` as a developer probe for comparing explicit
  StackWalk rows with TraceEvent `CallStackIndex` values attached to events.

### Fixed

- Prevented stack data on unrelated event families from enabling wait-stack
  diagnostics. `inspect_trace` and `diagnose_high_wait` now distinguish global
  stack availability from CSwitch and ReadyThread stack availability.
- Kept wait-stack warnings when CSwitch or ReadyThread events are present but do
  not carry call stacks, avoiding misleading `?!?`-only stack evidence.
- Made the wait-bound fixture capture script tolerate worker processes that exit
  before the parent starts waiting for them.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- `dotnet run --no-build -c Release --project src\WprMcp -- --probe-stacks tests\WprMcp.Tests\fixtures\small_wait_bound.etl`
- GitHub Actions `CI` passed on `main` for commit `ad7c433`.

## Previous Releases

- [v0.2.15](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.15)
- [v0.2.14](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.14)
- [All GitHub Releases](https://github.com/tooluse-labs/wpa-mcp/releases)

[Unreleased]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.16...HEAD
