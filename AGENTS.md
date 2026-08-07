# Agent Instructions

Canonical instructions for agents and contributors working in this project.

## What this is

ScreenX is a screen capture and annotation tool for macOS (Intel) and Windows,
built with Tauri: a Rust core and three plain HTML/CSS/JS webviews. It is
deliberately offline — there is no uploading, no telemetry, and no network code
anywhere in `src-tauri/src` or `ui`. Keep it that way.

It was originally written in Electron. That version is in the git history up to
`d2db785`; it is not a reference for anything except behaviour.

## Layout

```
src-tauri/src/
  lib.rs        app wiring: tray, hotkeys, capture flows, windows, commands
  capture.rs    xcap wrappers, coordinate maths, encoding, saving
  settings.rs   the single JSON settings file
  naming.rs     filename pattern expansion
ui/             overlay, editor and settings webviews; no framework, no build step
assets/         source icon; the generated icon set lives in src-tauri/icons
reference/      read-only third-party source, git-ignored, never copied from
```

## Rules

- **Pixels never cross IPC as text.** Captured images are parked in the `Frames`
  map and served over the `screenx:` URI scheme, so a webview loads them like any
  other image. Only the editor's finished PNG travels back as a data URL, once,
  on save.
- **No build step for the frontend.** `ui/` is loaded directly, so those files
  stay plain browser JavaScript — no `import`, no bundler, no npm packages.
- **Two coordinate spaces.** Monitors, windows and selection rectangles are all
  in device-independent pixels; captured images are in physical pixels.
  `capture::crop_to_rect` is the only place the scale factor is applied. Mixing
  them up silently produces off-by-scale-factor crops on Retina displays.
- **Hotkeys are stored literally.** `Control` means Control, on every platform.
  Do not reintroduce a per-platform modifier: it registers a shortcut the user
  never pressed. `global-hotkey`'s parser already accepts the literal form.
- **Settings are one JSON file** with `#[serde(default)]` on every struct, so a
  file from an older or newer version still loads. Adding a setting means adding
  it to the struct and its `Default`.
- **`reference/` is off limits for copying.** It is there to check behaviour
  against, not to lift code from, and it must never be committed.

## Tests

```sh
cd src-tauri && cargo test
```

Covers filename patterns, settings round-tripping, rectangle maths, monitor
clipping, scale-factor cropping and encoding. Run it before committing anything
that touches those.

The webviews are not covered by automated tests; they need a real run
(`npm run dev`) and a human at the keyboard. The riskiest parts to re-check by
hand after a change are the overlay's dwell highlight, the editor's cut-out
splice, and blur (which depends on the webview supporting `ctx.filter`).

## Platform notes

- macOS reports only windows that are visible on screen, so a minimised or fully
  covered window cannot be highlighted. This is a system limit, not a bug.
- The selection overlay is pushed to window level 1000 through `objc2` because
  Tauri's always-on-top sits below the macOS menu bar.
- `zune-jpeg` 0.5.15 does not compile on rustc 1.97; it arrives through
  `image` → `tiff`, so the fix is pinned as a direct dependency. Remove the pin
  once a stable 0.5.16 ships.
- Windows builds have to be produced on Windows; there is no cross-compilation
  path from macOS for the MSVC target.
