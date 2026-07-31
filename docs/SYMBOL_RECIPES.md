# Symbol resolution recipes

`_NT_SYMBOL_PATH` accepts semicolon-separated entries. Each `SRV*<cache>*<url>` entry is a symbol server with a local cache; bare paths point at folders containing PDBs.

## Path syntax

```
[entry];[entry];[entry]…
```

Each entry is one of:

| Form | Meaning |
|---|---|
| `SRV*<cache-dir>*<server-url>` | Symbol server with local cache. Cache dir is created on demand. |
| `SRV*<cache-dir>*\\server\share` | UNC-backed "server" — team-shared symbol drop. |
| `<bare-folder>` | Local folder, scanned recursively. PDBs matched by signature (GUID + age). |
| `cache*<dir>` | Cache-only entry (no fetch). Rare; usually you want `SRV*`. |

**Order matters** — entries are tried left-to-right; first signature match wins. Put faster / preferred sources first:

- Local dev build folders go **first** when iterating on a build (so your fresh PDB beats the public one).
- `SRV*` entries self-cache after first hit, so order between multiple servers matters less than freshness.

## Common setups

### Microsoft system symbols (always recommended)

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols
```

Resolves `ntoskrnl`, `ntdll`, `kernelbase`, `fltmgr`, `wdfilter`, and the rest of the Windows public surface.

### + Chromium-family browsers (Chrome, Edge, Brave, …)

```
SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols;SRV*C:\Symbols*https://chromium-browser-symsrv.commondatastorage.googleapis.com
```

Public Chromium PDBs cover official builds of any browser using the Chromium symbol server.

### + Private vendor symbol server

```
…above…;SRV*C:\Symbols*https://your-internal-symsrv.example.com/symbols
```

UNC variant for team shared drives:

```
…above…;SRV*C:\Symbols*\\fileserver\symbols
```

### + Local dev build PDBs

```
C:\src\myapp\out\Default;…above…
```

Bare-folder entries (no `SRV*` prefix) are scanned recursively for PDB matches by signature. Place ahead of public servers to prefer your local rebuild.

## Build prerequisites for your own DLLs

A symbol server with the right URL doesn't help if the build never produced a usable PDB, or if the PDB's signature doesn't match the deployed DLL.

**.NET / C#**

```xml
<PropertyGroup>
  <DebugType>portable</DebugType>
  <DebugSymbols>true</DebugSymbols>
</PropertyGroup>
```

Portable PDB is the default for new SDK-style projects. For legacy projects targeting full framework, `<DebugType>full</DebugType>` produces a Windows PDB that TraceEvent reads natively. Keep PDB output enabled in Release configurations — many template `.csproj` files disable it.

**C++ (MSVC)**

- Compiler: `/Zi` (or `/Z7`)
- Linker: `/DEBUG:FULL`
- Keep the PDB next to the DLL on the build output path

Both are needed in Release builds too — Release configurations strip PDBs by default in many project templates.

**Signature match is non-negotiable**

PDB and DLL must share the same signature (GUID + age). Re-linking the same source file produces a different signature; the old PDB will not resolve the new DLL. Whenever you redeploy a binary, redeploy its PDB.

## Persisting the path

| Lifetime | How |
|---|---|
| Per tool call | `set_symbol_path` / `add_symbol_server` |
| Per MCP-server process | `--symbol-path "..."` in the MCP client's `args` list (see manual install in README) |
| Per user, system-wide | `[Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH", "...", "User")` |
| Install-time, baked into client config | `install.ps1 -SymbolPath "..."` writes `--symbol-path` into every detected MCP client's args |

For team shared setups, a JSON/TOML `args` entry is usually the right answer: checked in alongside the rest of the config, no per-machine state.

## Setting at runtime

```
> set_symbol_path SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols false
> add_symbol_server https://chromium-browser-symsrv.commondatastorage.googleapis.com
> diagnose_symbols C:\my\trace.etl
```

`set_symbol_path`'s second argument is `append` (default `true`). Pass `false` to **replace** the entire path — useful when you want to start clean. `add_symbol_server` always appends and is idempotent.

## Verifying it worked

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

Look at:

- `diagnose_symbols` → `Modules` list. Every important module should have `Resolved: true`.
- `cpu_top_functions` → `Stats.ResolutionRate`. Should be ≥ 0.8 for actionable output. < 0.5 means most of your top-N is `module!?` and the answer is unusable.

If a module you expect to resolve is unresolved, the hint field tells you which server to add (or, for private DLLs, points at "provide local PDB folder").

## Changing paths mid-session

After `set_symbol_path` / `add_symbol_server`, traces already loaded into the cache **do not re-resolve symbols** — `LookupWarmSymbols` is sticky per loaded `TraceLog`. To force re-resolution today, restart the MCP server. Once MCP exposes cache unload, the intended flow is:

```
> unload_trace C:\my\trace.etl
> load_trace C:\my\trace.etl
```

For routine "add MS symbol server then load trace" flows this is a non-issue (set the path before the first `load_trace`). It only bites when you change the path after running an analyzer and the second run keeps showing the same `module!?`.

## Cache management

- Default cache: `%LocalAppData%\WpaMcp\Symbols`. Override per-server in the `SRV*` entry.
- Separate from PerfView's `C:\Symbols` to avoid PDB-lock contention if both tools run side by side.
- The cache grows monotonically. After heavy use it can reach several GB.
- Safe to delete the entire directory at any time; the next stack-resolving tool call re-fetches whatever it actually needs.

## Common gotchas

| Symptom | Likely cause |
|---|---|
| `Stats.ResolutionRate` near 0 for all your DLLs | No `_NT_SYMBOL_PATH` set, or set but pointed at a server that doesn't have your symbols. |
| MS modules resolve, your DLL does not | Your build skipped PDB output, or PDB and deployed DLL are from different builds. |
| Worked yesterday, fails today after `set_symbol_path` | You changed the path mid-session — see "Changing paths mid-session" above. |
| Internal symsrv timeouts | VPN required; `_NT_SYMBOL_PROXY` env var if you need an HTTP proxy for the symbol fetch. |
| Two MCP servers fighting over PDB locks | Point each at a different cache directory. |
| `diagnose_symbols` says "PDB not indexed" for a Windows system DLL (e.g. `crypt32`, `bcrypt`, `setupapi`) | Symbols themselves still resolve normally if `msdl.microsoft.com` is on `_NT_SYMBOL_PATH` — only the per-module hint text is missing. The hint comes from an explicit allowlist (kernel + GDI + COM + .NET runtime + Defender + graphics + network + DWM); modules not on the list fall through to the generic message. |
