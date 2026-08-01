# Changelog

All notable user-facing changes to `wpa-mcp` are tracked here.

This changelog starts with `v0.2.15`. Older releases remain available from
GitHub Releases and the git tag history.

## 0.3.0 - Unreleased

### Migration notes

- Process identity is now `(Pid, ProcessStartUs)`, and exact replayable thread
  identity adds `(Tid, ThreadStartUs, ThreadGeneration)`. Consumers must
  preserve these selectors from process/thread rows instead of treating PID,
  TID, or an inferred capture-boundary start timestamp as globally unique.
- Empty-result consumers must inspect `ScopeStatus`, `CapabilityStatus`,
  `MatchedEventCount`, `NoDataReason`, and `Warnings`. An empty `Rows` array no
  longer has a standalone meaning; `not_observed` is reserved for established
  whole-trace absence, while filtered uncertainty is `unknown`.
- Whole-trace and scoped evidence are now explicitly separated through
  `Trace*` and `Scoped*` fields. Legacy unmatched-interval fields remain as
  deprecated trace-global aliases; do not attribute them to the selected PID,
  TID, or window.
- Interval-backed tools separate scoped raw `MatchedEventCount` endpoints from
  completed `MatchedIntervalCount` projections. `source_events_unattributed`
  and `no_completed_intervals_in_scope` prevent identity loss or lone endpoints
  from being mislabeled as event-class absence.
- All tools use `ReadOnly=false` MCP metadata because calls may change server
  or filesystem state. Raw trace/cache paths are conservatively
  `OpenWorld=true` because caller-supplied paths may be UNC, mapped, or
  reparse-point targets; only `set_symbol_path` is `OpenWorld=false`.
  `Destructive=true` conservatively covers ETLX replacement/refresh, cache
  retirement, and process-wide symbol-path replacement; incremental
  `add_symbol_server` is the sole `Destructive=false` tool. All tools are
  idempotent except `set_symbol_path`. These flags do not claim mutation of the
  ETL's logical event stream or active remote access by `diagnose_symbols`.
- Symbol consumers must distinguish trace PDB identity, local candidate
  discovery, verified local readiness, and actual observed frame-name
  resolution. `LookupStatus` now distinguishes exact GUID/age match, identity
  mismatch, invalid data, and `candidate_identity_unverified`; its failure
  reason separates unreadable input from unavailable native-reader support.
  Deprecated module-resolution fields may be null.
- `load_trace.SymbolStatus.CacheDir`, `inspect_trace.SymbolQuality.CacheDir`,
  and `diagnose_symbols.CacheDir` are documented as the fallback used by
  `add_symbol_server` when no cache is supplied, not the cache currently
  configured in `_NT_SYMBOL_PATH`; the diagnose field remains a deprecated
  compatibility alias of `DefaultCacheDir`.
- Stack row metrics marked `float32_per_sample_approximate` remain approximate
  even when serialized as `long`; source totals/coverage marked `exact_long`
  are exact and are not required to equal the sum of approximate top rows.

### Added

- Added `unload_trace` so MCP clients can retire a resident trace explicitly and
  register an adjacent-ETLX refresh for the next raw-ETL load in the current
  server process. The response explicitly states that the request does not
  survive restart and is not proof that regeneration has already succeeded.
- Added `inspect_trace.AnalysisContract` and verified its descriptions through
  the real MCP output schema, giving LLM clients compact machine-visible scope,
  count, empty-result, stack, symbol, replay, and causality rules without
  unbounded `tools/list` schema duplication.
- Added consistent process/thread scope metadata, capability status,
  matched-event counts, stable no-data reasons, and replayable candidates across
  process-oriented analysis tools.
- Added per-domain event and metric-weighted stack coverage with explicit
  `StackSemantics`, coverage state, and synthetic-unknown disclosure. Global
  `HasStackWalks` remains compatibility-only.
- Added explicit trace/scoped scheduler and interval-completeness counters,
  including scoped CSwitch stack coverage and replayable `ThreadStartUs` plus
  `ThreadGeneration` on CPU precise and wait rows.
- Added pure-local PDB GUID/age validation through direct TraceEvent
  `OpenSymbolFile` calls. `diagnose_symbols` does not actively access remote
  SRV/UNC entries or download symbols and does not claim that identity
  readiness is observed frame-name resolution; OS redirection of a
  local-looking root remains possible.

### Fixed

- Prevented PID/TID reuse and out-of-order rundown stop events from creating or
  merging overlapping process/thread instances.
- Exact-only lifecycle/trend tools now return structured
  `process_start_required` plus replayable candidates for clean PID reuse;
  `ambiguous_process_instance` is reserved for conflicting lifetime evidence.
  A reused PID whose requested window intersects only one lifetime resolves
  that lifetime without requiring a redundant selector.
- Treats same-PID lifetimes whose intervals overlap the requested PID scope as
  `ambiguous_process_instance`, including exact-start requests, instead of
  forcing raw PID/time events into one overlapping instance.
- Corrected point and interval stack analyzers to use true whole-trace event
  presence for capability/no-data decisions while keeping matched counts and
  coverage scoped to the requested process/window.
- Aligned zero-byte allocation events between trace capability detection and
  direct stack analysis; stopped caller/callee queries from reporting
  `focus_not_found` when stacks are unavailable or a zero-metric focus exists.
- Omitted CPU stack tools from recommended flows when the CPU event domain has
  no attached stacks, and made focus-function matching descriptions accurately
  declare exact case-sensitive matching.
- Preserved fallback process identity when pairing security-scan endpoints,
  kept pool allocation/free pairing trace-wide before scoped projection, and
  exposed unpaired network close endpoints instead of calling them "no events."
- Added trace/scoped unresolved-identity counters to CPU Precise, GC heap
  snapshots, and finalizer analysis. Raw evidence is reported as
  `source_events_unattributed` only when it could belong to the selected
  lifetime/window without guessing a sibling PID lifetime; exact-thread zero
  placeholders no longer suppress a real `no_events_in_scope` result.
- Clarified VirtualAlloc results as allocation-plus-free operation traffic,
  split allocated/freed bytes and counts, and exposed observed net operation
  bytes instead of implying that every weighted byte is retained allocation.
- Resolved FileObject/FileKey names in trace order so pointer/key reuse or later
  rundown mappings cannot rename historical File I/O or hard-fault evidence.
- Restricted symbol-server recommendations to modules with complete PDB
  name/GUID/age identity; incomplete identity now leads to recapture/merge
  guidance rather than an unusable server recommendation.
- Fixed local symbol-candidate aggregation so a corrupt symbol-store entry
  cannot hide an exact matching flat PDB, and directory placement alone is no
  longer treated as identity evidence.
- Restricted bare-path diagnosis to direct `<root>\<pdbName>` candidates and
  symbol-store-layout probing to roots declared through `SRV`/`SYMSRV`/`CACHE`.
  All configured candidates are now verified before the 10-path display cap;
  total/truncation fields expose omitted paths and exact matches display first.
- Made PDB failure classification conservative: ambiguous Windows DIA/candidate
  failures remain `candidate_identity_unverified`, while invalid status requires
  a rejecting container probe or an explicit portable-PDB data error. The
  no-remote-I/O contract now disclaims OS-redirection through mapped drives and
  reparse points.
- Hardened trace caching with canonical Windows case-insensitive keys, a single
  winning concurrent open, lease-safe retirement, unload invalidation, and a
  freshness stamp containing timestamps, length, and Windows volume/file ID;
  failed last-lease native cleanup remains centrally retryable.

### Known boundary

- A same-file in-place rewrite that preserves file identity, length, and all
  tracked timestamps is indistinguishable from the cached input. Explicitly
  call `unload_trace` before querying the rewritten trace; restarting alone does
  not invalidate a newer stale ETLX sidecar.

## v0.2.24 - 2026-07-31

### Changed

- Renamed the internal WprMcp solution, project directories, assemblies, build
  identifiers, scripts, and documentation references to WpaMcp while retaining
  the public `wpa-mcp` executable and Windows Performance Recorder terminology.

## v0.2.23 - 2026-05-19

### Added

- Added `MaxLatencyTimeUs` to `hard_fault_by_file` rows, giving follow-up
  analysis an exact timestamp for the observed worst page-in stall.
- Added `diagnose_window`, a guarded window-evidence composite that aggregates
  hard-fault by-file rows, file IO top files, memory-pressure samples,
  security-scan evidence, and waits for the same `pid` / `startUs` / `endUs`
  without returning a synthesized root-cause verdict.
- Extended `diagnose_slow_startup` with `FirstImageLoadGapEvidence`, which
  reuses `diagnose_window` for slow `ProcessStart -> first ImageLoad` gaps so
  startup analysis carries hard-fault, file IO, memory, scan, and wait evidence
  for the pre-user-mode gap.
- Added `EnabledCapabilities` and `RecommendedDiagnosticFlows` to
  `inspect_trace`, so agents can choose viable composite workflows without
  manually reducing every capability flag.
- Added `JitOnlyCapture.wprp`, a minimal CLR JIT-only capture recipe for
  `clr_jit_analysis` traces without broader CLR GC/allocation/exception/
  contention runtime keywords.
- Added `Capture-JitOnly.ps1`, an Administrator PowerShell helper that builds a
  temporary .NET JIT workload, captures a minimal JIT trace, and validates CLR
  JIT start/load markers when the Release MCP DLL is available.

### Fixed

- Fixed `diagnose_window` wide-window guard to reject too-wide windows before
  loading an uncached trace.
- Fixed `diagnose_slow_startup` gap evidence to skip trace-resident processes
  whose real `ProcessStart -> first ImageLoad` interval was not captured.

### Changed

- Bumped the executable/package version to `0.2.23` for the release tag.

### Verification

- `dotnet build WpaMcp.sln -c Release --no-restore`
- `dotnet test WpaMcp.sln -c Release --no-build --no-restore`
- Administrator WPR validation with `Capture-JitOnly.ps1` produced
  `jit_probe.etl` with `JittingStarted=6272` and `LoadVerbose=6271`.

## v0.2.22 - 2026-05-19

### Added

- Added `security_scan_analysis` to aggregate AV/EDR scan activity across
  Microsoft Defender/Antimalware and scan-like third-party security events.
  Defender exposes paired scan durations when StreamScanRequestTask start/stop
  events are present; other providers such as Aliedr/Alibaba, 360/Qihoo,
  PCManager, Sense, and CrowdStrike degrade to provider/event/path evidence
  when their ETW provider or event names expose scan/security terms. Vendor
  names classify matched events but do not by themselves turn all vendor
  activity into scans.
- Added `startUs` and `endUs` filters to `hard_fault_by_file`, so page-in files
  can be ranked inside a startup or interaction window instead of only across
  the whole trace.
- Added a memory-pressure summary to `memory_resource_analysis`, including
  minimum free/available memory, peak modified memory, peak observed aggregate
  process working set/commit/private bytes, and top peak memory consumers.

### Changed

- Bumped the executable/package version to `0.2.22` for the release tag.
- Clarified that `memory_resource_analysis` pressure totals are ETW sampled
  batch evidence, not complete whole-system memory accounting.

### Fixed

- Fixed the memory-pressure summary to de-duplicate duplicate process snapshots
  within the same timestamped batch and to leave `MinAvailableBytes` unknown
  when zero-page counts are not present.

### Verification

- `dotnet test WpaMcp.sln -c Release --no-restore`
- `dotnet run --project src/WpaMcp -c Release --no-build -- --version`

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

- `dotnet test WpaMcp.sln -c Release --no-restore`
- `dotnet run --project src/WpaMcp -c Release --no-build -- --version`
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

- `dotnet test WpaMcp.sln -c Release --no-restore`

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

- `dotnet test WpaMcp.sln -c Release --no-restore`
- `dotnet run --project src/WpaMcp -c Release --no-build -- --version`
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

- `dotnet test WpaMcp.sln -c Release --no-restore`
- `dotnet test WpaMcp.sln -c Release --no-restore --filter FullyQualifiedName~SymbolServiceTests`

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

- `dotnet test WpaMcp.sln -c Release --no-restore`
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

- `dotnet test WpaMcp.sln -c Release --no-restore`
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

- `dotnet test WpaMcp.sln -c Release --no-restore`
- `dotnet run --no-build -c Release --project src\WpaMcp -- --probe-stacks tests\WpaMcp.Tests\fixtures\small_wait_bound.etl`
- GitHub Actions `CI` passed on `main` for commit `ad7c433`.

## Previous Releases

- [v0.2.22](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.22)
- [v0.2.21](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.21)
- [v0.2.20](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.20)
- [v0.2.19](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.19)
- [v0.2.18](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.18)
- [v0.2.17](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.17)
- [v0.2.16](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.16)
- [v0.2.15](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.15)
- [v0.2.14](https://github.com/tooluse-labs/wpa-mcp/releases/tag/v0.2.14)
- [All GitHub Releases](https://github.com/tooluse-labs/wpa-mcp/releases)

[Unreleased]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.24...HEAD
[v0.2.24]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.23...v0.2.24
[v0.2.23]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.22...v0.2.23
[v0.2.22]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.21...v0.2.22
[v0.2.21]: https://github.com/tooluse-labs/wpa-mcp/compare/v0.2.20...v0.2.21
