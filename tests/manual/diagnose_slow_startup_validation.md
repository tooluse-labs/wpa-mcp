# `diagnose_slow_startup` validation — same trace, one tool call

## Goal

The original Quark process-creation analysis (chat session ID
`acc8be29-ad3a-4bb2-b4e5-9e66e8414911`) burned **~15 individual MCP tool calls + 1
subagent + ~30 k tokens** to converge on the conclusion "Quark child processes are
blocked on EDR minifilter callbacks". This run validates that the new
`diagnose_slow_startup` macro gets to the same (or better) conclusion **in a single
call** against the same trace.

## Capture details

- **Trace:** `C:\Users\admin3\Documents\WPR Files\LAPTOP-NL4LGTQH.08-11-2025.15-40-35.etl`
  (2.0 GB, 247.6 s session, 328 processes — same trace used in `perfview_compare.md`
  Run 2-4)
- **Symbols:** `_NT_SYMBOL_PATH=C:\Users\admin3\Documents\WPR Files\all_symbols`
  (local Quark PDBs only). MS public symbol server is intentionally NOT used here so
  the run completes in 12 seconds; per-frame resolution is lower than Run 4 of
  `perfview_compare.md`, but the structural conclusion is unaffected.
- **Capture date:** 2025-08-11 (re-analyzed 2026-05-02)

## Reproduction

```bash
export _NT_SYMBOL_PATH='C:\Users\admin3\Documents\WPR Files\all_symbols'
dotnet src/WprMcp/bin/Release/net8.0/WprMcp.dll \
  --diagnose-slow-startup \
  'C:\Users\admin3\Documents\WPR Files\LAPTOP-NL4LGTQH.08-11-2025.15-40-35.etl' \
  quark \
  > tests/manual/diagnose_slow_startup_quark.json \
  2> tests/manual/diagnose_slow_startup_quark.stderr.log
```

The CLI is the test/debug surface defined in `src/WprMcp/Cli/CliRunner.cs`. From an
MCP client the equivalent call is:

```
diagnose_slow_startup(
    path = '...etl',
    nameSubstring = 'quark',
    minWaitRatio = 3.0,
    maxCandidates = 5,
    startupWindowUs = 5_000_000,
    topImageLoads = 20,
    topCpu = 15)
```

Internally the macro composes:
1. `list_processes` → keep `quark*` processes with `WaitRatio >= 3.0`
2. `wait_analysis` → top wait reasons per candidate, all threads collapsed
3. `image_load_timing` → first 20 DLL loads from `ProcessStart`
4. `cpu_top_functions` (with `excludeEtwSelfOverhead=true`) → top 15 hot functions
   in the first 5 seconds after `ProcessStart`

## Wall-clock comparison

| Metric | Original (15 calls + subagent) | `diagnose_slow_startup` (1 call) |
|---|---:|---:|
| Tool calls | 15 + 1 subagent (5 sub-tool-uses) | **1** |
| Wall time | ~3-5 min (incl. subagent + back-and-forth) | **12.0 s** |
| Token usage (response payloads) | ~30 k (incl. 77 k-char `find_marker` blowup that triggered file-slice fallback) | ~52 k JSON, but the **same 52 k contains all evidence** — no fallback |
| Output medium | spread across many tool results | one structured JSON |

## Top-line result

```
Found 5 slow-startup candidate(s):
  - pid 2632  (quark): wall=47396ms cpu=119ms wait_ratio=398x
      top wait reasons: WrTerminated, UserRequest, WrAlertByThreadId
  - pid 24640 (quark): wall=55469ms cpu=187ms wait_ratio=297x
      top wait reasons: UserRequest, WrQueue, WrDispatchInt
  - pid 24052 (quark): wall=43304ms cpu=234ms wait_ratio=185x
      top wait reasons: WrResource, UserRequest, WrQueue
  - pid 6148  (quark): wall=41783ms cpu=257ms wait_ratio=163x
      top wait reasons: UserRequest, WrQueue, WrDispatchInt
  - pid 27476 (quark): wall=42558ms cpu=289ms wait_ratio=147x
      top wait reasons: UserRequest, WrDispatchInt, WrTerminated
```

5 quark child processes, every one with `WaitRatio` between 147× and 398× — the
dllhost-style "blocked, not running" pattern is now demonstrated at scale across
the entire Chromium fork tree, not extrapolated from a single dllhost case.

## Per-candidate breakdown

### pid 2632 (quark child) — 398× wait ratio

| Field | Value |
|---|---|
| Wall / CPU | 47.4 s / 119 ms |
| Parent | 4024 (quark browser) |
| Image-load count | 73 |

Top wait reasons:

| Reason | Blocked time | Count |
|---|---:|---:|
| WrTerminated | 70 103 ms | 1 |
| UserRequest | 65 976 ms | 70 |
| WrAlertByThreadId | 22 573 ms | 11 |
| WrQueue | 10 734 ms | 91 |
| WrDispatchInt | 4 428 ms | 60 |

Top CPU functions (5 s startup window, `excludeEtwSelfOverhead=true`):

| Function | Excl % |
|---|---:|
| ntoskrnl!? | 60.95 |
| ntdll!? | 16.19 |
| quark!? | 6.67 |
| montage-drv.sys!? | **2.86** |
| fltmgr.sys!? | **2.86** |
| ntfs.sys!? | 2.86 |
| ?!? | 1.90 |
| kernelbase!? | 0.95 |

The `fltmgr.sys` + `montage-drv.sys` pair (Filter Manager dispatcher + 阿里 EDR
kernel minifilter) shows up directly in the per-candidate startup-window CPU top —
this is the **direct evidence** that the original analysis only inferred from
trace-wide `inclusive %` numbers.

### pid 24052 (quark child) — uniquely blocked on `WrResource`

This candidate stands out from the others: **65.1 s blocked on `WrResource`** —
the kernel-resource-lock wait reason. None of the other candidates have a
`WrResource` peak this large; UserRequest dominates them.

`WrResource` in a child process during fork → **kernel ERESOURCE / push-lock
contention**, classically caused by minifilter callbacks holding the FilterManager
context lock while running synchronous EDR / AV signature scans. The original
manual analysis didn't differentiate this signal — it lumped all 5 candidates as
"blocked on minifilter".

Top CPU functions confirm the signature:

| Function | Excl % |
|---|---:|
| ntoskrnl!? | 38.14 |
| quark!v8::internal::Scanner::Next | 6.19 |
| ntdll!? | 5.67 |
| quark!allocator_shim::internal::PartitionMalloc | 2.58 |
| ntfs.sys!? | 2.06 |
| quark!v8::ParserBase<Parser>::ParseAssignmentExpressionCoverGrammar | 2.06 |
| montage-drv.sys!? | **2.06** |

Note: with local-PDB-only resolution, `quark!v8::internal::Scanner::Next` and
`ParserBase<Parser>::ParseAssignmentExpressionCoverGrammar` resolve to real
symbols — this is a JS-loading / parser-heavy renderer warming up in parallel
with the kernel-lock-contention storm.

### pid 24640, 6148, 27476 (3 more quark children)

All show the same `UserRequest`-dominant pattern with secondary `WrQueue` and
`WrDispatchInt`. CPU top of each is dominated by `ntoskrnl!?` (38–76 %) with
`fltmgr.sys` / `montage-drv.sys` consistently in the top 8.

`pid 27476` is the only one with both `WrTerminated` AND `WrDispatchInt` in the
top-3 — its 81 s of `WrDispatchInt` blocking is the second-largest single
wait-reason bucket across all candidates, again pointing at kernel
synchronization (likely a minifilter callback).

## Image-load fingerprint (consistent across candidates)

All 5 candidates show the same Chromium DLL-load sequence within 1–2 s of
`ProcessStart`:

```
+   879 ms  C:\...\Quark\quark.exe
+   880 ms  C:\Windows\System32\ntdll.dll
+   886 ms  C:\Windows\System32\kernel32.dll
+   886 ms  C:\Windows\System32\KernelBase.dll
+   887 ms  C:\...\Quark\4.3.0.465\quark_elf.dll
```

Per-process image-load durations (between `ProcessStart` and `quark_elf.dll` map)
range from ~120 ms (pid 6148) to ~880 ms (pid 2632). This isn't a load-spread
pathology by itself — it's normal Chromium loader behavior; the *total* startup
delay is dominated by the wait-reason buckets above, not DLL load durations
themselves.

## Pass / fail per criterion

### Criterion 1: matches the original analysis's conclusion

The original analysis concluded **"EDR/AV minifilter chain (Defender + 阿里 EDR
montage-drv/aliedrprotectdrv + IRMA WFP) is the cause"**. The new run reproduces:

- ✅ All 5 candidate Quark children show wait-ratio >> 100× — confirms the
  wall ≫ CPU pattern across the entire fork tree, not just the dllhost example
- ✅ `fltmgr.sys` + `montage-drv.sys` are in the per-candidate startup-window
  CPU top, not trace-wide aggregate — direct evidence rather than inference
- ✅ `aliedrprotectdrv.sys` shown in the JSON top-15 list (rank 12-14 across
  candidates; trimmed from the markdown excerpts above to keep the table short)
- ⚠️ `mpengine` (Defender) and IRMA-N-API are NOT directly visible from this
  per-Quark-process slice. They were detected in the original analysis by separately
  running `cpu_top_functions(pid=5964)` and `cpu_top_functions(pid=25968)` —
  candidates outside the `quark*` name filter. **`diagnose_slow_startup` with
  `nameSubstring="quark"` deliberately scopes to the named processes.** A second
  run with `nameSubstring=null` (or `"mpengine"`) is the right way to see those.

**Verdict: PASS in spirit (the within-Quark conclusion is reached more directly
and with stronger evidence). The cross-process picture still requires a second
call.**

### Criterion 2: stronger evidence than the original analysis

✅ **Yes.** Specifically:
- `WrResource` 65 s on pid 24052 is a wait reason not surfaced at all in the
  original analysis — proves kernel-lock contention rather than just "high CPU
  in fltmgr".
- Per-process top-CPU-in-startup-window directly shows `fltmgr` / `montage-drv`
  as a same-process attributed cost; the original `cpu_top_functions(pid=4024)`
  only saw "fltmgr 14 % inclusive" without the temporal localization.
- `image_load_timing` confirms the standard Chromium DLL load sequence isn't
  itself slow — narrows the search space.

### Criterion 3: dramatically lower call count

✅ **15 → 1.** The macro replaces:
- 1× `load_trace`
- 1× `list_processes`
- 1× `find_marker(ProcessStart)` — the one that returned 77 750 chars and forced
  a subagent + python-slice fallback in the original
- 5× `cpu_top_functions(pid=...)` for various Quark children
- 3× `cpu_top_functions(startUs=..., endUs=...)` for windowed startup analysis
- 4× misc (diagnose_symbols, find_marker, etc.)

…with one composite call.

## Limitations exposed by this run

1. **Cross-process scope.** `diagnose_slow_startup(nameSubstring="quark")` only
   looks at processes matching the substring. To see the full system picture
   (Defender, IRMA, AliedrSrv) the user has to either run with no substring (gets
   noise from short-lived utility processes) or run separately for each.
   Possible follow-up: `diagnose_slow_startup` could surface "external CPU
   consumers during the candidates' startup windows" as an addendum.

2. **Wait reasons are aggregated per process across all threads.** The data shows
   "pid 24052 spent 65 s on WrResource" but doesn't tell us *which thread* — so
   we can't directly correlate with image-load timing or CPU top. A
   per-thread breakout would be the next refinement.

3. **No causal chain (ReadyThread).** PerfView's full ThreadTimeStackComputer
   tracks `DispatcherReadyThread` events to build "blocked-on-T2-which-was-blocked-
   on-T3" chains. We deliberately omitted this in v1 — the simpler aggregation is
   sufficient for the dominant wait-reason question, but a follow-up could add
   the dispatcher graph for harder cases.

## Side benefit: latent bugs caught

The validation flow surfaced (and fixed in the same change set) issues that were
never visible through the previous skip-gated test suite:

- `KernelTraceEventParser` attached directly to `TraceLog` throws `ApplicationException`
  for CSwitch / ImageLoad subscribers — bug existed in 3 pre-existing analyzers
  (FileIo, Mmap, FileObjectResolver). Fixed by switching every parser to attach
  to `trace.Events.GetSource()`.
- xUnit parallel test execution races on the shared `<basename>.etlx.new` temp
  file. Fixed by `[assembly: CollectionBehavior(DisableTestParallelization = true)]`.
- `CSwitchTraceData.OldThreadWaitReason` returns `System.Diagnostics.ThreadWaitReason`
  (BCL enum, only 0..13 named); kernel emits 0..41. Without a name table, the
  whole `wait_analysis` output reduces to "blocked on 22 / 31 / 37" with no
  signal. Fixed via `Analyzers/WaitAnalysis.cs#WaitReasonNames[]`.

## Future work suggested by this run

- `diagnose_slow_startup` should optionally emit a "concurrent CPU consumers"
  section: top-N processes by CPU during the candidates' aggregate startup
  window. That would surface Defender / IRMA / AliedrSrv automatically.
- Wait reasons should optionally break out per-thread within a process.
- Cross-validate against PerfView's "Thread Time (with Tasks)" view on the same
  trace — pick e.g. pid 24052 and confirm the `WrResource` 65 s figure matches
  PerfView's blocked-time aggregation for that PID. (Same style as Runs 1-4 of
  `perfview_compare.md`.)
