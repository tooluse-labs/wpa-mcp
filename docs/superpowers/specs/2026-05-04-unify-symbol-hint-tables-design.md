# Unify symbol-hint pattern tables

**Date:** 2026-05-04
**Status:** Design — pending user review
**Tracks:** simplify-pass deferred items #1 (path-substring false positives) and #2 (two parallel tables)

## Background

The repo has two near-parallel pattern tables that map module names → symbol hints:

| Site | Match scope | Pattern style | Output |
|---|---|---|---|
| `MetaTools.SymbolServerHints` (`src/WprMcp/Tools/MetaTools.cs:18`) | `module.Name` substring | Explicit module names (`ntoskrnl`, `ntdll`, `mpengine`, ~30 entries) | Per-server `SymbolRecommendation` list returned by `load_trace` |
| `SymbolTools.ServerHints` (`src/WprMcp/Tools/SymbolTools.cs:99`) | `module.FilePath` substring | Generic terms (`Microsoft`, `Windows`) plus 3-tier ffmpeg/Chromium specifics | Per-module hint string returned by `diagnose_symbols` |

A comment on `SymbolTools.ServerHints` previously claimed the two tables were "kept consistent" — they aren't. The recent simplify pass corrected the comment but left the structural divergence in place. This spec resolves it.

### Observable consequences of the divergence

1. **Behavioral inconsistency** — a self-redistributed `msvcp140.dll` under `C:\Tools\someApp\` matches `msvcp` in `MetaTools` (so `load_trace` recommends `msdl.microsoft.com`) but its file path contains neither "Microsoft" nor "Windows", so `SymbolTools` falls through to the generic `"PDB not indexed; …"` message. The two tools disagree about the same module.
2. **Path-substring false-positive risk** — `SymbolTools` matching against `module.FilePath` means `C:\Users\ffmpegfan\foo.exe` is misidentified as ffmpeg, `\Microsoft Store cache\bar.exe` as Microsoft, etc. Theoretical but real.
3. **Maintenance burden** — extending the no-public-PDB tier (added in the same simplify pass) requires editing one table and remembering the other doesn't have an analogue. Easy to forget and produce silent drift.

## Goal

Single source of truth for "given a module name, what symbol hint applies?" — consumed identically by `load_trace`'s recommendations builder and `diagnose_symbols`'s per-module hint lookup.

## Architecture

### New file: `src/WprMcp/Core/SymbolHintCatalog.cs`

```csharp
namespace WprMcp.Core;

/// <summary>
/// One row of the catalog. Patterns match (case-insensitive substring) against module
/// names — typically TraceEvent's <c>ModuleFile.Name</c>, which is the basename without
/// extension (e.g. <c>"ntoskrnl"</c>, <c>"ffmpeg"</c>).
///
/// <para>
/// <c>ServerUrl</c> + <c>LoadTraceReason</c> are paired: both set means "load_trace
/// should recommend this server"; both null means "no public PDB server exists for
/// these modules" (consumed only by diagnose_symbols, skipped by load_trace).
/// </para>
/// </summary>
public sealed record SymbolHintEntry(
    string[] Patterns,
    string DiagnoseHint,
    string? ServerUrl = null,
    string? LoadTraceReason = null);

public static class SymbolHintCatalog
{
    public static IReadOnlyList<SymbolHintEntry> Entries { get; }
    public static SymbolHintEntry? Match(string moduleName);
}
```

### Catalog content (order = first-match precedence)

1. **No public PDB** — patterns: `ffmpeg`, `ffprobe`, `ffplay`. `ServerUrl = null`. Hint: existing rebuild-from-source text.
2. **Chromium symbol server** — patterns: `chrome`, `chromium`, `electron`, `cef`. `ServerUrl = https://chromium-browser-symsrv.commondatastorage.googleapis.com`. Hint: `"Add Chromium symbol server: add_symbol_server('…')"`. **Notably excludes `msedge` / `msedgewebview2`**: those are Microsoft-shipped binaries whose PDBs live on `msdl.microsoft.com`, not on the Chromium server (verified empirically against Edge 147 — `msedge.exe.pdb` HEADs 200 on msdl, 404 on chromium-symsrv). The current `MetaTools` Chromium tier listing `msedge` is itself wrong and is fixed by this unification.
3. **Microsoft public symbols** — full enumerated module list ported from `MetaTools.SymbolServerHints` plus `msedge` (which catches `msedgewebview2` via substring): `ntoskrnl, ntdll, kernel32, kernelbase, win32k, user32, gdi32, advapi32, rpcrt4, combase, ole32, oleaut32, shell32, shlwapi, msvcrt, ucrtbase, vcruntime, msvcp, fltmgr, mssecflt, wdf01000, wdfldr, mpengine, mpsvc, msedge, dxgi, d3d11, d3d12, d2d1, dwrite, windows.ui, wininet, winhttp, afd.sys, netio.sys, tcpip.sys, http.sys, win32u, dwmapi, dwmcore`. The explicit `msedgewebview2` entry from MetaTools is dropped — `msedge` substring covers it. `ServerUrl = https://msdl.microsoft.com/download/symbols`. Hint: `"Add Microsoft symbol server: add_symbol_server('…')"`.

The "no public PDB" tier comes first so a module name like `ffmpeg.exe` matches there before the Microsoft tier could pick it up via a `Microsoft`-substring match. (The Microsoft tier is now name-anchored and the false-positive risk is gone, but the ordering invariant still holds for the same logical reason.)

### `Match` semantics

```csharp
public static SymbolHintEntry? Match(string moduleName)
{
    foreach (var entry in Entries)
        if (entry.Patterns.Any(p => moduleName.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return entry;
    return null;
}
```

First-match-wins. Returns null when nothing matches; consumers fall back to a tool-specific default message.

## Refactor sites

### `src/WprMcp/Tools/SymbolTools.cs`

- Delete inline `ServerHints` table (lines 92–110).
- Delete the helper `SuggestServerForModule(string filePath)` and its tests' coupling to file paths.
- Replace caller (line 75) `SuggestServerForModule(module.FilePath)` with:
  ```csharp
  var hint = SymbolHintCatalog.Match(module.Name)?.DiagnoseHint
             ?? "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";
  ```
- The signature change ripples through `SymbolServiceTests.SuggestServerForModule_RoutesByPathSubstring` — see Tests below.

### `src/WprMcp/Tools/MetaTools.cs`

- Delete inline `SymbolServerHints` table (lines 18–41).
- `BuildSymbolRecommendations` (lines 76–108) iterates the catalog instead:
  ```csharp
  var serverEntries = SymbolHintCatalog.Entries
      .Where(e => e.ServerUrl != null)
      .ToList();

  var hits = serverEntries.Select(e => (Entry: e, Modules: new SortedSet<string>(StringComparer.OrdinalIgnoreCase))).ToList();
  // … rest unchanged: walk trace.ModuleFiles, match name against Patterns, collect into Modules,
  //   then project to SymbolRecommendation(Reason: e.LoadTraceReason, ServerUrl: e.ServerUrl, …).
  ```
- The "no public PDB" tier is filtered out for `load_trace` because there's no URL to recommend; `diagnose_symbols` still surfaces it.

## Behavior changes

| Scenario | Before | After |
|---|---|---|
| `C:\Tools\someApp\msvcp140.dll` (self-redistributed VC runtime) | `load_trace` → MS recommendation; `diagnose_symbols` → "PDB not indexed" | Both tools → MS recommendation |
| `C:\Users\ffmpegfan\foo.exe` (no ffmpeg involved) | `diagnose_symbols` → ffmpeg "no public PDB" hint (false positive) | `diagnose_symbols` → "PDB not indexed" (correct) |
| `\Microsoft Store cache\unrelated.exe` | `diagnose_symbols` → "Add MS server" hint (false positive on path) | `diagnose_symbols` → "PDB not indexed" (correct) |
| `msedge.exe` / `msedgewebview2.exe` | `load_trace` → Chromium recommendation (**wrong**, msdl is the actual host); `diagnose_symbols` → MS recommendation (right via path-substring) | Both tools → MS recommendation (correct, via `msedge` pattern in MS tier) |
| **Unlisted Windows DLLs under `\Windows\System32\`** (e.g. `setupapi.dll`, `crypt32.dll`, `iphlpapi.dll`, `bcrypt.dll`, `ws2_32.dll`, `dnsapi.dll` — anything not in the explicit ~38-item MS list) | `diagnose_symbols` → MS recommendation (via path-substring on `\Windows\` or `\Microsoft\`) | `diagnose_symbols` → "PDB not indexed" fall-through |
| Standard `ntoskrnl.exe`, `chrome.dll`, `ffmpeg.exe` | Both tools agree | Both tools still agree (now via single catalog) |

**The unlisted-Windows-DLL row is an intentional regression** of recall in exchange for precision. Today's path-substring approach catches every DLL under `\Windows\` (correct most of the time, wrong on `C:\Users\WindowsLover\foo.exe`-style paths). The unified catalog trades that broad reach for an explicit allowlist with no false positives. Mitigations:

- Users with `_NT_SYMBOL_PATH` already pointing at `msdl.microsoft.com` get full resolution regardless of the hint — the hint only matters when symbols aren't yet resolved and the user is shopping for a server to add.
- The 38-item explicit list covers the modules that actually appear in typical CPU/wait traces (kernel, NT runtime, GDI, COM/RPC, CLR runtime, networking stack, graphics stack, Defender). Modules that fall through (`setupapi` etc.) are usually rarely-on-stack.
- Adding modules to the list is one PR away — the catalog is centralised and easy to extend.

If this regression turns out to bite real users, follow-up: add a path-based fallback tier matching `\Windows\System32\` → MS hint, behind the explicit allowlist. Out of scope for this spec.

## Tests

### Updated

- **`SymbolServiceTests.SuggestServerForModule_RoutesByPathSubstring`** (`tests/WprMcp.Tests/SymbolServiceTests.cs:111`)
  - Rename → `RoutesByModuleName`.
  - InlineData rows switch from full paths to module names. Expected fragments unchanged:
    - `"ntoskrnl"` → `"msdl.microsoft.com"`
    - `"msedge"` → `"msdl.microsoft.com"` *(unchanged from existing test expectation — msdl really does host Edge symbols; the unification reaches the same destination via an explicit `msedge` pattern in the MS tier instead of a path-substring on "Microsoft")*
    - `"msedgewebview2"` → `"msdl.microsoft.com"` *(new row — covered by the same `msedge` substring pattern)*
    - `"chrome"` → `"chromium-browser-symsrv"`
    - `"electron"` → `"chromium-browser-symsrv"`
    - `"ffmpeg"` → `"no public PDB server"`
    - `"ffprobe"` → `"no public PDB server"`
    - `"MyApp"` → `"set_symbol_path"` (fall-through)

### New

- **`SymbolHintCatalogTests`** (`tests/WprMcp.Tests/SymbolHintCatalogTests.cs`):
  - Per-tier `Match` smoke test (one input per tier).
  - `Match("")` → `null`.
  - `Match` is case-insensitive (e.g., `"NTOSKRNL"`).
  - First-match precedence: a synthetic input matching multiple patterns returns the earlier entry. (Example: `"ffmpeg-nt"` matches both `"ffmpeg"` and `"nt"`-prefixed names, returns ffmpeg tier.)
  - `Entries.Count >= 3` (sanity; future entries shouldn't break the count assertion).

### Pre-existing (unchanged)

- `LoadTrace` integration tests — should continue to pass; recommendations should match the prior list module-for-module on existing fixtures.
- All 164 currently-passing tests must continue to pass post-refactor.

## File-by-file change summary

| File | Change | Net lines |
|---|---|---|
| `src/WprMcp/Core/SymbolHintCatalog.cs` | New | +70 |
| `src/WprMcp/Tools/SymbolTools.cs` | Remove inline table; rewrite caller line | -18 |
| `src/WprMcp/Tools/MetaTools.cs` | Remove inline table; rewrite recommendation builder | -25 |
| `tests/WprMcp.Tests/SymbolServiceTests.cs` | Theory data → module names | ~0 |
| `tests/WprMcp.Tests/SymbolHintCatalogTests.cs` | New | +30 |
| **Total** | | **+57 net** |

## Resolved during review

1. **`msedge` routing — msdl, not Chromium.** Earlier draft proposed routing msedge to the Chromium tier and changing the existing `SymbolServiceTests` expectation to `chromium-browser-symsrv`. That was wrong on the destination: empirical HEAD requests against an Edge 147 install confirm `msedge.exe.pdb` is on `msdl.microsoft.com` (200) and not on `chromium-browser-symsrv` (404). Microsoft owns and ships Edge; PDBs land on msdl. The Chromium symbol server is Google's, scoped to Chrome official builds. The current `MetaTools.SymbolServerHints` Chromium tier listing `msedge` is itself a latent bug — `load_trace` recommends a server that 404s for Edge symbols. **Resolution: `msedge` lives in the MS tier; Chromium tier omits it. Existing test expectation stays unchanged.**

2. **`msedgewebview2` placement.** Same publisher as `msedge`; same symbol server (msdl). Substring matching means a single `msedge` pattern in the MS tier catches both `msedge.exe` and `msedgewebview2.exe`. **Resolution: drop the explicit `msedgewebview2` entry; rely on `msedge` substring.**

3. **Unlisted Windows DLL recall regression.** Switching from path-substring matching (`Microsoft`/`Windows`) to enumerated module-name matching means Windows DLLs not in the explicit ~38-item MS list (e.g. `setupapi`, `crypt32`, `iphlpapi`) lose their auto-MS-hint and fall through to the generic "PDB not indexed" message. **Resolution: documented in the Behavior changes section as an intentional precision-over-recall tradeoff. Mitigation paths (path-based fallback tier, list expansion) noted but out of scope for this spec.**

## Out of scope

- Fuzzy / regex / word-boundary pattern matching. Substring is intentionally lenient — modules sometimes have version suffixes (`msvcp140`, `msvcp140_1`) that name-anchored matching would miss.
- Customer/runtime-extensible catalog. The catalog is internal, recompile-to-update.
- Other deferred simplify findings — none.

## Effort estimate

~80 lines new code, ~45 lines removed; 5 files touched in one refactor. Expected to land in a single commit. All existing 164 tests continue to pass; ~5 new tests added.
