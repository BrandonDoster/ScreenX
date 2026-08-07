# Agent Instructions

Canonical instructions for agents and contributors. Read this before changing
anything; it exists so you do not have to rediscover the codebase each session.

Human-facing docs: [`README.md`](README.md) for users,
[`docs/TECHNICAL.md`](docs/TECHNICAL.md) for the full design rationale. This
file is the map and the rules.

---

## Orientation in 60 seconds

ScreenX is a **Tauri** screenshot tool for macOS and Windows. Rust does the
capture, encoding and file writing. Three plain HTML/JS pages provide the
selection overlay, the annotation editor and the settings form. Fully offline —
**there is no network code anywhere, and none may be added.**

- Rust entry point: `src-tauri/src/lib.rs` → `run()` at the bottom.
- Pages: `ui/*.html` + matching `.js`. No framework, no bundler, no build step.
- One JSON settings file. GIF recording is deliberately absent (parked).
- 6.9 MB bundle. 23 Rust tests, 28 JS tests, 6 self-checks. All must stay green.

---

## File map

| File | Lines | Holds |
| --- | ---: | --- |
| `src-tauri/src/lib.rs` | 833 | State, tray, hotkeys, capture flows, window builders, all commands |
| `src-tauri/src/capture.rs` | 408 | xcap wrappers, `Rect` maths, coordinate normalisation, encoding, saving |
| `src-tauri/src/naming.rs` | 329 | Filename pattern expansion + its tests |
| `src-tauri/src/settings.rs` | 201 | The single JSON settings file |
| `src-tauri/src/selfcheck.rs` | 179 | Debug only. Diagnostics needing a real webview/screen |
| `src-tauri/src/docshots.rs` | ~200 | Debug only. Generates `docs/images/*` |
| `ui/editor.js` | 752 | Annotation editor |
| `ui/overlay.js` | 347 | Selection overlay, one instance per monitor |
| `ui/settings.js` | 307 | Settings form, hotkey recorder |
| `ui/blur.js` | 73 | Blur, shared by editor and self-check |
| `tests/*.mjs` | ~610 | DOM stubs + behaviour tests for overlay and editor |

`reference/` holds third-party source for behavioural comparison. **Never
compile it, never copy from it, never commit it.** It is gitignored.

---

## Where do I change X?

| Task | Go to |
| --- | --- |
| Add a command the UI can call | `lib.rs` — write `#[tauri::command]`, then add it to `invoke_handler!` (line 776) |
| Add a setting | `settings.rs` `Settings` struct + its `Default`, then a control in `ui/settings.html`, then `fill()` and `collect()` in `ui/settings.js` |
| Add a filename token | `naming.rs` — `TOKENS` array (line 27, **longest first**) and `expand()` (line 101), then the README table |
| Add an editor tool | `ui/editor.js` — `TOOLS` array (line 18), a `case` in `drawShape()`, and `shapeBounds()` if the shape is not `x1,y1,x2,y2` |
| Change how a window is opened | `lib.rs` — `open_settings` (105), `open_editor` (126), overlays inside `start_region_select` (280) |
| Change what happens after a capture | `lib.rs` — `deliver()` (196) |
| Change region/window selection behaviour | `ui/overlay.js` — dwell logic near the top, `finish()` for the outcome |
| Change the tray menu | `lib.rs` — `build_tray()` (486) |
| Change hotkey recording | `ui/settings.js` — `toAccelerator()`; registration in `lib.rs` `register_hotkeys()` (453) |
| Touch coordinates | `capture.rs` — `to_dip()` (83) and `crop_to_rect()` (198). Read invariant 1 first |

---

## Invariants — do not break these

Each of these was a real bug. The code comments say so too.

**1. Everything is in device-independent pixels except pixel buffers.**
`capture::crop_to_rect` is the only place a scale factor is applied. Monitor
bounds, window rects, selection rects and window positions are all DIP.
`MonitorShot.image` is physical. Mixing them gives off-by-scale-factor crops on
Retina and silently wrong hit tests.

**2. xcap is not consistent across platforms.** It reports macOS geometry in
points and Windows geometry in physical pixels. `capture::to_dip` normalises it
once, at the boundary. On macOS it is an exact identity. Nothing downstream
should ever touch a scale factor.

**3. Hotkey modifiers are stored literally.** `Control` means Control on every
platform. Do **not** reintroduce `CommandOrControl` — the parser resolves it to
Command on macOS, so pressing Control+Shift+Q registered Command+Shift+Q and the
user's shortcut did nothing. Also refuse modifier-less combinations; a global
hotkey takes that key from every application.

**4. WKWebView does not implement `ctx.filter`.** Assigning to it *appears* to
work. Blur is hand-written in `ui/blur.js` by repeated halving. Do not
"simplify" it back to a canvas filter. It is a redaction tool: it must destroy
pixels, not soften them.

**5. The `screenx:` scheme must send `Access-Control-Allow-Origin`, and the
editor must load the image with `crossOrigin = 'anonymous'`.** Both halves.
Without the header the image will not load at all; without `crossOrigin` the
canvas is tainted and `toDataURL()` throws `SecurityError`, breaking Save, Save
As and Copy. It looks exactly like an OS permission problem and is not.

**6. Free frames when a window closes.** `close_window` and `close_overlays`
remove the frame keyed to that window. Otherwise every capture leaks megabytes
for the process lifetime.

**7. Window creation belongs on the main thread.** Commands run on worker
threads; use `on_main()` (lib.rs 226). Capture itself can run anywhere.

**8. Esc is claimed globally while an overlay is open** and released when it
closes. The overlay covers the menu bar, so the tray cannot rescue a stuck
overlay. Do not remove this safety valve.

**9. Pixels do not cross IPC as text.** They go in `State.frames` and are served
over the `screenx:` scheme. The one exception is the editor's finished PNG on
save, which is deliberate.

**10. No frontend build step.** `ui/` files load as-is. Plain browser JS only —
no `import`, no `require`, no npm packages. Add a `<script>` tag for new files,
and mirror the load order in the test harness.

---

## Key concepts and vocabulary

| Term | Means |
| --- | --- |
| **Frame** | Encoded image bytes parked in `State.frames`, served over `screenx:` |
| **Payload** | JSON a window fetches with `invoke('window_payload')` once it loads |
| **Pending** | `State.pending` — the monitor captures held while an overlay is on screen |
| **Shot** | A `MonitorShot`: one monitor's DIP bounds, scale, and physical-pixel image |
| **Dwell** | The pause before a hovered window highlights (`window_highlight_delay_ms`) |
| **Destructive tool** | Editor tool that rewrites `base` rather than adding a shape: `crop`, `cutout` |

**The payload pattern** is how every window receives data. Rust stores JSON in
`State.payloads` keyed by window label *before* building the window; the page
calls `invoke('window_payload')` when ready. This avoids the open-then-send
race. Use it for new windows too.

---

## Commands

```sh
npm run dev          # debug build, live webview
npm test             # both suites; must pass before committing
npm run test:ui      # node --test, the webview logic
npm run test:rust    # cargo test
npm run selfcheck    # real webview + screen diagnostics
npm run build        # release bundle + installers
npm run docshots     # regenerate docs/images
```

---

## Testing strategy

Three layers, because macOS WKWebView has **no WebDriver support** — there is no
conventional UI automation route.

1. **`cargo test`** — pure logic: patterns, settings, rect maths, clipping,
   cropping, DIP normalisation, encoding.
2. **`node --test`** — the real `ui/overlay.js` and `ui/editor.js` loaded into a
   `vm` context against a stubbed DOM (`tests/harness.mjs`,
   `tests/editor-harness.mjs`), driven with synthetic events. Covers dwell
   timing, front-most hit testing, drag-beats-highlight, single-outcome
   guarantees, cut-out direction, and history across destructive edits.
3. **`npm run selfcheck`** — what a stub cannot answer, inside the real engine:
   the whole capture→crop→name→save→read-back path, the `screenx:` scheme, the
   overlay's actual window level, that blur genuinely blurs, and that the
   editor's canvas is readable for saving.

Layer 3 has caught two real bugs (missing `ctx.filter`, tainted canvas). **When
you fix something a stub could not have caught, add it to the self-check.**

Gotcha: values returned from the `vm` realm must be normalised (the harnesses
JSON round-trip them) or `deepStrictEqual` rejects them on prototype identity.

---

## Platform notes

- **macOS lists only visible windows.** Minimised or fully covered windows
  cannot be highlighted. System limit, and correct behaviour for this feature.
- **Overlay window level is 1000** (`NSScreenSaverWindowLevel`), set via `objc2`
  because Tauri's always-on-top sits below the macOS menu bar (level 24).
- **`zune-jpeg` is pinned to `0.5.16-rc1`.** Not used directly; it arrives via
  `image` → `tiff`, and 0.5.15 does not compile on rustc 1.97. Remove the pin
  when a stable 0.5.16 ships.
- **Windows has never been run.** The code and coordinate handling exist but are
  unverified. There is no cross-compilation path from macOS for MSVC — it must
  be built on Windows.

---

## Style

Match the surrounding code. Comments explain *why*, not *what* — most existing
comments record a bug that was hit, so do not delete one without understanding
what it protects.

Keep the dependency list small; every crate in `Cargo.toml` earns its place.
Prefer deleting code to adding it.
