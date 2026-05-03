# Case studies

Real wpa-mcp investigations, sanitized for sharing.  Each case follows the same shape:
**Symptom → Tool chain → Evidence → Root cause → Recommendations → Takeaways**.  The
goal is to show how the tools compose, not to deliver a fix recipe — the same chain
generalises to any startup / fork / I/O regression on Windows.

---

## 1.  Process creation 50× slower than baseline — multiple EDR stacks colliding

> **Trace:** `<trace>.etl`, 248 s wall-clock, 328 processes, captured with CPU + CSwitch + FileIO + ImageLoad keywords (no StackWalk in this profile).
> **Symptom reported by the user:** "Why is creating a process so slow on this machine?"

### Investigation

Started broad, narrowed quickly using the canonical "load → list → drill" flow from the
quickstart.

```text
> load_trace <trace>.etl
   → 248 s, 328 processes; Capabilities reports CPU/CSwitch/FileIO/ImageLoad.

> list_processes orderBy=cpu top=30
   → Two processes stand out as fork-heavy parents:
     • myapp.exe   (PID 4024) — UI app spawning many children
     • SvcHost-A   (PID 5432) — third-party service spawning workers
```

Two parents in scope.  Run `process_create_timing` against each in parallel — one call
gives the kernel-window distribution across all children of one parent, which is what
matters here.  Then `diagnose_slow_startup` to surface the worst-startup candidates
in one shot.

```text
> process_create_timing parentPid=4024 top=100         # myapp.exe
> process_create_timing parentPid=5432 top=100         # SvcHost-A
> diagnose_slow_startup maxCandidates=8 startupWindowUs=3000000 \
    topImageLoads=15 topCpu=10
```

`process_create_timing` is the load-bearing tool here.  Its `FirstImageLoadOffsetUs` is
the kernel-side window between `ProcessStart` and the first `ImageLoad` event for a
child PID — i.e. **time the child spent inside the kernel before any of its own user
code runs**.  Process-create kernel callbacks (AV / EDR scanners, integrity providers,
ETW providers, etc.) all bill against this window.  A healthy fork on Windows is
~10–50 ms; anything north of a few hundred ms means a callback is doing real work
synchronously on the create path.

### Evidence

`process_create_timing parentPid=4024` produced this distribution across the **23
`myapp.exe` children spawned in the 188 s – 245 s range:**

| Statistic | `FirstImageLoadOffsetUs` |
|---|---|
| Median | **879 ms** (vs ~10–50 ms baseline → 17–88× slowdown) |
| p95    | **6.01 s** |
| Max    | **6.21 s** (PID 19656) |

The two worst children sit back-to-back in time:

| Child PID | Started at (s) | First ImageLoad after (s) |
|---|---|---|
| 19928 | 191.21 | **+6.01** |
| 19656 | 190.83 | **+6.21** — first DLL was `myapp.exe` itself, arriving at t=197.04 |

Six seconds with no user code executing.  All in kernel callbacks.

`image_load_timing pid=19656 top=25` confirmed this directly: the very first
`ImageLoad` for PID 19656 was its own `myapp.exe`, dated t=197.04 s — six full seconds
after the process was created at t=190.83 s.

`cpu_top_functions` against the candidate "burner" processes in the same time window
(188 s – 203 s) located the heat:

```text
> cpu_top_functions pid=<MsMpEng> startUs=188000000 endUs=203000000 top=15
   Top inclusive frames:
     mpengine!SigtreeHandlerInstance::siga_cksig_impl       ~32%
     mpengine!SigtreeHandlerInstance::MatchParameter
     mpengine!BMMatchWorker          ← Boyer-Moore signature match
     mpengine!luaV_execute           ← Lua signature engine

> cpu_top_functions pid=<vendor-EDR-svc> startUs=188000000 endUs=203000000 top=15
   Top inclusive frames:
     netio.sys!FilterMatchEx
     afd.sys!memcpy
     netm.sys!...
   → Kernel network filter doing inline scanning per-fork.
```

Each fork window during the burst hit **at least three EDR stacks in series**:

| Stack | Processes in trace | Total CPU during burst |
|---|---|---|
| Microsoft Defender (`MsMpEng` + `NisSrv`) | 2 | ~55 s |
| Vendor X EDR — service variant      | 2 instances | ~68 s |
| Vendor X EDR — server variant       | 6 instances + 1 helper | ~61 s |

### Root cause

Multiple EDR stacks (Microsoft Defender + two services from a single third-party
vendor) all register `PsSetCreateProcessNotifyRoutineEx` callbacks.  Windows
**serialises** every registered create-callback before letting the new process load its
first image.  With three stacks in line:

1. The two outliers (PIDs 19656, 19928, 6 s gaps) hit a **cold-cache scan** in
   `mpengine`: the Lua signature engine and Boyer-Moore matcher show up in CPU samples,
   indicating signature db (re)compilation.  Both happen in the same time window, which
   is why the spike is co-incident — same cold-load, different victims.
2. Subsequent forks fall back to ~800 ms – 1 s — still 16–50× slower than baseline,
   showing that **even the warm-cache path through three serial EDR callbacks costs
   ~1 s per fork**.

Across the 23-fork burst, the fleet pays roughly **21 s of pure kernel wait** that
contributes nothing to the application.

### Recommendations

In priority order (highest leverage first):

1. **Audit the EDR fleet.**  Most enterprise policies intend exactly one EDR per host;
   accidental layering (legacy AV not removed when a new vendor was rolled out, or two
   independent installs from different teams) is the dominant cause here.  Removing
   any one of the three would meaningfully cut the warm-cache path.
2. **Path-level exclusion** for the affected app folder
   (`C:\Users\<user>\AppData\Local\Programs\MyApp\`) on whichever scanner the security
   team allows tuning.  Useful only if the workload is trustworthy and the enterprise
   EDR's behavioural component remains active.
3. **App-side mitigation:** rework the fork pattern.  23 short-lived child processes
   in 57 s × ~1 s of kernel tax each = 21 s of latency the user sees as "the app froze".
   A worker-pool / long-lived helper process eliminates the per-fork tax entirely.
4. **Long-term:** add `process_create_timing` `medianFirstImageLoadOffsetUs` as a hard
   metric in EDR procurement / tuning.  A regression suite that captures a 10-fork
   burst before/after EDR config changes, and asserts the median stays below e.g.
   200 ms, catches "we shipped an EDR that 5×s every CreateProcess call" before it
   reaches production fleets.

### Takeaways for the tools

- `process_create_timing.FirstImageLoadOffsetUs` is the load-bearing measurement.
  It cleanly separates **kernel-side fork tax** from **user-side startup cost** — two
  fundamentally different problems that look identical in wall-clock time.
- The investigation took **eight tool calls** end-to-end (`load_trace`,
  `list_processes`, two `process_create_timing` in parallel, `diagnose_slow_startup`,
  two `wait_analysis`, `image_load_timing`, two `cpu_top_functions` in parallel).  No
  trace exported, no PerfView UI, no symbol re-resolution loop.
- The evidence chain closes itself: `process_create_timing` flags the gap →
  `image_load_timing` proves the gap is empty of user code → `cpu_top_functions` shows
  the kernel doing scanner work in the exact same window → CPU heat names the scanner
  by function.  Each tool answers one question and hands off cleanly to the next.
- **Beyond incident response:** the same chain works as a regression check.  Capture a
  fork-burst trace once a week, run `process_create_timing` on the same parent, alert
  if the median doubles.

---

*Have a sanitised investigation worth recording?  Open a PR adding it here.  The
template above (Symptom / Investigation / Evidence / Root cause / Recommendations /
Takeaways) keeps cases comparable.*
