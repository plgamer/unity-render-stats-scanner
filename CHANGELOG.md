# Changelog

All notable changes to this package are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-09-14

### Added

- Initial release, extracted from a private Unity project.
- Live sampling of `UnityEditor.UnityStats` during Play mode, one sample per
  rendered frame.
- Spike detection against a rolling 60-frame median of batch count, gated by
  both a ratio and a minimum absolute delta.
- Absolute threshold backstop on batches, SetPass calls, draw calls, triangles
  and vertices.
- Automatic screenshot on each anomaly, with a configurable cooldown.
- 17 render stats captured per event, including the draw-call split across
  dynamic batching, static batching and GPU instancing, RenderTexture switches,
  texture memory, and frame/render timings.
- Events flushed to `events.tsv` as they happen, so a session survives the
  domain reload triggered by exiting Play mode.
- Automatic finalization on `ExitingPlayMode`, plus a manual Stop Scan.
- Self-contained `report.html` with screenshots and per-event stats tables.
- History dropdown for reloading past sessions in the window.
- Detection settings persisted in `EditorPrefs`.
