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
| `<bare-folder>` | Local folder. `diagnose_symbols` probes only `<bare-folder>\<pdbName>`; it does not treat a plain path as a symbol-store root. |
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

For its no-download readiness check, `diagnose_symbols` probes a bare-folder entry only for a direct `<folder>\<pdbName>` file. It probes `<root>\<pdbName>\<GUIDAge>\<pdbName>` only when the root came from `SRV`, `SYMSRV`, or `CACHE`. Place the bare build-output folder ahead of public servers to prefer your local rebuild during real stack lookup.

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
| Current MCP-server process, set at runtime | `set_symbol_path` / `add_symbol_server`; the change remains until changed again or the server exits |
| Current MCP-server process, initialized at startup | `--symbol-path "..."` in the MCP client's `args` list (see manual install in README) |
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

When `add_symbol_server` is called without `cacheDir`, `DefaultCacheDir` is the fallback it would use. The legacy `diagnose_symbols.CacheDir` field is only a compatibility alias of that value; inspect `ConfiguredSymbolPath` rather than assuming the fallback is the active cache.

## Verifying it worked

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

Keep four layers separate:

1. `HasCompletePdbIdentity` means the trace has a PDB name + GUID + age lookup key. It does not prove that a PDB exists locally or remotely.
2. `LocalSymbolCandidates` shows at most 10 files with the expected name. `LocalSymbolCandidateCount` and `LocalSymbolCandidatesTruncated` disclose the complete discovery set. Every candidate is validated before display truncation, and an exact match is moved to the front of the displayed list.
3. `diagnose_symbols` directly opens each discovered candidate path and reports `exact_identity_match`, `identity_mismatch`, `invalid_local_pdb_candidate`, or `candidate_identity_unverified`. Container-probe rejection and explicit portable-PDB data errors can establish invalid data; ambiguous Windows DIA failures remain `candidate_identity_unverified` rather than claiming candidate corruption. `LocalPdbReady` is true only after the PDB's actual GUID/age matches and the format-appropriate reader succeeds.
4. The relevant stack tool reports `SymbolResolutionState` plus `Stats.ObservedUniqueCodeFrameNameResolutionRate` and `Stats.ObservedMetricWeightedCodeFrameNameResolutionRate`. These are measured only across real code frames reached by that query; synthetic `?!?` frames are excluded. A null rate means no eligible code frames were measured, not 0% resolution.

If identity metadata is incomplete, recapture or merge the ETL on the collection machine before choosing a server: without PDB name + GUID + age there is no executable symbol-server query. Otherwise use the module hint to improve PDB availability, then rerun the stack query. Interpret the observed rate with domain stack coverage and synthetic-frame counts; there is no universal threshold that makes every trace actionable.

## Changing paths mid-session

Each stack query snapshots the currently configured path and adds the ETL directory to that query's `SymbolReader` without writing it back to `_NT_SYMBOL_PATH`. After `set_symbol_path` / `add_symbol_server`, rerun the relevant stack tool so lookup uses the new snapshot. Previously resolved names can remain cached in the resident `TraceLog`; the response's lookup state and observed frame rates describe the current query rather than claiming a pristine resolver session.

## Filesystem side effects and MCP metadata

Symbol configuration and logical event analysis are separate from filesystem side effects:

- The first query for a raw `.etl` can call `TraceLog.OpenOrConvert`, creating or refreshing an adjacent `.etlx`.
- A stack query with `resolveSymbols=true` can contact configured servers and write/download PDBs into their caches.
- Consequently, every tool uses `ReadOnly=false` MCP metadata in 0.3.0 because a call can change server or filesystem state, even though analyzers do not edit the ETL's logical event content. Caller-supplied trace/cache paths may be UNC, mapped, or reparse-point targets, so raw-path tools conservatively use `OpenWorld=true` even when they do not run remote symbol lookup; `set_symbol_path` is the sole `OpenWorld=false` tool.
- `Destructive=true` conservatively covers adjacent-ETLX replacement/refresh, cache retirement, and replacement of the process-wide symbol path. Incremental `add_symbol_server` is the sole `Destructive=false` tool. All tools are marked idempotent except `set_symbol_path`.
- `diagnose_symbols` opens only the exact candidate paths it discovered, using an empty symbol search path. It does not actively access remote SRV/UNC entries or download PDBs. A configured local-looking filesystem root can nevertheless be redirected by the OS through a mapped drive or reparse point; the tool deliberately performs no expensive network-topology detection. This identity/readiness check remains distinct from an executed stack lookup and does not measure frame-name resolution.

## Cache management

- Default cache: `%LocalAppData%\WpaMcp\Symbols`. Override per-server in the `SRV*` entry.
- Separate from PerfView's `C:\Symbols` to avoid PDB-lock contention if both tools run side by side.
- The cache grows monotonically. After heavy use it can reach several GB.
- Safe to delete the entire directory at any time; the next stack-resolving tool call re-fetches whatever it actually needs.

## Common gotchas

| Symptom | Likely cause |
|---|---|
| An executed stack lookup has low observed code-frame resolution across your DLLs | The configured path may be missing, unreachable, or lack matching PDB signatures. Check `LookupState`, `LookupFailure`, and module readiness before attributing the cause. |
| MS modules resolve, your DLL does not | Your build skipped PDB output, or PDB and deployed DLL are from different builds. |
| Results differ after `set_symbol_path` | Rerun the stack query and compare its query-local lookup state/rates; already resolved frame names may remain cached in the loaded trace. |
| Internal symsrv timeouts | VPN required; `_NT_SYMBOL_PROXY` env var if you need an HTTP proxy for the symbol fetch. |
| Two MCP servers fighting over PDB locks | Point each at a different cache directory. |
| `diagnose_symbols` says "PDB not indexed" for a Windows system DLL (e.g. `crypt32`, `bcrypt`, `setupapi`) | Symbols themselves still resolve normally if `msdl.microsoft.com` is on `_NT_SYMBOL_PATH` — only the per-module hint text is missing. The hint comes from an explicit allowlist (kernel + GDI + COM + .NET runtime + Defender + graphics + network + DWM); modules not on the list fall through to the generic message. |
