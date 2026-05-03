# How to record `assets/quickstart-demo.gif`

This is the recording recipe for the animated GIF embedded at the top of the
README's Quickstart section.  Re-recording is a few-minutes job; this file
captures the exact prompts, the trace to use, and the export settings so the
result stays consistent across re-records.

The README's `<img src="assets/quickstart-demo.gif">` placeholder is already
in place — when you commit the GIF the image will start rendering on the
next GitHub README refresh.

---

## What the viewer should see (30 – 45 s)

A real Claude Code (or Codex) session against a real `.etl`, paced like an
actual investigation, ending on the punchline frame so a static viewer of
the last frame still gets the point.

| t       | What's on screen                                                                                       |
|---------|--------------------------------------------------------------------------------------------------------|
|  0 s    | Claude Code (or Codex) window with `wpa-mcp` listed in the connected MCP servers.                      |
|  2 s    | User types: `Load this trace: C:\path\to\<trace>.etl`                                                  |
|  4 s    | Agent calls `load_trace`; the JSON response shows up with `Capabilities` map highlighted.              |
| 10 s    | User types: `Which processes have the highest wait ratio?`                                             |
| 12 s    | Agent calls `list_processes` with `orderBy=wait_ratio`; top-5 rows visible (the slow ones obvious).    |
| 18 s    | User types: `Run process_create_timing on parent PID <X>` (PID picked from the previous response).     |
| 20 s    | Agent calls `process_create_timing`; response shows `medianKernelGapUs`, `p95KernelGapUs`, `maxKernelGapUs`. |
| 28 s    | Agent narrates briefly: *"Median fork gap is 879 ms — 17× the normal baseline. That's the kernel-side time AV / EDR callbacks burn."* |
| 35 s    | END — final frame still has the response visible.                                                      |

Aim for **30 – 45 s** total.  Anything longer and the GIF size balloons; anything
shorter and the viewer doesn't get to see the agent actually call the tools.

---

## Trace to use

Any trace where `process_create_timing` surfaces an interesting median.  The
case-study trace works well — see `docs/CASE_STUDIES.md` for the shape (one
parent forking ~10+ children of the same name, with a noticeable gap distribution).

The path **will be visible in the recording**.  Pick a path with no PII (no
real usernames, no internal codenames).  An anonymised `C:\demo\app-startup.etl`
or similar is best; the actual trace under that path is real, just the path
shown is generic.

---

## Recording

**Tool:** [ScreenToGif](https://www.screentogif.com/) — Windows, free, GUI-driven.

**Settings:**

| Knob              | Value                                                                                       |
|-------------------|---------------------------------------------------------------------------------------------|
| Recording region  | Just the Claude Code / Codex window — **no desktop chrome, no taskbar**.                    |
| Resolution        | Aim for ~1280 × 720.  Large enough to read the JSON tool output without zooming.            |
| Frame rate        | **15 FPS** is plenty for text-heavy IDE / chat content. 30 FPS doubles file size for no perceptible quality gain. |
| Cursor            | Optional but useful as a focus indicator.  ScreenToGif's "Show cursor" toggle.              |

**Editing pass** (in ScreenToGif's editor):

* **Trim** leading / trailing dead frames — start when the user begins typing, end on the
  punchline frame with the response visible.
* **Skip-frame** any pauses > 500 ms inside agent responses.  Preserves the feel of an
  interactive session, not a lecture.

**Export:**

| Knob               | Value                                                                                  |
|--------------------|----------------------------------------------------------------------------------------|
| Format             | GIF                                                                                    |
| Color quantization | 256 colors with FFmpeg-style palette (ScreenToGif default in v2.40+).  Lower (128 / 64) if file size > 5 MB. |
| Target size        | **~3 MB.**  Hard cap **8 MB** — GitHub starts rate-limiting playback over slow links above this. |
| Output path        | `assets/quickstart-demo.gif` (overwrite the placeholder reference).                    |

---

## After recording

```bash
git add assets/quickstart-demo.gif
git commit -m "docs(README): add quickstart demo gif"
git push
```

That's it.  The README's `<img>` tag is pre-wired to that path; the GIF starts
rendering on the next GitHub view of the README.

If you re-record later (e.g., to refresh the demo for a major release),
overwrite the same path — the image tag and all docs links remain stable.
