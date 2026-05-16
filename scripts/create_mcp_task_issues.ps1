<#
.SYNOPSIS
  Create GitHub issues for docs/MCP_IMPLEMENTATION_TASKS.md.

.DESCRIPTION
  This script turns the current implementation task list into one tracking
  issue plus one issue per unfinished task. It skips an issue when an open issue
  with the exact same title already exists.

  Requirements:
    gh auth login -h github.com

.EXAMPLE
  ./scripts/create_mcp_task_issues.ps1 -DryRun

.EXAMPLE
  ./scripts/create_mcp_task_issues.ps1
#>

[CmdletBinding()]
param(
    [string]$Repo = 'tooluse-labs/wpa-mcp',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function New-IssueSpec {
    param(
        [string]$Title,
        [string]$Body
    )

    @{
        Title = $Title
        Body = $Body
    }
}

function Assert-Gh {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw "GitHub CLI 'gh' is required. Install it, then run 'gh auth login -h github.com'."
    }

    if (-not $DryRun) {
        & $env:ComSpec /c "gh auth status -h github.com >NUL 2>NUL"
        if ($LASTEXITCODE -ne 0) {
            throw "GitHub CLI is not authenticated. Run 'gh auth login -h github.com' and retry."
        }
    }
}

function Test-OpenIssueExists {
    param([string]$Title)

    $titles = gh issue list `
        --repo $Repo `
        --state open `
        --limit 200 `
        --json title `
        --jq '.[].title'

    return @($titles) -contains $Title
}

function New-GitHubIssue {
    param($Issue)

    if ($DryRun) {
        Write-Host "[dry-run] would create: $($Issue['Title'])"
        return
    }

    if (Test-OpenIssueExists -Title $Issue['Title']) {
        Write-Host "[skip] open issue already exists: $($Issue['Title'])"
        return
    }

    $bodyFile = (New-TemporaryFile).FullName
    try {
        Set-Content -Path $bodyFile -Value $Issue['Body'] -Encoding UTF8
        gh issue create --repo $Repo --title $Issue['Title'] --body-file $bodyFile
    }
    finally {
        Remove-Item -LiteralPath $bodyFile -Force -ErrorAction SilentlyContinue
    }
}

$issues = @(
    New-IssueSpec `
        -Title 'Tracking: MCP implementation task list v4' `
        -Body @'
## Goal

Track execution of `docs/MCP_IMPLEMENTATION_TASKS.md` v4.

## Scope

Create and complete the task issues for:

- P0 MCP surface foundation
- P1 routing and workflow compression
- P2 low-risk capability gaps
- P3 higher-risk capability gaps

## Notes

- `T0.1` is already complete in the task list and does not need a work issue.
- P0 must preserve dependency order: `T0.2` SDK spike -> `T0.3` `inspect_trace` -> `T0.4` tests -> `T0.5` baseline -> `T0.6` compression.
- Composite tools stay preview routing targets until benchmark evidence promotes them.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md`
'@

    New-IssueSpec `
        -Title 'T0.2: Spike MCP SDK surface for annotations and structured output' `
        -Body @'
## Scope

Try one low-risk Tier-A tool and verify how `ModelContextProtocol 1.2.0` exposes:

- `readOnlyHint`
- `idempotentHint`
- `openWorldHint`
- tool `outputSchema`
- structured result content
- resource links

## Acceptance

- Decide whether an SDK upgrade is required.
- Determine whether annotations and output schema are declared through attributes or programmatic registration.
- Determine whether attributed tools can return `structuredContent` and `resource_link`.
- Do not mass-annotate tools or wire `inspect_trace` responses until this spike is resolved.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t02-run-an-mcp-sdk-surface-spike`
'@

    New-IssueSpec `
        -Title 'T0.3: Implement inspect_trace' `
        -Body @'
## Scope

Add `inspect_trace(path)` to `MetaTools` and define response records.

## Dependency

Requires T0.2 SDK surface spike.

## Return fields

- Trace basics: duration, event count, lost events, process count
- Capability flags: CPU, CSwitch, FileIO, DiskIO, CLR, ALPC, Network, related signals
- Symbol quality: symbol path, resolution rate, unresolved-module hints
- Capture quality warnings: missing keywords, missing stackwalks, lost events
- Orientation tools and capability-supported tools: `tool_name` + `reason` records without a single global rank

## Acceptance

- One call tells an LLM what the trace can and cannot answer, and which tools to use next.
- Orientation and capability-supported tool hints are machine-readable and stable enough for routing/composites.
- Existing `tools/list` behavior is unchanged.
- `inspect_trace` returns raw signals; `diagnose_trace_quality` owns opinionated verdicts.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t03-implement-inspect_tracepath`
'@

    New-IssueSpec `
        -Title 'T0.4: Test inspect_trace diagnostics and capability projection' `
        -Body @'
## Scope

Add tests for `inspect_trace` in `tests/WprMcp.Tests`.

## Dependency

Requires T0.3 implementation.

## Coverage

- Capability projection
- Lost events warning
- Missing symbol path guidance
- Missing key-provider recapture guidance
- Capability projection agrees with downstream analyzer behavior on fixture traces, including read-only, write-only, and missing-provider cases

## Acceptance

Tests lock the response shape and core diagnostic rules.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t04-add-tests-for-inspect_trace`
'@

    New-IssueSpec `
        -Title 'T0.5: Establish MCP surface measurement baseline' `
        -Body @'
## Scope

Add server-side observability, synthetic evaluation, and CI guardrails.

## Work

- Add structured per-call telemetry: tool name, salted argument hash or session trace id, latency, response byte count, error flag, cache-hit flag.
- Runtime persisted telemetry is opt-in via `WPRMCP_TELEMETRY=1`.
- Use per-session HMAC salt for argument hashes; never persist the salt.
- Write telemetry only to stderr or `%LocalAppData%\WprMcp\Logs\`; stdout is reserved for MCP JSON-RPC framing.
- Record `tools/list` payload size and add a CI guard for approved baseline growth.
- Define 10 canonical synthetic investigation scenarios, including tools-only mode.
- Track the six success metrics from `MCP_SURFACE_DESIGN.md`.

## Acceptance

- Every P0/P1 change can quote a delta against the baseline.
- Privacy review passes: no raw paths, deterministic path hashes, or payload contents.
- Transport review passes: stdout contains only MCP JSON-RPC frames.
- Runtime telemetry is default-off without `WPRMCP_TELEMETRY`.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t05-establish-measurement-baseline`
'@

    New-IssueSpec `
        -Title 'T0.6: Add token-compact stack responses' `
        -Body @'
## Scope

Add compact output options to `*_top_stacks` tools and composites that embed stack rows.

## Work

- Add `compactStacks=true` to cap stack-summary rows at the documented compact limit.
- Add `summaryOnly=true` to return a lossy smaller leaf / metric summary with the same row cap.
- Preserve existing detailed output as the default unless measurement supports changing the preferred composite path.

## Acceptance

- Compact defaults stay below Claude Code's 10,000-token warning threshold approximation on representative traces and below the 25,000-token default maximum.
- Sizing tests cover representative committed stack fixtures and guard against accidental full stack arrays in row DTOs.
- Truncation is explicit and callers can rerun without compact flags or drill into a focus frame.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t06-add-token-compact-stack-responses`
'@

    New-IssueSpec `
        -Title 'T1.1: Gate and implement list_applicable_tools' `
        -Body @'
## Scope

Implement `list_applicable_tools(path, goal?)` only if T0.5 data shows `inspect_trace` orientation / capability-supported tool hints are insufficient for goal-directed routing.

## Dependency

- T0.3 `inspect_trace`
- T0.5 measurement baseline

## Acceptance

- Input supports optional goal values: `cpu`, `startup`, `memory`, `gc`, `io`, `symbols`, `wait`.
- Returns ranked recommendations, applicability reasons, and non-applicability reasons.
- Does not dynamically mutate `tools/list`.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t11-implement-list_applicable_toolspath-goal`
'@

    New-IssueSpec `
        -Title 'T1.2: Add high-frequency composite tools' `
        -Body @'
## Scope

Add 2-3 composite tools, starting with `diagnose_high_wait(path, focus="general|lock|io|sync")`.

## Priority

1. `diagnose_high_wait(path, focus="general|lock|io|sync")`
2. `diagnose_image_load_blocker`
3. `diagnose_gc_pressure`
4. `diagnose_trace_quality`

## Requirements

- `diagnose_trace_quality` returns structured per-dimension verdicts with `status`, reason, and actionable next step.
- Embedded stack sections default to `summaryOnly=true` or `compactStacks=true`.
- Composites ship as preview routing targets until T0.5 benchmark data promotes them over Layer-1 building blocks.

## Acceptance

Common investigations take fewer tool-call rounds while low-level tools remain available.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t12-add-high-frequency-composite-tools`
'@

    New-IssueSpec `
        -Title 'T1.3: Add resources and prompts with workflow drift checks' `
        -Body @'
## Scope

Add MCP Resources and Prompts as enhancement layers.

## Resources

- `capability-matrix`
- `tool-catalog`
- `workflow-catalog`

## Prompts

- `slow_startup`
- `missing_symbols`
- `high_wait`
- `gc_pressure`
- `baseline_regression`

## Acceptance

- Tools-only clients can still complete core investigations.
- Each Prompt and sibling composite Tool derive from one source-of-truth workflow artifact.
- CI fails when Prompt or composite Tool diverges from the source artifact, or both sides carry auditable metadata until generator enforcement lands.
- Agent-only Prompt invocation near zero is expected.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t13-add-resources-and-prompts`
'@

    New-IssueSpec `
        -Title 'T2.1: Expose trace quality and system configuration' `
        -Body @'
## Scope

Expose OS build, CPU model, core count, driver list, provider event counts, and stackwalk completeness.

## Acceptance

- Fields feed `inspect_trace` first.
- An LLM can judge whether the trace is trustworthy.
- Analysis limitations caused by capture quality are explicit.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t21-trace-quality-and-system-configuration`
'@

    New-IssueSpec `
        -Title 'T2.2: Unify ROI and time-window semantics' `
        -Body @'
## Scope

Audit and standardize `startUs` / `endUs` behavior across analyzers.

## Acceptance

- Boundary semantics are half-open: include iff `startUs <= timestamp < endUs`.
- A conformance fixture covers boundary events.
- Every time-windowed analyzer follows the same rule.
- Trace-global tools document why they do not accept a window.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t22-unify-roi--time-window-semantics`
'@

    New-IssueSpec `
        -Title 'T2.3: Add CPU precise and scheduler analysis' `
        -Body @'
## Scope

Use CSwitch data to compute on-CPU microseconds, ready latency, per-core attribution, and priority / quantum signals.

## Acceptance

The server can answer questions sampled CPU cannot:

- how long a thread actually ran
- how long it waited after becoming ready
- which cores it ran on

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t23-cpu-usage-precise-and-scheduler-analysis`
'@

    New-IssueSpec `
        -Title 'T2.4: Add memory resource views after capture verification' `
        -Body @'
## Scope

Expose working set, commit, private bytes, paged / non-paged pool, and handle count, but only after verifying capture data exists.

## Risk gate

Confirm whether existing WPR profiles capture the required data. If not:

- Author `MemoryCapture.wprp` beside `MmapCapture.wprp`.
- Capture a passing fixture.
- Document keyword requirements in `docs/WPR_PROFILE.md`.

## Acceptance

The server can answer resident-footprint, pool-exhaustion, and handle-leak questions instead of only allocation-event questions.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t24-memory-resource-views`
'@

    New-IssueSpec `
        -Title 'T3.1: Add cross-trace diff support' `
        -Body @'
## Scope

Add baseline-vs-regression diffs for CPU, wait, and image-load gaps.

## Prerequisite

Define process identity matching and metric schema first. Reuse `docs/archive/OPTIMIZATION.md` O2 for identity alternatives.

## Acceptance

- Output uses `MetricName`, `DeltaMetric`, `DeltaPct`, and appeared / disappeared markers.
- Do not use an incorrect universal `DeltaUs`.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t31-cross-trace-diff`
'@

    New-IssueSpec `
        -Title 'T3.2: Explore .gcdump retention paths' `
        -Body @'
## Scope

Load `.gcdump` files, build object reference graphs, and expose retention paths.

## Acceptance

The server can answer "who still holds these objects?", closing the memory-leak gap that ETW GC events cannot cover.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t32-gcdump-and-retention-paths`
'@

    New-IssueSpec `
        -Title 'T3.3: Stitch CLR async task chains' `
        -Body @'
## Scope

Reassemble CLR Task continuations and recover async call chains across threads.

## Acceptance

Async workflows split across threads can be presented as explainable chains.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t33-async--task-chain-stitching`
'@

    New-IssueSpec `
        -Title 'T3.4: Add generic event group_by pivot' `
        -Body @'
## Scope

Extend `generic_event_top_stacks` with a constrained `group_by` pivot.

## Phases

1. Minimum viable: task + opcode `group_by` on the existing tool. No new tool and no widened DSL.
2. Validate against 1-2 real scenarios before widening axes such as event_id or payload field.

## Acceptance

Phase 1 captures core WPA pivot value without exposing an unbounded query surface. Widening happens only with validation data showing usage.

Source: `docs/MCP_IMPLEMENTATION_TASKS.md#t34-generic-event-group_by--pivot`
'@
)

Assert-Gh

foreach ($issue in $issues) {
    New-GitHubIssue -Issue $issue
}
