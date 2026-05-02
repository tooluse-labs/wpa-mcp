# Fixture capture

Re-capture if WPR profile schemas change or fixtures get corrupted.
Each fixture must be ≤ 5 MB to keep the repo lean.

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

If size > 5 MB, reduce sleep to 2s and recapture.

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

If size > 5 MB: edit `MmapCapture.wprp` and reduce `<Buffers Value="64"/>` to `"32"`, recapture.

Used by: MmapAnalysisTests.

## After capturing all 3 fixtures

Notify the controller (or run yourself):
- Remove `Skip = "..."` from all `[Fact(Skip = "...")]` attributes in:
  - `TraceEventSmokeTests.cs`, `TraceCacheTests.cs`, `MetaToolsTests.cs`,
    `CpuAnalysisTests.cs`, `FileObjectResolverTests.cs`, `MarkerSearchTests.cs`,
    `SymbolServiceTests.cs`, `FileIoAnalysisTests.cs`, `MmapAnalysisTests.cs`.
- Run `dotnet test` and verify all 33 tests pass (15 previously-passing + 18 newly-runnable).
