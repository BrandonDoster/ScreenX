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
npm test           # both suites
npm run test:ui    # ui/overlay.js against a DOM stub, node --test
npm run test:rust  # cargo test
```

The Rust suite covers filename patterns, settings round-tripping, rectangle
maths, monitor clipping, scale-factor cropping and encoding. The UI suite loads
the real `ui/overlay.js` in a `vm` context with a stubbed DOM (`tests/harness.mjs`)
and asserts the selection behaviour. Values crossing back out of that realm must
be normalised or `deepStrictEqual` rejects them on prototype identity.

`npm run selfcheck` runs a debug-only diagnostic inside the real webview and
window server: the `screenx:` URI scheme, the overlay's window level, and that
blur actually blurs. Run it after touching `ui/blur.js`, the URI scheme handler
or `raise_above_menu_bar`.

**WKWebView does not implement `ctx.filter`.** Blur is done by hand in
`ui/blur.js` with progressive halving; do not "simplify" it back to a canvas
filter. It is a redaction tool, so it has to destroy pixels, not soften them.

The editor and settings webviews have no other automated coverage — WKWebView
has no WebDriver support on macOS.

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
