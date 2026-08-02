# Changelog

All notable user-facing changes to `wpa-mcp` are tracked here.

This changelog starts with `v0.2.15`. Older releases remain available from
GitHub Releases and the git tag history.

## Unreleased

### Fixed

- `cpu_top_functions_batch` now cursor-pages complete per-selector result rows from a bounded,
  session-bound immutable snapshot. Frame fitting can shrink an oversized successful batch
  without rescanning the ETL on continuation; only a single indivisible result row that exceeds
  the hard response frame can still return `response_too_large`.

## 0.4.1 - 2026-08-02

### Fixed

- Accept a newly created artifact root whose initial owner is the current
  elevated token's `BUILTIN\\Administrators` default owner, then re-home it to
  the current user and verify the same protected, exact ACL as before. This
  fixes trace lifecycle startup on GitHub's Windows Server 2025 runner without
  accepting arbitrary owners or weakening the final artifact boundary.
- The `v0.4.0` tag failed its quality workflow before any GitHub Release or
  assets were created. Version 0.4.1 is the first published 0.4.x build.

## 0.4.0 - 2026-08-02 (tagged; not published)

### Migration notes

- Contract and trace-reference modes are now immutable startup settings.
  Configure `WPAMCP_CONTRACT_MODE=2.0|legacy` / `--contract-mode` and
  `WPAMCP_TRACE_REFERENCE_MODE=id_only|compatibility` /
  `--trace-reference-mode`; command-line values win. The active runtime has no
  released legacy result contract, so `legacy` is recognized only to fail
  closed instead of returning a mislabeled Contract 2.0 envelope. Contract 2.0
  + ID-only is the 0.4.x default, and Contract 2.0 is its only result shape.
  The absence of an unshipped legacy adapter is not a release blocker. Raw-path compatibility is
  deprecated and removed in 1.0.0. Read `wpa://runtime/profile` for the selected
  pair and blockers; see `docs/CONTRACT_MIGRATION.md`.

- The validated development catalog is now 61 active tools joined to 51
  declared capabilities, 15 goals, and 15 workflows. The capability map is
  exhaustive for this server surface, not for the complete WPA/ETW universe;
  clients must follow every `tools/list` and `list_capabilities` cursor page.
- Default `tools/list` is now a lean projection capped at 250,000 aggregate
  bytes. It retains each native tool name, description, complete input schema,
  annotations, and content-addressed Contract 2.0 URI/hash metadata, but no
  longer broadcasts deep output schemas inline. The historical approximately
  2.5 MB inline catalog remains a before-measurement only.
- MCP hosts/clients must traverse all discovery pages, cache the static catalog,
  and may progressively inject task-relevant descriptors into the model. The
  server does not dynamically activate tools and adds no universal dispatcher.
- All active tools now use the closed Contract 2.0 envelope. Consumers must
  interpret `scope`, `capabilityEvidence`, `completeness`, per-section state,
  `evidenceBoundary`, `precision`, `noData`, and `error` before domain data.
  Each tool's complete section semantics are available at
  `wpa://tools/{toolName}/sections` and linked pages.
- A `response_too_large` result is a terminal delivery failure with
  `data=null`, `scope=null`, empty sections, and `hasMore=false`. It neither
  proves that the requested scope was empty nor provides continuation.
- Process identity is `(Pid, ProcessStartUs)`; exact replayable thread identity
  adds `(Tid, ThreadStartUs, ThreadGeneration)`. Opaque trace, symbol,
  connection, file, handle, and address identifiers are JSON strings and must
  not pass through a JavaScript `number`.
- Secure-default trace queries require the canonical TraceId from `load_trace`.
  The raw source is snapshotted into the owned artifact store; `unload_trace`
  retires a handle but does not claim artifact deletion.
- Symbol consumers must keep trace PDB identity, local candidate discovery,
  verified readiness, and observed frame-name resolution separate.
  `prepare_symbols` is the sole local preparation boundary, returns an immutable
  SymbolContextId, and performs no network access. Queries never fall back to
  `_NT_SYMBOL_PATH`, the trace directory, arbitrary disk search, or a server.
  This build has no context-bound TraceEvent frame adapter, so
  `resolveSymbols=true` fails closed with `symbol_resolution_unavailable` rather
  than relabeling readiness or unsymbolized stacks as measured resolution.
- In the ID-only profile, 58 discovery/analysis tools are read-only,
  idempotent, closed-world, and non-destructive. The three stateful boundaries
  are `load_trace`, `prepare_symbols`, and `unload_trace`; profile-projected
  annotations remain authoritative in compatibility mode.
- Public stack rows now use checked Int64 metrics retained in parallel with the
  TraceEvent topology. The library's float sample field is no longer cast back
  to `long` and exposed as exact.

### Added

- Added `list_capabilities`, the paged Server Capability Map, plus
  `wpa://capabilities/server`, per-domain capability pages,
  `wpa://tools/server`, per-domain tool pages, workflow resources, and complete
  per-tool section-contract resources. Tools-only clients retain full discovery
  through `tools/list` plus `list_capabilities`.
- Added immutable full-output-contract Resources at
  `wpa://contracts/tools/{toolName}/{sha256}` with linked canonical UTF-8 pages,
  plus `get_tool_contract(toolName, page)` as the deterministic 8,192-byte
  Tools-only fallback. Clients reassemble fragments without normalization and
  verify the advertised byte count and SHA-256; the server validates results
  against the same schema source.
- Added the paged Trace Evidence Map to `inspect_trace`, including runtime
  capability evaluators, same-domain stack coverage, capture/symbol boundaries,
  self-attribution status, applicable tools, and workflows.
- Added closed output schemas and synchronized structured/text Contract 2.0
  envelopes for all 61 active tools, with section-local order, tie-breakers,
  total/more state, proof mode, continuation, evidence IDs, measurement basis,
  relationship, and conclusion status.
- Added exact full-frame fitting and terminal structured budget failure.
  `list_processes` plus admitted timeline queries use cursor paging bound to the
  principal, trace generation, contract, query, scope, symbol context, and
  privacy profile; clients must follow every page for a complete inventory.
- Added explicit trace and symbol lifecycles: canonical principal-scoped TraceId,
  immutable owned artifacts, lease-safe `unload_trace`, startup-approved local
  symbol roots, a private verified store, and immutable SymbolContextId.
- Added a generation-level single-flight `TraceFactsSnapshot` and typed
  `QueryPlanner` admission for `inspect_trace`. Direct composites continue to
  disclose that they do not prove a single shared planner dispatch.
- Added a version-aware ADR 0005 rollout policy for the 0.4, 0.5, and 1.0
  default/removal windows. Version 0.4.x activates the Contract 2.0 + ID-only
  release profile; pre-0.4 versions remain machine-marked release-blocked.
- Added `wpa://runtime/profile`, privacy-safe runtime-profile telemetry, and
  `--runtime-profile` / `--validate-release-profile`. `tools/list` cursors now
  receive the selected startup contract mode from the same profile.
- Added release gates that compare the exact published executable's runtime
  profile with its project version, commit-bound package stdio evidence,
  corrected active snapshots, manifests, and uploaded artifact hashes.
- Kept the 0.x assembly binding identity stable while advancing package,
  file, and informational versions, preventing a version-only release from
  changing canonical Contract 2.0 schema bytes or content addresses.

- Added consistent process/thread scope metadata, capability status,
  matched-event counts, stable no-data reasons, and replayable candidates across
  process-oriented analysis tools.
- Added per-domain event and metric-weighted stack coverage with explicit
  `StackSemantics`, coverage state, and synthetic-unknown disclosure. Global
  `HasStackWalks` remains compatibility-only.
- Added explicit trace/scoped scheduler and interval-completeness counters,
  including scoped CSwitch stack coverage and replayable `ThreadStartUs` plus
  `ThreadGeneration` on CPU precise and wait rows.
- Added exact local PDB name/GUID/age validation before a candidate is copied to
  and pinned from the private verified-symbol store. Preparation reports frame
  resolution as unmeasured; context-bound lookup remains a declared gap.

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
- Corrected wait denominators and stack evidence so trace-wide CSwitch counts,
  scoped CSwitch counts, scoped blocking-stack coverage, and rows retain their
  own scopes.
- Replaced ambient symbol-path mutation and query-time discovery with a
  startup-approved local policy. Candidate and store roots must be local and
  disjoint; candidate reparse traversal, identity mismatch, and unverified
  artifacts fail closed.
- Hardened trace loading with allowlisted local roots, opened-handle snapshots,
  immutable owned artifacts, generation single-flight, lease-safe retirement,
  and principal-scoped opaque handles.
- Made Top-N and budget omission observable through per-section exact/lower-
  bound/unknown totals, more state, concrete comparators, and continuation state.
- Prevented ready-thread association evidence and heuristic security-scan
  matches from being presented as mechanistic unblock or scanner attribution.

### Known boundary

- The 0.4.x profile publishes Contract 2.0 + ID-only. `legacy` fails closed
  because no released compatibility contract exists; that deliberate lack of
  an adapter does not block Contract 2.0.
- Corrected active-tool, DTO/stdio, lean-payload, pagination, and full-contract
  registry baselines are regenerated and reviewed together; automated gates
  bind them to the active manifests/profile. This closes the former active-
  baseline blocker without, by itself, asserting every full-suite/package gate.
  Named-client paging/token/cache measurements remain compatibility observations,
  while the package harness must prove complete pagination and both
  full-contract lookup paths.
- Retained artifact quotas do not hard-limit the opaque converter's transient
  physical disk peak. This is an explicitly accepted 0.4.x residual risk,
  recorded without claiming that the whole-root peak is proven.
- `inspect_trace` uses the typed planner and generation snapshot. Composites not
  admitted by the manifest still execute directly and do not claim one physical
  shared dispatch; large-trace performance/cancellation evidence remains a gate.
- `symbols.frame_resolution.measured` remains a declared gap. The current
  SymbolContextId lifecycle proves local readiness but cannot yet resolve frames;
  real-trace context-bound resolver evidence is required before that claim changes.

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
