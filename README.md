# Render Stats Scanner

*[中文文档](README.zh-CN.md)*

A Unity editor window that watches the Stats panel while you play and
screenshots the moment rendering goes wrong. When you stop playing, it hands you
a standalone HTML report pairing each screenshot with the full render stats of
that exact frame — so you can see *what was on screen* when batches spiked,
instead of guessing from a number that already scrolled past.

Menu: **Tools → Render Stats Scanner**

## Why another one

The Stats panel tells you the current frame. The Profiler tells you everything
but makes you go hunting. Neither one answers the question you actually have
when a mobile build stutters: *what was drawing when the batch count tripled?*

Three things this does that watching the Stats panel by hand doesn't:

- **It catches spikes relative to a baseline, not just fixed thresholds.** A
  fixed "batches > 150" rule is useless on a scene that idles at 160 and
  worthless on one that idles at 30. This tracks a rolling median over the last
  60 frames and fires when the current frame is some multiple above it, so the
  same settings work across menus, gameplay and popups. Absolute thresholds are
  still there as a backstop.
- **It survives exiting Play mode.** Events are flushed to disk the instant they
  happen, so the domain reload that wipes every in-memory field on exit doesn't
  take your session with it. Stop playing and the report is simply there.
- **It records the whole frame, not just the number that tripped.** Every
  capture stores 17 fields, including the draw-call split across dynamic
  batching / static batching / GPU instancing — which is what actually tells you
  whether batching broke or you just added geometry.

## Requirements

| | |
|---|---|
| Unity | 2022.3 (the version this is verified on; it uses no newer API, so earlier LTS releases will likely work) |
| Packages | None |
| Platform | Editor only — it reads `UnityEditor.UnityStats`, the same source the Stats panel uses, and is never compiled into a player |

## Install

### Via UPM

Window → Package Manager → **+** → *Add package from git URL*:

```
https://github.com/plgamer/unity-render-stats-scanner.git
```

### Manually

Copy `Editor/` into your project under any `Editor` folder.

## Usage

1. Press **Play**.
2. Open the window and press **Start Scan**.
3. Play normally. The window shows live stats; captures happen on their own.
4. **Exit Play mode.** The report generates automatically and loads back into
   the window. (**Stop Scan** does the same without leaving Play mode.)

Output lands in `Logs/RenderStatsScanner/session_<timestamp>/` — outside
`Assets/`, so Unity never imports it and `Logs/` is already gitignored in a
standard Unity project:

| File | |
|---|---|
| `report.html` | Self-contained report. Screenshots are referenced relatively, so the whole session folder can be zipped and sent to someone else |
| `events.tsv` | One row per capture, 25 columns. Also what the window reads back |
| `summary.tsv` | Averages, maxima and the thresholds that were in effect |
| `shots/` | PNG screenshots |

The window keeps a history dropdown of past sessions, so you can compare a
before/after run without leaving the editor.

## Detection settings

| Setting | Default | |
|---|---|---|
| Spike detection | on | Fires when `batches ≥ median × ratio` **and** `batches − median ≥ min delta`. Both conditions must hold, so a scene idling at 5 batches doesn't fire at 8 |
| Ratio | 1.5× | Multiple of the rolling median that counts as a spike |
| Min delta | 30 | Absolute increase floor, to suppress noise on cheap scenes |
| Absolute thresholds | 150 / 80 / 200 / 200k / 200k | Backstop on batches, SetPass calls, draw calls, triangles, vertices |
| Screenshot cooldown | 1.0s | A spike usually persists for many frames; this stops one event from filling the disk |

The baseline is a **median**, not a mean, specifically so that the spike frames
being measured don't drag the baseline up behind them. It needs 60 sampled
frames before spike detection arms; absolute thresholds work from frame one.

Settings persist in `EditorPrefs`.

## Known limitations

- `ScreenCapture.CaptureScreenshot` is asynchronous. The image lands a frame or
  two after the stats it is paired with, so on a fast-moving scene the
  screenshot may show slightly later content than the numbers describe. Report
  generation is deliberately delayed 1.5s to let the last files finish writing.
- Sampling is driven from `EditorApplication.update`, which ticks once per
  editor frame. If the editor throttles (background window, paused domain), you
  sample fewer frames than the game renders. Timings in the report are wall
  clock, not a substitute for the Profiler.
- Editor-only by design. It measures the editor's rendering, which is *not* your
  device's rendering — use it to find what's wrong, then confirm the fix on
  hardware.

## License

MIT — see [LICENSE](LICENSE).
