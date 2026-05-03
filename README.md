<p align="right">
  <strong>English</strong> | <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

# wpa-mcp

A C# MCP server that exposes Windows ETW (`.etl`) trace analyzers — CPU, wait, image-load, file / disk / mmap I/O — over any MCP-compatible client (Claude Code, Claude Desktop, Codex, Cursor). Domain-neutral: works on any Windows trace; commonly used to debug app startup, slow forks, AV-induced stalls, and disk-bound regressions.

> **Status — PoC.** ~17 tools live, internal use only until validated. Windows-only (TraceEvent kernel parsers are not portable). Apache-2.0.

---

## Tools at a glance

| Group | Tools |
|---|---|
| Meta | `load_trace` (returns `Capabilities` keyword map), `list_processes`, `process_create_timing` |
| CPU | `cpu_top_functions`, `cpu_top_functions_batch`, `cpu_caller_callee` |
| Wait | `wait_analysis`, `wait_top_stacks`, `wait_caller_callee` |
| Image load | `image_load_timing`, `image_load_top_gaps`, `image_load_top_stacks`, `image_load_caller_callee` |
| File / disk / mmap I/O | `file_io_top_files`, `file_io_top_stacks`, `file_io_caller_callee`, `disk_io_top_stacks`, `disk_io_caller_callee`, `mmap_hot_files`, `mmap_top_stacks`, `mmap_caller_callee` |
| Markers / symbols | `find_marker`, `set_symbol_path`, `add_symbol_server`, `diagnose_symbols`, `diagnose_slow_startup` |

Each "top" view has a matching "caller-callee" drill-down that takes a focus frame.

---

## Requirements

- Windows 10 / 11 (TraceEvent kernel APIs are Windows-only)
- .NET 8 — auto-installed user-scope by `install.ps1` if missing (uses Microsoft's official `dotnet-install.ps1`; no admin needed). Pass `-SkipDotNetInstall` to opt out.
- For symbol resolution: `_NT_SYMBOL_PATH` set, or use the symbol tools at runtime (see [Symbol resolution](#symbol-resolution)).

---

## Install — one-liner (no clone, no build)

**PowerShell:**

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"
```

**Git Bash on Windows:**

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash
```

Both routes do the same thing: download the latest GitHub Release zip (pre-built DLL), cache under `%LOCALAPPDATA%\wpa-mcp\releases\<tag>\`, and run the bundled `setup.ps1`. Auto-detects every MCP client on the machine (Claude Code / Codex / Claude Desktop) and registers `wpa-mcp` against each. .NET 8 runtime is auto-installed user-scope if missing. Subsequent runs are instant (cache hit).

Forward extra flags through the one-liner:

```powershell
# PowerShell — pin tag, force a single client, set custom symbol path
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.1.0 -InstallArgs @('-Client','claude-desktop','-SymbolPath','SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols')"
```

```bash
# Bash — flags after `bash -s --` go to install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.1.0
```

---

## Uninstall — one-liner

Symmetric with install: web-invokable, edits the same client configs in reverse. No download / cache touched.

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

This removes the `wpa-mcp` entry from every detected MCP client. The cached release zip and symbol cache stay (delete `%LOCALAPPDATA%\wpa-mcp\` and `%LocalAppData%\WprMcp\Symbols\` to remove those).

---

## Install — from a clone (developers)

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
.\scripts\setup.ps1
```

```bash
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
./scripts/setup.sh
```

Builds (Release) and registers `wpa-mcp` with every detected MCP client. Idempotent — re-run to update.

Common flags:

```powershell
.\scripts\setup.ps1 -Client claude-desktop                    # force a specific client
.\scripts\setup.ps1 -SymbolPath "SRV*C:\Symbols*https://..." # custom _NT_SYMBOL_PATH
.\scripts\setup.ps1 -SkipBuild                                # use existing DLL
```

Uninstall from clone (also `-CleanBuild` to wipe `bin/` `obj/`):

```powershell
.\scripts\uninstall.ps1
.\scripts\uninstall.ps1 -CleanBuild
```

```bash
./scripts/uninstall.sh
./scripts/uninstall.sh -CleanBuild
```

---

## Install — manual (if the script doesn't fit your setup)

Build:

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp
cd wpa-mcp
dotnet build -c Release
# DLL: src\WprMcp\bin\Release\net8.0\WprMcp.dll
```

Smoke-check:

```powershell
dotnet src\WprMcp\bin\Release\net8.0\WprMcp.dll --version    # prints "WprMcp 0.1.0-poc"
dotnet test                                                   # runs the xUnit suite (needs fixtures, see CONTRIBUTING.md)
```

Then register with your MCP client. The DLL path must be **absolute**.

### Claude Code

Per-project (`<project>/.mcp.json`) or global (`~/.claude.json`):

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

The server speaks stdio MCP; any client that accepts a `command + args` config works. Use the same JSON snippet.

### Verify

After restart, the client exposes the tools as `mcp__wpa-mcp__load_trace`, etc. First call to `load_trace` on a fresh `.etl` takes 30 s – 3 min while the `.etlx` index is built (logged to stderr).

---

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

---

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

For a real-world investigation walkthrough — symptoms, tool chain, evidence, root cause — see `docs/CASE_STUDIES.md`.

---

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

## Troubleshooting

- **`dotnet: command not found`** — install the SDK: `winget install Microsoft.DotNet.SDK.8`, then restart your shell / MCP client.
- **MCP server fails to start** — run the DLL directly: `dotnet C:\path\to\WprMcp.dll --version`. If that fails, the build is broken or the path is wrong.
- **Tool list is missing the new tools** — your MCP client cached an old binary. Fully quit and re-launch (Claude Desktop) or re-run `claude mcp restart` (Claude Code).
- **`Cannot create type. Only core types are supported in this language mode`** — your shell is in PowerShell Constrained Language Mode (AppLocker / WDAC). Use `wpa-mcp ≥ v0.1.1`; older release zips have a `setup.ps1` that calls `[StringBuilder]::new(...)` which CLM blocks.
- **`SymbolStatus.Warning` says `_NT_SYMBOL_PATH is not set`** — the server's process didn't inherit the env var. Use option 2 (per-server `env` block) or call `set_symbol_path` at runtime.
- **`ResolutionRate < 0.5`** with paths set — first downloads in progress, or no network to symbol servers. Re-run after a minute, or run `diagnose_symbols` for module-by-module hints.
- **`mmap_hot_files` returns empty** — the trace lacks the `HardFaults` keyword. Check `load_trace` response: `Capabilities.HasHardFaults` will be `false`. Re-capture with `MmapCapture.wprp`.
- **`file_io_top_files` returns empty** — same as above for `Capabilities.HasFileIo`. Default `CPU.light` profile omits FileIO.
- **First `load_trace` taking forever** — the `.etlx` index is being built. Watch stderr; expect 30 s for a 100 MB `.etl`, several minutes for multi-GB. Subsequent loads of the same file are instant.

## License

Apache License 2.0. See `LICENSE` for the full text. Contributions are accepted under the same license per Apache 2.0 § 5.
