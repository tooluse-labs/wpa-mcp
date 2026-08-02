# wpa-mcp Production Remediation Program Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Execute the approved eleven-workstream remediation program without allowing correctness, runtime-safety, MCP-contract, or release changes to drift apart.

**Architecture:** This document is the orchestration index. Each child plan produces independently testable software, but shared files are serialized through the ownership rules below. Child 11 has an early platform/protocol decision gate (`11A`) and a final immutable-release gate (`11B`).

**Tech Stack:** C#; the TFM and MCP SDK selected by Child 11A; TraceEvent; Model Context Protocol over stdio; xUnit; PowerShell; GitHub Actions on Windows.

## Global Constraints

- Approved specification: `docs/superpowers/specs/2026-07-29-wpa-mcp-production-remediation-design.md` at commit `7ef8ff5` (base design `a6014ee` plus the approved thread-scoped CPU/wait amendment), extended by `docs/decisions/0002-capability-map-evidence-contract.md` and `docs/MCP_CAPABILITY_MAP_AND_CONTRACT_REFACTORING.zh-CN.md`. No decision commit is recorded until these accepted-amendment documents are committed.
- Execute implementation in an isolated worktree created with `superpowers:using-git-worktrees`.
- Every production change starts with a failing focused test, then the minimum implementation, the focused test, the affected suite, and one independently reviewable commit.
- Time windows are half-open `[StartUs,EndUs)`; timestamps are floored to integer microseconds before arithmetic.
- `ProcessInstanceKey(Pid,StartUs)` and `ThreadInstanceKey(Process,Tid,Generation)` are the only internal stable process/thread identities.
- V2 contract version is the literal `"2.0"`; status and error codes serialize as lowercase strings.
- A valid `tools/call` execution failure is an MCP tool result with `IsError=true`, not a clean empty success.
- Secure-default trace roots are non-empty; remote symbols are disabled until startup policy explicitly allows them.
- Public request and response caps are configurable downward; 100,000 UTF-8 bytes is their default and hard ceiling. Response accounting includes the complete JSON-RPC frame, request ID, text content, structured content, and newline, and startup must reject a configured response cap below the active catalog's measured minimum viable indivisible response.
- Do not weaken a hard ceiling, security policy, golden tolerance, or benchmark threshold in the same commit that needs the weaker rule to pass.
- Preserve unrelated user changes; stop if a planned file has overlapping uncommitted edits.

---

## Child plan map

| Child | Plan | Exit artifact |
|---:|---|---|
| 1 | `2026-07-29-input-time-instance-foundations.md` | Shared validators, `TimeWindow`, canonical time conversion, and one immutable process/thread identity index per trace |
| 2 | `2026-07-29-wait-analysis-consistency.md` | One thread selector and blocked-interval stream across CPU/wait summaries, old-thread blocking stacks, caller/callee, and `when`; legacy PID-only aggregation remains compatible |
| 3 | `2026-07-29-gc-duration-accounting.md` | Correct GC/pause association and clipped duration accounting across duration analyzers |
| 4 | `2026-07-29-slow-startup-window.md` | Startup-scoped candidates and evidence tied to an observed process start |
| 5 | `2026-07-29-mcp-contract-privacy-budgets.md` | V2 envelopes, errors, per-section completeness, schemas, capability quality, privacy, wire budgets |
| 6 | `2026-07-29-trace-symbol-access-policy.md` | Validated source snapshots, artifact store, trace IDs, and immutable symbol policy |
| 7 | `2026-07-29-trace-lifecycle-leases.md` | Registry state machine, leases, unload/eviction, quotas, and shutdown drain |
| 8 | `2026-07-29-cancellation-budgets-worker-isolation.md` | Operation context, cooperative cancellation, progress, budgets, and restricted worker backend |
| 9 | `2026-07-29-mcp-e2e-hostile-inputs.md` | Real stdio protocol, concurrency, cancellation, hostile-input, and packaged-exe tests |
| 10 | `2026-07-29-parity-agent-benchmarks.md` | Golden manifest, cross-tool invariants, S01-S10 runner, and controlled agent metrics |
| 11 | `2026-07-29-platform-release-governance.md` | TFM/MCP ADR, locked dependencies, reusable quality workflow, immutable release artifact, docs |

### Accepted amendment ownership map

The new phases map onto the existing child owners; they do not create a second execution graph.

| Amendment phase | Existing owner(s) |
|---:|---|
| Phase 0 inventory/snapshots | Child 5 + Child 9 + Child 10 |
| Phase 1 analyzer truth repair | Children 1–5 + Child 10 |
| Phase 2 active catalog/error skeleton | Child 5 + Child 9 |
| Phase 3 trace/symbol lifecycle | Child 6 + Child 7 + Child 8 + Child 9 |
| Phase 4 vNext structured contract/exact fitting | Child 5 + Child 9 |
| Phase 5 capability maps/routing | Child 5 + Child 10 |
| Phase 6 Query Planner/shared scans | Child 8 + Child 10 |
| Phase 7 default/release cleanup | Child 11B |

Open choices in the amendment design §19 stay gated by their follow-up ADR. Existing child plans retain ownership of shared files, security boundaries, budgets, and release gates.

## Execution graph

```text
Child 11 Tasks 1-4 (11A: platform/protocol/dependency gate)
              |
              v
           Child 1
              |
              v
           Child 2
           /     \
          v       v
      Child 3   Child 4
           \     /
            v   v
           Child 5
              |
              v
           Child 6
              |
              v
           Child 7
              |
              v
           Child 8
              |
              v
           Child 9
              |
              v
          Child 10
              |
              v
  Child 11 Tasks 5-9 (11B)
```

Child 3 and Child 4 may be implemented on separate branches after Child 2 exposes the shared scheduler intervals, but their `Records.cs` integrations are merged serially. Child 5 consumes the completed correctness metadata, Child 6 consumes Child 5's active contract/options surface, and Child 8 consumes Child 5's completion/error vocabulary plus Child 7's lease API. Child 9 begins only after Children 2-8 pass their focused gates.

## Shared-file serialization

| Shared file or area | Required order | Rule |
|---|---|---|
| `src/WprMcp/Output/Records.cs` | C1 identity fields -> C2 wait DTOs -> C3 duration DTOs -> C4 startup DTOs -> C5 extraction/envelopes | Rebase before each child; no parallel edits to this file. Child 5 moves new contracts to focused files instead of adding another large block. |
| `src/WprMcp/Program.cs` | C5 serialization/logging -> C6 policy/registry DI -> C7 maintenance/shutdown -> C8 operation/worker DI -> C9 test seams -> C11 hosting/release finalization | Each child must run `ProgramCompositionTests` after rebasing. |
| `src/WprMcp/McpServerOptions.cs` | C5 contract/privacy -> C6 trace/symbol policy -> C7 registry/quota -> C8 budget/worker -> C11 selected TFM/protocol | Preserve the additive top-level shape while appending one nested options record per child; never flatten, reconstruct, rename, or re-own fields introduced by an earlier child. |
| Analyzer walkers | C1 time/identity -> C2/C3/C4 domain work -> C8 cancellation | C8 changes signatures only after correctness behavior is locked by tests. |
| `tests/WprMcp.Tests/AssemblyInfo.cs` | C9 only | Keep parallelization disabled until the new conversion/cache concurrency tests pass; removal is its own commit. |
| `.github/workflows/*.yml` | C11A pinned reusable baseline -> C7 lifecycle additions in `quality.yml` -> C8 worker/cancellation additions in `quality.yml` -> C11B protocol/golden/agent/package/release composition | `ci.yml` remains a trigger-only caller after 11A; C7/C8 edit only `quality.yml`; C9/C10 deliver commands, tests, and policy but do not edit workflows; C11B alone changes final package/release topology and preserves every earlier quality gate. |
| Project/lock files | C11A initial locks -> C5 protocol-test project -> C9 protocol test-host/thread fixture projects -> C10 golden/oracle/benchmark projects -> C11B final lock refresh | Every child adding a project updates the solution; Child 11B regenerates and validates the complete final lock graph. |

## Program checkpoints

### Checkpoint A: semantic foundation

Required commits: Child 11A and Child 1.

Run:

```powershell
dotnet restore WprMcp.sln
dotnet build WprMcp.sln -c Release --no-restore -warnaserror
dotnet test WprMcp.sln -c Release --no-build --filter "FullyQualifiedName~TimeWindow|FullyQualifiedName~Validation|FullyQualifiedName~ProcessInstance|FullyQualifiedName~McpSurfaceConformance"
```

Pass condition: every windowed MCP method is covered by the conformance inventory, time conversion is exact, and ambiguous lifecycle metadata cannot select an arbitrary process.

### Checkpoint B: analyzer correctness

Required commits: Children 2-4 and the analyzer-facing part of Child 5.

Run:

```powershell
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Wait|FullyQualifiedName~Gc|FullyQualifiedName~EventPair|FullyQualifiedName~SecurityScan|FullyQualifiedName~Jit|FullyQualifiedName~Finalizer|FullyQualifiedName~Contention|FullyQualifiedName~DiagnoseSlowStartup"
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~ThreadScopedCpuWait|FullyQualifiedName~DurationAnalyzerInvariant|FullyQualifiedName~SlowStartupInvariant"
```

Pass condition: the requested thread is selected before TopN; PID-only calls aggregate reused process lifetimes with a warning; wait methods attribute duration to the old thread's switch-out/blocking stack rather than the ordinary switch-in stack; missing CSwitch/blocking-stackwalk/symbols remain distinct; same-window totals agree; TopN affects only page metadata; GC pauses are neither orphaned nor double-counted; and post-startup activity cannot affect startup ranking.

### Checkpoint C: bounded runtime

Required commits: Children 5-8.

Run:

```powershell
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~ToolEnvelope|FullyQualifiedName~Privacy|FullyQualifiedName~ResponseBudget|FullyQualifiedName~TraceAccess|FullyQualifiedName~SymbolPolicy|FullyQualifiedName~TraceRegistry|FullyQualifiedName~AnalysisBudget|FullyQualifiedName~Worker"
```

Pass condition: denied input has no filesystem/network side effects, valid tool failures cannot be clean successes, every acquired backend is leased, and cancellation/quota cleanup is bounded.

### Checkpoint D: integrated product evidence

Required commits: Children 9-10.

Run:

```powershell
dotnet test tests/WprMcp.ProtocolTests/WprMcp.ProtocolTests.csproj -c Release --filter "Category!=Package"
dotnet test WprMcp.sln -c Release --filter "FullyQualifiedName~Golden|FullyQualifiedName~CrossToolInvariant"
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- verify --trials benchmarks/agent/baseline/trials --scenarios benchmarks/agent/scenarios.v1.json --policy benchmarks/agent/policy.v1.json
dotnet run --project benchmarks/WprMcp.AgentBenchmarks/WprMcp.AgentBenchmarks.csproj -c Release -- compare --candidate benchmarks/agent/baseline/trials --baseline benchmarks/agent/baseline/manifest.json --policy benchmarks/agent/policy.v1.json
```

Pass condition: real stdio frames and schemas are valid, hostile/concurrent cases terminate cleanly, deterministic evidence is exact, and retained benchmark trials are provenance-complete and self-comparable. The package-category test is intentionally deferred to Child 11B Task 6, which creates and smokes the immutable zip in one stage.

### Checkpoint E: immutable release

Execute the remaining Child 11 tasks. The authoritative commands come from the checked-in reusable workflow and must include locked restore, Release build/test, protocol E2E, golden/invariants, one publish, packaged smoke, native dependency checks, SHA-256, and attestation against the same artifact.

## Completion record

- [ ] Child 11A decision record merged.
- [ ] Children 1-10 each satisfy their plan's final verification section.
- [ ] Child 11B reusable quality workflow passes on the release commit.
- [ ] No confirmed HIGH review finding remains open.
- [ ] The release artifact digest tested by packaged smoke equals the uploaded artifact digest.
- [ ] README/capability documentation describes only the tested headless WPA subset.
