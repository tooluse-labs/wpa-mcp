<p align="center">
  <img src="https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

<p align="center">
  <a href="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/tooluse-labs/wpa-mcp/releases"><img alt="Release" src="https://img.shields.io/github/v/release/tooluse-labs/wpa-mcp"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue"></a>
</p>

<p align="center">
  <strong>English</strong> | <a href="README.zh-CN.md">简体中文</a> | <a href="CHANGELOG.md">Changelog</a>
</p>

---

A C# MCP server that exposes Windows ETW (`.etl`) trace analyzers — CPU, scheduler waits, image loads, file / disk / mmap / network I/O, registry, memory resources, and CLR runtime events — over any MCP-compatible client (Claude Code, Claude Desktop, Codex, Cursor). Domain-neutral: works on any Windows trace; common uses include diagnosing app startup, slow process creation, AV / EDR-induced stalls, and disk-bound regressions.

> **Status — PoC.** Broad MCP tool surface available. Windows-only (TraceEvent kernel parsers are not portable). Apache-2.0.

> **See it in action:** [a real investigation](docs/CASE_STUDIES.md) — process creation 50× slower than baseline, traced to multiple EDR stacks colliding on `PsSetCreateProcessNotifyRoutineEx`. Reproduced independently by two LLM agents on the same trace.

---

## Quickstart

<!-- Animated demo. Drop the recorded GIF at assets/quickstart-demo.gif and it
     will render here. Recording recipe: assets/quickstart-demo-recording.md -->
<p align="center">
  <img src="assets/quickstart-demo.gif" alt="wpa-mcp Quickstart demo — load a trace, find slow processes, drill into a process-creation burst" width="800">
</p>

Once installed ([one-liner below](#install)), ask the agent in plain language and it picks the matching tools:

```
> Load this trace: C:\path\to\trace.etl
(load_trace — first call takes 30 s – 3 min while the .etlx index is built;
 subsequent calls reuse the cached index. Returns trace metadata plus a
 Capabilities map of event classes actually observed after materialization.)

> Inspect the trace and tell me what it can answer.
(inspect_trace — observed capabilities, per-domain stack coverage, PDB
 identity metadata/configuration, quality warnings, and applicable next tools;
 use diagnose_symbols to probe verified local readiness)

> Diagnose high wait in PID <X> between <t0> and <t1>.
(diagnose_high_wait — one window-consistent call returning candidates,
 evidence, not-concluded reasons, executed-call provenance, and next tools)

> For parent PID <X>, what was each child's kernel-side gap?
(process_create_timing — one call gives the kernel-window distribution across
 every child of one parent)

> Drill into one of the top wait frames from the evidence: who calls it?
(wait_caller_callee — caller / callee neighbors of the focus frame)
```

The same `summary → stacks → caller/callee` pattern works across stack-oriented domains — CPU (`cpu_top_functions` → `cpu_caller_callee`), file / disk / mmap I/O, image loads, CLR allocation / exception / contention, network, registry. Lifecycle and resource tools that don't fit a stack shape (memory resource snapshots, thread lifetime, process creation) have their own rows in the tables below.

For an end-to-end walkthrough — symptoms, tool chain, evidence, findings or hypotheses, and recommendations — see [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md).

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

Both routes do the same thing: download the latest self-contained `wpa-mcp-win-x64.exe` from GitHub Releases into `%USERPROFILE%\.local\bin\wpa-mcp.exe`, then register that executable directly with every detected MCP client (Claude Code / Codex / Claude Desktop). No local .NET runtime or SDK is required.

Forward extra flags through the one-liner:

```powershell
# PowerShell — pin tag, force a single client, set custom symbol path
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.ps1) } -Tag v0.2.24 -Client claude-desktop -SymbolPath 'SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols'"
```

```bash
# Bash — flags after `bash -s --` go to install.ps1
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/install.sh | bash -s -- -Tag v0.2.24
```

### Uninstall (one-liner, symmetric)

Web-invokable, edits the same client configs in reverse.  No download / cache touched.

```powershell
iex "& { $(irm https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.ps1) }"
```

```bash
curl -fsSL https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/scripts/uninstall.sh | bash
```

This removes the `wpa-mcp` entry from every detected MCP client and deletes `%USERPROFILE%\.local\bin\wpa-mcp.exe`. The symbol cache stays (delete `%LocalAppData%\WpaMcp\Symbols\` to remove it).

### Requirements

- Windows 10 / 11 (TraceEvent kernel APIs are Windows-only)
- No .NET runtime is required for the one-line installer; releases ship a self-contained Windows executable.
- For symbol resolution: pass `-SymbolPath` at install time, set `_NT_SYMBOL_PATH`, or use the symbol tools at runtime (see [Configuration → Symbols](#symbols)).

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
# DLL: src\WpaMcp\bin\Release\net10.0\WpaMcp.dll
```

Smoke-check:

```powershell
dotnet src\WpaMcp\bin\Release\net10.0\WpaMcp.dll --version    # prints "WpaMcp 0.3.0"
dotnet test                                                   # runs the xUnit suite (needs fixtures, see CONTRIBUTING.md)
```

Then register with your MCP client.  The command path must be **absolute**. For release installs, use `%USERPROFILE%\.local\bin\wpa-mcp.exe`; for clone builds, use `dotnet` plus the absolute DLL path.

**Claude Code** — per-project (`<project>/.mcp.json`) or global (`~/.claude.json`):

```json
{
  "mcpServers": {
    "wpa-mcp": {
      "command": "C:/Users/me/.local/bin/wpa-mcp.exe",
      "args": [
        "--symbol-path",
        "SRV*C:\\Symbols*https://msdl.microsoft.com/download/symbols",
        "--cache-size",
        "2"
      ]
    }
  }
}
```

Or via the CLI helper:

```powershell
claude mcp add wpa-mcp --scope user -- C:/Users/me/.local/bin/wpa-mcp.exe --symbol-path "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols" --cache-size 2
```

**Claude Desktop** — `%APPDATA%\Claude\claude_desktop_config.json`, same shape as above.

**Codex / Cursor / other MCP-compatible clients** — the server speaks stdio MCP; any client that accepts a `command + args` config works.  Use the same JSON snippet.

**Verify** — after restart, the client exposes the tools as `mcp__wpa-mcp__load_trace`, etc.  First call to `load_trace` on a fresh `.etl` takes 30 s – 3 min while the `.etlx` index is built (logged to stderr).

</details>

---

## Tools

The MCP surface covers multiple ETW analysis domains and is built on the same `Microsoft.Diagnostics.Tracing.TraceEvent` library PerfView uses. The shared parser does not by itself guarantee view-for-view parity: each analyzer reports its scope, observed capabilities, coverage, and no-data state so callers can judge what the trace supports.

### What wpa-mcp adds vs PerfView

* **Agent-driven, not UI-driven.** PerfView is a Windows GUI you click through; wpa-mcp is a stdio MCP server you talk to in plain language. Same data, no UI fatigue, easy to compose into CI / regression scripts.
* **Composite tools.** `diagnose_window`, `diagnose_high_wait`, `diagnose_slow_startup`, `process_create_timing`, `image_load_top_gaps` fold multi-step PerfView workflows into one call.
* **Capabilities-aware.** `load_trace` reports event classes actually observed after ETLX materialization, while individual responses distinguish `scope_not_found`, `event_class_not_observed`, `no_events_in_scope`, and `stacks_unavailable`. Observing no events does not prove which capture keyword was disabled.
* **Per-trace symbol recommendations.** `load_trace` recommends servers only for matching modules with a complete PDB name/GUID/age lookup identity; incomplete identity gets recapture/merge guidance instead. PerfView leaves symbol setup to the user.

### Design philosophy

wpa-mcp is built to **avoid misleading the model without constraining what the model can infer**.

* **Orientation tools** (`load_trace`, `inspect_trace`) expose capabilities, enabled-signal lists, quality gaps, recommended diagnostic flows, and symbol health up front, so the model picks the next call from real signals instead of inferring from empty results.
* **Diagnostic composites** (`diagnose_window`, `diagnose_high_wait`, `diagnose_slow_startup`) shorten the call path but preserve the evidence chain through `Evidence`, `NotConcluded`, `ExecutedToolCalls`, and `NextTools`. They deliberately do not return a synthesized "root cause" field.
* **Per-domain row and stack tools** stay close to the PerfView shape. Process-targeted tools expose the selected `(Pid, ProcessStartUs)` lifetime (or explicitly label PID aggregation), and stack tools report event-domain coverage instead of inferring stack support from unrelated events.

### 0.3.0 result-contract migration

Clients must interpret the structured contract before interpreting `Rows`:

| Field | Contract |
|---|---|
| `ScopeStatus` / `ScopeMode` | Whether the requested process/thread instance resolved, and whether the result is exact, all-process, or an explicit PID aggregate. A non-`ok` scope is not an empty successful analysis. |
| `CapabilityStatus` | `observed` means the resolved requested scope matched the tool's source evidence; `not_observed` is reserved for established whole-trace absence; filtered uncertainty remains `unknown`. |
| `MatchedEventCount` / `MatchedIntervalCount` | Scoped raw source events/endpoints versus completed projected intervals. Neither is a trace-wide denominator or necessarily the row count after aggregation/top-N. |
| `NoDataReason` / `Warnings` | Distinguish missing or ambiguous scope, `event_class_not_observed`, `no_events_in_scope`, `source_events_unattributed`, `no_completed_intervals_in_scope`, `stacks_unavailable`, and `focus_not_found`. An empty array alone has no stable meaning. |
| `MetricPrecision` / `RowMetricAccounting` / `ExactTotalAccounting` | Stack-row metrics accumulated through TraceEvent call trees can be `float32_per_sample_approximate` even when serialized as `long`; source totals and coverage counters marked `exact_long` remain exact. Do not require approximate rows to sum exactly to exact totals. |

Fields prefixed `Trace*` describe the materialized whole trace; fields prefixed `Scoped*` describe the selected instance/window. Do not combine them as numerator and denominator unless the field description explicitly defines that ratio. Always replay a process row with `pid + processStartUs`. For a thread row preserve `pid + processStartUs + tid + threadStartUs + threadGeneration`; generation remains exact even when capture-boundary inference gives two lifetimes the same start timestamp.

`inspect_trace` returns the same rules in its non-null `AnalysisContract`, and that compact contract is exported through the tool's real MCP `outputSchema`. This gives an MCP client machine-visible guidance without duplicating every large response schema across the complete tool catalog and overrunning the guarded `tools/list` budget.

All tools are advertised as `ReadOnly=false` in the 0.3.0 MCP metadata because calls can change server state or filesystem state: the first `TraceLog.OpenOrConvert` may create/replace an adjacent `.etlx`, cache unload can retire resident state, symbol configuration is process-wide, and `resolveSymbols=true` may download/write PDBs. Caller-supplied trace/cache paths can also be UNC, mapped, or reparse-point targets, so raw-path tools are conservatively `OpenWorld=true` even without symbol lookup; only `set_symbol_path` is `OpenWorld=false`. `Destructive=true` conservatively covers sidecar replacement/refresh, cache retirement, and global symbol-path replacement; incremental `add_symbol_server` is the sole `Destructive=false` tool. All tools are idempotent except `set_symbol_path`. These flags describe operational risk, not mutation of the ETL's logical event stream.

### Usage pattern

**Always call `load_trace` first.** It opens the `.etl`, builds (or reuses) the `.etlx` index, and returns a `Capabilities` map showing which supported event classes were observed in the materialized TraceLog. These flags are evidence about parsed events, not proof of the original capture-keyword configuration. The map covers:

* **CPU sampling and scheduling** — `HasCpuSamples`, `HasCSwitch`, `HasReadyThread`, `HasStackWalks`
* **File / disk / mmap I/O and loader** — `HasFileIo`, `HasDiskIo`, `HasHardFaults`, `HasImageLoad`
* **Memory** — `HasVirtualAlloc`, `HasNtHeap`, `HasMemoryProcessInfo`, `HasHandleEvents`, `HasPoolEvents`
* **Network** — `HasNetIo`, `HasNetConnections`
* **Kernel infrastructure** — `HasRegistry`, `HasInterrupt`, `HasAlpc`, `HasThreadEvents`
* **CLR runtime** — `HasClrGc`, `HasClrJit`, `HasClrAlloc`, `HasClrException`, `HasClrContention`

`HasStackWalks` is a compatibility-wide union only. Before using a stack result, inspect that domain's `StackCoverage`: event coverage (`TotalEventCount`, `StackedEventCount`, `StackCoveragePct`), metric-weighted coverage (`TotalMetric`, `StackedMetric`, `MetricStackCoveragePct`), and `CoverageState` (`no_events`, `no_stacks`, `partial`, or `full`). `StackSemantics` identifies the exact stack source. In particular, the `cswitch` domain uses the switch-out `BlockingStack`; the debug stack probe's ordinary CSwitch `CallStackIndex` is a different stack and can have a different coverage. A synthetic `?!?` row accounts for unstacked events; `ContainsSyntheticUnknown` reports whether the current result contains one, and it is never a real call chain.

The full call flow:

```
.etl trace
    │
    ▼
load_trace  ──►  returns Capabilities map
    │
    │  (optional: inspect_trace if capture profile / path unclear)
    ▼

  Composite  (recommended for known workflows)
  ─────────────────────────────────────────────
  diagnose_window, diagnose_slow_startup, diagnose_high_wait
  returns Evidence + NotConcluded + ExecutedToolCalls + NextTools
                                                          │
                                                          │  via NextTools
                                                          ▼

  Domain drill  (custom investigation or composite follow-up)
  ────────────────────────────────────────────────────────────
  summary  ──►  stacks  ──►  caller_callee
  top-N         top-N         focus-frame
  rows          call chains   drill

  Example: cpu_top_functions  ──►  cpu_top_stacks  ──►  cpu_caller_callee
```

If the capture profile or investigation path is unclear, call `inspect_trace` next. For common workflows, prefer composites such as `diagnose_window`, `diagnose_high_wait`, and `diagnose_slow_startup` before manually stitching individual calls together — their `Evidence`, `NotConcluded`, `ExecutedToolCalls`, and `NextTools` fields show what was run, what could not be concluded, and where to drill down.

Most stack-oriented groups follow the same three-tool shape: a **summary** (top-N flat rows), a **stacks** view (top-N call stacks weighted by the metric), and a **caller-callee drill-down** (given a focus frame, returns its caller / callee neighbors weighted by the same metric — same shape as PerfView's "Callers" / "Callees" tabs).

In the tables below, "PerfView equivalent" is the matching view in PerfView's GUI. Entries tagged **[Composite]** combine multiple PerfView views into one call, **[Manual filter]** expose raw events that PerfView's Events view shows but doesn't pre-aggregate, and **[Programmatic]** replace a GUI dialog with structured JSON. Most other tools are 1:1 mappings of PerfView views.

### Time-window semantics

Tools that accept `startUs` and `endUs` use a half-open interval: an event is included only when `startUs <= timestamp < endUs`. A null boundary means the trace start or trace end respectively.

For PID-targeted tools, pass `processStartUs` from `list_processes` whenever the PID was reused. A PID-only aggregate explicitly reports `ScopeMode=pid_aggregate` and retains the included lifetime keys in `IncludedProcesses`; rows/totals may combine those lifetimes according to the tool-specific accounting contract. Exact-only tools return structured `ScopeStatus/NoDataReason=process_start_required` for clean reuse, with replayable candidates. `ambiguous_process_instance` is reserved for unsafe/conflicting lifetime evidence. Check `ScopeStatus`, `CapabilityStatus`, `MatchedEventCount`, `NoDataReason`, `PidReuseObserved`, and `IncludedProcesses` before interpreting an empty `Rows` array. `CapabilityStatus=observed` means source events matched the resolved requested scope; `not_observed` is reserved for an established unfiltered/global absence; otherwise the value is `unknown`.

For CPU/Wait tools that also accept `tid`, reuse is resolved with `threadStartUs` and the optional `threadGeneration`. Missing or ambiguous thread selectors return structured `scope_not_found` / `ambiguous_thread_instance` results rather than falling back to PID-only data. `IncludedThreads` contains `ThreadStartUs`, `ThreadEndUs`, and `Thread.Generation`; replay a candidate with `pid + processStartUs + tid + threadStartUs + threadGeneration`. Generation disambiguates rare capture-boundary lifetimes whose inferred start timestamps are equal.

Tools without `startUs` / `endUs` operate on intentionally different scopes; each tool's MCP description states which:

* **Whole-trace orientation / configuration** — `load_trace`, `inspect_trace`, `list_processes`, `find_marker`, `diagnose_symbols`, `set_symbol_path`, `add_symbol_server`.
* **Lifecycle views** — `process_create_timing`, `thread_lifetime`, `image_load_timing`, `image_load_top_gaps`, and `diagnose_slow_startup` use process-start or lifecycle-relative windows instead of an arbitrary trace window.
* **Whole-trace or windowed by-file summaries** — `file_io_top_files` and `hard_fault_by_file` aggregate over file names and support explicit `startUs` / `endUs` windows. Use the corresponding stack tools for event-associated call-chain evidence.

### Meta

| Tool | What it does | PerfView equivalent |
|---|---|---|
| **`load_trace`** | Opens / caches a `.etl`. Returns trace metadata, observed-event capabilities, and per-trace symbol-server recommendations. `EventCount` is the ETLX-materialized logical-event count; raw ETW record count and a parser-coverage ratio are reported as not measured rather than inferred. First call may take 30 s–3 min while `.etlx` builds; subsequent calls reuse it. | Open a trace file (no `Capabilities` equivalent) |
| **`unload_trace`** | Retires the in-memory entry without interrupting active leases. For a raw `.etl`, it registers a sidecar-refresh request in the current server process; the next successful load attempts the refresh. The request does not survive restart, so call it after an in-place rewrite and call it again after any restart before loading that path. | Close and reopen after invalidating the derived index |
| **`inspect_trace`** | One-shot orientation: observed capabilities, system metadata, provider counts, per-domain stack coverage, PDB identity metadata/configuration, quality warnings, and supported next-tool hints. It does not probe local PDB candidates or readiness; run `diagnose_symbols` for that, then a stack tool for observed frame resolution. | **[Programmatic]** — replaces manual trace-quality inspection across Events, Modules, and capture metadata |
| `list_processes` | Lists process lifetimes (sortable by `cpu` / `wall` / `wait_ratio`). `WaitRatio = WallUs / CpuUs` ranks "high wall, low CPU" candidates; the ratio does not identify what they waited on. PID 0 (Idle) and PID 4 (System) are hidden by default. | Processes view |
| `process_create_timing` | Per-child timing for a parent process lifetime. `FirstImageLoadOffsetUs` is the observed interval between `ProcessStart` and the first DLL load. It can include callbacks, scanning, suspension, scheduling, and other work; the interval alone does not identify a mechanism or root cause. | **[Composite]** — Processes + Events + Excel; see [`docs/CASE_STUDIES.md`](docs/CASE_STUDIES.md) |
| `thread_lifetime` | Per-PID chronological thread lifecycle: every `ThreadStart` / `ThreadStop` with `StartTimeUs`, `EndTimeUs`, `LifetimeUs`, and `PeakConcurrentThreads`. Catches thread-pool thrash and fork-bomb patterns. `TraceResidentStart/End` flags threads bounded by trace capture rather than real spawn / exit. | **[Manual filter]** — Events view, filter on `Thread/Start` + `Thread/Stop`, pair by hand |

### CPU stacks

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `cpu_top_functions` | Top-N hot functions by exclusive CPU samples in a window / for a PID.  Optional `excludeEtwSelfOverhead` folds `EtwpLogKernelEvent` etc. into a single `[ETW Overhead]` bucket. Filtered calls omit `*PctOfTrace` by default to avoid an extra whole-trace CPU sample-count pass; set `includeTracePct=true` when those columns matter. | CPU Stacks → ByName |
| `cpu_precise_analysis` | CSwitch + ReadyThread scheduler summary: exact on-CPU microseconds, ready-to-run latency, per-core runtime attribution, and quantum/preemption counters by thread. Use when sampled CPU cannot answer "how long did it actually run?" or "how long was it ready before dispatch?" | CPU Usage (Precise) |
| `cpu_top_functions_batch` | Same as above for multiple PIDs in a single trace load. Each PID gets an independent CallTree (its inclusive-% column normalizes to that PID's samples). | **[Composite]** — batch variant, saves N round-trips through CPU Stacks → ByName |
| `cpu_caller_callee` | Drill into a focus frame: callers (frames calling INTO it) and callees (frames it calls OUT to), each ranked by inclusive CPU samples. Recursion-safe. | CPU Stacks → Callers / Callees tabs |

### Wait / blocked time (CSwitch-derived)

Requires the `CSwitch` kernel keyword (default WPR `CPU` profiles include it).

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `wait_analysis` | Per-thread blocked time + observed wait reasons. Reasons such as `WrFilterContext` identify the scheduler wait state; they do not by themselves identify the responsible component or root cause. The response separates trace-wide and scoped CSwitch counts and scoped stack coverage. | Thread Time → blocked-time per thread |
| `wait_top_stacks` | Top-N call stacks ranked by blocked μs, built from the blocking stack attached to selected switch-out `ThreadCSwitch` intervals. This is code-path evidence associated with blocked time; it does not identify the responsible external component or root cause. | Thread Time / Wait Time → BlockedTime metric (`ThreadTimeStackComputer`) |
| `wait_caller_callee` | Drill into a focus frame; metric is blocked μs. | Thread Time → Callers / Callees tabs |

### Image / DLL load

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `image_load_timing` | Per-process-lifetime chronological list of every `ImageLoad` event with offset from `ProcessStart`. It reveals late loads and long intervals, but an interval alone cannot attribute the delay to a minifilter, signature scan, or another mechanism. | **[Manual filter]** — Events view, filter on `ImageLoad`, compute offsets by hand |
| `image_load_top_gaps` | Top-N largest **gaps** between consecutive image loads. Pairs with the chronological view; same data, ranked by gap. Response also carries `FirstLoadOffsetUs` (kernel-side fork tax before any DLL loads). | **[Manual filter]** — same `ImageLoad` filter as above, sort by inter-event delta |
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
| `hard_fault_by_file` | Top-N files by **hard page-in** bytes, optionally scoped by `startUs` / `endUs`. Most hard faults are mmap'd files being touched for the first time (DLLs, data files, network-share content); some also come from paged-out heap/stack pages and the page file. Rows include `MaxLatencyTimeUs`, so follow-up analysis can zoom into the exact worst page-in stall. Requires the `HardFaults` keyword (NOT in default WPR profiles — see [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md)). | Memory Hard Fault → ByFile |
| `hard_fault_top_stacks` | Top-N event-attached stacks by hard-fault page-in bytes. These stacks support hypotheses about eager/lazy access or concurrent scanning; they do not by themselves establish the higher-level cause. | Memory Hard Fault Stacks |
| `hard_fault_caller_callee` | Drill on a focus frame; metric is page-in bytes. | Memory Hard Fault Stacks → Callers / Callees tabs |

### Virtual memory

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `memory_resource_analysis` | Process memory resource snapshots from `Memory/ProcessMemInfo`: working set, commit, derived private bytes, private working set, virtual size, observed handle create/close deltas, and observed pool allocation/free deltas. Requires `MemoryInfoWS`, `Handle`, and `Pool`; use `MemoryCapture.wprp`. Rows are ordered by resource size/delta, not severity or causality. Pool rows are captured-window deltas, not absolute current counters. | Memory / Handles views |
| `virtual_alloc_top_stacks` | Top-N stacks by observed `VirtualMemAlloc` + `VirtualMemFree` operation bytes. Allocated and freed bytes/counts are reported separately, with total operation traffic and observed net operation bytes. This is not live virtual size, commit, retention, or leak accounting. Requires the `VirtualAlloc` kernel keyword (NOT in default WPR `CPU` profiles). | VirtualAlloc Stacks |
| `virtual_alloc_caller_callee` | Drill on a focus frame; metric is virtual-memory bytes. | VirtualAlloc Stacks → Callers / Callees tabs |
| `heap_alloc_top_stacks` | Top-N stacks by **NT-heap** allocation bytes (`RtlAllocateHeap` / `HeapAlloc` / `malloc` / `new`). This is allocation flow, not retained-memory or leak proof: free events carry no size and are not counted. Distinct from VirtualAlloc; splits `AllocBytes` / `ReallocBytes`. Requires the `Heap` provider enabled **per-process**. | HeapAllocStacks |
| `heap_alloc_caller_callee` | Drill on a focus frame; metric is NT-heap bytes. | HeapAllocStacks → Callers / Callees tabs |

### Network I/O

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `net_top_stacks` | Top-N stacks by network bytes — TCP + UDP, IPv4 + IPv6 send/recv merged. Splits `TcpBytes` / `UdpBytes` in the response.  Pairs well with `wait_analysis` for "high wall, low CPU" cases where the wait is on a network round-trip. `Connect` / `Accept` / `Disconnect` events have no byte metric — use `find_marker` for those. Requires the `NetworkTrace` keyword (NOT in default `CPU` profiles). | TCP/IP Stacks + UDP/IP Stacks (merged) |
| `net_caller_callee` | Drill on a focus frame; metric is network bytes. | TCP/IP Stacks → Callers / Callees tabs |
| `net_connections` | Per-connection lifecycle list — Connect/Accept paired with Disconnect/Reconnect by `connid` to give "connection X opened at T1, closed at T2, lasted T2−T1". It finds unusually long observed lifecycles; the duration is not connection-establishment latency, request/response latency, or RTT, so it cannot by itself attribute a slow RPC to setup. IPv4 + IPv6 are merged with an `IsIPv6` flag. Connections still open at trace end have `TraceResidentEnd=true`. | **[Manual filter]** — Events view, pair `TcpIp/Connect` with `TcpIp/Disconnect` by `connid` by hand |

### Registry

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `registry_top_stacks` | Top-N stacks by registry-operation count (Query / Open / Create / SetValue / EnumerateKey / etc.). Useful for "who's pounding the registry on every hot-path call". Metric is op count (no natural byte cost for registry). Requires the `Registry` keyword (NOT in default `CPU` profiles). | Registry Stacks |
| `registry_caller_callee` | Drill on a focus frame; metric is registry op count. | Registry Stacks → Callers / Callees tabs |

### ReadyThread (association evidence)

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `ready_thread_top_stacks` | Top-N associated **readier/wakeup** stacks, aggregated by optional `awakenedPid` and the requested window. The stack belongs to the readier, not the awakened thread. These events are not paired one-to-one with a specific wait interval or subsequent CSwitch and cannot alone establish root cause. Use with `wait_analysis` as supporting evidence. | ReadyThread Stacks |
| `ready_thread_caller_callee` | Drill into the same associated readier/wakeup evidence around a focus frame; metric is ready-event count and carries the same non-causal limitation. | ReadyThread Stacks → Callers / Callees tabs |

### Interrupts (DPC / ISR)

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `interrupt_top_stacks` | Top-N stacks by observed kernel interrupt time (DPC + ISR microseconds), split into `DpcUs` / `IsrUs`. Interpret concentration and CPU share against comparable workload/hardware baselines; there is no universal healthy threshold here, and a hot routine alone does not establish a driver fault. Requires `Interrupt` + `DPC` keywords (default `CPU` profiles enable both). | DPC/ISR Stacks |
| `interrupt_caller_callee` | Drill on a focus frame; metric is interrupt μs. | DPC/ISR Stacks → Callers / Callees tabs |

### ALPC (cross-process IPC)

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `alpc_top_stacks` | Top-N stacks by ALPC message count (Send + Receive). ALPC is the kernel IPC primitive used by RPC, COM, AppContainer broker calls, lsass, the SCM, and much of the Windows service surface. It identifies call chains associated with message activity; message counts alone do not measure a round-trip or explain latency. Requires the `ALPC` keyword (NOT in default `CPU` profiles). | ALPC Stacks |
| `alpc_caller_callee` | Drill on a focus frame; metric is ALPC message count. | ALPC Stacks → Callers / Callees tabs |

### CLR (.NET runtime)

Requires the `Microsoft-Windows-DotNETRuntime` ETW provider in the capture profile (WPR `.wprp` files need an explicit `<EventCollectorId>` for it).
For minimal JIT-only traces, run `tests/WpaMcp.Tests/fixtures/Capture-JitOnly.ps1` or use `JitOnlyCapture.wprp!ClrJitOnly`; it enables the CLR JIT + Loader bits needed by `clr_jit_analysis` without GC/allocation/exception/contention runtime keywords.

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `clr_gc_analysis` | Per-GC wall and stop-the-world pause intervals, paired over the whole trace before projection into the requested window. `DurationUs` / `PauseUs` and `TotalGcUs` / `TotalPauseUs` are compatibility aliases for accounted clipped overlap; `FullDurationUs` / `FullPauseUs` and `TotalFullGcUs` / `TotalFullPauseUs` preserve complete paired interval values. `GCStart`→`GCStop` brackets the wall interval; `GCSuspendEEStart`→`GCRestartEEStop` brackets the mutator pause. | GCStats |
| `clr_jit_analysis` | Top-N methods by JIT compilation duration. Matches `MethodJittingStarted`→`MethodLoadVerbose` on `(ProcessInstanceKey, ClrInstanceId, MethodId)` over the whole trace, then projects into the requested window. `JitDurationUs` is the accounted clipped-overlap alias and `FullDurationUs` is the complete paired duration; `MethodIlSize` is IL byte size, not generated native-code size. R2R / NGen / pre-jitted methods don't fire `JittingStarted`, so they're invisible. | JIT Stats |
| `clr_alloc_top_stacks` | Top-N stacks by managed-heap allocation bytes, driven by `GCAllocationTick` events (one per ~100 KB allocated per `(heap, generation, type)` — sampled, low-overhead, on every CLR ≥ 4.0). Response includes `TopTypes` (top type names by total bytes). The canonical "who's allocating all the strings on the request hot path" tool. Requires the `GC` keyword. | GC Heap Alloc Stacks |
| `clr_alloc_caller_callee` | Drill on a focus frame; metric is allocation bytes. | GC Heap Alloc Stacks → Callers / Callees tabs |
| `clr_exception_top_stacks` | Top-N stacks by .NET exception throw count (`ExceptionStart` events). Useful for "is this code path throwing 1000 exceptions per second" / "where is `FormatException` being swallowed in a retry loop". Response includes `TopTypes` (top exception type names by count). Requires the `Exception` keyword. | Exceptions Stacks |
| `clr_exception_caller_callee` | Drill on a focus frame; metric is exception count. | Exceptions Stacks → Callers / Callees tabs |
| `clr_contention_top_stacks` | Top-N stacks by managed-monitor blocked μs — `lock` / `Monitor.Enter` waits. Matches `ContentionStart`→`ContentionStop` by `ThreadInstanceKey` (process lifetime + TID generation), then charges only overlap with the requested window; `TotalFullBlockedUs` preserves complete paired time. Only completed pairs contribute blocked-time metrics and stack coverage. Filters to `ContentionFlags.Managed` (native contention is excluded). Requires the `Contention` keyword. | Monitor Contention Stacks |
| `clr_contention_caller_callee` | Drill on a focus frame; metric is blocked μs. | Monitor Contention Stacks → Callers / Callees tabs |
| `clr_gc_heap_stats` | Managed-heap snapshot timeline with heap-generation sizes, pinned-object count, and GC-handle count. Use it to identify trends; an upward trend alone is not proof of a leak or an object-retention path. Pairs with `clr_gc_analysis`. | GCStats per-GC snapshot table |
| `clr_finalizer_analysis` | Top types observed being finalized + finalizer-thread execution batches. Aggregates `GCFinalizeObject` by `TypeName` and pairs `GCFinalizersStart`→`GCFinalizersStop`. Batch duration is not automatically an application pause. This supports investigating whether finalizer work overlaps GC delays; it does not by itself attribute a slow GC or identify allocation sites. | **[Composite]** — GCStats fields + Events view filtering combined into one call |

### Markers / generic ETW events

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `find_marker` | Search all materialized ETW events whose name or task contains a substring. Default mode `count_by_event` returns a histogram; `count_by_process` and `rows` expose more detail. It can surface Defender / EDR provider events such as `AMFilter_FileScan`, but event presence is not a duration or performance-causality claim. Empty results return `no_name_match`. | Events view |
| `security_scan_analysis` | Aggregates known Defender scan schemas plus scan-like vendor/provider events. PID means payload target PID; missing target identity uses an explicitly marked emitter fallback. `EvidenceKind`, `Provenance`, and `Confidence` separate paired high-confidence schemas from low-confidence name heuristics. Presence, vendor classification, and timing overlap do not by themselves prove an AV scan or performance root cause. Used by `diagnose_window`. | **[Composite]** — paired Defender events + heuristic Events-view evidence |
| `generic_event_top_stacks` | Top-N stacks by event count for **any** user-mode ETW provider — AspNetCore, Kestrel, EFCore, Antimalware-AMFilter, Sense (Defender for Endpoint), `Microsoft-Windows-DxgKrnl` (GPU), `Microsoft-Windows-Kernel-Power` (CPU frequency / C-state), or any custom EventSource. Use `find_marker` first to identify which providers are in the trace, then plug the exact `ProviderName` here. Optional `eventNameSubstring` narrows to a specific event class. Stack quality depends on whether stack-walks were enabled for the provider in the `.wprp`. | Any Stacks (single-provider) |
| `generic_event_caller_callee` | Drill on a focus frame; metric is event count. | Any Stacks → Callers / Callees tabs |

### Composite diagnostics

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `diagnose_window` | Windowed evidence composite for one `startUs` / `endUs` interval, optionally scoped to one PID. It returns hard-fault by-file rows sorted by bytes and max latency, file IO top files, memory-pressure summary, security-scan evidence, wait rows, executed-call provenance, not-concluded reasons, and optional zoom-in tools. It has a `maxWindowDurationUs` guard and intentionally returns no root-cause verdict. | **[Composite]** — wraps hard faults, file IO, memory, security scan, and wait views |
| `diagnose_high_wait` | Preview composite for high blocked-time investigations. It keeps candidates separated by process lifetime, adds stack evidence only when scoped CSwitch stack coverage supports it, and treats ReadyThread stacks as associated wakeup evidence rather than proof of who caused a wait. It returns explicit not-concluded reasons and no root-cause field. | **[Composite]** — wraps wait, stack, and ReadyThread views with evidence provenance |
| `diagnose_slow_startup` | Picks slowest-by-wait-ratio processes (or matches `nameSubstring`), then runs `wait_analysis` + `image_load_timing` + `cpu_top_functions` for each startup window. When a candidate's `ProcessStart -> first ImageLoad` gap meets `slowFirstImageLoadThresholdUs`, it also attaches `FirstImageLoadGapEvidence` from `diagnose_window` for that exact pre-user-mode gap. | **[Composite]** — wraps startup wait, loader, CPU, and window evidence |

### Symbols

| Tool | What it does | PerfView equivalent |
|---|---|---|
| `set_symbol_path` | Sets `_NT_SYMBOL_PATH` for the running server (replaces or appends). | File → Set Symbol Path… |
| `add_symbol_server` | Appends a symbol server URL with optional local cache (defaults to `%LocalAppData%\WpaMcp\Symbols`). | File → Set Symbol Path… (single entry) |
| `diagnose_symbols` | Reports module PDB identity/local-candidate state and suggests symbol-path fixes. It does not label a module ready/resolved merely because a PDB file or name exists. Frame counts and resolution rates are null/not measured until a stack lookup observes real code frames. | **[Programmatic]** — replaces Modules tab + Set Symbol Path dialog with structured JSON + auto-recommendations |

---

## Configuration

### Trace cache

LRU, default capacity 2 traces. Override with `WPAMCP_CACHE_SIZE=N`. A query holds a cache lease for its entire use of the trace; eviction, unload, or shutdown retires an entry and disposes it only after the final active lease ends. Failed native/mmap cleanup remains centrally owned and is retried by a later cache shutdown call instead of becoming unreachable after a `using` scope exits. Concurrent first access uses one winning Lazy open rather than converting the same large ETL multiple times.

Cache keys use the canonical full path and, on Windows, case-insensitive comparison, so path-casing aliases share one entry and `unload_trace` invalidates the same entry through either spelling. Freshness uses last-write time, creation time, length, and—when Windows exposes it—volume/file identity. Replacement and most rewrites therefore retire the old entry. Residual boundary: an in-place rewrite that preserves the same file identity, length, and timestamps cannot be detected automatically; call `unload_trace` before querying that path again. For a raw `.etl`, this registers a current-server refresh request; the next successful load attempts to regenerate the adjacent `.etlx`. A restart clears the request, so call `unload_trace` again after restart and before loading the rewritten ETL. Active queries finish on their existing lease.

### Capturing your own traces

See [`docs/WPR_PROFILE.md`](docs/WPR_PROFILE.md) for a recommended `.wprp` that captures CPU + CSwitch + FileIO + DiskIO + HardFaults + Loader stacks.  Quick canonical capture:

```powershell
wpr.exe -start tests\WpaMcp.Tests\fixtures\MmapCapture.wprp -filemode
# … reproduce the slow case …
wpr.exe -stop C:\path\to\my_capture.etl
```

### Symbols

> **Judge symbol quality from the stack tool that actually ran lookup.** Keep four layers separate: trace PDB identity (name + GUID + age), a locally discovered PDB candidate, verified local readiness, and actual observed frame-name resolution. `diagnose_symbols` directly opens discovered candidate paths and sets readiness only for an exact GUID/age match; it does not actively access remote SRV/UNC entries or download symbols. A configured local-looking root can still be redirected by Windows through a mapped drive or reparse point. A lookup-executing stack query is still required to measure frame resolution. A null rate means no eligible code frames were measured, not 0% resolution.

#### Where to set the path

`_NT_SYMBOL_PATH` accepts semicolon-separated entries: `SRV*<cache>*<url>` for symbol servers, bare folder paths for local PDBs, mix and match. Three setup paths:

1. **Pre-launch env var** (cleanest, survives restarts):
   ```powershell
   [Environment]::SetEnvironmentVariable("_NT_SYMBOL_PATH",
       "SRV*C:\Symbols*https://msdl.microsoft.com/download/symbols", "User")
   ```
2. **Per-MCP-server `--symbol-path` arg** in the config JSON/TOML (see manual install above).  Easiest to share between teammates.
3. **Runtime via tool calls** — ask the agent: *"set the symbol path to SRV\*C:\Symbols\*https://msdl.microsoft.com/download/symbols, then run `diagnose_symbols` on this trace."*

When `add_symbol_server` is called without `cacheDir`, its fallback cache is `%LocalAppData%\WpaMcp\Symbols` (separate from PerfView's `C:\Symbols` to avoid PDB-lock contention). In `diagnose_symbols`, `DefaultCacheDir` reports only that fallback; legacy `CacheDir` is its compatibility alias, not proof that the current `ConfiguredSymbolPath` uses it. Per-trace recommendations come back inside `load_trace`'s `SymbolStatus.Recommendations` field, telling you which servers to add for the modules actually present in this trace.

#### Beyond Microsoft modules

The auto-recommendation in `load_trace` only knows the public servers it has patterns for (Microsoft, Chromium). For your own DLLs, third-party SDKs, or internal builds, append entries explicitly — common shapes:

| What you have | Entry to append |
|---|---|
| Internal team symbol server | `SRV*C:\Symbols*https://internal-symsrv.example.com/symbols` |
| Team shared drop on a UNC share | `SRV*C:\Symbols*\\fileserver\symbols` |
| Local dev build output (your own PDBs) | `C:\src\myapp\out\Default` (bare folder, no `SRV*`) |

Order matters — entries are tried left-to-right, first signature match wins. Put the local dev folder **first** when iterating on a build so your fresh PDB beats the public one.

#### Build prerequisites for your own DLLs

A symbol server doesn't help if the build never produced a PDB, or if PDB and deployed DLL are from different builds.

- **.NET / C#**: `<DebugType>portable</DebugType>` + `<DebugSymbols>true</DebugSymbols>`. Check that Release configurations don't disable PDB output.
- **C++ (MSVC)**: `/Zi` + `/DEBUG:FULL`, even in Release. Keep PDB next to DLL.
- PDB and DLL must share the same signature (GUID + age) — re-link → new signature → old PDB no longer resolves.

#### Verifying it worked

```
> load_trace C:\my\trace.etl
> diagnose_symbols C:\my\trace.etl
> cpu_top_functions C:\my\trace.etl
```

`diagnose_symbols` lists PDB identity and local candidate state with configuration hints. A bare path entry is checked only as `<root>\<pdbName>`; only non-UNC filesystem roots declared by `SRV`/`SYMSRV`/`CACHE` are checked as `<root>\<pdbName>\<GUIDAge>\<pdbName>`. Every discovered candidate is validated before the displayed list is capped at 10; `LocalSymbolCandidateCount` and `LocalSymbolCandidatesTruncated` disclose the total, and any exact match is shown first. A recognizable container or symbol-store placement alone is not proof of identity: the tool reports `exact_identity_match`, `identity_mismatch`, `invalid_local_pdb_candidate`, or `candidate_identity_unverified`. Windows DIA failures that cannot distinguish candidate incompatibility from reader failure remain `candidate_identity_unverified`; they are not labeled corrupt. `LocalPdbReady=true` requires an exact GUID/age match from the format-appropriate reader. Run the relevant stack tool with `resolveSymbols=true` to measure actual observed code-frame resolution. Queries use a query-local effective symbol path and do not mutate `_NT_SYMBOL_PATH`; only `set_symbol_path` and `add_symbol_server` intentionally change that process setting. `diagnose_symbols` does not actively access remote SRV/UNC entries or download PDBs, but Windows may redirect a local-looking root through a mapped drive or reparse point; loading the trace may also write an `.etlx` sidecar as described above.

For full recipes (UNC paths, private vendors, Chromium-family browsers, cache management, troubleshooting), see [`docs/SYMBOL_RECIPES.md`](docs/SYMBOL_RECIPES.md) ([中文](docs/SYMBOL_RECIPES.zh-CN.md)). Architecture overview and contribution invariants live in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`CONTRIBUTING.md`](CONTRIBUTING.md).
