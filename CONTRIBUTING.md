# Contributing to wpa-mcp

Project-specific gotchas and conventions for anyone (human or AI agent) modifying source code in this repository. End users of the MCP tool don't need this file — the tool's compiled binary doesn't reference it.

## Project

`wpa-mcp` is a C# MCP server (.NET 10, single console exe `WpaMcp.dll`) that reads Windows ETW `.etl` traces via `Microsoft.Diagnostics.Tracing.TraceEvent` and exposes ~13 tools over stdio JSON-RPC. **Windows-only** — the kernel TraceEvent parsers don't exist on Linux/macOS.

Tool surface (all under `[McpServerToolType]` attribute classes in `src/WpaMcp/Tools/`):
- **Meta**: `load_trace`, `list_processes` (incl. `WaitRatio`/`ParentPid`/`ImageLoadCount`, hides Idle/System by default)
- **CPU**: `cpu_top_functions`, `cpu_top_functions_batch` (multi-PID in one trace pass)
- **IO / hard fault**: `file_io_top_files`, `hard_fault_by_file`
- **Marker**: `find_marker` (default mode `count_by_event` to avoid token blowup; `rows` mode for full detail)
- **Symbols**: `set_symbol_path`, `add_symbol_server`, `diagnose_symbols` (`load_trace` also returns `Recommendations` based on observed module names)
- **Wait / Image-load / Diagnose** (PerfView-derived): `wait_analysis`, `image_load_timing`, `diagnose_slow_startup`

## Build, run, test

```powershell
dotnet build -c Release                          # build everything
dotnet test                                      # run xUnit suite
dotnet test --filter "FullyQualifiedName~CpuAnalysisTests"   # one class
dotnet test --filter "DisplayName~RejectsBadTop"             # one fact
dotnet src\WpaMcp\bin\Release\net10.0\WpaMcp.dll              # run server (stdio; talks MCP — exit with Ctrl+C)
dotnet src\WpaMcp\bin\Release\net10.0\WpaMcp.dll --version    # only non-MCP CLI flag
```

There is **no app entry point besides `--version` and the MCP stdio server** — historical PerfView-comparison reports mention an ad-hoc `--cpu-top` flag, but it has been reverted from the tree. Don't rely on it; if you need to drive the analyzers from a CLI, add it temporarily in `Program.cs` and revert before commit (commit `a37c8df` shows the pattern).

## High-level architecture

```
Tools/*Tools.cs       [McpServerTool] entry points (CpuTools, IoTools, HardFaultTools, MarkerTools, MetaTools, SymbolTools)
   ↓ inject
Core/{TraceCache,SymbolService,LruCache}.cs
   ↓
Analyzers/*.cs        Pure analysis on TraceLog (CpuAnalysis, FileIoAnalysis, HardFaultByFileAnalysis, MarkerSearch, FileObjectResolver)
   ↓
Output/{Records,Warnings}.cs   JSON DTOs
```

`Program.cs` registers `TraceCache` + `SymbolService` as singletons and calls `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` — tools are auto-discovered by the `[McpServerToolType]` / `[McpServerTool]` attributes. The DI container is the only thing wiring tools to caches.

### Trace lifecycle (don't bypass)

`TraceCache.Acquire(path)` is the only correct way to get a `TraceLog`. Keep the
returned `TraceLease` alive for the complete query:

```csharp
using var traceLease = cache.Acquire(path);
var trace = traceLease.Trace;
```

The cache:
1. Canonicalizes the path and stats mtime — re-loads if the file has changed under the cache.
2. LRU-evicts old entries (default capacity 2; override via `WPAMCP_CACHE_SIZE`).
3. First load runs `TraceLog.OpenOrConvert` which builds a `.etlx` index alongside the `.etl` (30s–3min for large traces); subsequent loads are instant. The `.etlx` files are mmap'd and may hold 200 MB–1.5 GB of address space per trace.
4. Retires evicted or explicitly unloaded entries. An active lease keeps its trace usable;
   the final lease release disposes the retired `TraceLog`.

Don't call `TraceLog.OpenOrConvert` directly from analyzers or tools, and don't retain
`lease.Trace` after disposing its lease.

### Symbol resolution path

`_NT_SYMBOL_PATH` is the configured source of truth. Only `set_symbol_path` and `add_symbol_server` mutate it. Read-only stack queries obtain an immutable `SymbolPathState` snapshot and add the owning ETL directory to that query's `SymbolReader`; they must never append a trace directory to the process environment. `TraceSymbolContext` associates a cached `TraceLog` with its source path. Default cache dir is `%LocalAppData%\WpaMcp\Symbols` (kept separate from PerfView's `C:\Symbols` to avoid PDB-lock contention). See `docs/SYMBOL_RECIPES.md`.

### CpuAnalysis: PerfView-parity invariants (READ BEFORE EDITING `Analyzers/CpuAnalysis.cs`)

Two non-obvious behaviors exist specifically to match PerfView's `SaveCPUStacksAsCsv` output:

1. **No-stack samples are attributed to a synthetic `?!?` root**, not dropped. PerfView counts every CPU sample in its grand total; without this, wpa-mcp under-counts by ~20–30%. The `?!?` frame is interned as `Interner.FrameIntern("?!?")` then turned into a `CallStackIntern(noStackFrame, Invalid)` — re-using the same intern call ensures all no-stack samples share one stack identity.
2. **Unresolved per-address frames are collapsed into per-module `module!?` buckets via a second normalized `MutableTraceEventStackSource`.** Without this, a single hot DLL fills the top-10 with hex offsets. Symbol resolution (`LookupWarmSymbols(50, …)`) **must** run on the raw source before normalization, or real symbols get wiped to `module!?`. Symbol statistics are measured on sample-reachable code frames in the raw source, exclude synthetic frames, and expose both unique-frame and metric-weighted observed name-resolution rates. A null rate means no eligible code frames were measured.

### Scope, capability, and empty-result contract

Process-targeted tools should resolve a shared `ProcessAnalysisScope` from `(pid, processStartUs, half-open window)`. A PID-only selector may aggregate reused lifetimes only when the response says `ScopeMode=pid_aggregate` and keeps per-instance rows distinguishable. Exact and missing selectors return structured `ScopeStatus=scope_not_found`; they do not silently fall back to the PID aggregate.

Thread-targeted CPU/Wait tools use `ThreadAnalysisScope` and expose replayable `IncludedThreads` entries with `ThreadStartUs`. A missing selector returns `scope_not_found`; multiple generations return `ambiguous_thread_instance` and candidates. Neither case may throw from a read-only analysis or fall back to PID-only matching.

Responses that can be empty expose `ScopeStatus`, `CapabilityStatus`, `MatchedEventCount`, `NoDataReason`, and `Warnings`. `CapabilityStatus` follows one invariant: `observed` only when the resolved requested scope matched source events; `not_observed` only when an unfiltered/global check established that the supported event class was absent; otherwise it is `unknown`. `NoDataReason` distinguishes `scope_not_found`, `event_class_not_observed`, `no_events_in_scope`, `no_completed_intervals_in_scope`, `stacks_unavailable`, `focus_not_found`, and other stable cases. A matched endpoint without a completed GC/JIT/finalizer interval uses `no_completed_intervals_in_scope`, never `no_events_in_scope`. None of these fields proves that a WPR keyword was configured or disabled.

Stack support is per event domain. Return `DomainStackCoverage` counts/metrics and `CoverageState`; never use the global compatibility `HasStackWalks` flag to claim that FileIO, DiskIO, HardFault, CLR, or another target event class has stacks. `?!?` represents accounted events with no stack and must not be described as a call chain.

If you change CpuAnalysis, re-validate against PerfView on a representative trace before claiming correctness. Acceptance criteria: 7/10 top-N name overlap, ±10% sample counts, ±15pp percentages, grand total within ~1%. (Open the same `.etl` in PerfView, dump CPU Stacks → By Name, compare against `cpu_top_functions` output for the same pid+window.)

### TraceEvent parser-attachment rule (READ BEFORE WRITING ANY ANALYZER)

**Always attach `KernelTraceEventParser` to `trace.Events.GetSource()`, never to the `TraceLog` directly.** TraceLog overrides `ITraceParserServices.RegisterEventTemplate` and throws

> `You may not register callbacks in TraceEventParsers that you attach directly to a TraceLog.`

…for events it synthesizes (CSwitch, ImageLoad, possibly others). The kernel rundown / image-load events fail loudly; some FileIO events appear to silently no-op. Either way, the source-based pattern works for all event types:

```csharp
var source = trace.Events.GetSource();
var kernel = new KernelTraceEventParser(source);
kernel.ThreadCSwitch += data => { ... };
source.Process();
```

All analyzers in `src/WpaMcp/Analyzers/` follow this pattern. If a copy-paste introduces `new KernelTraceEventParser(trace)`, the corresponding test will throw `ApplicationException` on the first event.

### CSwitch-based wait analysis (`Analyzers/WaitAnalysis.cs`)

Simplified port of PerfView's `src/TraceEvent/Computers/ThreadTimeStackComputer.cs`. Per-thread state machine on `KernelTraceEventParser.ThreadCSwitch`:
- **Switch out**: `cpuTime[oldTid] += now - lastSwitchInTime[oldTid]`; record `lastSwitchOutTime` and `OldThreadWaitReason`.
- **Switch in**: `blocked[newTid] += now - lastSwitchOutTime[newTid]`; the wait was "blocked on the reason recorded at switch-out".

Threads on their first switch-in (no anchor switch-out) are skipped — under-counts blocked-from-trace-start time but avoids inventing blocked time for pre-existing threads. We don't build a stack source like PerfView's full implementation; the goal is "which threads spent wall-time blocked, on what reason", not a flame graph.

Requires the `CSwitch` keyword in the capture profile. Default `CPU` / `CPU.light` WPR profiles include it; some custom `.wprp` files don't.

### File-IO vs hard-fault analyzers — two distinct keying schemes

Critical TraceEvent gotcha that the headers explain in detail but is easy to miss:

- **`FileIoAnalysis`** uses `FileObjectResolver`, which maps `FileObject` (a kernel handle) → filename via `FileIORead`/`FileIOWrite`/`FileIOCreate`/etc. (events of type `FileIOReadWriteTraceData` and friends).
- **`HardFaultByFileAnalysis`** uses `MemoryHardFault` events whose key is **`FileKey`** (a section-object key), not `FileObject`. It builds its own `FileKey → FileName` map by subscribing to `FileIONameTraceData` events (`FileIOName`/`FileIOFileCreate`/`FileIOFileDelete`/`FileIOFileRundown`).

Don't try to share `FileObjectResolver` with `HardFaultByFileAnalysis` — the keys are different kernel concepts. See the long comment block at the top of each file before refactoring.

`MemoryHardFault` events require the `HardFaults` kernel keyword, which **is not enabled by default WPR profiles**. `hard_fault_by_file` returns empty results on traces captured with `wpr -start CPU` etc. Use `tests/WpaMcp.Tests/fixtures/MmapCapture.wprp` (also referenced from `docs/WPR_PROFILE.md`).

## Test fixtures

The `*.etl` fixtures under `tests/WpaMcp.Tests/fixtures/` are gitignored by default, except those explicitly committed (the .gitignore allows `tests/**/fixtures/*.etl`). Most fixture-backed tests assume the three locally captured canonical fixtures are present:
- `small_cpu.etl` (~60 MB) — `wpr -start CPU.light`, ~3 s
- `small_fileio.etl` (~150 MB) — `wpr -start FileIO.light`, ~3 s
- `small_mmap.etl` (~35 MB) — custom `MmapCapture.wprp` with `HardFaults` keyword + 8 spawned processes

Capture all three with `tests/WpaMcp.Tests/fixtures/capture_all.ps1` (requires **Administrator PowerShell**). Without fixtures, ~15 fixture-dependent tests will fail with `FileNotFoundException`; the test runner does not auto-skip.

`perfview_gcevents.etl` is a committed third-party CLR fixture from the MIT-licensed PerfView repository. It is intentionally not captured by `capture_all.ps1`; its source, upstream commit, SHA256, and license text are recorded in `tests/WpaMcp.Tests/fixtures/PROVENANCE.md`.

xUnit assembly-level parallelization is **disabled** (`tests/WpaMcp.Tests/AssemblyInfo.cs`) because every fixture-touching test calls `TraceLog.OpenOrConvert` against the same `.etlx`, and parallel writers race on the `.etlx.new` temp file. Suite still runs in ~5 seconds.

## Conventions specific to this repo

- One `WpaMcp` csproj for now. The architecture doc mentions a future `WpaMcp.Analyzers`/`WpaMcp.Core` split, but until then everything stays under `src/WpaMcp/`.
- All public response DTOs are `sealed record` types in `Output/Records.cs` — keep them immutable and add new fields rather than mutating shape so MCP clients don't break.
- All MCP tool methods accept an absolute `path` to the `.etl` and hold a `TraceCache.Acquire` lease for the complete query. New tools should follow the same pattern.
- Symbol-related warnings are emitted as a `Warnings` list on the response (see `WarningBuilder`) instead of throwing — clients should surface them to the user.
