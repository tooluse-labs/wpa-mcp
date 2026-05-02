# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project

`wpa-mcp` is a C# MCP server (.NET 8, single console exe `WprMcp.dll`) that reads Windows ETW `.etl` traces via `Microsoft.Diagnostics.Tracing.TraceEvent` and exposes ~13 tools over stdio JSON-RPC. **Windows-only** — the kernel TraceEvent parsers don't exist on Linux/macOS.

Tool surface (all under `[McpServerToolType]` attribute classes in `src/WprMcp/Tools/`):
- **Meta**: `load_trace`, `list_processes` (incl. `WaitRatio`/`ParentPid`/`ImageLoadCount`, hides Idle/System by default)
- **CPU**: `cpu_top_functions`, `cpu_top_functions_batch` (multi-PID in one trace pass)
- **IO / mmap**: `file_io_top_files`, `mmap_hot_files`
- **Marker**: `find_marker` (default mode `count_by_event` to avoid token blowup; `rows` mode for full detail)
- **Symbols**: `set_symbol_path`, `add_symbol_server`, `diagnose_symbols` (`load_trace` also returns `Recommendations` based on observed module names)
- **Wait / Image-load / Diagnose** (PerfView-derived): `wait_analysis`, `image_load_timing`, `diagnose_slow_startup`

## Build, run, test

```powershell
dotnet build -c Release                          # build everything
dotnet test                                      # run xUnit suite
dotnet test --filter "FullyQualifiedName~CpuAnalysisTests"   # one class
dotnet test --filter "DisplayName~RejectsBadTop"             # one fact
dotnet src\WprMcp\bin\Release\net8.0\WprMcp.dll              # run server (stdio; talks MCP — exit with Ctrl+C)
dotnet src\WprMcp\bin\Release\net8.0\WprMcp.dll --version    # only non-MCP CLI flag
```

There is **no app entry point besides `--version` and the MCP stdio server** — historical PerfView-comparison reports mention an ad-hoc `--cpu-top` flag, but it has been reverted from the tree. Don't rely on it; if you need to drive the analyzers from a CLI, add it temporarily in `Program.cs` and revert before commit (commit `a37c8df` shows the pattern).

## High-level architecture

```
Tools/*Tools.cs       [McpServerTool] entry points (CpuTools, IoTools, MmapTools, MarkerTools, MetaTools, SymbolTools)
   ↓ inject
Core/{TraceCache,SymbolService,LruCache}.cs
   ↓
Analyzers/*.cs        Pure analysis on TraceLog (CpuAnalysis, FileIoAnalysis, MmapAnalysis, MarkerSearch, FileObjectResolver)
   ↓
Output/{Records,Warnings}.cs   JSON DTOs
```

`Program.cs` registers `TraceCache` + `SymbolService` as singletons and calls `AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` — tools are auto-discovered by the `[McpServerToolType]` / `[McpServerTool]` attributes. The DI container is the only thing wiring tools to caches.

### Trace lifecycle (don't bypass)

`TraceCache.Get(path)` is the only correct way to get a `TraceLog`. It:
1. Canonicalizes the path and stats mtime — re-loads if the file has changed under the cache.
2. LRU-evicts old entries (default capacity 2; override via `WPRMCP_CACHE_SIZE`).
3. First load runs `TraceLog.OpenOrConvert` which builds a `.etlx` index alongside the `.etl` (30s–3min for large traces); subsequent loads are instant. The `.etlx` files are mmap'd and may hold 200 MB–1.5 GB of address space per trace.

Don't call `TraceLog.OpenOrConvert` directly from analyzers or tools — always go through the cache.

### Symbol resolution path

`_NT_SYMBOL_PATH` is the source of truth. `SymbolService` mutates the process env var when `set_symbol_path`/`add_symbol_server` are called, so any later `SymbolReader` constructed inside an analyzer picks up the change. CPU analysis pulls the env var directly inside `CpuAnalysis.TopFunctions` — there's intentionally no plumbing of the symbol service into analyzers. Default cache dir is `%LocalAppData%\WprMcp\Symbols` (kept separate from PerfView's `C:\Symbols` to avoid PDB-lock contention). See `docs/SYMBOL_RECIPES.md`.

### CpuAnalysis: PerfView-parity invariants (READ BEFORE EDITING `Analyzers/CpuAnalysis.cs`)

Two non-obvious behaviors exist specifically to match PerfView's `SaveCPUStacksAsCsv` output, validated in `tests/manual/perfview_compare.md` (Runs 1–4):

1. **No-stack samples are attributed to a synthetic `?!?` root**, not dropped. PerfView counts every CPU sample in its grand total; without this, wpa-mcp under-counts by ~20–30%. The `?!?` frame is interned as `Interner.FrameIntern("?!?")` then turned into a `CallStackIntern(noStackFrame, Invalid)` — re-using the same intern call ensures all no-stack samples share one stack identity.
2. **Unresolved per-address frames are collapsed into per-module `module!?` buckets via a second normalized `MutableTraceEventStackSource`.** Without this, a single hot DLL fills the top-10 with hex offsets. Symbol resolution (`LookupWarmSymbols(50, …)`) **must** run on the raw source before normalization, or real symbols get wiped to `module!?`. The physical resolution rate (`SymbolStats.ResolutionRate`) is computed against the unnormalized frame set so it remains a true symbol-quality signal.

If you change CpuAnalysis, re-run the PerfView comparison documented in `tests/manual/perfview_compare.md` (criteria: 7/10 top-N name overlap, ±10% sample counts, ±15pp percentages, grand total within ~1%) before claiming correctness.

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

All analyzers in `src/WprMcp/Analyzers/` follow this pattern. If a copy-paste introduces `new KernelTraceEventParser(trace)`, the corresponding test will throw `ApplicationException` on the first event.

### CSwitch-based wait analysis (`Analyzers/WaitAnalysis.cs`)

Simplified port of PerfView's `src/TraceEvent/Computers/ThreadTimeStackComputer.cs`. Per-thread state machine on `KernelTraceEventParser.ThreadCSwitch`:
- **Switch out**: `cpuTime[oldTid] += now - lastSwitchInTime[oldTid]`; record `lastSwitchOutTime` and `OldThreadWaitReason`.
- **Switch in**: `blocked[newTid] += now - lastSwitchOutTime[newTid]`; the wait was "blocked on the reason recorded at switch-out".

Threads on their first switch-in (no anchor switch-out) are skipped — under-counts blocked-from-trace-start time but avoids inventing blocked time for pre-existing threads. We don't build a stack source like PerfView's full implementation; the goal is "which threads spent wall-time blocked, on what reason", not a flame graph.

Requires the `CSwitch` keyword in the capture profile. Default `CPU` / `CPU.light` WPR profiles include it; some custom `.wprp` files don't.

### File / mmap analyzers — two distinct keying schemes

Critical TraceEvent gotcha that the headers explain in detail but is easy to miss:

- **`FileIoAnalysis`** uses `FileObjectResolver`, which maps `FileObject` (a kernel handle) → filename via `FileIORead`/`FileIOWrite`/`FileIOCreate`/etc. (events of type `FileIOReadWriteTraceData` and friends).
- **`MmapAnalysis`** uses `MemoryHardFault` events whose key is **`FileKey`** (a section-object key), not `FileObject`. It builds its own `FileKey → FileName` map by subscribing to `FileIONameTraceData` events (`FileIOName`/`FileIOFileCreate`/`FileIOFileDelete`/`FileIOFileRundown`).

Don't try to share `FileObjectResolver` with `MmapAnalysis` — the keys are different kernel concepts. See the long comment block at the top of each file before refactoring.

`MemoryHardFault` events require the `HardFaults` kernel keyword, which **is not enabled by default WPR profiles**. `mmap_hot_files` returns empty results on traces captured with `wpr -start CPU` etc. Use `tests/WprMcp.Tests/fixtures/MmapCapture.wprp` (also referenced from `docs/WPR_PROFILE.md`).

## Test fixtures

The `*.etl` fixtures under `tests/WprMcp.Tests/fixtures/` are gitignored by default, except those explicitly committed (the .gitignore allows `tests/**/fixtures/*.etl`). Tests assume the three canonical fixtures are present:
- `small_cpu.etl` (~60 MB) — `wpr -start CPU.light`, ~3 s
- `small_fileio.etl` (~150 MB) — `wpr -start FileIO.light`, ~3 s
- `small_mmap.etl` (~35 MB) — custom `MmapCapture.wprp` with `HardFaults` keyword + 8 spawned processes

Capture all three with `tests/WprMcp.Tests/fixtures/capture_all.ps1` (requires **Administrator PowerShell**). Without fixtures, ~15 fixture-dependent tests will fail with `FileNotFoundException`; the test runner does not auto-skip.

xUnit assembly-level parallelization is **disabled** (`tests/WprMcp.Tests/AssemblyInfo.cs`) because every fixture-touching test calls `TraceLog.OpenOrConvert` against the same `.etlx`, and parallel writers race on the `.etlx.new` temp file. Suite still runs in ~5 seconds.

## Conventions specific to this repo

- One `WprMcp` csproj for now. The architecture doc mentions a future `WprMcp.Analyzers`/`WprMcp.Core` split, but until then everything stays under `src/WprMcp/`.
- All public response DTOs are `sealed record` types in `Output/Records.cs` — keep them immutable and add new fields rather than mutating shape so MCP clients don't break.
- All MCP tool methods accept an absolute `path` to the `.etl` and route through `TraceCache.Get`. New tools should follow the same pattern.
- Symbol-related warnings are emitted as a `Warnings` list on the response (see `WarningBuilder`) instead of throwing — clients should surface them to the user.
