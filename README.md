<p align="right">
  <strong>English</strong> | <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

# wpa-mcp

A C# MCP server that exposes Windows ETW (`.etl`) trace analyzers — CPU, wait, image-load, file / disk / mmap I/O — over any MCP-compatible client (Claude Code, Claude Desktop, Codex, Cursor). Domain-neutral: works on any Windows trace; commonly used to debug app startup, slow forks, AV-induced stalls, and disk-bound regressions.

> **Status — PoC.** 26 tools live, internal use only until validated. Windows-only (TraceEvent kernel parsers are not portable). Apache-2.0.

> **See it in action:** [a real investigation](docs/CASE_STUDIES.md) — process creation 50× slower than baseline, root-caused via wpa-mcp's tools to multiple EDR stacks colliding on `PsSetCreateProcessNotifyRoutineEx`.  Reproduced independently by two different LLM agents on the same trace.

---

## Quickstart

<!-- Animated demo. Drop the recorded GIF at assets/quickstart-demo.gif and it
     will render here. Recording recipe: assets/quickstart-demo-recording.md -->
<p align="center">
  <img src="assets/quickstart-demo.gif" alt="wpa-mcp Quickstart demo — load a trace, find slow processes, drill into a fork burst" width="800">
</p>

Once installed ([one-liner below](#install)), ask the agent in plain language and it picks the matching tools:

```
> Load this trace: C:\path\to\trace.etl
(load_trace; first call 30 s – 3 min as .etlx index is built; subsequent are
 instant.  Response includes a Capabilities map so you know upfront which
 keywords are present in the trace.)

> Which processes have the highest wait ratio?
(list_processes orderBy=wait_ratio — trace-resident processes auto-filtered out)

> For parent PID <X>, what was each fork's kernel-side gap?
(process_create_timing — one call gives kernel-window distribution across all
 children of one parent)

> Top wait stacks for PID <X> between <t0> and <t1>, with 20-bucket histogram
(wait_top_stacks — shows the Filter Manager / driver chain blocking the thread)

> Drill into "<frame!?>": who calls it?
(wait_caller_callee — caller / callee neighbours of the focus frame)
```

The same pattern works for CPU (`cpu_top_functions` → `cpu_caller_callee`), file / disk / mmap I/O, image loads, etc.  Each "top" view has a matching "caller-callee" drill-down that takes a focus frame.

For an end-to-end walkthrough — symptoms, tool chain, evidence, root cause, recommendations — see [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md).

---

## Install

### One-liner (no clone, no build)

**PowerShell:**

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) }"
```

**Git Bash on Windows:**

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash
```

Both routes do the same thing: download the latest GitHub Release zip (pre-built DLL), cache under `%LOCALAPPDATA%\wpa-mcp\releases\<tag>\`, and run the bundled `setup.ps1`.  Auto-detects every MCP client on the machine (Claude Code / Codex / Claude Desktop) and registers `wpa-mcp` against each.  .NET 8 runtime is auto-installed user-scope if missing.  Subsequent runs are instant (cache hit).

Forward extra flags through the one-liner:

```powershell
# PowerShell — pin tag, force a single client, set custom symbol path
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.1.2 -InstallArgs @('-Client','claude-desktop','-SymbolPath','SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols')"
```

```bash
# Bash — flags after `bash -s --` go to install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.1.2
```

### Uninstall (one-liner, symmetric)

Web-invokable, edits the same client configs in reverse.  No download / cache touched.

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

This removes the `wpa-mcp` entry from every detected MCP client.  The cached release zip and symbol cache stay (delete `%LOCALAPPDATA%\wpa-mcp\` and `%LocalAppData%\WprMcp\Symbols\` to remove those).

### Requirements

- Windows 10 / 11 (TraceEvent kernel APIs are Windows-only)
- .NET 8 — auto-installed user-scope by the installer if missing (uses Microsoft's official `dotnet-install.ps1`; no admin needed).  Pass `-SkipDotNetInstall` to opt out.
- For symbol resolution: `_NT_SYMBOL_PATH` set, or use the symbol tools at runtime (see [Configuration → Symbols](#symbols)).

<details>
<summary><strong>Install from a clone (developers)</strong></summary>

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

Builds (Release) and registers `wpa-mcp` with every detected MCP client.  Idempotent — re-run to update.

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

</details>

<details>
<summary><strong>Install manually (custom JSON / non-standard MCP client)</strong></summary>

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

Then register with your MCP client.  The DLL path must be **absolute**.

**Claude Code** — per-project (`<project>/.mcp.json`) or global (`~/.claude.json`):

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

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`, same shape as above.

**Codex / Cursor / other MCP-compatible clients** — the server speaks stdio MCP; any client that accepts a `command + args` config works.  Use the same JSON snippet.

**Verify** — after restart, the client exposes the tools as `mcp__wpa-mcp__load_trace`, etc.  First call to `load_trace` on a fresh `.etl` takes 30 s – 3 min while the `.etlx` index is built (logged to stderr).

</details>

---

## Tools

26 tools across 9 groups.  All built on the same `Microsoft.Diagnostics.Tracing.TraceEvent` library PerfView uses, so the underlying analysis quality is identical — what changes is the surface (stdio MCP + JSON instead of a Windows GUI) and the addition of composite tools that package multi-step PerfView workflows into one call.

### What wpa-mcp adds vs PerfView

* **Agent-driven, not UI-driven.** PerfView is a Windows GUI you click through; wpa-mcp is a stdio MCP server you talk to in plain language. Same data, no UI fatigue, easy to compose into a CI / regression script.
* **Composite tools.** `diagnose_slow_startup`, `process_create_timing`, `image_load_top_gaps` package multi-step PerfView workflows into one call.
* **Capabilities-aware.** Every tool's "won't return data" state maps to a single keyword bit in `load_trace`'s `Capabilities` map — no more "why is this view empty" detective work in PerfView.
* **Per-trace symbol recommendations.** `load_trace` inspects modules in the trace and recommends which symbol servers to add. PerfView leaves symbol setup to the user.

### Pattern

**Always call `load_trace` first.** It opens the `.etl`, builds (or reuses) the `.etlx` index, and returns a `Capabilities` map — a per-keyword presence check (`HasCpuSamples`, `HasCSwitch`, `HasFileIo`, `HasDiskIo`, `HasImageLoad`, `HasHardFaults`, `HasStackWalks`). Every other tool's behaviour depends on those keywords.

Most groups follow the same three-tool shape: a **summary** (top-N flat rows), a **stacks** view (top-N call stacks weighted by the metric), and a **caller-callee drill-down** (given a focus frame, returns its caller / callee neighbours weighted by the same metric — same shape as PerfView's "Callers" / "Callees" tabs).

### Meta

| Tool | What it does | PerfView equivalent |
|---|---|---|
| **`load_trace`** | Opens / caches a `.etl`. Returns trace metadata, the `Capabilities` keyword presence map, and per-trace symbol-server recommendations.  First call 30 s – 3 min while `.etlx` builds; subsequent are instant. | Open a trace file (no `Capabilities` equivalent) |
| `list_processes` | Lists processes (sortable by `cpu` / `wall` / `wait_ratio`). `WaitRatio = WallUs / CpuUs` surfaces "high wall, low CPU" processes (blocked on minifilter / IPC / etc.). PID 0 (Idle) and PID 4 (System) hidden by default. | Processes view |
| `process_create_timing` | Per-fork timing for a parent PID. `FirstImageLoadOffsetUs` = the kernel-side window between `ProcessStart` and the first DLL load — exactly where AV / EDR process-create callbacks burn time invisibly. Median / p95 / max aggregates across all children. | (no equivalent — composite, see [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md)) |

### CPU stacks

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `cpu_top_functions` | Top-N hot functions by exclusive CPU samples in a window / for a PID.  Optional `excludeEtwSelfOverhead` folds `EtwpLogKernelEvent` etc. into a single `[ETW Overhead]` bucket. | CPU Stacks → ByName |
| `cpu_top_functions_batch` | Same as above for multiple PIDs in a single trace load. Each PID gets an independent CallTree (its inclusive-% column normalises to that PID's samples). | (no equivalent — saves N round-trips) |
| `cpu_caller_callee` | Drill into a focus frame: callers (frames calling INTO it) and callees (frames it calls OUT to), each ranked by inclusive CPU samples. Recursion-safe. | CPU Stacks → Callers / Callees tabs |

### Wait / blocked time (CSwitch-derived)

Requires the `CSwitch` kernel keyword (default WPR `CPU` profiles include it).

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `wait_analysis` | Per-thread blocked time + dominant wait reasons. The canonical answer to "why was this slow?" when CPU is low. Reasons like `WrFilterContext` (blocked in a Filter Manager minifilter callback) directly identify the kernel state. | Thread Time → blocked-time per thread |
| `wait_top_stacks` | Top-N call stacks ranked by blocked μs, built from the resume-point stack walk on each `ThreadCSwitch` event. Answers "*where in the code* is the wait happening" (vs `wait_analysis` which answers "which thread / which reason"). | Thread Time / Wait Time → BlockedTime metric (`ThreadTimeStackComputer`) |
| `wait_caller_callee` | Drill into a focus frame; metric is blocked μs. | Thread Time → Callers / Callees tabs |

### Image / DLL load

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `image_load_timing` | Per-process chronological list of every `ImageLoad` event with offset from `ProcessStart`. Spot late-loading DLLs or per-load minifilter / sig-scan delays between loads. | (no direct equivalent — events list filtered manually) |
| `image_load_top_gaps` | Top-N largest **gaps** between consecutive image loads. Pairs with the chronological view; same data, ranked by gap. Response also carries `FirstLoadOffsetUs` (kernel-side fork tax before any DLL loads). | (no equivalent — custom view) |
| `image_load_top_stacks` | Top-N call stacks ranked by `ImageLoad` event count.  Distinguishes eager loads (`LoadLibraryEx` in a main initialiser) from lazy / cascading loads (`CoCreateInstance`, `AmsiOpenSession`, EDR-injected providers). | Image Load Stacks |
| `image_load_caller_callee` | Drill into a focus frame; metric is image-load count. | Image Load Stacks → Callers / Callees tabs |

### File / disk / mmap I/O

The three layers cover different parts of the I/O stack — diff them to localise where time actually goes.

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `file_io_top_files` | Top-N files by total `read + write` bytes. | File I/O view → ByFile |
| `file_io_top_stacks` | Top-N stacks by file-IO bytes. Captures **all** syscalls including cache-served reads — diff with `disk_io_top_stacks` to find cache hits. Requires the `FileIO` keyword (default `CPU.light` omits it). | File I/O Stacks |
| `file_io_caller_callee` | Drill on a focus frame; metric is file-IO bytes. | File I/O Stacks → Callers / Callees tabs |
| `disk_io_top_stacks` | Top-N stacks by **physical** disk-IO bytes — only events that hit physical media (no cache).  Requires the `DiskIO` keyword. | Disk I/O Stacks |
| `disk_io_caller_callee` | Drill on a focus frame; metric is physical disk bytes. | Disk I/O Stacks → Callers / Callees tabs |
| `mmap_hot_files` | Top-N memory-mapped files by **hard page-in** bytes. Identifies which mmap'd file caused page-in load (e.g., a network-share PDF, an oversized dependency DLL). Requires the `HardFaults` keyword (NOT in default WPR profiles — see [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)). | Memory Hard Fault → ByFile (composite) |
| `mmap_top_stacks` | Top-N stacks by hard-fault page-in bytes. Distinguishes eager loader-driven page-in from lazy / scanner-induced page-in. | Memory Hard Fault Stacks |
| `mmap_caller_callee` | Drill on a focus frame; metric is page-in bytes. | Memory Hard Fault Stacks → Callers / Callees tabs |

### Markers / generic ETW events

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `find_marker` | Search all ETW events whose name or task contains a substring. Default mode `count_by_event` returns a histogram (avoids token blow-up); also `count_by_process` and `rows` (full event detail). Useful for surfacing first-party Defender / EDR provider telemetry — e.g., the `Microsoft-Antimalware-AMFilter` provider's `AMFilter_FileScan` rows directly show what the scanner is doing. | Events view |

### Composite diagnostics

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `diagnose_slow_startup` | Picks slowest-by-wait-ratio processes (or matches `nameSubstring`), then runs `wait_analysis` + `image_load_timing` + `cpu_top_functions` for each in the startup window — one call instead of orchestrating four. | (no equivalent — composite) |

### Symbols

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `set_symbol_path` | Sets `_NT_SYMBOL_PATH` for the running server (replaces or appends). | File → Set Symbol Path… |
| `add_symbol_server` | Appends a symbol server URL with optional local cache (defaults to `%LocalAppData%\WprMcp\Symbols`). | File → Set Symbol Path… (single entry) |
| `diagnose_symbols` | Reports per-module symbol status for a loaded trace and suggests fixes (which servers to add) for unresolved modules. | (no equivalent — programmatic) |

---

## Configuration

### Trace cache

LRU, default capacity 2 traces.  Override with `WPRMCP_CACHE_SIZE=N`.  First load builds `.etlx` (slow); cached calls are instant.  `Capabilities` and `TraceLog` are both cached per `(path, mtime)` — re-loading the same `.etl` is free.

### Capturing your own traces

See [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md) for a recommended `.wprp` that captures CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks.  Quick canonical capture:

```powershell
wpr.exe -start tests\WprMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … reproduce the slow case …
wpr.exe -stop C:\path\to\my_capture.etl
```

### Symbols

> **If `cpu_top_functions` shows `module!?` everywhere and `Stats.ResolutionRate < 0.8`, your symbols are not working.**  This is the single biggest source of "garbage output".

Three setup paths (any one suffices — they all set the same `_NT_SYMBOL_PATH`):

1. **Pre-launch env var** (cleanest, survives restarts):
   ```powershell
   [Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH",
       "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols", "User")
   ```
2. **Per-MCP-server `env` block** in the config JSON (see manual install above).  Easiest to share between teammates.
3. **Runtime via tool calls** — ask the agent: *"set the symbol path to SRV\*C:\Symbols\*https://msdl.microsoft.com/download/symbols, then run `diagnose_symbols` on this trace."*

Symbol cache defaults to `%LocalAppData%\WprMcp\Symbols` (separate from PerfView's `C:\Symbols` to avoid PDB-lock contention).  Per-trace recommendations come back inside `load_trace`'s `SymbolStatus.Recommendations` field, telling you which servers to add for the modules actually present in this trace.

For private vendor symbol servers, Chromium-family browsers, and local-build PDB folders, see [`docs/SYMBOL_RECIPES.md`](docs/SYMBOL_RECIPES.md).  Architecture overview and contribution invariants live in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md).
