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

### Cross-validation: independent reproduction (OpenAI Codex 5.5)

The same trace was later analysed end-to-end by a different agent (OpenAI Codex 5.5
xhigh) using the same wpa-mcp tools but choosing its own call sequence.  The
conclusion converged on the same root cause — multiple EDR stacks synchronising on
`PsSetCreateProcessNotifyRoutineEx` callbacks — and the reproduction surfaced three
pieces of harder evidence that the original chain above did not collect.

**1. First-party Defender ETW telemetry via `find_marker`**

The independent run used `find_marker nameSubstring=Filter` to surface the
`Microsoft-Antimalware-AMFilter` provider's events directly:

| Marker | Count |
|---|---|
| `AMFilter_CacheHit` | 874 |
| `AMFilter_FileScan` | 253 |
| `AMFilter_FileScanResult` | 259 |
| `AMFilter_ProcessContext` | 30 |
| `AMFilter_TrustedProcess` | 28 |

This is Defender's own minifilter writing to ETW — first-party "I scanned X, status
Y" telemetry, strictly stronger than the inferred-from-CPU-samples evidence in the
original Investigation section.  Pulling the `AMFilter_FileScan{,Result}` rows with
`mode=rows` revealed scans targeting MyApp user-data, LevelDB stores, journal files,
and the EDR vendor's own state DBs — all in the burst window.

**2. Direct AMSI provider injection (proof of "who is on the callback chain")**

Pulling the full image-load list for one of the slowest children (PID 15904) with
`image_load_timing` (not just `image_load_top_gaps`) showed two AMSI provider DLLs
loaded into the Quark child process:

* `C:\ProgramData\Microsoft\Windows Defender\...\MpOAV.dll` — Defender's AMSI
  provider.
* `C:\Program Files (x86)\<Vendor X>\<Service>\...\<vendor>_amsi_provider_64.dll`
  — the third-party EDR's AMSI provider.

This is direct evidence — not inference — that two AMSI scanners synchronously
participate in every child-process create.  Each provider runs its own inline-scan
pass, which compounds the kernel-callback latency.

**3. Trigger anchored to a concrete user action**

The parent process command-line was captured as a stack-trace label by
`file_io_top_stacks`: `--brand-myapp "<path>\<file>.pdf"` — the 23-fork burst was
triggered by the user opening a single PDF file.  This anchors "why so many forks at
once" to a real end-user gesture, not background activity.

**Why this matters for the tools, not just for this case**

* The same tool surface, no UI, no out-of-band exports — two independent agents
  choosing different call orders both converge on the same root cause.  The wpa-mcp
  surface is **agent-agnostic** by design (stdio MCP, structured JSON, no implicit
  state) and this run validates that empirically.
* `find_marker` is under-used in EDR investigations.  Any security product that
  ships its own ETW provider — Defender's `Microsoft-Antimalware-AMFilter` is the
  canonical example, but most vendors do this — becomes directly observable, no
  stack-walk needed, no symbol resolution loop.  Worth pulling into the default
  investigation muscle-memory.
* `image_load_timing`'s complete DLL list (not just `image_load_top_gaps`'s top-N)
  is independently valuable.  It directly answers "who is loaded into this process"
  and surfaces injected security DLLs by name and on-disk path — much more legible
  than a stack-trace inference.

---

*Have a sanitised investigation worth recording?  Open a PR adding it here.  The
template above (Symptom / Investigation / Evidence / Root cause / Recommendations /
Takeaways) keeps cases comparable.*
