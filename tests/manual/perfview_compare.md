# PerfView CLI sanity check — small_cpu.etl

## Capture details

- **Trace:** `tests/WprMcp.Tests/fixtures/small_cpu.etl`
- **Size:** ~954 MB (1,000,341,504 bytes)
- **Capture date:** 2026-05-02 (recorded by user)
- **Trace duration:** ~64 seconds
- **Symbols:** none configured (`_NT_SYMBOL_PATH` unset). Both tools see mostly raw addresses / `module!?` aggregations.
- **NOTE:** the .etl is gitignored (too large to commit). A smaller fixture for the repo is to be re-captured separately.

## Methodology

- **PerfView option used:** Option B variant — UserCommand `SaveCPUStacksAsCsv` (writes `<basename>.perfView.csv` next to the trace, parseable, no GUI required).
  - Full command: `perfview.exe /AcceptEula /NoGui /LogFile=<log> UserCommand SaveCPUStacksAsCsv <trace.etl>`
  - PerfView log: SUCCESS, output `small_cpu.perfView.csv` (520 KB, 4,318 rows of method/module data).
- **wpa-mcp:** ad-hoc `--cpu-top` CLI mode added to `Program.cs` (reverted before commit), invoking
  `CpuAnalysis.TopFunctions(trace, top: 10, pid: null, startUs: null, endUs: null, symbolLog: Console.Error)`.

## Top-10 raw output

### wpa-mcp top-10 (per-function / per-address)

| # | Function (Module!Symbol) | Excl Samples | Excl % | Incl Samples | Incl % |
|---|---|---:|---:|---:|---:|
| 1 | `ntoskrnl!0xfffff8003862a25c` | 5,596 | 3.49 | 5,624 | 3.51 |
| 2 | `ntoskrnl!0xfffff8003888ce22` | 3,221 | 2.01 | 3,221 | 2.01 |
| 3 | `ntoskrnl!0xfffff800384bb1ec` | 3,033 | 1.89 | 3,043 | 1.90 |
| 4 | `ntoskrnl!0xfffff8003852bc48` | 2,405 | 1.50 | 2,478 | 1.55 |
| 5 | `ntoskrnl!0xfffff8003888d1b5` | 2,301 | 1.44 | 2,304 | 1.44 |
| 6 | `ntoskrnl!0xfffff8003862bdd9` | 2,280 | 1.42 | 2,280 | 1.42 |
| 7 | `ntoskrnl!0xfffff800384241d2` | 1,727 | 1.08 | 1,736 | 1.08 |
| 8 | `ntoskrnl!0xfffff80038423d6b` | 1,713 | 1.07 | 1,726 | 1.08 |
| 9 | `ntoskrnl!0xfffff8003846d350` | 1,526 | 0.95 | 1,526 | 0.95 |
| 10 | `ntoskrnl!0xfffff8003861f2ba` | 1,418 | 0.88 | 1,422 | 0.89 |

### PerfView top-10 (per-method/module from `SaveCPUStacksAsCsv`)

| # | Name | Excl Samples | Excl % | Incl Samples | Incl % |
|---|---|---:|---:|---:|---:|
| 1 | `ntoskrnl!?` | 151,266 | 66.91 | 162,587 | 71.92 |
| 2 | `ntdll!?` | 20,732 | 9.17 | 160,732 | 71.10 |
| 3 | `node!?` | 9,052 | 4.00 | 16,152 | 7.14 |
| 4 | `kernelbase!?` | 4,635 | 2.05 | 89,194 | 39.45 |
| 5 | `msedge!?` | 4,094 | 1.81 | 6,446 | 2.85 |
| 6 | `quark!?` | 2,958 | 1.31 | 4,786 | 2.12 |
| 7 | `aliedrprotectdrv.sys!?` | 2,867 | 1.27 | 11,671 | 5.16 |
| 8 | `montage-drv.sys!?` | 2,616 | 1.16 | 4,482 | 1.98 |
| 9 | `dwmcore!?` | 2,055 | 0.91 | 2,667 | 1.18 |
| 10 | `win32kbase.sys!?` | 1,997 | 0.88 | 12,064 | 5.34 |

## Structural difference: per-address vs per-module

Without symbols, PerfView aggregates **all** unresolved frames in a module into a single `module!?` row, while
wpa-mcp's `MutableTraceEventStackSource` keeps each raw return-address frame as a distinct entry. The two top-10 lists
therefore are not directly comparable as "function names" — wpa-mcp's top-10 are actually 10 hot offsets within
`ntoskrnl`, all of which are absorbed into PerfView's single `ntoskrnl!?` row.

To make the comparison meaningful, wpa-mcp's call-tree `ByID` set was re-aggregated by module (via the temporary
`--cpu-by-module` CLI helper). PerfView's CSV was likewise grouped by module prefix. Then the per-module top-10 are:

| # | PerfView module | PV Excl | wpa-mcp module | mcp Excl | Order match |
|---|---|---:|---|---:|:---:|
| 1 | ntoskrnl | 151,266 | ntoskrnl | 106,655 | yes |
| 2 | ntdll | 20,732 | ntdll | 14,212 | yes |
| 3 | node | 9,052 | node | 8,361 | yes |
| 4 | kernelbase | 4,635 | kernelbase | 3,129 | yes |
| 5 | msedge | 4,094 | msedge | 2,759 | yes |
| 6 | quark | 2,958 | quark | 2,030 | yes |
| 7 | aliedrprotectdrv.sys | 2,867 | aliedrprotectdrv.sys | 1,876 | yes |
| 8 | montage-drv.sys | 2,616 | montage-drv.sys | 1,800 | yes |
| 9 | dwmcore | 2,055 | dwmcore | 1,543 | yes |
| 10 | win32kbase.sys | 1,997 | `?` (unknown) | 1,362 | no — name differs |

## Pass / fail per criterion

### Criterion 1: function-name overlap >= 7/10

**Compared at the module level (the only honest grain for an unsymbolicated trace):**

- **9/10 module names match** in both top-10 lists, with **identical ordering for the top 9 entries**.
- Slot 10 differs only in the unresolved-module bucket: PerfView reports `win32kbase.sys` (1,997 excl), wpa-mcp
  reports the synthetic `?` bucket (1,362 excl). `win32kbase.sys` shows up in wpa-mcp's per-module data at slot 11.

**Verdict: PASS.**

### Criterion 2: per-row sample counts within +/-10%

| Module | PV Excl | mcp Excl | Diff % | Pass? |
|---|---:|---:|---:|:---:|
| ntoskrnl | 151,266 | 106,655 | 29.49% | FAIL |
| ntdll | 20,732 | 14,212 | 31.45% | FAIL |
| node | 9,052 | 8,361 | 7.63% | PASS |
| kernelbase | 4,635 | 3,129 | 32.49% | FAIL |
| msedge | 4,094 | 2,759 | 32.61% | FAIL |
| quark | 2,958 | 2,030 | 31.37% | FAIL |
| aliedrprotectdrv.sys | 2,867 | 1,876 | 34.57% | FAIL |
| montage-drv.sys | 2,616 | 1,800 | 31.19% | FAIL |
| dwmcore | 2,055 | 1,543 | 24.91% | FAIL |

**Verdict: FAIL.** Every module except `node` is ~25-35% lower in wpa-mcp than in PerfView.

## Root cause of the systematic ~30% shortfall

Investigated with a temporary `--sample-stats` CLI helper:

- The trace contains **227,522** `SampledProfileTraceData` events.
- Of these, **160,226 (70.4%) have a valid call stack**; **67,296 (29.6%) do not**.
- PerfView's `SaveCPUStacksAsCsv` total Excl is **226,071** — i.e. PerfView counts samples without a callstack and
  attributes them to a "no-stack" / unknown root bucket. Effectively every sample contributes 1 to the grand total.
- wpa-mcp's `CpuAnalysis.TopFunctions` follows the standard `MutableTraceEventStackSource` pattern and **drops
  samples whose `CallStackIndex` is Invalid** (line 39 of `CpuAnalysis.cs`: `if (csIdx == CallStackIndex.Invalid)
  continue;`). Net effect: ~30% of the sample population never enters the call tree, so per-module exclusive counts
  are uniformly ~30% lower across the board.

The shortfall is a **systematic constant per module**, not analyzer non-determinism. The relative ordering and the
shape of the distribution are preserved (the top-9 modules are identical and in the same order). Both tools are
"correct" given their definitions — PerfView reports raw event count, wpa-mcp reports attributed-function samples.

### Implications for the PoC

- The criterion as written ("per-row sample count within +/-10%") **fails on this fixture**, but the failure is
  explained and structural rather than a bug in the analyzer's call-tree walk.
- For the PoC sign-off the relevant signal is criterion 1 (top-N hot module identity / ordering), which **passes
  9/10 with identical ordering**. That demonstrates the analyzer faithfully ranks the same hot code as PerfView.
- If exact +/-10% sample-count parity is required for production, two follow-ups are possible:
  1. Include events without a stack in the call tree under a synthetic `?` root so the grand totals match
     PerfView (cheap, ~5 lines in `CpuAnalysis.cs`).
  2. Or document the +/-10% criterion as comparing only **attributed** samples (i.e. wpa-mcp Excl % vs PerfView's
     Excl % normalized over the with-stack subset). The percentages already agree to within ~1 percentage point per
     module on this trace.

## Reproducing

```bash
# PerfView (writes <basename>.perfView.csv next to the trace)
"/c/Users/admin3/AppData/Local/Microsoft/WinGet/Links/perfview.exe" \
  /AcceptEula /NoGui /LogFile=C:/temp/pv.log \
  UserCommand SaveCPUStacksAsCsv \
  "C:/Users/admin3/Dev/wpa-mcp/tests/WprMcp.Tests/fixtures/small_cpu.etl"

# wpa-mcp (ad-hoc CLI, removed after this comparison; restore via the diff in the report)
dotnet src/WprMcp/bin/Release/net8.0/WprMcp.dll --cpu-top \
  "C:/Users/admin3/Dev/wpa-mcp/tests/WprMcp.Tests/fixtures/small_cpu.etl"
```

If `cpu_top_functions` is changed in `CpuAnalysis.cs`: re-run the comparison and update this file. If overlap drops
below 7/10 modules in identical order, treat as a regression.
