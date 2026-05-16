# MCP Stack Response Compactness

Status: implemented for T0.6 on 2026-05-15.

The current `*_top_stacks` responses do not return full frame arrays. Each row is already a frame-level summary with metric columns such as exclusive/inclusive bytes, samples, events, or microseconds. That means there is no full stack payload to truncate in the wire response today.

To keep the API stable for clients that ask for compact stack output, every `*TopStacks` tool now accepts:

| Parameter | Behavior |
|---|---|
| `compactStacks` | Lossy compact mode for token-constrained clients. Caps returned rows at `StackResponseOptions.CompactTopLimit` (`25`). Current rows are already compact frame summaries. |
| `summaryOnly` | Lossy smaller leaf/metric summary. Same row cap, intended for clients that want only the first-pass signal. |

Default behavior is unchanged: if both parameters are false, the requested `top` value is honored up to the existing validation limit of 1000. If compact output shows a flat distribution, missing expected frames, or any other long-tail signal, rerun the same tool with `compactStacks=false` / `summaryOnly=false`, optionally with a larger `top`. Increasing `top` while a compact flag remains true will still cap rows at 25.

Sizing guardrails:

- Warning threshold: `StackResponseOptions.WarningResponseBytes` (`40000` serialized JSON bytes), approximating a 10,000-token warning budget for ASCII-heavy JSON.
- Maximum threshold: `StackResponseOptions.MaximumResponseBytes` (`100000` serialized JSON bytes), approximating a 25,000-token hard budget for ASCII-heavy JSON.
- Tests assert representative default stack responses remain under the warning threshold and that compact modes cap row count.

If a future analyzer adds full per-row stack frame arrays, `compactStacks` should truncate those arrays to the documented compact depth and include an explicit tail marker before increasing row counts or payload limits.
