# Unify Symbol-Hint Pattern Tables — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace two parallel pattern tables (`SymbolTools.ServerHints`, `MetaTools.SymbolServerHints`) with a single `SymbolHintCatalog` in `Core/`, fixing a behavioural inconsistency between `load_trace` and `diagnose_symbols` and removing path-substring false-positive risks.

**Architecture:** New `SymbolHintEntry` record + static `SymbolHintCatalog` with three tiers (no-public-PDB / Chromium / Microsoft). Both `SymbolTools.SuggestServerForModule` and `MetaTools.BuildSymbolRecommendations` consume it. Match is case-insensitive substring against `ModuleFile.Name`; first-match-wins. `msedge` lives in the MS tier (not Chromium) — `msdl.microsoft.com` actually hosts Edge symbols (verified empirically).

**Tech Stack:** C# / .NET 8 / xUnit / TraceEvent (`Microsoft.Diagnostics.Tracing`). PowerShell host. `dotnet` is at `"C:\Program Files\dotnet\dotnet.exe"` (not on PATH).

**Spec:** `docs/superpowers/specs/2026-05-04-unify-symbol-hint-tables-design.md`

---

## File structure overview

| File | Action | Purpose |
|---|---|---|
| `src/WprMcp/Core/SymbolHintCatalog.cs` | Create | Record + 3-tier catalog + Match helper |
| `tests/WprMcp.Tests/SymbolHintCatalogTests.cs` | Create | Direct catalog unit tests |
| `src/WprMcp/Tools/SymbolTools.cs` | Modify | Delete inline `ServerHints`; `SuggestServerForModule` delegates to catalog; caller passes `module.Name` |
| `src/WprMcp/Tools/MetaTools.cs` | Modify | Delete inline `SymbolServerHints`; `BuildSymbolRecommendations` consumes catalog |
| `tests/WprMcp.Tests/SymbolServiceTests.cs` | Modify | Theory data: paths → module names; rename test method |

**Starting state:** Working tree has uncommitted simplify-pass edits in three files (`CpuTools.cs`, `SymbolTools.cs`, `SymbolServiceTests.cs`). The Setup task commits them first so the unification builds on a clean baseline.

---

## Setup: Commit pending simplify-pass changes

The simplify pass (already applied in working tree) added the `cpu_top_functions` scoping note, the ffmpeg "no public PDB" tier inside `SymbolTools.ServerHints`, and 2 ffmpeg test rows. These are independently-valuable improvements; commit them as a baseline before the unification refactor (which will then supersede the inline `ServerHints` table).

- [ ] **Step 1: Verify the three modified files exist and contain the expected simplify edits**

Run: `git -C C:/Users/admin3/Dev/wpa-mcp status -s`

Expected output (exactly these three lines plus the `??` for any new spec/plan files):
```
 M src/WprMcp/Tools/CpuTools.cs
 M src/WprMcp/Tools/SymbolTools.cs
 M tests/WprMcp.Tests/SymbolServiceTests.cs
```

If extra files appear modified, stop and ask the operator before proceeding.

- [ ] **Step 2: Stage and commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Tools/CpuTools.cs src/WprMcp/Tools/SymbolTools.cs tests/WprMcp.Tests/SymbolServiceTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "feat(symbols): no-public-PDB tier for ffmpeg + cpu_top_functions scoping note"
```

- [ ] **Step 3: Verify clean working tree**

Run: `git -C C:/Users/admin3/Dev/wpa-mcp status -s`
Expected: empty output (or only untracked files like the spec/plan markdown).

---

## Task 1: Create catalog skeleton + ffmpeg tier (TDD)

**Files:**
- Create: `src/WprMcp/Core/SymbolHintCatalog.cs`
- Create: `tests/WprMcp.Tests/SymbolHintCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/WprMcp.Tests/SymbolHintCatalogTests.cs` with this content:

```csharp
using WprMcp.Core;

namespace WprMcp.Tests;

public class SymbolHintCatalogTests
{
    [Fact]
    public void Match_FfmpegName_ReturnsNoPdbEntry()
    {
        var entry = SymbolHintCatalog.Match("ffmpeg");
        Assert.NotNull(entry);
        Assert.Null(entry!.ServerUrl);
        Assert.Contains("no public PDB server", entry.DiagnoseHint);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: build error or test failure. The build error will say `SymbolHintCatalog` is undefined (because the file doesn't exist yet).

- [ ] **Step 3: Create the catalog file**

Create `src/WprMcp/Core/SymbolHintCatalog.cs` with this content:

```csharp
namespace WprMcp.Core;

/// <summary>
/// One row of the catalog. Patterns match (case-insensitive substring) against the
/// module name as TraceEvent exposes it via <c>ModuleFile.Name</c>. The exposed form
/// is fixture-dependent — sometimes basename (<c>ntoskrnl</c>), sometimes basename plus
/// extension (<c>tcpip.sys</c>); substring matching tolerates either, so patterns may
/// include or omit the extension as long as they remain specific enough.
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

/// <summary>
/// Single source of truth for "given a module name, what symbol hint applies?".
/// Consumed by <c>SymbolTools.SuggestServerForModule</c> (per-module hint string) and
/// <c>MetaTools.BuildSymbolRecommendations</c> (load_trace's grouped server suggestions).
/// </summary>
/// <remarks>
/// Order matters — first-match-wins. Specific patterns precede generic ones so
/// <c>ffmpeg</c>'s no-PDB tier wins over a hypothetical Microsoft entry that might
/// otherwise catch it via substring.
/// </remarks>
public static class SymbolHintCatalog
{
    public static IReadOnlyList<SymbolHintEntry> Entries { get; } = new SymbolHintEntry[]
    {
        // Tier 1: no public PDB server
        new(
            Patterns: new[] { "ffmpeg", "ffprobe", "ffplay" },
            DiagnoseHint:
                "This module has no public PDB server — public Windows builds (gyan.dev / BtbN / " +
                "vendor app bundles) ship stripped.  Rebuild from source with linker /DEBUG:FULL " +
                "and place the local PDB folder ahead of any SRV* entries on _NT_SYMBOL_PATH; " +
                "otherwise treat this module as opaque and focus on resolved kernel frames."),
    };

    public static SymbolHintEntry? Match(string moduleName)
    {
        foreach (var entry in Entries)
            if (entry.Patterns.Any(p => moduleName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                return entry;
        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: `Passed!  - Failed:     0, Passed:     1`.

- [ ] **Step 5: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Core/SymbolHintCatalog.cs tests/WprMcp.Tests/SymbolHintCatalogTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "feat(symbols): introduce SymbolHintCatalog with no-PDB tier"
```

---

## Task 2: Add Chromium tier (TDD)

**Files:**
- Modify: `src/WprMcp/Core/SymbolHintCatalog.cs`
- Modify: `tests/WprMcp.Tests/SymbolHintCatalogTests.cs`

- [ ] **Step 1: Write the failing test**

Append this test method to `SymbolHintCatalogTests.cs` inside the class:

```csharp
[Theory]
[InlineData("chrome")]
[InlineData("chromium")]
[InlineData("electron")]
[InlineData("cef")]
public void Match_ChromiumModuleName_ReturnsChromiumEntry(string moduleName)
{
    var entry = SymbolHintCatalog.Match(moduleName);
    Assert.NotNull(entry);
    Assert.Equal("https://chromium-browser-symsrv.commondatastorage.googleapis.com", entry!.ServerUrl);
    Assert.Equal("Chromium-based browser (Chrome / Electron / CEF)", entry.LoadTraceReason);
    Assert.Contains("Add Chromium symbol server", entry.DiagnoseHint);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: 4 failures (one per Theory row) — `Match("chrome")` returns null because catalog only has the no-PDB tier.

- [ ] **Step 3: Add the Chromium tier**

In `SymbolHintCatalog.cs`, add this entry to the `Entries` array AFTER the existing ffmpeg entry (between the closing `)` of the ffmpeg `new(...)` and the closing `}` of the array):

```csharp
        // Tier 2: Chromium symbol server (Google Chrome official builds, Electron, CEF).
        // Excludes msedge / msedgewebview2 — those are MS-shipped and live on msdl
        // (verified empirically: msedge.exe.pdb HEADs 200 on msdl, 404 on chromium-symsrv).
        new(
            Patterns: new[] { "chrome", "chromium", "electron", "cef" },
            DiagnoseHint: "Add Chromium symbol server: add_symbol_server('https://chromium-browser-symsrv.commondatastorage.googleapis.com')",
            ServerUrl: "https://chromium-browser-symsrv.commondatastorage.googleapis.com",
            LoadTraceReason: "Chromium-based browser (Chrome / Electron / CEF)"),
```

The full `Entries` initializer should now contain exactly two `new(...)` entries — ffmpeg first, Chromium second. Don't forget the comma between them.

- [ ] **Step 4: Run test to verify it passes**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: `Passed!  - Failed:     0, Passed:     5` (1 ffmpeg + 4 Chromium theory rows).

- [ ] **Step 5: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Core/SymbolHintCatalog.cs tests/WprMcp.Tests/SymbolHintCatalogTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "feat(symbols): catalog Chromium tier (chrome, chromium, electron, cef)"
```

---

## Task 3: Add Microsoft tier with msedge correction (TDD)

**Files:**
- Modify: `src/WprMcp/Core/SymbolHintCatalog.cs`
- Modify: `tests/WprMcp.Tests/SymbolHintCatalogTests.cs`

- [ ] **Step 1: Write the failing tests**

Append this method to `SymbolHintCatalogTests.cs`:

```csharp
[Theory]
[InlineData("ntoskrnl")]
[InlineData("ntdll")]
[InlineData("kernel32")]
[InlineData("msvcp")]
[InlineData("mpengine")]
[InlineData("tcpip.sys")]   // kernel-driver pattern: extension is part of Name
[InlineData("msedge")]       // corrected routing: MS, not Chromium
[InlineData("msedgewebview2")] // covered by `msedge` substring
public void Match_MicrosoftModuleName_ReturnsMicrosoftEntry(string moduleName)
{
    var entry = SymbolHintCatalog.Match(moduleName);
    Assert.NotNull(entry);
    Assert.Equal("https://msdl.microsoft.com/download/symbols", entry!.ServerUrl);
    Assert.Equal("Microsoft public symbols", entry.LoadTraceReason);
    Assert.Contains("Add Microsoft symbol server", entry.DiagnoseHint);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: 8 failures (Theory rows) — catalog has no MS tier yet.

- [ ] **Step 3: Add the Microsoft tier**

In `SymbolHintCatalog.cs`, add this entry after the Chromium tier (so the order is ffmpeg → Chromium → Microsoft):

```csharp
        // Tier 3: Microsoft public symbols (msdl). Patterns ported from the previous
        // MetaTools.SymbolServerHints list, plus `msedge` (which catches `msedgewebview2`
        // via substring — both are MS-published and live on msdl).
        new(
            Patterns: new[]
            {
                "ntoskrnl", "ntdll", "kernel32", "kernelbase", "win32k", "user32", "gdi32",
                "advapi32", "rpcrt4", "combase", "ole32", "oleaut32", "shell32", "shlwapi",
                "msvcrt", "ucrtbase", "vcruntime", "msvcp",
                "fltmgr", "mssecflt", "wdf01000", "wdfldr",
                "mpengine", "mpsvc",
                "msedge",
                "dxgi", "d3d11", "d3d12", "d2d1", "dwrite", "windows.ui", "wininet", "winhttp",
                "afd.sys", "netio.sys", "tcpip.sys", "http.sys",
                "win32u", "dwmapi", "dwmcore",
            },
            DiagnoseHint: "Add Microsoft symbol server: add_symbol_server('https://msdl.microsoft.com/download/symbols')",
            ServerUrl: "https://msdl.microsoft.com/download/symbols",
            LoadTraceReason: "Microsoft public symbols"),
```

- [ ] **Step 4: Run test to verify it passes**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: `Passed!  - Failed:     0, Passed:    13` (1 ffmpeg + 4 Chromium + 8 Microsoft).

- [ ] **Step 5: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Core/SymbolHintCatalog.cs tests/WprMcp.Tests/SymbolHintCatalogTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "feat(symbols): catalog Microsoft tier with msedge → msdl correction"
```

---

## Task 4: Add invariant tests (ordering, case, fall-through, count)

The catalog already implements these properties; this task adds explicit regression guards.

**Files:**
- Modify: `tests/WprMcp.Tests/SymbolHintCatalogTests.cs`

- [ ] **Step 1: Write all four tests**

Append to `SymbolHintCatalogTests.cs`:

```csharp
[Fact]
public void Match_UnknownModule_ReturnsNull()
{
    Assert.Null(SymbolHintCatalog.Match("MyAppPrivateDll"));
    Assert.Null(SymbolHintCatalog.Match(""));
}

[Fact]
public void Match_IsCaseInsensitive()
{
    Assert.NotNull(SymbolHintCatalog.Match("NTOSKRNL"));
    Assert.NotNull(SymbolHintCatalog.Match("FFmpeg"));
    Assert.NotNull(SymbolHintCatalog.Match("Chrome"));
}

[Fact]
public void Match_NoPdbTakesPrecedenceOverMicrosoft()
{
    // A name containing both "ffmpeg" and "ntdll" must match the no-PDB tier first
    // because no-PDB precedes Microsoft in the catalog. This locks in the
    // first-match-wins ordering invariant.
    var entry = SymbolHintCatalog.Match("ffmpeg-with-ntdll-in-name");
    Assert.NotNull(entry);
    Assert.Null(entry!.ServerUrl);
}

[Fact]
public void Entries_HasExactlyThreeTiers()
{
    // Sanity guard: extending the catalog with a 4th tier should be a deliberate
    // decision, not an accident. Update this assertion when adding tiers.
    Assert.Equal(3, SymbolHintCatalog.Entries.Count);
}
```

- [ ] **Step 2: Run tests to verify they pass**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SymbolHintCatalogTests"`

Expected: `Passed!  - Failed:     0, Passed:    17` (13 prior + 4 new).

If any fail, the catalog implementation has a real defect — investigate before proceeding.

- [ ] **Step 3: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add tests/WprMcp.Tests/SymbolHintCatalogTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "test(symbols): catalog ordering / case-insensitivity / fall-through invariants"
```

---

## Task 5: Refactor SymbolTools to consume the catalog

`SymbolTools.SuggestServerForModule` becomes a one-line delegate to `SymbolHintCatalog.Match`. The inline `ServerHints` table and its 7-line comment block are deleted. The call site (`DiagnoseSymbols`) switches from passing `module.FilePath` to passing `module.Name`. The existing `SymbolServiceTests` theory rewrites its InlineData rows from full paths to module names.

**Files:**
- Modify: `src/WprMcp/Tools/SymbolTools.cs`
- Modify: `tests/WprMcp.Tests/SymbolServiceTests.cs`

- [ ] **Step 1: Update the existing test theory to module names (test-first per TDD)**

In `tests/WprMcp.Tests/SymbolServiceTests.cs`, replace the existing `[Theory] / [InlineData] / SuggestServerForModule_RoutesByPathSubstring` test method (around line 111) with this:

```csharp
[Theory]
[InlineData("ntoskrnl", "msdl.microsoft.com")]
[InlineData("msedge", "msdl.microsoft.com")]
[InlineData("msedgewebview2", "msdl.microsoft.com")]
[InlineData("chrome", "chromium-browser-symsrv")]
[InlineData("electron", "chromium-browser-symsrv")]
[InlineData("ffmpeg", "no public PDB server")]
[InlineData("ffprobe", "no public PDB server")]
[InlineData("MyApp", "set_symbol_path")]
public void SuggestServerForModule_RoutesByModuleName(string moduleName, string expectedHintFragment)
{
    var hint = SymbolTools.SuggestServerForModule(moduleName);
    Assert.Contains(expectedHintFragment, hint);
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --filter "FullyQualifiedName~SuggestServerForModule_RoutesByModuleName"`

Expected: 8 failures — `SuggestServerForModule("ntoskrnl")` currently does substring on full path containing "Microsoft" / "Windows", which doesn't match the bare name `"ntoskrnl"`. So most rows will fall through to `"PDB not indexed; …"` and the assertion fails.

- [ ] **Step 3: Refactor SymbolTools**

In `src/WprMcp/Tools/SymbolTools.cs`:

(a) **Update the `using` directives** at the top of the file — add `using WprMcp.Core;` if it's not already there. The existing file already imports `WprMcp.Core` (for `SymbolService`), so this is likely a no-op; verify by reading the top of the file.

(b) **Update the caller in `DiagnoseSymbols`** (around line 75). Find this block:

```csharp
            var resolved = !string.IsNullOrEmpty(module.PdbName);
            var hint = resolved
                ? "PDB resolved."
                : SuggestServerForModule(module.FilePath);
```

Change `module.FilePath` to `module.Name`:

```csharp
            var resolved = !string.IsNullOrEmpty(module.PdbName);
            var hint = resolved
                ? "PDB resolved."
                : SuggestServerForModule(module.Name);
```

(c) **Replace the inline `ServerHints` table and the body of `SuggestServerForModule`** (lines around 92–109 — the comment block, `ServerHints` initialiser, and the function). Replace the entire block with this:

```csharp
    // Per-module hint lookup is centralised in SymbolHintCatalog (see Core/).
    // Both this method and MetaTools.BuildSymbolRecommendations consume that catalog.
    internal static string SuggestServerForModule(string moduleName)
        => SymbolHintCatalog.Match(moduleName)?.DiagnoseHint
           ?? "PDB not indexed; provide local PDB folder via set_symbol_path or contact the module owner.";
```

After this edit the file should be ~20 lines shorter.

- [ ] **Step 4: Build and run all tests**

Run: `& "C:\Program Files\dotnet\dotnet.exe" build --nologo -v q`

Expected: build succeeds, 0 warnings, 0 errors.

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --no-build`

Expected: all tests pass — both the new `SymbolHintCatalogTests` (17 tests), the renamed `SuggestServerForModule_RoutesByModuleName` (8 theory rows), and all other existing tests. Total around `Passed: 182` (164 baseline + 17 new catalog tests + 8 new module-name theory rows − 7 old path-substring theory rows = 182).

- [ ] **Step 5: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Tools/SymbolTools.cs tests/WprMcp.Tests/SymbolServiceTests.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "refactor(symbols): SymbolTools.SuggestServerForModule consumes SymbolHintCatalog"
```

---

## Task 6: Refactor MetaTools to consume the catalog

`MetaTools.BuildSymbolRecommendations` deletes its inline `SymbolServerHints` table and iterates `SymbolHintCatalog.Entries.Where(e => e.ServerUrl != null)`. The grouping logic stays. The "no public PDB" tier is filtered out — `load_trace` doesn't recommend a server that doesn't exist.

**Files:**
- Modify: `src/WprMcp/Tools/MetaTools.cs`

- [ ] **Step 1: Delete the inline `SymbolServerHints` table**

In `src/WprMcp/Tools/MetaTools.cs`, find the `SymbolServerHints` private static field (around lines 15–41 — the comment block plus the table initialiser). Delete the entire block. The file should now have nothing between the field declarations on `_cache`/the constructor and the `[McpServerTool, Description(` attribute on `LoadTrace`.

Add `using WprMcp.Core;` near the top of the file if it's not already present (check the existing usings; the file already imports `WprMcp.Analyzers` and `WprMcp.Output`, so this is a single-line addition).

- [ ] **Step 2: Rewrite `BuildSymbolRecommendations`**

Find the existing `BuildSymbolRecommendations` method (around lines 76–108). Replace its body with this:

```csharp
    private static IReadOnlyList<SymbolRecommendation> BuildSymbolRecommendations(
        Microsoft.Diagnostics.Tracing.Etlx.TraceLog trace)
    {
        // Catalog entries that recommend a server (skip the no-public-PDB tier — it has no
        // URL to recommend, only diagnose_symbols consumes it).
        var serverEntries = SymbolHintCatalog.Entries
            .Where(e => e.ServerUrl != null && e.LoadTraceReason != null)
            .ToList();

        var hits = serverEntries
            .Select(e => (Entry: e, Modules: new SortedSet<string>(StringComparer.OrdinalIgnoreCase)))
            .ToList();

        foreach (var module in trace.ModuleFiles)
        {
            // Already-resolved modules don't need a recommendation.
            if (!string.IsNullOrEmpty(module.PdbName)) continue;

            var name = module.Name ?? string.Empty;
            for (var i = 0; i < hits.Count; i++)
            {
                var patterns = hits[i].Entry.Patterns;
                if (patterns.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    hits[i].Modules.Add(name);
                    break;
                }
            }
        }

        return hits
            .Where(h => h.Modules.Count > 0)
            .Select(h => new SymbolRecommendation(
                Reason: h.Entry.LoadTraceReason!,
                ServerUrl: h.Entry.ServerUrl!,
                MatchedModuleCount: h.Modules.Count,
                SampleModules: h.Modules.Take(5).ToList()))
            .ToList();
    }
```

The bang operators (`!`) on `LoadTraceReason!` and `ServerUrl!` are safe because of the `Where` filter at the top.

- [ ] **Step 3: Build and run all tests**

Run: `& "C:\Program Files\dotnet\dotnet.exe" build --nologo -v q`

Expected: 0 warnings, 0 errors.

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --no-build`

Expected: all tests pass. The relevant tests are:
- `SymbolHintCatalogTests` (17 tests) — unchanged.
- `SymbolServiceTests.SuggestServerForModule_RoutesByModuleName` (8 theory rows) — unchanged.
- `MetaToolsTests` — these don't assert specific recommendation contents (they only check shape: `Capabilities` not null, `SymbolStatus.Warning` set when path unset). Should pass without modification.
- All other tests — unaffected.

If `MetaToolsTests` does fail unexpectedly, the failure mode would be a NullReferenceException from the bang operators, which would indicate the catalog has an entry with one of `ServerUrl` / `LoadTraceReason` set and the other null. Inspect the catalog and ensure the pairing invariant holds.

- [ ] **Step 4: Commit**

```powershell
git -C C:/Users/admin3/Dev/wpa-mcp add src/WprMcp/Tools/MetaTools.cs
git -C C:/Users/admin3/Dev/wpa-mcp commit -m "refactor(symbols): MetaTools.BuildSymbolRecommendations consumes SymbolHintCatalog"
```

---

## Task 7: Final verification sweep

No code changes. Confirm the working tree is clean and all tests pass.

- [ ] **Step 1: Verify clean working tree**

Run: `git -C C:/Users/admin3/Dev/wpa-mcp status -s`
Expected: empty (no modified or untracked files apart from possibly the spec/plan markdown if those weren't committed).

- [ ] **Step 2: Full build with warnings as errors**

Run: `& "C:\Program Files\dotnet\dotnet.exe" build --nologo -v q -warnaserror`

Expected: build succeeds, 0 warnings, 0 errors.

- [ ] **Step 3: Full test suite**

Run: `& "C:\Program Files\dotnet\dotnet.exe" test --nologo --no-build`

Expected: all tests pass — total around 180+ (baseline 164 minus removed simplify rows plus the 17 new catalog tests plus the 8 theory rows).

- [ ] **Step 4: Visual diff sanity check**

Run: `git -C C:/Users/admin3/Dev/wpa-mcp log --oneline -10`

Expected to see (most recent first):
```
<sha> refactor(symbols): MetaTools.BuildSymbolRecommendations consumes SymbolHintCatalog
<sha> refactor(symbols): SymbolTools.SuggestServerForModule consumes SymbolHintCatalog
<sha> test(symbols): catalog ordering / case-insensitivity / fall-through invariants
<sha> feat(symbols): catalog Microsoft tier with msedge → msdl correction
<sha> feat(symbols): catalog Chromium tier (chrome, chromium, electron, cef)
<sha> feat(symbols): introduce SymbolHintCatalog with no-PDB tier
<sha> feat(symbols): no-public-PDB tier for ffmpeg + cpu_top_functions scoping note
…
```

If any commit is missing, the operator skipped a Task's commit step — re-run that Task.

- [ ] **Step 5: Final summary message**

Print to operator: 7 commits added (1 setup + 6 unification). `SymbolHintCatalog` is the single source of truth for module → symbol-hint routing. Both `SymbolTools` and `MetaTools` consume it. Behavioral fixes (`msedge` → `msdl`, no false positives on user-named directories) and intentional regression (unlisted Windows DLLs fall through to "PDB not indexed") are documented in the spec.

---

## Self-review checklist (already run; recorded for traceability)

- **Spec coverage:** Architecture (Tasks 1–4), refactor sites (Tasks 5–6), behavior changes (test asserts in Tasks 3, 5; verified in Task 7), tests (Tasks 1–4 new file, Task 5 update of existing). ✓
- **Placeholder scan:** No "TODO", "TBD", "implement later", or "similar to Task N". All code blocks contain the literal content the engineer needs to type. ✓
- **Type consistency:** `SymbolHintEntry` record fields (`Patterns`, `DiagnoseHint`, `ServerUrl`, `LoadTraceReason`) used identically across all tasks. `Match` signature consistent across catalog definition (Task 1), call sites (Tasks 5, 6), and tests. The renamed test `SuggestServerForModule_RoutesByModuleName` is referenced consistently in Task 5's grep filter and the fluent description. ✓
- **Risk note for Task 6:** `MetaToolsTests` doesn't assert specific recommendation contents (verified during planning), so no test updates needed there. If a future fixture trace adds modules whose routing changes, that's the only place to watch.
