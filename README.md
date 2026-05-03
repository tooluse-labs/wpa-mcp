<p align="center">
  <img src="assets/wpa-mcp-logo.svg" alt="wpa-mcp" width="720">
</p>

# wpa-mcp

C# MCP server that exposes Windows ETW (`.etl`) trace analyzers — CPU / wait / image-load / file-disk-mmap I/O, with top-N + caller-callee + time-bucketing variants — over any MCP-compatible client (Claude Code, Claude Desktop, Codex, Cursor). Domain-neutral: works on any Windows trace; commonly used for app startup, IO, AV-induced stalls, slow-fork investigations.

## Status

PoC. ~17 tools across:
- **Meta**: `load_trace` (returns `Capabilities` keyword-presence map), `list_processes`, `process_create_timing`
- **CPU**: `cpu_top_functions`, `cpu_top_functions_batch`, `cpu_caller_callee`
- **Wait**: `wait_analysis`, `wait_top_stacks`, `wait_caller_callee`
- **Image load**: `image_load_timing`, `image_load_top_gaps`, `image_load_top_stacks`, `image_load_caller_callee`
- **File / disk / mmap I/O**: `file_io_top_files`, `file_io_top_stacks`, `file_io_caller_callee`, `disk_io_top_stacks`, `disk_io_caller_callee`, `mmap_hot_files`, `mmap_top_stacks`, `mmap_caller_callee`
- **Marker / symbols**: `find_marker`, `set_symbol_path`, `add_symbol_server`, `diagnose_symbols`, `diagnose_slow_startup`

Not for production. Internal use only until validated.

## Requirements

- Windows 10/11 (TraceEvent kernel APIs are Windows-only).
- .NET 8 SDK — `winget install Microsoft.DotNet.SDK.8`.
- For symbol resolution: `_NT_SYMBOL_PATH` set, or use the symbol tools at runtime (see below).

## Install (one-liner — no clone, no build)

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/bootstrap.ps1) }"
```

The bootstrap downloads the latest GitHub Release zip (pre-built DLL), caches it under `%LOCALAPPDATA%\wpa-mcp\releases\<tag>\`, and runs the bundled `install.ps1`. Subsequent runs are instant (cache hit). To uninstall, run the cached `uninstall.ps1` from the same folder, or re-bootstrap a different tag.

Forward flags through the bootstrap:

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/bootstrap.ps1) } -InstallArgs @('-Client','claude-desktop','-SymbolPath','SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols')"
```

## Install (from a clone)

If you've already cloned the repo (e.g. for development):

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
.\scripts\install.ps1
```

Builds (Release) and registers `wpa-mcp` with whichever MCP client(s) it detects (Claude Code via `claude mcp add`, Claude Desktop via `%APPDATA%\Claude\claude_desktop_config.json` edit). Idempotent — re-run to update.

Common flags:

```powershell
.\scripts\install.ps1 -Client claude-desktop                                  # force a specific client
.\scripts\install.ps1 -SymbolPath "SRV*C:\Symbols*https://..."               # custom _NT_SYMBOL_PATH
.\scripts\install.ps1 -SkipBuild                                              # use existing DLL
```

Uninstall (works for either install path):

```powershell
.\scripts\uninstall.ps1                  # remove from all detected clients
.\scripts\uninstall.ps1 -CleanBuild      # also wipe bin/ obj/
```

## Manual install (if the script doesn't fit your setup)

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
dotnet build -c Release
# DLL produced at: src\WprMcp\bin\Release\net8.0\WprMcp.dll
```

Smoke-check the build:

```powershell
dotnet src\WprMcp\bin\Release\net8.0\WprMcp.dll --version    # prints "WprMcp 0.1.0-poc"
dotnet test                                                   # runs the xUnit suite (needs fixtures, see below)
```

Then register with your MCP client. Pick one path below. The DLL path must be **absolute**.

### Claude Code (CLI)

Per-project (`<project>/.mcp.json`) or global (`%APPDATA%\Claude\claude.json` / `~/.claude.json`):

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "dotnet",
      "args": ["C:/Users/me/Dev/wpa-mcp/src/WprMcp/bin/Release/net8.0/WprMcp.dll"],
      "env": {
        "_NT_SYMBOL_PATH": "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
        "WPRMCP_CACHE_SIZE": "2"
      }
    }
  }
}
```

Or via the CLI helper:

```powershell
claude mcp add wpa-mcp --scope user -- dotnet C:/Users/me/Dev/wpa-mcp/src/WprMcp/bin/Release/net8.0/WprMcp.dll
```

(Add `-e _NT_SYMBOL_PATH=...` for env vars.)

### Claude Desktop

`%APPDATA%\Claude\claude_desktop_config.json` — same shape as above.

### Codex / Cursor / other MCP-compatible clients

The server speaks stdio MCP; any client that takes a `command + args` config works. Use the same JSON snippet.

### Verify

After restart, the client should expose the tools as `mcp__wpa-mcp__load_trace`, etc. First call to `load_trace` on a fresh `.etl` takes 30 s – 3 min while the `.etlx` index is built (logged to stderr).

## Symbol resolution

The single biggest source of "garbage output". If `cpu_top_functions` shows `module!?` everywhere and `Stats.ResolutionRate < 0.8`, you don't have working symbols.

### Three setup paths (pick one — they all set the same `_NT_SYMBOL_PATH`)

**1. Pre-launch env var (cleanest, survives restarts).** Set once and let MCP inherit:

```powershell
[Environment]::SetEnvironmentVariable(
    "_NT_SYMBOL_PATH",
    "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols",
    "User")
# restart MCP client / re-login to pick up
```

**2. Per-MCP-server `env` block in the config JSON** (shown above). Easiest to share between teammates.

**3. Runtime via tool calls** (when 1+2 weren't done):

```
> Set the symbol path: SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols
(MCP calls set_symbol_path)

> Add another symbol server: <your-server-URL>
(MCP calls add_symbol_server)

> diagnose_symbols on this trace: C:\path\to\trace.etl
```

### Symbol cache location

`%LocalAppData%\WprMcp\Symbols` by default (separate from PerfView's `C:\Symbols` to avoid PDB-lock contention). First-time downloads of e.g. `ntoskrnl.pdb` can take a minute on a slow link; cached after.

### Per-trace recommendations

`load_trace` inspects the trace's module list and returns `SymbolStatus.Recommendations`:

```json
{ "Reason": "Microsoft public symbols",
  "ServerUrl": "https://msdl.microsoft.com/download/symbols",
  "MatchedModuleCount": 12,
  "SampleModules": ["ntoskrnl", "ntdll", "kernelbase", "fltmgr", "wdfilter"] }
```

Use that to pick which servers to add. Built-in hint groups cover Microsoft public symbols and Chromium-family browsers; see `docs/SYMBOL_RECIPES.md` for adding your own (private vendor symbol servers, local-build PDB folders, etc.).

## Quickstart

After restart, ask the agent in plain language; it picks the matching tools. A typical investigation flow:

```
> Load this trace: C:\path\to\trace.etl
(load_trace; first call 30s-3min as .etlx is built; subsequent are instant.
 Response includes Capabilities so you know upfront which keywords are present.)

> Which processes have the highest wait ratio?
(list_processes orderBy=wait_ratio — trace-resident processes auto-filtered out)

> For parent PID <X>, what was each fork's kernel-side gap?
(process_create_timing — one call gives kernel-window distribution across all children)

> Top wait stacks for PID <X> between <t0> and <t1>, with 20-bucket histogram
(wait_top_stacks — shows the Filter Manager / driver chain blocking the thread)

> Drill into "<frame!?>": who calls it?
(wait_caller_callee — caller/callee neighbors of the focus frame)
```

The same pattern works for CPU (`cpu_top_functions` → `cpu_caller_callee`), file/disk/mmap I/O, image loads, etc. Each "top" view has a matching "caller-callee" drill-down.

## Trace caching

LRU, default capacity 2 traces. Override with `WPRMCP_CACHE_SIZE=N`. First load builds `.etlx` (slow); cached calls are instant. `Capabilities` and `TraceLog` are both cached per (path, mtime) — re-loading the same `.etl` is free.

## Capturing your own traces

See `docs/WPR_PROFILE.md` for a recommended `.wprp` that captures CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks.

Quick canonical capture:

```powershell
wpr.exe -start tests\WprMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … reproduce the slow case …
wpr.exe -stop C:\path\to\my_capture.etl
```

## Architecture

See `docs/ARCHITECTURE.md` for the high-level layout. Modifying the analyzers? Read `CONTRIBUTING.md` first — it documents non-obvious invariants (PerfView-parity in `CpuAnalysis`, kernel-parser attachment rules, file-vs-mmap keying) that are easy to break in a refactor.

## License

Apache License 2.0. See `LICENSE` for the full text. Contributions are accepted under the same license per Apache 2.0 § 5.

## Troubleshooting

- **`dotnet: command not found`** — install the SDK: `winget install Microsoft.DotNet.SDK.8`, then restart your shell / MCP client.
- **MCP server fails to start** — run the DLL directly: `dotnet C:\path\to\WprMcp.dll --version`. If that fails, the build is broken or the path is wrong.
- **Tool list is missing the new tools** — your MCP client cached an old binary. Fully quit and re-launch (Claude Desktop) or re-run `claude mcp restart` (Claude Code).
- **`SymbolStatus.Warning` says `_NT_SYMBOL_PATH is not set`** — the server's process didn't inherit the env var. Use option 2 (per-server `env` block) or call `set_symbol_path` at runtime.
- **`ResolutionRate < 0.5`** with paths set — first downloads in progress, or no network to symbol servers. Re-run after a minute, or run `diagnose_symbols` for module-by-module hints.
- **`mmap_hot_files` returns empty** — the trace lacks the `HardFaults` keyword. Check `load_trace` response: `Capabilities.HasHardFaults` will be `false`. Re-capture with `MmapCapture.wprp`.
- **`file_io_top_files` returns empty** — same as above for `Capabilities.HasFileIo`. Default `CPU.light` profile omits FileIO.
- **First `load_trace` taking forever** — the `.etlx` index is being built. Watch stderr; expect 30s for a 100 MB `.etl`, several minutes for multi-GB. Subsequent loads of the same file are instant.
