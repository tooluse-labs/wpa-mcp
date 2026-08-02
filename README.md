<p align="center">
  <img src="https://raw.githubusercontent.com/tooluse-labs/wpa-mcp/main/assets/wpa-mcp-logo.svg" alt="wpa-mcp">
</p>

<p align="center">
  <a href="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml"><img alt="CI" src="https://github.com/tooluse-labs/wpa-mcp/actions/workflows/ci.yml/badge.svg"></a>
  <a href="https://github.com/tooluse-labs/wpa-mcp/releases"><img alt="Release" src="https://img.shields.io/github/v/release/tooluse-labs/wpa-mcp"></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-Apache--2.0-blue"></a>
</p>

# wpa-mcp

Local, evidence-driven Windows ETL analysis for MCP clients.

[中文](README.zh-CN.md) | [Latest release](https://github.com/tooluse-labs/wpa-mcp/releases/latest) | [Client compatibility](docs/CLIENT_COMPATIBILITY.md) | [Contributing](CONTRIBUTING.md)

wpa-mcp lets an AI client investigate an ETL trace without loading the entire trace into model context. The server opens the trace locally, applies explicit process, thread, and time scopes, and returns structured evidence in bounded pages.

## What it does

- Reuses an opened trace through a stable trace reference instead of reopening the ETL for every question.
- Analyzes sampled CPU, precise CPU and scheduler activity when the trace contains the required events.
- Narrows analysis by process, PID, thread, TID, module, stack, and time window.
- Uses `thread_compare_windows` to compare named fast/slow intervals for one exact thread instance without mixing PID/TID reuse.
- Resolves symbols on demand and reports when missing symbols limit attribution.
- Returns capability evidence, warnings, partial failures, and pagination state instead of silently treating missing data as zero.
- Keeps high-cardinality stack output compact and supports snapshot-backed pagination for batch CPU and thread-window analysis.

## Quick start

### 1. Install the complete bundle

Windows x64 users should install the ZIP bundle from the latest stable release. Keep the `bin` and `native` directories together.

```powershell
$archive = Join-Path $env:TEMP 'wpa-mcp-win-x64.zip'
$install = Join-Path $HOME '.local\share\wpa-mcp'
Invoke-WebRequest 'https://github.com/tooluse-labs/wpa-mcp/releases/latest/download/wpa-mcp-win-x64.zip' -OutFile $archive
Expand-Archive -LiteralPath $archive -DestinationPath $install -Force
& "$install\bin\wpa-mcp.exe" --version
```

The release bundle is self-contained and does not require a separately installed .NET runtime or SDK. A standalone `wpa-mcp-win-x64.exe` asset is also published for portable use, but the complete ZIP bundle is the recommended installation for in-place updates.

### 2. Connect an MCP client

Configure the client to launch `bin\wpa-mcp.exe` over stdio. Use an absolute path. JSON-based clients commonly use this shape:

```json
{
  "mcpServers": {
    "wpa": {
      "command": "C:\\Users\\you\\.local\\share\\wpa-mcp\\bin\\wpa-mcp.exe"
    }
  }
}
```

Codex, Claude Code, and Claude Desktop use different configuration locations. Follow the exact recipe in [Client compatibility](docs/CLIENT_COMPATIBILITY.md).

### 3. Ask the first question

```text
Open C:\traces\startup.etl. Summarize trace duration and available capabilities,
then show the top CPU processes. Do not resolve symbols yet.
```

Start broad, select the relevant PID or TID, and then request stacks or symbols. This produces better evidence and smaller responses than asking for every stack in one call.

## Update

A bundle installation can update itself to the latest stable GitHub Release:

```powershell
wpa-mcp.exe update
```

If the executable is not on `PATH`, invoke it by absolute path. The updater accepts only a published, non-draft, non-prerelease release. It verifies GitHub's asset digest, immutable release evidence, the ZIP SHA-256, and the staged executable version before replacing the installed bundle.

Updating does not change MCP client registration. If a client keeps the executable locked, close that client and run the command again. Installations created before the built-in updater must install the latest ZIP bundle once.

## Analysis workflow

1. Open the ETL and inspect duration, processes, and capability evidence.
2. Select one process, thread, or time interval that matches the observed symptom.
3. Compare intervals before requesting a large stack expansion.
4. Resolve symbols only for the selected scope.
5. Follow `hasMore` and continuation metadata until the required evidence is complete.

Useful prompts:

- `Compare TID 4120 during 3-8 seconds and 8-13 seconds. Report sampled CPU, wait duration, top stacks, and any evidence the trace cannot provide.`
- `For PID 9000, find the hottest CPU functions, excluding ETW self-overhead. Resolve symbols only for the top modules.`
- `Explain why this thread is runnable but not running. Separate CPU execution, ready time, and blocked time.`
- `Analyze these PIDs in bounded pages. Continue from the returned snapshot instead of restarting the batch.`

A wait duration does not by itself identify the blocking method. Reliable attribution depends on scheduler events, stack capture, symbols, and a sufficiently narrow time scope.
`thread_compare_windows` therefore reports sampled counts, scheduler running time, ready latency, and blocked duration separately; ready latency and blocked duration are not additive.

## Capture a useful trace

wpa-mcp can only analyze events that were recorded. Sampled CPU needs profile events and stacks; scheduler delay analysis needs context-switch and ready-thread events; method attribution usually needs resolvable symbols.

See [WPR profile guidance](docs/WPR_PROFILE.md) for provider choices and capture tradeoffs. The repository includes the focused `JitOnlyCapture.wprp` profile and the `Capture-JitOnly.ps1` helper under `tests\WpaMcp.Tests\fixtures`.

Do not enable every provider by default. Capture the smallest event set that can answer the performance question, and record markers around the scenario when possible.

## Understand results

- `traceRef` identifies the opened trace used by later calls.
- `scope` records the process, thread, and time boundaries applied to a result.
- `capabilityEvidence` distinguishes available, absent, and unmeasured trace data.
- `warnings` and `failedSections` expose partial analysis instead of hiding it.
- `hasMore` and continuation metadata indicate that more bounded pages are available.

Treat unavailable capability evidence as unknown, not as a measured zero. Preserve the trace reference, scope, and symbol context when comparing results.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| `response_too_large` | Reduce PID count, `top`, stack depth, or time range. Consume continuation pages instead of requesting every high-cardinality stack at once. A single oversized atomic item can still exceed the hard frame limit. |
| Functions remain unresolved | Configure a symbol path and retry only the selected process or modules. See [Symbol recipes](docs/SYMBOL_RECIPES.md). |
| A slow thread shows little CPU | Inspect ready time and blocked time. CPU samples alone cannot explain scheduler delay. |
| A tool reports unavailable data | Read `capabilityEvidence`; recapture with the required providers rather than interpreting absence as zero. |
| Update cannot replace the executable | Close MCP clients and any terminal currently running wpa-mcp, then retry the update. |
| Results are noisy | Narrow the process, thread, and interval before resolving symbols or expanding stacks. |

ETL files remain on the machine running the MCP server, but tool results are returned to the connected client. Symbol resolution may contact the symbol servers configured on that machine.

## Documentation

- [Architecture](docs/ARCHITECTURE.md)
- [Client compatibility](docs/CLIENT_COMPATIBILITY.md)
- [Capability gaps](docs/CAPABILITY_GAPS.md)
- [Symbol recipes](docs/SYMBOL_RECIPES.md)
- [WPR profile guidance](docs/WPR_PROFILE.md)
- [Case studies](docs/CASE_STUDIES.md)
- [Contract migration](docs/CONTRACT_MIGRATION.md)

The README documents the stable user journey. Detailed protocol design, rollout history, measurement baselines, and implementation tasks belong under `docs/`.

## Build from source

Use the SDK selected by `global.json`:

```powershell
git clone https://github.com/tooluse-labs/wpa-mcp.git
cd wpa-mcp
dotnet restore --locked-mode
dotnet build WpaMcp.sln -c Release --no-restore
dotnet test WpaMcp.sln -c Release --no-build
```

Source builds require the configured .NET SDK. Release bundles remain self-contained. See [CONTRIBUTING.md](CONTRIBUTING.md) before changing contracts or reviewed baselines.
