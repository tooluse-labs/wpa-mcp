# Case studies

Real wpa-mcp investigations, sanitized for sharing.  Each case follows the same shape:
**Symptom → Tool chain → Evidence → Finding or hypothesis → Recommendations → Takeaways**.  The
goal is to show how the tools compose, not to deliver a fix recipe — the same chain
generalises to any startup / fork / I/O regression on Windows.

---

## 1.  Slow process creation with concurrent security-product activity

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

Two parents in scope. Run `process_create_timing` against each in parallel — one call
gives the observed ProcessStart-to-first-ImageLoad interval distribution across the
children of one parent. Then `diagnose_slow_startup` surfaces the largest candidates
in one shot.

```text
> process_create_timing parentPid=4024 top=100         # myapp.exe
> process_create_timing parentPid=5432 top=100         # SvcHost-A
> diagnose_slow_startup maxCandidates=8 startupWindowUs=3000000 \
    topImageLoads=15 topCpu=10
```

`process_create_timing` is the load-bearing tool here. Its `FirstImageLoadOffsetUs` is
the observed interval between `ProcessStart` and the first `ImageLoad` event for a
child process lifetime. Process callbacks, scanning, suspension, scheduling, and
other work can all fall in this interval; the measurement alone does not identify
which one consumed the time. Any healthy-host baseline must come from comparable
captures rather than a universal threshold.

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

Six seconds with no `ImageLoad` event observed for those children. That is an
investigation anchor, not proof that all time was spent in kernel callbacks or that
the children were continuously runnable.

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

The same burst window also contained substantial CPU activity in several security
products. These are correlated system-wide observations; CPU samples alone do not
pair a product's work to each child-create interval or prove serial execution:

| Stack | Processes in trace | Total CPU during burst |
|---|---|---|
| Microsoft Defender (`MsMpEng` + `NisSrv`) | 2 | ~55 s |
| Vendor X EDR — service variant      | 2 instances | ~68 s |
| Vendor X EDR — server variant       | 6 instances + 1 helper | ~61 s |

### Working hypothesis (not established by these tools alone)

The observations are consistent with security products contributing synchronous work
during the fork burst, but this trace evidence does not establish the exact callback
registration, ordering, or per-child attribution. Confirming that mechanism requires
product/provider telemetry paired to each target lifetime or an controlled A/B capture.
Under that hypothesis:

1. The two outliers (PIDs 19656, 19928, 6 s gaps) hit a **cold-cache scan** in
   `mpengine`: the Lua signature engine and Boyer-Moore matcher show up in CPU samples,
   which is consistent with signature work. Coincidence in one window does not prove
   database recompilation or identify the affected child.
2. Subsequent forks fall back to ~800 ms – 1 s — still 16–50× slower than baseline,
   which is consistent with a repeatable synchronous cost but does not by itself prove
   a warm-cache path through three serial callbacks.

Across the 23-fork burst, the summed intervals are about **21 s**. Because process
intervals can overlap and contain multiple mechanisms, this is neither additive user
latency nor a measured "pure kernel wait" total.

### Recommendations

In priority order (highest leverage first):

1. **Audit the EDR fleet and validate with A/B captures.** Check whether overlapping
   products are intended, then change one approved variable at a time and compare the
   same fork workload before attributing impact to a product.
2. **Path-level exclusion** for the affected app folder
   (`C:\Users\<user>\AppData\Local\Programs\MyApp\`) on whichever scanner the security
   team allows tuning.  Useful only if the workload is trustworthy and the enterprise
   EDR's behavioural component remains active.
3. **App-side experiment:** evaluate a worker pool or long-lived helper. It can reduce
   repeated process-creation work, but the trace does not prove that all summed gaps
   were serialized user-visible latency or that this change eliminates them entirely.
4. **Long-term:** add `process_create_timing` `medianFirstImageLoadOffsetUs` as a hard
   metric in EDR procurement / tuning.  A regression suite that captures a 10-fork
   burst before/after EDR config changes, and asserts the median stays below e.g.
   200 ms, catches "we shipped an EDR that 5×s every CreateProcess call" before it
   reaches production fleets.

### Takeaways for the tools

- `process_create_timing.FirstImageLoadOffsetUs` is the load-bearing measurement, but
  it is an event-to-event interval, not a decomposition of kernel versus user cost.
- The investigation took **eight tool calls** end-to-end (`load_trace`,
  `list_processes`, two `process_create_timing` in parallel, `diagnose_slow_startup`,
  two `wait_analysis`, `image_load_timing`, two `cpu_top_functions` in parallel).  No
  trace exported, no PerfView UI, no symbol re-resolution loop.
- The evidence chain narrows a hypothesis: `process_create_timing` flags an interval →
  `image_load_timing` confirms the event boundary → `cpu_top_functions` shows
  concurrent scanner work. Pairing or an A/B capture is still needed for causality.
- **Beyond incident response:** the same chain works as a regression check.  Capture a
  fork-burst trace once a week, run `process_create_timing` on the same parent, alert
  if the median doubles.

### Cross-validation: independent reproduction (OpenAI Codex 5.5)

The same trace was later analysed end-to-end by a different agent (OpenAI Codex 5.5
xhigh) using the same wpa-mcp tools but choosing its own call sequence.  The
investigation converged on the same EDR-contribution hypothesis; it did not independently
prove callback serialization. The reproduction surfaced three
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

**2. AMSI provider DLLs observed in the target process**

Pulling the full image-load list for one of the slowest children (PID 15904) with
`image_load_timing` (not just `image_load_top_gaps`) showed two AMSI provider DLLs
loaded into the Quark child process:

* `C:\ProgramData\Microsoft\Windows Defender\...\MpOAV.dll` — Defender's AMSI
  provider.
* `C:\Program Files (x86)\<Vendor X>\<Service>\...\<vendor>_amsi_provider_64.dll`
  — the third-party EDR's AMSI provider.

This is direct evidence that both provider DLLs were loaded in that process. It is not
evidence that both synchronously participated in every child creation or a measurement
of their contribution to the interval.

**3. Workload correlated with a concrete user action**

The parent process command-line was captured as a stack-trace label by
`file_io_top_stacks`: `--brand-myapp "<path>\<file>.pdf"`. The 23-fork burst occurred
with this PDF-oriented workload, which is consistent with a user opening one file.
The label and timing do not by themselves prove a one-to-one trigger relationship.

**Why this matters for the tools, not just for this case**

* The same tool surface, no UI, no out-of-band exports — two independent agents
  choosing different call orders surfaced the same hypothesis and evidence gaps. The wpa-mcp
  surface is **agent-agnostic** by design (stdio MCP, structured JSON, no implicit
  state) and this run validates that empirically.
* `find_marker` is under-used in EDR investigations.  Any security product that
  ships its own ETW provider — Defender's `Microsoft-Antimalware-AMFilter` is the
  canonical example, but most vendors do this — becomes directly observable, no
  stack-walk needed, no symbol resolution loop.  Worth pulling into the default
  investigation muscle-memory.
* `image_load_timing`'s returned image-load rows (rather than only
  `image_load_top_gaps`'s largest intervals) are independently valuable. They show
  which images were observed loading into the selected process lifetime and surface
  security DLL names and paths. They do not prove that an image remains loaded or
  that its component executed on a particular callback path.

---

*Have a sanitised investigation worth recording?  Open a PR adding it here.  The
template above (Symptom / Investigation / Evidence / Finding or hypothesis / Recommendations /
Takeaways) keeps cases comparable.*
