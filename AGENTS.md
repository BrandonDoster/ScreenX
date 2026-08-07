# Agent Instructions

Canonical instructions for agents and contributors working in this project.

## What this is

ScreenX is an Electron desktop app for screen capture and GIF recording on
macOS (Intel) and Windows. It is deliberately offline: there is no uploading,
no telemetry, and no network code anywhere in `src/`. Keep it that way.

## Layout

```
src/main/       main process: app lifecycle, tray, hotkeys, capture, settings
src/preload/    one contextBridge per window type; the only place `require` runs
src/renderer/   plain HTML/CSS/JS windows, no framework, no build step
assets/         tray and application icons (generated PNGs)
test/           see "Tests" below
reference/      read-only third-party source, git-ignored, never copied from
```

There is no bundler. Renderer files are loaded directly with `<script src>`, so
they must stay plain browser JavaScript with no `import`/`require`.

## Rules

- **Context isolation stays on.** Renderers reach the main process only through
  a preload that exposes a narrow named API on `window.screenx`. Node modules
  (`gifenc`, `fs`, …) are required in the main process or a preload, never in a
  renderer.
- **`reference/` is off limits for copying.** It is there to check behaviour
  against, not to lift code or comments from, and it must never be committed.
- **No new dependencies** without a real reason. The runtime dependency list is
  one entry (`gifenc`) and should stay close to that.
- Filenames written to disk go through `parseName` in `src/main/naming.js`,
  which strips characters no filesystem accepts. Do not build paths by hand.
- Settings are merged over defaults in `src/main/settings.js`; unknown keys are
  dropped and types must match. Adding a setting means adding it to `defaults()`.

## Tests

```sh
npm test          # node only: filename pattern rules
npm run test:ui   # electron: drives every renderer with synthetic events
npm run test:e2e  # electron: real capture, cropping, saving, GIF encoding
```

Run all three before committing anything that touches capture, the editor, or
settings. `test:e2e` needs Screen Recording permission on macOS; if it starts
failing along with the system `screencapture` tool, the macOS capture daemon is
wedged — `killall replayd` fixes it.

## Platform gotchas

- macOS reports only windows that are visible on screen, so the window picker
  cannot list minimised or fully covered windows. This is a system limit.
- Region rectangles travel in device-independent pixels and are converted to
  physical pixels at the point of use (`cropToDisplayRect`, `startRecording`).
  Mixing the two up silently produces off-by-scale-factor crops on Retina.
- Electron below 43 returns empty capture thumbnails on recent macOS. Do not
  downgrade.
