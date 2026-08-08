# ScreenX — technical documentation

How the program is put together, and why it is put together that way.

For using the program, see [`../README.md`](../README.md).
For a code map aimed at AI coding assistants, see [`../AGENTS.md`](../AGENTS.md).

---

## Contents

1. [What it is built on](#1-what-it-is-built-on)
2. [Where everything lives](#2-where-everything-lives)
3. [How the parts talk to each other](#3-how-the-parts-talk-to-each-other)
4. [Moving images without IPC](#4-moving-images-without-ipc)
5. [Coordinate systems](#5-coordinate-systems)
6. [The capture flows, step by step](#6-the-capture-flows-step-by-step)
7. [The selection overlay](#7-the-selection-overlay)
8. [The editor](#8-the-editor)
9. [Settings](#9-settings)
10. [File names](#10-file-names)
11. [Global hotkeys](#11-global-hotkeys)
12. [Building and releasing](#12-building-and-releasing)
13. [Tests](#13-tests)
14. [Things that will bite you](#14-things-that-will-bite-you)
15. [History](#15-history)
16. [WebView2 reentrancy, and the deadlock it caused](#16-webview2-reentrancy-and-the-deadlock-it-caused)

---

## 1. What it is built on

ScreenX is a [Tauri](https://tauri.app) application. That means:

- The program itself is Rust. It does all the screen capture, image handling
  and file writing.
- The three screens (selection overlay, editor, settings) are ordinary HTML,
  CSS and JavaScript, shown in the operating system's own web view: WKWebView
  on macOS, WebView2 on Windows.

There is no bundled browser engine, which is why the application is a few
megabytes rather than the hundreds Electron needed.

There is no front-end build step. No bundler, no TypeScript, no npm packages in
the browser. The files in `ui/` are loaded exactly as they are on disk. If you
add a file there, add a `<script>` tag for it. Do not use `import` or `require`
in those files.

### Runtime dependencies

| Crate | Used for |
| --- | --- |
| `tauri` | Windows, tray icon, the bridge to the web views |
| `tauri-plugin-global-shortcut` | System-wide hotkeys |
| `tauri-plugin-dialog` | Folder picker, Save As dialog |
| `tauri-plugin-opener` | Opening a folder in Finder or Explorer |
| `xcap` | Screen capture, and the list of on-screen windows with their positions |
| `image` | PNG and JPEG encoding and decoding |
| `arboard` | Putting an image or text on the clipboard |
| `chrono` | Local date and time for file name patterns |
| `rand` | Random tokens in file name patterns |
| `dirs` | Finding the Pictures and config folders |
| `base64` | Decoding the edited image the editor sends back |
| `objc2`, `objc2-app-kit` | macOS only: raising the overlay above the menu bar |

`zune-jpeg` is pinned to `0.5.16-rc1`. It is not used directly. It arrives
through `image` → `tiff`, and version 0.5.15 does not compile on rustc 1.97.
Remove the pin when a stable 0.5.16 is published.

The only Node dependency is the Tauri command line tool, used to build.

---

## 2. Where everything lives

```
src-tauri/
  Cargo.toml              dependencies and the release profile
  tauri.conf.json         app identity, bundle targets, icons
  capabilities/           which commands each window may call
  src/
    main.rs               five lines; calls lib.rs
    lib.rs                everything that wires the app together
    capture.rs            screen and window capture, geometry, encoding, saving
    settings.rs           the one JSON settings file
    naming.rs             file name pattern expansion
    selfcheck.rs          debug only: diagnostics that need a real webview
    docshots.rs           debug only: makes the documentation screenshots

ui/
  app.css                 shared styles
  overlay.html/.js        the selection overlay, one instance per monitor
  editor.html/.js         the annotation editor
  blur.js                 blur, shared by the editor and the self-check
  settings.html/.js       the settings form
  selfcheck.html          debug only: runs checks inside the real web view

tests/
  harness.mjs             DOM stub for the overlay
  overlay.test.mjs        selection behaviour
  editor-harness.mjs      DOM stub for the editor
  editor.test.mjs         editor geometry and history

docs/
  TECHNICAL.md            this file
  images/                 screenshots used in the README

assets/icon.png           the source icon; src-tauri/icons/ is generated from it
reference/                third-party source kept for comparison. Not compiled,
                          not committed, never copied from.
```

`lib.rs` is the largest file at about 860 lines. It holds the shared state, the
tray menu, the hotkey registration, the three capture flows, the window builders
and all the commands the web views can call. Everything else is a focused module
that `lib.rs` calls into.

---

## 3. How the parts talk to each other

Three mechanisms, used for three different things.

### Commands: web view calls Rust

A web view calls `invoke('command_name', { argument: value })`. Rust receives it
in a function marked `#[tauri::command]`, listed in the `invoke_handler` block
near the bottom of `lib.rs`.

Argument names convert automatically: JavaScript `monitorIndex` arrives as Rust
`monitor_index`.

The full list:

| Command | Called by | Does |
| --- | --- | --- |
| `window_ready` | every window | Shows the window, now that it has painted |
| `window_payload` | every window | Returns the data that window was opened with |
| `get_settings` | settings | Reads the settings |
| `save_settings` | settings | Writes the settings, re-registers hotkeys, returns any the system refused |
| `default_settings` | settings | The factory defaults, for the Restore button |
| `preview_name` | settings | Expands a name pattern, for the live example |
| `settings_file` | settings | The path of the settings file |
| `open_path` | settings | Opens a folder or file in Finder or Explorer |
| `pick_folder` | settings | Shows the folder picker |
| `region_selected` | overlay | A rectangle was dragged |
| `window_selected` | overlay | A highlighted window was clicked |
| `region_cancelled` | overlay | The selection was abandoned |
| `editor_save` | editor | Saves using the folder and name pattern |
| `editor_save_as` | editor | Saves through a file dialog |
| `editor_copy` | editor | Puts the image on the clipboard |
| `close_window` | editor, settings | Closes the calling window and frees its image |

### The payload pattern: Rust hands data to a new window

A new window cannot be given data at construction time, and sending it an event
straight after opening is a race: the page may not be listening yet.

So it works the other way round. Before Rust opens a window, it stores a JSON
payload in `State.payloads`, keyed by the window label. The page asks for it
when it is ready:

```js
const payload = await invoke('window_payload');
```

No race, no event ordering to think about. Every window in the app starts this
way.

### Events: Rust tells a window something later

Only used for errors (`screenx:error`). Everything else fits the two patterns
above.

---

## 4. Moving images without IPC

A full-screen capture on a Retina display is 3584 × 2240 pixels — about 2 MB as
a PNG, and about 3 MB again if you base64-encode it into a string. Sending that
through IPC for every capture would be slow and wasteful.

Instead, Rust registers a custom URI scheme, `screenx:`. Encoded image bytes go
into `State.frames`, a map from a key to bytes plus a MIME type. The page is
given a URL and loads it like any other image:

```
macOS, Linux:  screenx://localhost/f/editor-3
Windows:       http://screenx.localhost/f/editor-3
```

The URL is built in Rust by `frame_url()`, so no page ever has to know the
platform difference.

Two rules that come out of this:

**The response must carry `Access-Control-Allow-Origin`, and the editor must
request the image with `crossOrigin = 'anonymous'`.** The page's origin is not
`screenx://localhost`, so without both of these the image taints the canvas and
`toDataURL()` throws `SecurityError` — which breaks Save, Save As and Copy. Both
halves are needed; `crossOrigin` on its own stops the image loading at all.

**Frames must be freed.** `close_window` and `close_overlays` remove the frame
that belongs to the window being closed. Forget this and every capture leaks a
few megabytes for the lifetime of the process.

The one place a large image does travel as text is the other direction: the
editor sends its finished PNG back as a data URL, once, on save. That is a
deliberate trade, because it happens once per save and the alternative needs a
raw-body IPC channel.

---

## 5. Coordinate systems

This is where bugs come from. There are two systems in play.

**Device-independent pixels (DIP)** — what the user sees as "a pixel" and what
window positions are measured in. On a Retina display, a 1792 × 1120 DIP screen
has a scale factor of 2.

**Physical pixels** — what a captured image actually contains. That same screen
captures as 3584 × 2240.

The rule in ScreenX:

> Everything is in device-independent pixels, except the pixel buffers
> themselves. `capture::crop_to_rect` is the only place a scale factor is
> applied.

So monitor bounds, window rectangles, the selection rectangle the overlay
reports, and the size and position of the overlay window are all DIP.
`MonitorShot.image` is physical. `crop_to_rect` bridges them, and it derives the
scale from the image itself rather than trusting the reported scale factor:

```rust
let scale = shot.image.width() as f32 / shot.bounds.width as f32;
```

### xcap is not consistent across platforms

`xcap` reports macOS geometry in points, and Windows geometry in physical
pixels. `capture::to_dip` normalises this once, at the boundary, using
`RAW_IS_PHYSICAL`, which is `cfg!(windows)`.

On macOS the conversion is an exact identity, so it cannot change behaviour
that already works there.

On Windows it divides by the scale factor of the monitor the rectangle sits on.
That is exact when every monitor uses the same scale, and approximate when they
do not. Doing mixed-DPI Windows exactly needs each monitor's own origin, which
is worth writing if anyone reports drift.

---

## 6. The capture flows, step by step

### Whole screen — `capture_fullscreen`

1. A worker thread captures every monitor.
2. `monitor_under_cursor` picks one. It converts the pointer position from
   physical to DIP and finds the monitor containing it. If none contains it, it
   takes the nearest by centre distance.
3. Control moves to the main thread, and `deliver` takes over.

### Region or window — `start_region_select`

1. A worker thread lists the on-screen windows and captures every monitor.
   The window list costs well under a millisecond, so it is fetched before the
   overlay opens rather than while the pointer is moving.
2. For each monitor it encodes the capture as JPEG at quality 92 and stores it
   as a frame. JPEG keeps the overlay quick to draw; the crop still comes from
   the untouched capture held in Rust.
3. The monitor shots are parked in `State.pending`.
4. On the main thread, one borderless overlay window is built per monitor, at
   that monitor's DIP position and size, and raised above the menu bar.
5. Esc is claimed as a global hotkey.
6. The user drags or clicks. The page calls `region_selected` or
   `window_selected`.
7. Rust closes every overlay, releases Esc, takes the pending shots, crops the
   right monitor's image, and hands the result to `deliver`.

Cancelling takes the same path through `region_cancelled`.

### `deliver` — what happens to a finished image

Reads `after_capture` from the settings:

| Value | Result |
| --- | --- |
| `editor` | Opens the editor with the image |
| `save` | Writes the file |
| `copy` | Puts the image on the clipboard only |
| `saveCopy` | Writes the file and puts the image on the clipboard |

If `features.editor` is off, `editor` falls through to saving.

---

## 7. The selection overlay

One window per monitor, borderless, positioned exactly over that monitor.

### It has to be above the menu bar

Tauri's always-on-top is not high enough: on macOS the menu bar sits above it.
`raise_above_menu_bar` reaches the underlying `NSWindow` through `objc2` and
sets its level to 1000, which is `NSScreenSaverWindowLevel`. The menu bar is 24
and the dock is 20.

The function returns the level it actually set, so `npm run selfcheck` can prove
it took rather than assume it.

### Esc is claimed globally while it is open

Because the overlay covers the menu bar, the tray icon cannot be reached while
it is open. If the page were to stop responding, the user would be stuck looking
at a frozen picture of their own screen.

So for exactly as long as an overlay exists, ScreenX registers Esc as a global
hotkey, and releases it the moment the overlay closes. This works even if the
web view never takes keyboard focus.

### The window highlight

The page receives the list of windows for its monitor, already clipped to that
monitor and rebased into its local coordinates, front-most first. Hit testing is
therefore just "the first rectangle in the list that contains the point".

The highlight waits for the pointer to settle, which is what
`window_highlight_delay_ms` controls:

- Moving the pointer clears any pending timer.
- If the pointer is still inside the window that is already highlighted, nothing
  is reset. Without that check the highlight flickers while a hand settles.
- When the timer fires, the position is re-tested before anything lights up.

A drag longer than 4 pixels drops the highlight, so dragging always wins. Held
Ctrl or Cmd suppresses highlighting for that movement.

### It never reports twice

A `done` flag guards `finish()` and `cancel()`. A stray mouse-up after the
decision, or Esc arriving from both the page and the global hotkey, must not
produce a second capture. There is a test for exactly this.

---

## 8. The editor

The capture lives on an offscreen canvas called `base`. Annotations are plain
JavaScript objects in a `shapes` array, redrawn from scratch on every frame.

This makes undo trivial: a history entry is `{ base, shapes }`, and undo is
restoring an earlier one. Because `base` is replaced rather than mutated by the
destructive tools, old history entries keep pointing at the old canvas and stay
valid.

### Shapes

Every shape has `type`, `stroke` and `lineWidth`. Most also have `x1, y1, x2,
y2`. Freehand has `points`. Text has `x, y, text, fontSize`. Step has `x, y,
radius, number`.

`shapeBounds()` turns any shape into a rectangle, and everything else — drawing,
hit testing, the selection outline — works from that.

### Destructive tools

`crop` and `cutout` rewrite `base` instead of adding a shape. Both flatten the
current annotations into the new base first, then clear the shapes array. From
the user's point of view the annotations are still there; they are just baked in
and no longer editable.

`cutout` decides its direction from the shape of the drag: wider than tall
removes a full-height column, taller than wide removes a full-width row. It
refuses a cut that would leave less than 2 pixels.

### Blur

**WKWebView does not implement `ctx.filter`.** Assigning to it appears to
succeed — the property takes the value — but nothing is filtered. A feature
check written as `typeof ctx.filter === 'string'` correctly returns false, so
the original code fell back to pixelate without saying so.

`ui/blur.js` therefore does it by hand: repeated halving with bilinear
filtering, then the same steps back up. This approximates a Gaussian blur,
needs no per-pixel loop, and runs on the same accelerated `drawImage` path as
everything else.

This is a redaction tool, so it has to destroy information rather than merely
soften it. Every halving throws pixels away permanently. Do not replace it with
a canvas filter.

`npm run selfcheck` asserts it on a hard black-and-white edge: mid greys must
appear either side of the seam, and the far edges must stay black and white.
That second half is what separates a blur from a flat grey wash.

---

## 9. Settings

One JSON file:

- macOS: `~/Library/Application Support/ScreenX/settings.json`
- Windows: `%APPDATA%\ScreenX\settings.json`

Held in memory behind a `OnceLock<Mutex<Settings>>`, written on every change.

Every struct carries `#[serde(default)]` and `#[serde(rename_all = "camelCase")]`.
That means an absent key gets its default and an unknown key is ignored, so a
file written by an older or newer version still loads. The settings file from
the previous Electron version loads into this one without any migration code.

To add a setting: add the field, add it to `Default`, add a control to
`ui/settings.html`, and read and write it in `fill()` and `collect()` in
`ui/settings.js`.

`auto_increment_number` is bumped by `advance_counter()` after every save, which
writes the file. It is the one setting the program changes on its own.

---

## 10. File names

`naming.rs` expands a pattern into a file name stem.

The token table is ordered longest-first, so `%mon2` is never read as `%mo`
followed by the letters `n2`. A token may be followed by a width in braces:
`%i{4}`.

Three deliberate behaviours:

- **An unknown token is left in the output.** `%nope` stays as `%nope`, so a
  typo is visible in the file name instead of silently disappearing.
- **Every expanded value is sanitised**, then the whole result is sanitised
  again. Characters no filesystem accepts are removed, and runs of separators
  left behind by an empty token are collapsed.
- **The result is never empty.** It falls back to `capture`.

`capture::unique_path` then appends ` (2)`, ` (3)` and so on until the path is
free, so nothing is ever overwritten.

---

## 11. Global hotkeys

Stored as strings such as `Control+Shift+Q` and parsed by
`global-hotkey`'s parser, which accepts modifiers `Control`/`Ctrl`,
`Command`/`Cmd`/`Super`, `Alt`/`Option` and `Shift`, and keys in either form
(`Q` or `KeyQ`, `4` or `Digit4`, `F5`, `Space`).

**Modifiers are stored literally, and this matters.** An earlier version stored
`CommandOrControl`, which the parser resolves to Command on macOS. Pressing
Control+Shift+Q registered Command+Shift+Q, and the shortcut the user pressed
did nothing. The settings page now records `event.ctrlKey` as `Control` and
`event.metaKey` as `Command`, and there is no remapping layer left to get wrong.

The page also refuses a combination with no modifier, because a global hotkey
takes that key away from every application on the system.

`register_hotkeys` returns the accelerators the system refused, and the settings
page reports them after saving.

---

## 12. Building and releasing

```sh
npm install          # the Tauri CLI only
npm run dev          # debug build with a live web view
npm run build        # release build and installers
```

Installers go to `src-tauri/target/release/bundle/`. The bare binary is left at
`src-tauri/target/release/screenx.exe`, and on Windows that is shipped too — the
release workflow attaches it as the portable download, because the bundler only
publishes installers.

The release profile uses `opt-level = 3` rather than `opt-level = "z"`. Capture
and PNG encoding are the hot paths, and the size win comes from LTO and
stripping instead. `panic = "abort"` was removed deliberately: turning any panic
in a web view callback into a hard crash is a bad trade for a few kilobytes.

### Timings

Measured on an Intel Mac against a 3584 × 2240 Retina display:

| | Roughly |
| --- | --- |
| Full-screen capture | 100–250 ms |
| PNG encode, full screen | ~35 ms |
| Window list with positions | under 1 ms |

Orders of magnitude, not benchmarks. What matters is that the window list is
fast enough to fetch before the overlay opens, and that a capture stays well
under half a second.

Artifact sizes are deliberately not recorded here. They move with every release
and with the build shape — a universal macOS binary is roughly twice a
single-arch one — so a written figure is stale almost immediately. The release
assets are the source of truth.

### Windows

The Windows build has to be produced on Windows. There is no cross-compilation
path from macOS for the MSVC target, so it needs the MSVC toolchain and the
Visual Studio Build Tools VCTools workload.

It runs. Capture, region select, the editor and saving were all exercised on
Windows 11 in 0.2.1, and the self-check passes there. The first attempt
deadlocked; section 16 is why. What is still unverified is more than one
monitor, and monitors at different scale factors in particular — see the
`ponytail:` note on `capture::to_dip`.

The binary is self-contained. Its only imports are Windows system DLLs, and the
frontend is compiled in, so `screenx.exe` runs from anywhere with nothing beside
it. That is why the release ships it as a portable download next to the
installer. The installer earns its place for the Start menu entry and the
Apps & features uninstall entry, not for the WebView2 Runtime, which Windows 11
includes.

### Signing

Neither build is signed, and neither will be. A Developer ID costs 99 USD a
year and this project does not carry that cost, so the release artefacts stay
unsigned and unnotarised. That decision has three consequences worth knowing
before someone files it as a bug.

**Control-click → Open no longer works on macOS.** It is the bypass everyone
learned, and macOS 15 removed it. On 15 and later the first launch of a
quarantined unsigned app offers only Done, and the approval moved to System
Settings → Privacy & Security → Open Anyway — several clicks deep, in a place
nobody thinks to look.

**Only downloaded copies are affected.** Gatekeeper's hard block needs the
`com.apple.quarantine` attribute, which the browser sets and a local build
never has. `npm run build` produces a bundle that launches immediately, and
`xattr -dr com.apple.quarantine` puts a downloaded one in the same state.

**Homebrew is the way around all of it.** `brew install --cask` does not set
the quarantine attribute, so a cask install starts with no warning and no trip
through System Settings. `Casks/screenx.rb` lives in this repository rather
than a `homebrew-tap` one; the name of a tap repository has to begin with
`homebrew-`, so the short `brew install --cask user/tap/screenx` form would
need a second repository, and bumping a cask there from this repository's
release workflow would need a personal access token — a credential outliving
its one job. The cost of keeping it here is the explicit URL users pass to
`brew tap`. The release workflow rewrites the version and sha256 lines after a
non-prerelease tag builds, and pushes that to master with the `GITHUB_TOKEN`
it already holds. This is why the README leads with Homebrew and treats the
.dmg as the fallback.

**Tauri does not sign at all by default**, which matters beyond Gatekeeper.
TCC keys the Screen Recording grant to code identity, and with no signature it
falls back to the binary's hash — so every rebuild looks like a new app and the
permission has to be granted again. Ad-hoc signing is free and fixes that:

```sh
codesign --force --deep --sign - ScreenX.app
```

This gives the bundle a stable `com.screenx.app` identity and the TCC grant
survives rebuilds. Set `APPLE_SIGNING_IDENTITY: '-'` in the `tauri-action` env
block to have releases do the same. It does nothing for the quarantine warning
— ad-hoc is not a trusted signature — but it stops the permission churn.

---

## 13. Tests

```sh
npm test           # both suites below
npm run test:ui    # node --test, the web view logic
npm run test:rust  # cargo test
npm run selfcheck  # debug binary, needs a real screen and web view
```

**Rust (23 tests)** — file name patterns, settings round-tripping, rectangle
maths, clipping windows to a monitor, scale-factor cropping, DIP normalisation
and image encoding.

**Web view logic (28 tests)** — the real `ui/overlay.js` and `ui/editor.js` are
loaded into a Node `vm` context with a stubbed DOM, and driven with synthetic
events. This covers the dwell timing, front-most hit testing, drag beating a
highlight, reporting exactly one outcome, the cut-out direction rules, and
history across destructive edits.

macOS WKWebView has no WebDriver support, so this stub is the only automated
route to that code. Values coming back out of the `vm` realm must be normalised
(the harnesses do it with a JSON round trip) or `deepStrictEqual` rejects them
on prototype identity.

**Self-check** — the things a stub cannot answer, run inside the real engine:

- a capture crops, is named, is written and reads back at the right size
- the `screenx:` scheme loads an image in a web view
- the overlay window really reaches level 1000
- blur genuinely blurs
- the editor's canvas can be read back for saving

The self-check has found two real bugs so far: the missing `ctx.filter`, and
the tainted canvas that broke saving.

---

## 14. Things that will bite you

**macOS reports only visible windows.** A minimised or fully covered window is
not in the list and cannot be highlighted. This is a system limit. It is also
correct behaviour for this feature: you cannot point at a window you cannot see.

**Window creation belongs on the main thread, and a command must not already be
on it.** A plain `#[tauri::command]` runs inline on the thread that received the
IPC message, which is not a worker thread; on Windows it is inside WebView2's
message callback. Any command that opens or closes a window therefore needs
`#[tauri::command(async)]`. `on_main` is not a defence on its own —
`run_on_main_thread` executes the closure immediately if the caller is already
the main thread. See section 16.

**The `screenx:` scheme needs its CORS header.** See section 4. Removing it
breaks saving in a way that looks like an operating system permission problem.

**Do not reintroduce `CommandOrControl`.** See section 11.

**Do not replace `ui/blur.js` with `ctx.filter`.** See section 8.

**Free frames when you close a window.** See section 4.

**`reference/` must never be committed or copied from.** It holds third-party
source kept for behavioural comparison only.

---

## 15. History

Version 0.1 was Electron. It worked, but the bundle ran to hundreds of
megabytes, and the region tool could not highlight windows usefully: getting
window positions needed a helper subprocess costing roughly half a second per
call, far too slow to run while the pointer moves.

Version 0.2 is this rewrite. `xcap` returns the same information in-process in
under a millisecond — three orders of magnitude — which is what makes
hover-to-highlight possible at all. The Electron
version is in the git history up to commit `d2db785`.

GIF recording existed in 0.1 and is not in 0.2. It is paused, not abandoned.
The parts of it that were hard — frame pacing and palette handling — are in the
history if they are wanted back.

The code was written on macOS and Windows was not run at all until 0.2.1. The
first thing tried on Windows deadlocked; section 16 is what that turned out to
be.

---

## 16. WebView2 reentrancy, and the deadlock it caused

Everything in 0.2.0 worked on macOS. On Windows, choosing an area froze the
process: no window, no output, "Not Responding" in the title bar, and no CPU
use. Nothing had been logged, so the hang was somewhere inside
`region_selected` before its first `println!`.

The cause was an assumption that reads as obviously true and is not:

```rust
/// Commands run on a worker thread; window creation belongs on the main one.
fn on_main(app: &AppHandle, image: RgbaImage, title: String) {
```

Two facts break it.

**A synchronous command does not run on a worker thread.** In
`tauri-macros/src/command/wrapper.rs` the default is `ExecutionContext::Blocking`,
which runs the body inline on the thread that received the IPC message. For
WebView2 that thread is inside the `WebMessageReceived` callback of the webview
that sent the message.

**`run_on_main_thread` does not always defer.** `send_user_message` in
`tauri-runtime-wry/src/lib.rs` checks whether the caller is already the main
thread, and if so calls `handle_user_message` immediately instead of posting to
the event loop. So does `WebviewWindow::close()`.

Put together, `region_selected` did two things inside the overlay's own
WebView2 callback:

1. `close_overlays()` destroyed the `ICoreWebView2Controller` of the webview
   that was dispatching the message.
2. `open_editor()` reached `WebViewBuilder::build()`, which calls
   `webview2_com::wait_with_pump` — a nested Win32 message pump.

Microsoft's WebView2 documentation forbids both: you may not run a nested
message loop inside an event handler, and you may not tear the webview down
from within its own callback. Doing either deadlocks the browser process.

WKWebView tolerates both, which is why macOS never showed it. The bug was not
in the capture code, the coordinate handling, or the `screenx:` scheme — all of
which were the obvious suspects, and all of which were correct.

The fix is `#[tauri::command(async)]` on every command that opens or closes a
window: `region_selected`, `window_selected`, `region_cancelled` and
`close_window`. On a non-async function that attribute moves the body to the
runtime's thread pool, after which `close()` and `run_on_main_thread` both take
their posting path and the work happens on the event loop instead of inside a
webview callback.

The self-check asserts this directly. `selfcheck_report` is invoked from a real
webview and compares its thread against the one recorded in `MAIN_THREAD` at
startup, so dropping the attribute fails a check rather than hanging a user.
