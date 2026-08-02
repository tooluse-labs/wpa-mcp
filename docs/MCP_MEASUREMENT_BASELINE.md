# MCP Measurement Baseline

Status: T0.5 implemented on 2026-05-15; discovery/contract measurement policy
amended on 2026-08-02. Projection-specific baselines were regenerated, reviewed,
and verified by the implementation change.

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

Current recorded event shape:

| Event | Fields |
|---|---|
| `tool_call` | `session_id`, `tool_name`, `argument_hash`, `latency_ms`, `response_bytes`, `error`, `cache_hit`, `cache_hits`, `cache_misses`, `timestamp_utc` |
| `tools_list_payload` | `session_id`, `tool_count`, `payload_bytes`, `max_payload_bytes`, `timestamp_utc`; after the split these byte fields measure only the lean aggregate |
| `prompt_invocation` | `session_id`, `method`, `timestamp_utc` |

## Metric Mapping

| Success metric | Source |
|---|---|
| Tool wrong-selection rate | Synthetic scenario run: count calls that violate scenario constraints, ignore known missing capabilities, or fail due to a clearly unrelated tool family. Do not penalize calls solely because they use a different acceptable path. |
| Avg tool calls per investigation | Count `tool_call` events per `session_id` and scenario. |
| Lean `tools/list` discovery size | Startup log and `tools_list_payload.payload_bytes`. This measures names, descriptions, complete input schemas, annotations, and contract URI/version/hash; it excludes full output schemas. |
| Full contract registry size | Reviewed `tool-output-contract-registry.v1.json` aggregate bytes and per-tool schema hashes. This is an on-demand validation cost and must never be reported as the LLM's default discovery-prefix cost. |
| Host-injected descriptor tokens/cache | Named client/version observation, recorded separately from server telemetry. Useful for compatibility guidance, but non-blocking unless a future ADR explicitly guarantees that client/version. |
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
| S05 | Recover from unresolved symbols. | `inspect_trace` -> `prepare_symbols` with approved local roots/store -> repeat the affected stack tool with the returned `symbolContextId`. | Interpreting `module!?` frames as application hot code, or treating PDB metadata as proof that frames resolved. |
| S06 | Identify file-cache and physical-disk activity. | `inspect_trace` -> `file_io_top_files` -> `file_io_top_stacks` -> `disk_io_top_stacks`; or start with `file_io_top_files` / `disk_io_top_stacks` when the capture profile is known. | Treating file IO and disk IO as the same layer. |
| S07 | Investigate hard-fault page-ins. | `inspect_trace` -> `hard_fault_by_file` -> `hard_fault_top_stacks` -> `hard_fault_caller_callee`; or `hard_fault_by_file` directly when hard-fault capture is known. | Using file IO tools when only hard-fault providers are present. |
| S08 | Analyze managed GC pressure. | `inspect_trace` -> `clr_gc_analysis` -> `clr_gc_heap_stats` -> `clr_alloc_top_stacks`; or `clr_gc_analysis` directly when the user asks for GC and the trace is known to include CLR GC events. | Using native heap tools for CLR allocation questions. |
| S09 | Explain network or IPC volume. | `inspect_trace` -> `net_top_stacks` or `net_connections` -> `alpc_top_stacks` when IPC dominates; or start with the known protocol/domain tool when the prompt names it. | Running ALPC tools on traces without ALPC send/receive events. |
| S10 | Investigate a custom provider marker. | `inspect_trace` -> `find_marker` -> `generic_event_top_stacks` -> `generic_event_caller_callee`; or `find_marker` directly when the provider/event name is the user's explicit target. | Guessing provider names instead of discovering or verifying them with marker search. |

## Baseline Guard

Discovery and validation costs are gated separately:

- Aggregate lean `tools/list` JSON must be at most **250,000 bytes**. Every
  descriptor remains complete and every advertised page is included; satisfying
  the limit by hiding tools, weakening input schemas, or dropping contract
  locators is forbidden.
- The reviewed artifact records aggregate discovery bytes, maximum page bytes,
  page count, and catalog hash. Pagination controls a frame peak; it does not
  erase aggregate discovery cost.
- The full Contract 2.0 registry records aggregate bytes and one canonical
  SHA-256 per active tool separately. Registry growth does not consume the lean
  discovery budget, but a changed schema requires explicit snapshot review.
- Every advertised URI/hash must resolve to exactly the same schema through the
  immutable Resource page index and `get_tool_contract(toolName, page)`
  Tools-only path. Ordered fragments must reassemble to the advertised UTF-8
  size and SHA-256. Each response must satisfy the complete-frame policy; a
  missing, mismatched, or externally resolved contract fails closed.
- Both lookup paths share fixed 8,192-UTF-8-byte boundaries. Startup separately
  measures all Resource and mirrored Tool frames with a 128-byte serialized
  request ID; the reviewed current maximum is 15,911 bytes for Resource and
  35,858 bytes for the Contract 2.0 Tool mirror. The latter is the current
  catalog's unified startup floor, not a permanent hard-coded constant.

The historical approximately 2.5 MB inline catalog is retained only as a
before-measurement of full schemas embedded in `tools/list`. It is not the
target discovery budget and must not be presented as default LLM context cost
after the two projections are separated.
