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
WPR profiles do NOT enable the `HardFaults` keyword that `mmap_hot_files`
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

Used by: MmapAnalysisTests.

## After capturing all 3 fixtures

Notify the controller (or run yourself):
- Remove `Skip = "..."` from all `[Fact(Skip = "...")]` attributes in:
  - `TraceEventSmokeTests.cs`, `TraceCacheTests.cs`, `MetaToolsTests.cs`,
    `CpuAnalysisTests.cs`, `FileObjectResolverTests.cs`, `MarkerSearchTests.cs`,
    `SymbolServiceTests.cs`, `FileIoAnalysisTests.cs`, `MmapAnalysisTests.cs`.
- Run `dotnet test` and verify all 33 tests pass (15 previously-passing + 18 newly-runnable).
