# Fixture capture

Re-capture if WPR profile schemas change or fixtures get corrupted.

**Size target: ≤ 10 MB per fixture, committed directly to git (no LFS).**  Realistic
WPR captures are 30-200 MB raw; we shrink them via `tools/etlshrink/` (an
`ETWReloggerTraceEventSource` wrapper) before committing.  Workflow:

```powershell
# Capture (admin shell required) — see per-fixture sections below
wpr.exe -start <profile> -filemode
# … workload …
wpr.exe -stop big.etl

# Shrink — relogger compression alone gives ~4-7× reduction; a time cut goes further.
dotnet run --project tools/etlshrink -- big.etl small.etl 500
# `500` = keep events with TimeStampRelativeMSec ≤ 500ms.  Omit for compression-only.
```

Aggressive time cuts can drop late-firing rundown events (process / image metadata logged
at trace stop) — if a shrunk fixture breaks tests that need that data, raise the cutoff
or omit it entirely.  See per-fixture sections for the cuts known to work.

ALL captures require an Administrator PowerShell — kernel ETW tracing is privileged.

`perfview_gcevents.etl` is the exception in this folder: it is a small committed
third-party CLR fixture from the MIT-licensed PerfView repository, not a local WPR
capture. Do not regenerate it with `capture_all.ps1`; see `PROVENANCE.md` for
its source URL, upstream commit, SHA256, and license text.

## small_cpu.etl

Captured with: `wpr.exe -start CPU -filemode` for ~3 seconds.

```powershell
cd tests\WprMcp.Tests\fixtures
wpr.exe -start CPU -filemode
Start-Sleep -Seconds 3
wpr.exe -stop small_cpu.etl
Get-Item small_cpu.etl | Select Length
```

After capture, shrink: `dotnet run --project tools/etlshrink -- small_cpu.etl small_cpu.shrunk.etl 500` then replace.  Current committed size: ~5 MB at 500ms cut.

Used by: SmokeTests, MetaToolsTests, CpuAnalysisTests, MarkerSearchTests, SymbolServiceTests, FileObjectResolverTests.

## small_fileio.etl

Captured with: `wpr.exe -start FileIO -filemode` for ~5 seconds while running
`Get-ChildItem -Recurse C:\Windows\System32 | Out-Null` in another shell.

```powershell
# Window 1 (admin):
wpr.exe -start FileIO -filemode

# Window 2 (any):
Get-ChildItem -Recurse C:\Windows\System32 -ErrorAction SilentlyContinue | Out-Null

# Back in window 1 after 5 seconds:
wpr.exe -stop small_fileio.etl
Get-Item small_fileio.etl | Select Length
```

After capture, shrink: `dotnet run --project tools/etlshrink -- small_fileio.etl small_fileio.shrunk.etl 300` then replace.  Current committed size: ~4 MB at 300ms cut.

Used by: FileIoAnalysisTests.

## small_mmap.etl

Captured with the custom profile `MmapCapture.wprp` (this folder) — the default
WPR profiles do NOT enable the `HardFaults` keyword that `hard_fault_by_file`
depends on.

```powershell
cd tests\WprMcp.Tests\fixtures
wpr.exe -start MmapCapture.wprp -filemode

# Trigger workload — open a moderately-sized file that will mmap-page-in:
Start-Process notepad C:\Windows\System32\drivers\etc\hosts
# Or: open a small PDF in your browser, or
# `Get-Content -Raw C:\some\big\file > $null` (use $null sink so the
# read goes through but isn't kept in memory).

Start-Sleep -Seconds 5
wpr.exe -stop small_mmap.etl
Get-Item small_mmap.etl | Select Length
```

After capture, shrink WITHOUT a time cut (the test that uses this fixture needs late-firing image-load events that aggressive cuts drop):
`dotnet run --project tools/etlshrink -- small_mmap.etl small_mmap.shrunk.etl`
Current committed size: ~8 MB (compression-only).  If still too large, reduce `<Buffers Value="64"/>` in `MmapCapture.wprp` to `"32"` and recapture.

Used by: HardFaultByFileAnalysisTests + PageFaultStackAnalysisTests + ImageLoad tests (the mmap fixture spawns 8 short-lived processes).

## small_memory.etl

Capture with the custom profile `MemoryCapture.wprp` (this folder). The existing
`MmapCapture.wprp` enables `MemoryInfo` but does not produce `Memory/ProcessMemInfo`;
`MemoryInfoWS` is required for `memory_resource_analysis` process rows.

Run from Administrator PowerShell:

```powershell
powershell.exe -ExecutionPolicy Bypass -File D:\wpa-mcp\tests\WprMcp.Tests\fixtures\Capture-SmallMemory.ps1
```

The script starts `MemoryCapture.wprp!MemoryMcp`, allocates 64 MB of private
memory, repeatedly reads `kernel32.dll` to create handle activity, waits 1
second by default, stops WPR to `small_memory.etl`, and prints the captured path
and byte size. During stop, WPR may print `Press Ctrl+C to cancel`; do not press
Ctrl+C while it merges the trace. The script uses `-skipPdbGen -compress` to
keep stop time and fixture size down. If capture fails after WPR starts, the
script cancels the active WPR session before exiting.

After capture, verify before replacing the committed fixture:

```powershell
dotnet run --project src\WprMcp -- --find-marker small_memory.etl "Memory/ProcessMemInfo" count_by_event 10
dotnet run --project src\WprMcp -- --find-marker small_memory.etl "Object/" count_by_event 10
dotnet run --project src\WprMcp -- --find-marker small_memory.etl "Pool/" count_by_event 10
```

Avoid `tools/etlshrink` for the pool-positive fixture unless the resulting ETL
is re-verified with the `Pool/` marker command above. A previous 520 ms relogged
fixture retained `Memory/ProcessMemInfo` and `Object/*Handle` but lost
`PoolAllocation` / `PoolFree` when freshly converted from ETL.

Used by: positive-path `MemoryResourceAnalysisTests`. The first local agent
attempt on 2026-05-16 failed with `0x80070005 Access is denied` because the shell
was not elevated; WPR kernel capture requires Administrator PowerShell. The
successful capture was recorded from Administrator PowerShell and shrunk from
783,161,353 bytes to 19,453,742 bytes while preserving `Memory/ProcessMemInfo`
and `Object/*Handle` events. Pool event visibility in that shrunk fixture has
been conversion-environment sensitive, so recapture with the lighter no-stack
`MemoryCapture.wprp` before making pool-positive fixture assertions strict.

## After capturing all 3 fixtures

Notify the controller (or run yourself):
- Remove `Skip = "..."` from all `[Fact(Skip = "...")]` attributes in:
  - `TraceEventSmokeTests.cs`, `TraceCacheTests.cs`, `MetaToolsTests.cs`,
    `CpuAnalysisTests.cs`, `FileObjectResolverTests.cs`, `MarkerSearchTests.cs`,
    `SymbolServiceTests.cs`, `FileIoAnalysisTests.cs`, `HardFaultByFileAnalysisTests.cs`.
- Run `dotnet test` and verify all 33 tests pass (15 previously-passing + 18 newly-runnable).
