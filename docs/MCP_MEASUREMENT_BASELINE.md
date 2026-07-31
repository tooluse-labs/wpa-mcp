# MCP Measurement Baseline

Status: implemented for T0.5 on 2026-05-15.

## Runtime Telemetry

Runtime telemetry is default-off. Enable it explicitly with:

```powershell
$env:WPAMCP_TELEMETRY = "1"
```

By default, enabled telemetry writes JSONL under `%LocalAppData%\WpaMcp\Logs\`. Set `WPAMCP_TELEMETRY_DEST=stderr` to write to stderr instead, or `WPAMCP_TELEMETRY_FILE=<absolute path>` to choose a dedicated file. Stdout remains reserved for MCP JSON-RPC framing.

Telemetry privacy rules:

- Raw arguments, trace paths, payload contents, and private trace metadata are never written.
- Each server process generates a fresh session salt and never persists it.
- Argument fingerprints use `HMACSHA256(session_salt, args_json)` and are only comparable within one server session.

Recorded event shapes:

| Event | Fields |
|---|---|
| `tool_call` | `session_id`, `tool_name`, `argument_hash`, `latency_ms`, `response_bytes`, `error`, `cache_hit`, `cache_hits`, `cache_misses`, `timestamp_utc` |
| `tools_list_payload` | `session_id`, `tool_count`, `payload_bytes`, `max_payload_bytes`, `timestamp_utc` |
| `prompt_invocation` | `session_id`, `method`, `timestamp_utc` |

## Metric Mapping

| Success metric | Source |
|---|---|
| Tool wrong-selection rate | Synthetic scenario run: count calls that violate scenario constraints, ignore known missing capabilities, or fail due to a clearly unrelated tool family. Do not penalize calls solely because they use a different acceptable path. |
| Avg tool calls per investigation | Count `tool_call` events per `session_id` and scenario. |
| `tools/list` payload size | Startup log and `tools_list_payload` telemetry event. CI guard: `ToolListPayload.BaselineGuardPayloadBytes`; hard warning cap: `ToolListPayload.DefaultMaxPayloadBytes`. |
| Prompt invocation rate, human-in-the-loop | `prompt_invocation` events from clients that support prompts. |
| Prompt invocation rate, agent-only | Same event stream; expected to be near zero for tools-only agents. |
| `inspect_trace` adoption | For unknown-profile or unclear-goal scenarios, `inspect_trace` appears within the first three `tool_call` events. Goal-explicit scenarios may legitimately start with the relevant domain tool. |

## Canonical Synthetic Scenarios

Each scenario should be run in two modes:

- Full MCP mode: normal server capabilities.
- Tools-only mode: Resources and Prompts disabled in the harness. An acceptable path must still complete with tools alone.

The sequences below are **acceptable paths**, not a single ground truth. A run can pass with a shorter or different path when it uses available user context, respects trace capabilities, and reaches the same evidence.

Warning severity is global to the trace, not relative to the user's goal. Harnesses should allow agents to treat an `info` warning as blocking when it removes the provider family required by the scenario.

| ID | Goal | Acceptable tool-call sequences | Common confusions to check |
|---|---|---|---|
| S01 | Orient on an unknown ETL before choosing an analyzer. | `inspect_trace` -> one capability-supported domain tool. | Calling a `*_top_stacks` tool before checking captured capabilities. |
| S02 | Find CPU hot code in a CPU-sample trace. | `inspect_trace` -> `list_processes` -> `cpu_top_functions` -> `cpu_caller_callee`; or `cpu_top_functions` directly when the user already identified a CPU-sample trace and whole-trace ranking is acceptable. | Using `wait_analysis` as the first diagnostic when CPU samples dominate, unless the user also reports blocking symptoms. |
| S03 | Explain high wall-clock time with low CPU. | `inspect_trace` -> `list_processes` -> `wait_analysis` -> `wait_top_stacks` -> `ready_thread_top_stacks`; or `wait_analysis` directly when the user already supplied the high-wait symptom. | Treating CPU stacks as sufficient evidence when wait ratio/blocking is the stated problem. |
| S04 | Diagnose slow process startup. | `inspect_trace` -> `diagnose_slow_startup`; or `diagnose_slow_startup` directly when the prompt already asks for startup diagnosis. Drill down with `image_load_top_gaps` or `wait_top_stacks` only if needed. | Starting with whole-trace image-load stacks when a bounded startup composite would answer faster. |
| S05 | Recover from unresolved symbols. | `inspect_trace` -> `diagnose_symbols` -> `add_symbol_server` or `set_symbol_path` -> repeat the affected stack tool; or `diagnose_symbols` directly after seeing unresolved frames. | Interpreting `module!?` frames as application hot code. |
| S06 | Identify file-cache and physical-disk activity. | `inspect_trace` -> `file_io_top_files` -> `file_io_top_stacks` -> `disk_io_top_stacks`; or start with `file_io_top_files` / `disk_io_top_stacks` when the capture profile is known. | Treating file IO and disk IO as the same layer. |
| S07 | Investigate hard-fault page-ins. | `inspect_trace` -> `hard_fault_by_file` -> `hard_fault_top_stacks` -> `hard_fault_caller_callee`; or `hard_fault_by_file` directly when hard-fault capture is known. | Using file IO tools when only hard-fault providers are present. |
| S08 | Analyze managed GC pressure. | `inspect_trace` -> `clr_gc_analysis` -> `clr_gc_heap_stats` -> `clr_alloc_top_stacks`; or `clr_gc_analysis` directly when the user asks for GC and the trace is known to include CLR GC events. | Using native heap tools for CLR allocation questions. |
| S09 | Explain network or IPC volume. | `inspect_trace` -> `net_top_stacks` or `net_connections` -> `alpc_top_stacks` when IPC dominates; or start with the known protocol/domain tool when the prompt names it. | Running ALPC tools on traces without ALPC send/receive events. |
| S10 | Investigate a custom provider marker. | `inspect_trace` -> `find_marker` -> `generic_event_top_stacks` -> `generic_event_caller_callee`; or `find_marker` directly when the provider/event name is the user's explicit target. | Guessing provider names instead of discovering or verifying them with marker search. |

## Baseline Guard

`ToolListPayload_StaysWithinBaselineGuard` fails when generated `tools/list` JSON exceeds `ToolListPayload.BaselineGuardPayloadBytes` (`180000` bytes), while the runtime warning cap remains `ToolListPayload.DefaultMaxPayloadBytes` (`200000` bytes). When a legitimate tool surface change needs more room, update the baseline only with a measured before/after note and the corresponding scenario impact.
