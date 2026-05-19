# Changelog

All notable user-facing changes to `wpa-mcp` are tracked here.

This changelog starts with `v0.2.15`. Older releases remain available from
GitHub Releases and the git tag history.

## Unreleased

### Added

- Added `security_scan_analysis` to aggregate AV/EDR scan activity across
  Microsoft Defender/Antimalware and scan-like third-party security events.
  Defender exposes paired scan durations when StreamScanRequestTask start/stop
  events are present; other providers such as Aliedr/Alibaba, 360/Qihoo,
  PCManager, Sense, and CrowdStrike degrade to provider/event/path evidence
  when their ETW provider or event names expose scan/security terms. Vendor
  names classify matched events but do not by themselves turn all vendor
  activity into scans.

## v0.2.21 - 2026-05-18

### Added

- Added `orderBy` to `hard_fault_by_file` with `bytes`, `count`, and
  `max_latency` modes, so one-off hard-fault stalls can be surfaced even when
  they do not dominate total page-in bytes.

### Changed

- Bumped the executable/package version to `0.2.21` for the release tag.

### Fixed

- Fixed `cpu_precise_analysis` over-counting by tracking open running intervals
  per logical processor instead of per thread. Values from prior versions could
  be inflated by multiples on traces with dropped, missing, or inconsistent
  CSwitch events.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- `dotnet run --project src/WprMcp -c Release --no-build -- --version`
- Real ETL probe confirmed the `187s-214s` analysis window stays below the
  16-core physical CPU-time limit after the per-core accounting fix.

## v0.2.20 - 2026-05-18

### Added

- Added `process_create_timing` warnings for child processes with slow
  first-image-load gaps before user-mode code can run, pointing users toward
  process-creation callbacks, AV/EDR scanning, or minifilter contention.
- Added `startUs` and `endUs` filters to `file_io_top_files`, matching the
  half-open `[startUs, endUs)` semantics used by stack analyzers.

### Fixed

- Centralized stack percentage calculation across stack analyzers and clamped
  display percentages to `[0, 100]`, preventing floating-point or CallTree
  overshoot from producing values such as `100.006%`.
- Added a development assertion for unexpectedly large percentage overshoot so
  real metric accounting bugs are still visible during debug builds.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`

## v0.2.19 - 2026-05-18

### Changed

- Updated Windows executable metadata so Task Manager and version resources show
  `wpa-mcp` branding, and embedded the existing project avatar as the
  application icon.
- Updated the zip installer to skip unchanged native TraceEvent DLLs by SHA256,
  avoiding unnecessary failures when an active MCP process has the same native
  dependency loaded.
- Bumped the executable/package version to `0.2.19` for the release tag.

### Fixed

- Fixed `cpu_precise_analysis` interval accounting so scheduler gaps and thread
  identity reuse cannot inflate per-thread CPU time beyond the analyzed window.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- `dotnet run --project src/WprMcp -c Release --no-build -- --version`
- Real ETL probe confirmed no per-thread CPU duration exceeded trace duration
  after the scheduler-gap fix.

## v0.2.18 - 2026-05-17

### Changed

- Expanded `diagnose_symbols` with local symbol-path parsing for cache entries
  and shared warning/root handling.

### Fixed

- Prevented `diagnose_symbols` from reporting stale flat same-name PDB files as
  resolved unless a symbol-store GUID/Age match or real DIA verification proves
  the PDB identity.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- `dotnet test WprMcp.sln -c Release --no-restore --filter FullyQualifiedName~SymbolServiceTests`

## v0.2.17 - 2026-05-17

### Changed

- Made broad stack-heavy MCP calls skip native symbol downloads by default, with
  `resolveSymbols=true` available after narrowing a query by PID or time window.
- Added the trace directory as a local symbol lookup path so colocated PDBs are
  found without remote downloads.
- Updated release and installer staging so TraceEvent DIA native DLLs remain
  available in zip installs.

### Fixed

- Fixed release zip packaging for native TraceEvent dependencies by publishing
  native libraries externally instead of embedding them into the single-file
  executable.

### Verification

- `dotnet test WprMcp.sln -c Release --no-restore`
- Manual Quark symbol rerun improved frame resolution and removed the
  `msdia140.dll` load failure.

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

- [v0.2.20](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.20)
- [v0.2.19](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.19)
- [v0.2.18](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.18)
- [v0.2.17](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.17)
- [v0.2.16](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.16)
- [v0.2.15](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.15)
- [v0.2.14](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.14)
- [All GitHub Releases](https://github.com/tooluse-labs/wpa-mcp/releases)

[Unreleased]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.21...HEAD
[v0.2.21]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.20...v0.2.21
