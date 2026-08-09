# Agent Instructions

Canonical instructions for agents and contributors. Read this before changing
anything; it exists so you do not have to rediscover the codebase each session.

Human-facing docs: [`README.md`](README.md) for users,
[`docs/TECHNICAL.md`](docs/TECHNICAL.md) for the full design rationale. This
file is the map and the rules.

---

## Orientation in 60 seconds

ScreenX is a screenshot tool for macOS and Windows, written in **Rust with an
egui interface**. There is no webview, no HTML and no JavaScript. Fully offline
— **there is no network code anywhere, and none may be added.**

- Two crates. `core/` reads the screen and writes files; `app/` draws.
- **Two programs.** `screenx` (`app/src/listener.rs`) is what stays running:
  tray, shortcuts, settings, and no renderer at all. `screenx-capture`
  (`app/src/main.rs`) is spawned per screenshot and exits when the editor
  closes. Read "The split" below before assuming either is the whole app.
- 23 core tests, 22 capture tests, 3 listener tests. All must stay green.
- `src-tauri/` and `ui/` are the **previous webview build**, kept only until the
  native one has been used for a while. Do not add to them.

### The split

A GL context is not freed by anything short of process exit, so a program that
holds one idles at the cost of one whether or not it is drawing. Measured on
Windows: the single-process build idled at 42.9 MB and the listener idles at
1.2 MB. The price is paid per screenshot, in spawn and renderer start-up —
median 159 ms to the overlay before, 267 ms after, both warm.

One cold first capture measured about 1.2 s, immediately after a build. That
figure is a single sample and does not match how it feels in use, so treat it as
unmeasured rather than as a number: re-measure after a reboot before quoting it.

`screenx-capture` reads the screen **before it creates any window**. That is not
a speed trick, it is the reason the overlay is not in its own photograph.

### Why it was rewritten

The webview build worked, but a selection overlay cost 634 MB across four
processes and would not come back down; the same overlay is 127 MB in one
process here. Most of its hard-won rules existed to survive the Rust-to-webview
boundary rather than to take a screenshot. `docs/TECHNICAL.md` has the numbers.

---

## File map

| File | Lines | Holds |
| --- | ---: | --- |
| `app/src/editor.rs` | 669 | Editor state, tools, history, toolbar, canvas input |
| `app/src/main.rs` | 723 | `screenx-capture`: one screenshot's overlay, editor and window lifecycle, then exit |
| `app/src/listener.rs` | 247 | `screenx`: tray, shortcuts, and spawning the worker. No renderer |
| `app/src/render.rs` | 366 | Shapes drawn twice: onto the saved image, and on screen |
| `app/src/edits.rs` | 227 | Crop, cut out, blur, pixelate — the edits that rewrite pixels |
| `app/src/overlay.rs` | 207 | Selection overlay |
| `app/src/tray.rs` | 101 | Menu bar icon and the paths it opens |
| `app/build.rs` | 20 | Links the Windows icon resource. Windows host only |
| `core/src/capture.rs` | 528 | Screen reading, `Rect` maths, coordinate normalisation, encoding, saving |
| `core/src/naming.rs` | 332 | Filename pattern expansion + its tests |
| `core/src/settings.rs` | 237 | The single JSON settings file |

`reference/` holds third-party source for behavioural comparison. **Never
compile it, never copy from it, never commit it.** It is gitignored.

---

## Where do I change X?

| Task | Go to |
| --- | --- |
| Add a setting | `core/src/settings.rs` `Settings` struct + its `Default`, then read it where it applies. There is no settings UI — the tray opens the JSON file |
| Add a filename token | `core/src/naming.rs` — `TOKENS` array (**longest first**) and `expand()`, then the README table |
| Add an editor tool | `app/src/editor.rs` — `Tool` enum (39) and `Tool::ALL` (61), a case in the drag or click handler in `ui()` (437), then **both** functions in `render.rs`. Read invariant 5 |
| Add a shape | `app/src/editor.rs` `Shape` (79), then `draw_shapes_into` (render.rs 215) **and** `draw_on_screen` (render.rs 268) |
| Change what happens after a capture | `app/src/main.rs` — `deliver()` (136) |
| Change region selection behaviour | `app/src/overlay.rs` — `update()` |
| Change the tray menu | `app/src/tray.rs` — `build()` (66), then the `MenuEvent` arms in `listener.rs` `poll()` (85). Read invariant 17 |
| Change hotkey parsing | `app/src/listener.rs` — `parse_hotkey()` (151). Read invariant 3 |
| Start or stop a screenshot | `app/src/listener.rs` — `spawn()` (58) |
| Show or hide the window | `app/src/main.rs` — `open_editor` (153), `begin_region` (96), `go_idle` (192). Read invariants 6, 14 and 15 |
| Change the Windows taskbar button | `app/src/main.rs` — `mod taskbar` (252). Read invariant 16 |
| Move the editor window | `app/src/main.rs` — `editor_position()` (391) |
| Read the screen | `core/src/capture.rs` — `capture_primary()` (208). `capture_monitors()` (218) reads every display and is only for the old webview build |
| Touch coordinates | `core/src/capture.rs` — `to_dip()` (86) and `crop_to_rect()` (297). Read invariant 1 |
| Measure memory | `app/src/main.rs` — `memcheck()` (483) |

---

## Invariants — do not break these

Each of these was a real bug. The code comments say so too.

**1. Everything is in device-independent points except pixel buffers.**
`capture::crop_to_rect` is the only place a scale factor is applied. Monitor
bounds, window rects and selection rects are all points. `MonitorShot.image` is
physical pixels. Mixing them gives off-by-scale-factor crops on Retina and
silently wrong hit tests.

**2. xcap is not consistent across platforms.** It reports macOS geometry in
points and Windows geometry in physical pixels. `capture::to_dip` normalises it
once, at the boundary. Nothing downstream should ever touch a scale factor.

**3. Hotkey modifiers are stored literally.** `Control` means Control on every
platform. Do **not** reintroduce a "CommandOrControl" that resolves to Command
on macOS — pressing Control+Shift+Q then registered Command+Shift+Q and the
user's shortcut did nothing. Also refuse modifier-less combinations; a global
hotkey takes that key from every application.

**4. macOS captures read the framebuffer, not a window list.** xcap's
`Monitor::capture_image` calls `CGWindowListCreateImage`, which composites the
windows it is handed. The menu bar background, the clock and every status item
belong to WindowServer and SystemUIServer, do not come back through that list,
and vanish from the screenshot while the menu titles stay — it looks like a
Screen Recording permission fault and is not. `capture::display_image` uses
`CGDisplayCreateImage` instead. That is deprecated in favour of
ScreenCaptureKit, so expect to port it; xcap is still correct on Windows.

**5. A shape is drawn twice and both must agree.** `render::draw_on_screen` is
what the user sees; `render::draw_shapes_into` is what lands in the file.
Reading pixels back off the GPU would avoid the duplication but is slow and
fails outright on some drivers. A shape added to one and not the other makes the
editor lie about the result.

**6. Closing the editor must not take the tray with it.** In the single-process
build the editor's close request ended the process, so the tray icon vanished
the first time anyone dismissed an editor, and the close had to be cancelled.
The split satisfies this by construction — the editor's close ends
`screenx-capture`, and the tray belongs to `screenx`, which is a different
program. If the two are ever merged back, the cancel comes back with them.

**7. ~~Going idle has to shrink the window~~ — retired by the split.** A hidden
window kept a framebuffer the size it was last shown at, so an editor left at
full screen size held it for as long as the app ran. `screenx-capture` has no
idle state: it exits. Reinstate this the moment anything makes the worker
persist, along with `forget_all_images`, because dropping a texture handle only
queues the release.

**8. A capture is in physical pixels and egui lays out in points.** The density
travels with the image into the editor, which divides by it. Drawing one for one
made every capture from a Retina panel appear at twice its real size.

**9. Fitting shrinks, never enlarges.** Growing an image is the zoom control's
job. An automatic enlargement on open looks like the editor has zoomed into the
screenshot for no reason.

**10. Editor history is bounded by bytes, not steps.** Adding a shape shares the
image through an `Arc`, but a crop or a blur allocates a new one. A step count
alone would let twenty-four destructive edits on a full screen capture hold most
of a gigabyte, which is the failure the rewrite exists to avoid.

**11. Blur and pixelate must destroy pixels, not soften them.** They are
redaction tools. Test them against high-frequency detail: a gradient stays a
gradient under any blur and proves nothing.

**12. Pointer input is clamped to the image.** The pointer keeps reporting once
it leaves the canvas, and every tool reads from `Editor::point`. Unclamped,
dragging past an edge to cut the last 20 px measured the whole 40 and took 20 px
of real image with it.

**13. An accessory app must activate, not just take focus.** ScreenX is outside
the normal activation order, so ordering the overlay front does not make it
*key*, and a window that is not key gets no mouse or keyboard events — it
arrives dimmed, with an arrow cursor, and its first click is spent activating.
`raise_and_activate` uses `activateIgnoringOtherApps:` because the modern
`activate()` is cooperative on macOS 14 and later and the system defers it.

**14. On Windows an egui window must never be hidden and then expected to run.**
Windows does not paint a window it considers hidden, winit emits no
`RedrawRequested` for an unpainted window, and `eframe::App::update` only runs
on a redraw — so hiding the window stopped the app draining its own request
channel. The hotkey still fired and the system still delivered it, with nothing
left listening: it worked exactly once per launch, until the first capture
finished and put the window away.

The single-process build worked around this by *parking* the window: visible,
one point square, click-through, in a corner. The split removes the need — the
worker never idles, and the listener has no window at all — but the rule stands
for anything that reintroduces a hidden-but-live window.
`ViewportBuilder::with_visible(false)` also does not hold on Windows, which is
why the first frame sets the geometry rather than trusting the builder.

**15. `ViewportCommand::Focus` goes last.** winit applies a window level change
as an asynchronous `SetWindowPos` carrying `SWP_NOACTIVATE`. Sent after a focus
request it lands later and takes the activation straight back, so the editor
opened behind every other window and would not come forward until the user
clicked a different application. Anything reordering the window belongs before
the focus request, never after.

**16. Do not set window styles behind winit's back.** `apply_diff` recomputes
the whole extended style from winit's own flags and writes it with
`SetWindowLongW`, so any bit it does not model is erased the next time
decorations, window level, visibility or mouse passthrough change — and opening
the editor changes four of them. The taskbar button therefore uses
`ITaskbarList::AddTab`/`DeleteTab`, which is independent of the style.

**17. The tray menu needs tray-icon 0.24 or newer.** 0.19 shows the menu with
`TrackPopupMenu` on its own hidden window, never calls
`attach_menu_subclass_for_hwnd`, and has no `WM_COMMAND` arm — so every
selection reached `DefWindowProcW` and was dropped. The whole tray menu was
inert on Windows: Quit, Open Screenshots Folder and Settings all did nothing,
and the only way to stop the program was Task Manager.

**18. ~~A quit must not be swallowed by the editor~~ — retired by the split.**
Quit now ends the listener's event loop and the editor is a different process,
so there is nothing to swallow it. The original bug, kept because it returns
with any merge: `ViewportCommand::Close`
arrives as `close_requested` on the *next* frame, by the same route as the
window's close button — so invariant 6's `CancelClose` cancelled the tray's Quit
too, and the program could not be closed at all while the editor was open.
`App::quitting` marks the close that must be allowed through.

---

## Key concepts and vocabulary

| Term | Means |
| --- | --- |
| **Shot** | A `MonitorShot`: one monitor's point bounds, scale, and physical-pixel image |
| **Mode** | What the single window is currently for: `Idle`, `Selecting` or `Editing` |
| **Density** | Physical pixels per point in a capture. Retina is 2.0 |
| **Destructive tool** | Editor tool that rewrites the image rather than adding a shape: `crop`, `cutout`, `blur`, `pixelate` |
| **Flatten** | Bake the live shapes into the image, which a destructive tool must do first |

---

## Commands

```sh
cargo run -p screenx --bin screenx    # the listener; spawns the worker beside it
cargo run -p screenx --bin screenx-capture -- --region   # one screenshot, alone
cargo test --manifest-path core/Cargo.toml
cargo test --manifest-path app/Cargo.toml
./app/bundle.sh                       # release .app, ad-hoc signed, host arch
./app/bundle.sh --universal           # both Apple architectures; what CI ships
```

`.github/workflows/release.yml` runs on a `v*` tag. It builds this bundle on
macOS and `screenx.exe` on Windows, and moves the Homebrew cask only for a tag
with no `-` in it, so a release candidate never becomes the `brew` version.

Memory is measured, not asserted. `--memcheck WIDTH HEIGHT SECONDS [--editor]`
stands the overlay or the editor up on a synthetic capture and holds it, so
`ps` can sample it. It needs no Screen Recording permission, which is what made
the claim unverifiable before:

```sh
./app/target/release/screenx --memcheck 3584 2240 10 &
for i in $(seq 1 8); do ps -o rss= -p $! ; sleep 1; done
```

---

## Testing strategy

Two layers, both `cargo test`.

1. **`core/`** — patterns, settings, rect maths, clipping, cropping, DIP
   normalisation, encoding.
2. **`app/`** — the edits that rewrite pixels, the overlay's coordinate
   reporting, and the editor's history behaviour: that adding a shape does not
   copy the image, that history stays inside its byte budget, and that undo
   walks back across a destructive edit.

There is no third layer any more. The webview build needed one because macOS
WKWebView has no WebDriver support, so the only way to test the real engine was
to run it; the things that layer existed to check — a URI scheme, a tainted
canvas, a missing `ctx.filter` — do not exist here.

What is still not covered by a test: anything needing a real screen. Window
level, key focus, activation and the tray icon are checked by running it.

---

## Platform notes

- **Windows has been run.** Capture, the overlay, the editor, both hotkeys, the
  tray menu, the taskbar button and quitting were all exercised on Windows 11
  across a two-monitor desktop. Invariants 14 to 18 are the bugs that found.
  What is *not* covered by a test there is the same list as anywhere else:
  window level, focus and the tray need a real screen.
- **The Windows executable icon comes from `app/build.rs`.** Explorer, Alt-Tab
  and the taskbar read an `RT_GROUP_ICON` resource out of the binary, which
  `tauri-build` used to supply. Without it winit finds no icon to fall back on
  and the window shows a blank one. Explorer caches icons hard, so rename the
  file or clear the cache before deciding it did not work.
- **Idle memory is renderer warm-up, not a leak.** Private working set settles
  about 30 MB above launch after the first rendered frame and then stays flat.
  It was measured against overlay texture size and overlay window size and does
  not move with either, so it is not the capture, the texture or the
  framebuffer. Do not "fix" it by downscaling the overlay; that was tried and
  bought nothing. Ending the process is what gives it back, which is what the
  split does.
- **The two binaries ship together.** `screenx` finds `screenx-capture` beside
  its own `current_exe`, not on the PATH. Shipping one without the other gives a
  tray icon whose shortcuts silently do nothing. `app/bundle.sh` and the release
  workflow still package a single binary and need updating before this ships.
- **Overlay window level is 1000** (`NSScreenSaverWindowLevel`), set via `objc2`
  because always-on-top sits below the macOS menu bar (level 24).
- **macOS lists only visible windows.** Minimised or fully covered windows
  cannot be highlighted. System limit, and correct behaviour for this feature.
- **`zune-jpeg` is pinned to `0.5.16-rc1`.** Not used directly; it arrives via
  `image` → `tiff`. Remove the pin when a stable 0.5.16 ships.

---

## Documentation

Changing `README.md`, `docs/TECHNICAL.md` or `AGENTS.md`? Read
`.agents/skills/screenx-docs/SKILL.md` first. It covers which register each
document uses, the ASD-STE100 rules the README follows, and the rules on figures
— performance numbers are approximate and marked so, artifact sizes are not
written down at all.

---

## Style

Match the surrounding code. Comments explain *why*, not *what* — most existing
comments record a bug that was hit, so do not delete one without understanding
what it protects.

Keep the dependency list small; every crate earns its place. `imageproc` was
added for shape rasterisation and then removed again, because the geometry it
was wanted for is a few dozen lines. Prefer deleting code to adding it.
