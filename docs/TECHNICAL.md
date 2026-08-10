# ScreenX — technical documentation

How the program is put together, and why it is put together that way.

For using the program, see [`../README.md`](../README.md).
For a code map aimed at AI coding assistants, see [`../AGENTS.md`](../AGENTS.md).

---

## Contents

1. [What it is built on](#1-what-it-is-built-on)
2. [Where everything lives](#2-where-everything-lives)
3. [One window, three modes](#3-one-window-three-modes)
4. [Coordinate systems](#4-coordinate-systems)
5. [Reading the screen](#5-reading-the-screen)
6. [The selection overlay](#6-the-selection-overlay)
7. [The editor](#7-the-editor)
8. [Drawing a shape twice](#8-drawing-a-shape-twice)
9. [Memory](#9-memory)
10. [Settings](#10-settings)
11. [File names](#11-file-names)
12. [Global hotkeys](#12-global-hotkeys)
13. [Building](#13-building)
14. [Tests](#14-tests)
15. [Things that will bite you](#15-things-that-will-bite-you)
16. [Why the webview build was replaced](#16-why-the-webview-build-was-replaced)

---

## 1. What it is built on

Rust throughout. `eframe`/`egui` draws the interface, `xcap` enumerates monitors
and windows, `image` encodes, `tray-icon` and `global-hotkey` provide the menu
bar icon and the shortcuts, `arboard` handles the clipboard.

`eframe` is configured with the `glow` backend rather than `wgpu`. The program
draws one full-screen image and a rectangle. A GPU backend would allocate a
device, a swapchain and driver-side buffers for work a blit already does, and
memory is the constraint that caused this design in the first place.

There is no HTML, no JavaScript, no bundler and no build step for the interface.
There is also no network code anywhere, and none may be added.

`imageproc` was added for shape rasterisation and then removed again: the
geometry it was wanted for is a few dozen lines in `render.rs`, and a dependency
that saves fifty lines is not worth its compile time. Text uses `ab_glyph` with
the font `egui` already embeds, so no second font file ships.

---

## 2. Where everything lives

Two crates.

`core/` reads the screen, works out filenames and holds the settings file. It
has no windowing dependency at all, which is what let the interface be replaced
without touching it.

`app/` draws. It depends on `core` and on `eframe`.

The split is not decoration. When the webview interface was replaced, `core/`
moved across whole — the same 23 tests, unchanged — and only the drawing was
rewritten.

`src-tauri/` and `ui/` are the previous build. They are kept until the native
one has seen enough use, then they go.

---

## 3. One window, three modes

There are two programs. `screenx` stays running and draws nothing at all;
`screenx-capture` is spawned per screenshot, owns the one window, and exits when
the editor closes. See [Splitting the process](#splitting-the-process) in
section 13 for why, and for what it costs.

Inside the worker there is one event loop and one window, which is not a
preference: a winit event loop can only be run once per process on macOS, so
`eframe::run_native` cannot be called twice. The window is a state the worker
moves through, once:

```
spawn ──▶ Selecting ──drag──▶ Editing ──save/close──▶ exit
              │                                         ▲
              └──────────── Esc ────────────────────────┘
```

`Mode` in `main.rs` holds exactly one of those. The window covers the screen in
`Selecting` and is an ordinary titled window in `Editing`. `Idle` now means only
that the work is finished and the process is on its way out.

This is the shape the webview build spent a long session trying to reach by
pre-warming its overlay, because standing up a WKWebView per capture was the
largest single cost in getting the overlay on screen. Here it is free: showing a
window that already exists costs nothing, and the only per-capture work is
uploading one texture.

Both of the awkward consequences this shape used to have were paid for by the
single-process build and are now gone, which is worth recording because they
return with any merge.

**The close button had to be cancelled.** One viewport meant the editor's close
request ended the process and took the menu bar icon with it. Now it ends only
the worker.

**Going idle had to shrink the window.** A hidden window keeps a framebuffer the
size it was last shown at, so an editor left at full screen size held it for as
long as the program ran. The worker has no idle state to shrink into.

---

## 4. Coordinate systems

Two units meet, and mixing them is the oldest bug in this codebase.

- **Points** — what windows are positioned and sized in, and what egui lays out
  in. Monitor bounds, selection rectangles and window rectangles are points.
- **Physical pixels** — what a captured image is made of. On a Retina panel
  there are two per point.

`capture::crop_to_rect` is the only place a scale factor is applied. Nothing
else may touch one.

`xcap` is not consistent: it reports macOS geometry in points and Windows
geometry in physical pixels. `capture::to_dip` normalises that once, at the
boundary, so nothing downstream has to care which platform it is on.

The editor is the third place this matters. A capture is physical pixels and the
editor lays out in points, so drawing it one for one made every screenshot from
a Retina panel appear at twice its real size — it looked like the editor had
zoomed in for no reason. The density travels with the image and the editor
divides by it.

---

## 5. Reading the screen

On Windows, `xcap` does it.

On macOS it does not, and the reason is worth recording. `xcap`'s
`Monitor::capture_image` calls `CGWindowListCreateImage`, which composites a
*window list*. The menu bar background, the clock and every status item belong
to WindowServer and SystemUIServer. They do not come back through that list, so
a full screen capture kept the menu titles and lost the bar around them. It
reads exactly like a Screen Recording permission fault and is not one.

`capture::display_image` calls `CGDisplayCreateImage` instead, which reads the
display rather than a window list. Rows are copied one at a time because the
framebuffer pads them to the hardware's alignment, and the alpha byte is set
rather than trusted because the pixel format skips it.

`CGDisplayCreateImage` is deprecated in favour of ScreenCaptureKit, which is
async and several hundred lines of bridging. It still works, so the port waits
until it does not. This is the one piece of the program with a known expiry
date.

### The permission is asked for, not discovered

`CGDisplayCreateImage` does not fail without the Screen Recording grant. It
succeeds and returns the desktop picture with every window missing — so an
unauthorised ScreenX saved a photograph of the wallpaper and looked broken
rather than unauthorised. The overlay showed the same thing, which is the
clearest way to see it: a dimmed desktop with no windows on it.

`display_image` therefore calls `CGPreflightScreenCaptureAccess` first, and
`CGRequestScreenCaptureAccess` when that is false. The request adds ScreenX to
the Screen Recording list and shows the system prompt; the grant only reaches
the process on its next launch, so the run that asks returns an error instead
of an image. There is nothing useful it could return.

This is easy to test wrongly, and it cost most of a session. Started from a
terminal the worker inherits the *terminal's* grant, so an unauthorised build
takes correct screenshots and every check passes. The failure only appears under
`open -a` or a double click, where no granted parent is in the chain.

The denial has a specific look. It is not a black or empty screen: the overlay
comes up over a dimmed **desktop picture with no windows on it**, which is easy
to read as a rendering fault instead of a permission one.

A grant also does not follow a build. TCC keys on the path and the code hash, so
a rebuild, a move, or an install into `/Applications` is a new identity and
prompts again. An older bundle that still holds the grant says nothing about the
new one — testing against it is the second way to get a false pass.

---

## 6. The selection overlay

The capture is read first, uploaded as a texture, and the overlay draws that
frozen image. The screen underneath can change while the overlay is open; the
screenshot cannot.

Two macOS details, both of which were bugs:

**Window level.** An always-on-top window still sits below the menu bar, which
is a higher level of its own. The overlay is set to 1000,
`NSScreenSaverWindowLevel`, which clears both the menu bar (24) and the dock
(20).

**Activation.** ScreenX is an accessory app, so it is outside the normal
activation order. Ordering the overlay front does not make it *key*, and a
window that is not key receives no mouse or keyboard events: it arrived dimmed,
with an arrow cursor instead of a crosshair, and its first click was spent
activating rather than starting a selection. `activateIgnoringOtherApps:` is
used rather than the modern `activate()`, which is cooperative on macOS 14 and
later — the system defers it while another application still owns activation,
which is the same bug with a delay in front of it.

Escape and right click both cancel. The overlay covers the menu bar, so there
has to be a way out that does not depend on reaching anything else on screen.

The texture is uploaded at the size the screen can draw rather than the size of
the capture. On a Retina panel that is a quarter of the pixels, paid for three
times over — in the source buffer, in egui's copy of it, and in the texture
itself. The selection is still cropped from the untouched capture, so nothing
about the saved file changes.

---

## 7. The editor

Annotations are shapes in image coordinates drawn over the capture, not pixels
in it, so they stay editable. Four tools rewrite pixels instead: crop, cut out,
blur and pixelate. Those flatten the live shapes into the image first, or a crop
would move the capture out from under its annotations.

**Blur and pixelate destroy pixels.** They are redaction tools; softening is not
good enough. Blur shrinks the region hard and stretches it back, so the detail
is thrown away rather than smeared. Pixelate averages each block flat.

Testing that needs care. A gradient stays a gradient under any blur, so a test
on one proves nothing — the fixture is a checkerboard, which is the
high-frequency detail redaction actually has to remove. Text is the real case.

**Pointer input is clamped to the image.** The pointer keeps reporting once it
leaves the canvas, and every tool reads its coordinates from one place.
Unclamped, dragging 20 px past an edge to cut out the last 20 px measured all 40
and took 20 px of real image with it — and the trailing edge was already
clamped, which made the tool look inconsistent rather than broken.

**Fitting shrinks but never enlarges.** Growing an image is the zoom control's
job. An automatic enlargement on open reads as the editor having zoomed into the
screenshot for no reason.

---

## 8. Drawing a shape twice

Every shape is rasterised twice: once by `render::draw_on_screen` with egui, and
once by `render::draw_shapes_into` by hand, into the image that gets saved.

The duplication is deliberate. The alternative is reading pixels back off the
GPU, which is slow and on some drivers does not work at all — and it is the sort
of thing that works on the development machine and fails on a user's.

The cost is that the two must agree. A shape added to one and not the other
makes the editor lie about the result, which is worse than a shape that is
missing from both. Both functions carry a comment saying so.

---

## 9. Memory

This is the constraint the previous build failed, so it is measured rather than
asserted.

`--memcheck WIDTH HEIGHT SECONDS [--editor]` stands the overlay or the editor up
on a synthetic capture and holds it, so `ps` can sample the process. It needs no
Screen Recording permission, which is what made the claim unverifiable from a
shell before.

At 3584x2240, roughly a Retina laptop panel:

| State | Native | Previous webview build |
| --- | --- | --- |
| Idle | tens of MB | tens of MB, rising after each capture |
| Selection overlay | ~130 MB | ~630 MB across four processes |
| Whole screen in the editor | ~200 MB | — |

The overlay figure came down from about 165 MB when the texture was sized to
what the screen can draw instead of to the capture.

Two rules keep it there:

**History is bounded by bytes, not steps.** Adding a shape shares the image
through an `Arc`, so it costs a `Vec` entry. A crop or a blur allocates a new
image. A step count alone would have let twenty-four destructive edits on a full
screen capture hold most of a gigabyte.

**Idle releases explicitly.** Dropping a texture handle only queues the release,
so going idle calls `forget_all_images` and shrinks the window.

Measured across an open, close and reopen, the footprint does not grow between
cycles and comes back down afterwards. What can look like the editor being held
is memory that was freed and handed straight back out by the allocator.

---

## 10. Settings

One JSON file, read fresh on every capture so an edit takes effect without a
restart. Unknown keys are ignored and absent keys use their default, so a file
from an older version still loads.

There is no settings window. The menu bar icon opens the file in whatever the
desktop uses for it. The file is small and documented in the README, so a second
interface would be a thing to build and keep in step for no gain over the editor
the user already has.

---

## 11. File names

`core/naming.rs` expands a pattern such as `ScreenX_%y-%mo-%d_%h-%mi-%s`.

The token table is matched **longest first**. `%m` and `%mo` and `%mi` all start
the same way, so a shortest-first match would turn every `%mo` into a month
followed by a stray `o`.

`capture::unique_path` adds a numeric suffix rather than overwriting. Two
screenshots in the same second are common.

---

## 12. Global hotkeys

`global-hotkey` registers them. Modifiers are parsed literally: `Control` means
Control on every platform.

A "CommandOrControl" convenience that resolves to Command on macOS was tried in
an earlier build and removed. Someone who pressed Control+Shift+Q got
Command+Shift+Q registered, and their shortcut did nothing with no error to
explain it.

Modifier-less combinations are refused. A global hotkey takes that key from
every application on the system.

The shortcuts never apply `captureDelayMs`. Only the delayed items in the menu
bar icon do. The delay exists so a menu can be opened before the screen is read;
charging every capture for it made the ordinary one feel slow.

---

## 13. Building

```sh
cargo run -p screenx --bin screenx
./app/bundle.sh          # macOS .app, ad-hoc signed
```

`bundle.sh` writes the smallest bundle macOS will accept. Four parts of it
matter:

**Both binaries.** `Contents/MacOS/` holds `screenx` and `screenx-capture`, and
the release ships them together. The listener resolves the worker from
`current_exe`, so a bundle with only the listener in it has a working tray icon
and two shortcuts that do nothing at all — which reads as a refused Screen
Recording permission and is not one. The same applies to the Windows archive,
which is why it is an archive rather than a bare `.exe`.

Placing the worker beside the listener is also what gives it an identity.
`CFBundleGetMainBundle` walks up from the executable path, so a helper directly
inside `Contents/MacOS/` gets the enclosing bundle's `Info.plist` — its
`LSUIElement`, and its name in the permission prompt. The macOS dialog says
"ScreenX", not "screenx-capture", and one grant covers both processes.

**Ad-hoc signing.** Screen Recording permission is granted to a bundle with a
stable code signature, not to a loose binary — run from a terminal, the
permission is attributed to the terminal instead, which is the trap when testing
this. Ad-hoc signing keeps the identity stable across rebuilds so the grant is
not given again every time. The worker is signed first and the bundle second:
it is nested code, and sealing the bundle over an unsigned nested executable
leaves it unsigned. `--deep` is deprecated for exactly this.

**`LSUIElement`.** A capture tool belongs in the menu bar, not the Dock. It is
also the condition the overlay has to work under; see the activation note in
section 6.

**The icon.** `Contents/Resources/icon.icns` and a `CFBundleIconFile` key, both
written by `bundle.sh`. Nothing generates an `.icns`, so the file is the one in
`src-tauri/icons/` — the same folder `app/build.rs` takes the Windows `.ico`
from. Without them the bundle gets the blank default document icon in Finder,
the `.dmg` window and Applications, which reads as a broken build rather than a
missing file. `LSUIElement` hides the Dock icon, not this one.

Windows has no equivalent script. It does not need one: the release job builds
the two executables, puts them in `ScreenX_windows.zip` so they cannot be
downloaded apart, and nothing about Screen Recording permission applies. What
Windows does need is `app/build.rs`, which links the
`RT_GROUP_ICON` resource that Explorer, Alt-Tab and the taskbar read out of the
binary. `tauri-build` supplied that for the webview build and the native one had
no replacement, so the executable shipped with a blank icon. winit falls back to
that resource for the window icon too, so one build script covers both.

### What Windows needed that macOS did not

The native build has now been run on Windows 11 across a two-monitor desktop.
Four things behaved differently enough to be worth recording, because all four
present as something other than what they are.

**A hidden window stops the program.** Windows does not paint a window it
considers hidden, winit raises no `RedrawRequested` for a window it does not
paint, and `eframe::App::update` runs only on a redraw. Hiding the window
between captures therefore stopped the app draining its own request channel. The
shortcut still fired and the system still delivered it — nothing was listening.
It looked like the shortcut had been unregistered, and it was not: probing
`RegisterHotKey` from outside returned `ERROR_HOTKEY_ALREADY_REGISTERED`, which
proved the app still held it. So on Windows the window is parked instead: one
point square, click-through, in the corner of the primary display, and still
nominally visible. `ViewportBuilder::with_visible(false)` does not hold there
either, which is why the idle state is applied from inside the first frame.

**Focus is taken back by whatever reorders the window next.** winit applies a
window level change as an asynchronous `SetWindowPos` carrying `SWP_NOACTIVATE`.
Sent after a focus request it lands afterwards and undoes it, so the editor
opened behind everything and stayed half-buried until the user clicked another
application, which forced Windows to recompute the Z-order. The focus request
now goes last in both the overlay and editor paths.

**Window styles set behind winit's back do not survive.** `apply_diff`
recomputes the entire extended style from winit's own flags and writes it with
`SetWindowLongW`, erasing any bit it does not model. Opening the editor changes
four flags, so a `WS_EX_APPWINDOW` taskbar button was overwritten every time.
`ITaskbarList::AddTab`/`DeleteTab` is used instead, which is what winit itself
uses and is independent of the style.

**The tray menu was inert.** `tray-icon` 0.19 shows the menu with
`TrackPopupMenu` on its own hidden window, never calls
`attach_menu_subclass_for_hwnd`, and carries no `WM_COMMAND` arm — so every
selection fell through to `DefWindowProcW`. Quit, Open Screenshots Folder and
Settings had never worked on Windows, and the only way to stop the program was
Task Manager. 0.24 installs the subclass. That is the minimum version.

### Splitting the process

The idle cost below is a GL context, and nothing frees a GL context short of
process exit. So the program was split in two: `screenx` holds the tray, the
shortcuts and the settings and links no renderer at all, and `screenx-capture`
is spawned for one screenshot and exits when the editor closes.

Measured on the same machine, alternating between the two builds so that
ordering and file-cache effects cancel:

| | idle | overlay, warm |
| --- | ---: | ---: |
| One process | 42.9 MB | 159 ms |
| Split | 1.2 MB | 267 ms |

So the trade is about 41 MB of permanently resident memory against about 110 ms
on every screenshot.

A cold first capture measured about 1.2 s once, taken immediately after a build.
It is one sample, it does not match how the program feels in use, and it is
recorded here only so nobody repeats it as fact. Measure it after a reboot
before believing it.

The worker reads the screen **before it creates any window**. On this shape that
is a correctness rule rather than an optimisation: anything the worker draws
would otherwise appear in its own photograph. The alternative — having the
listener capture and hand the image over — keeps the freeze instant but puts
full-screen pixel buffers back in the process that is supposed to stay small,
which is the thing the split exists to avoid.

Two pieces of state had to move out of memory and into the settings file,
because the process that held them no longer outlives one screenshot: where the
editor window was last left, and the auto-increment counter, which was already
there. A second overlay is prevented by the listener holding the child handle
rather than by a `Mode`, since the state now lives in another process.

### The overlay stops one point short of the screen

Every capture flickered the whole panel twice: once as the overlay appeared, and
once roughly 27 seconds after it was dismissed. The second one was the difficult
part, because by then the capture process had been gone for half a minute and
nothing obvious connected the two.

A window whose size matches the display exactly is the one thing DWM will hand
an independent flip. Entering that path and leaving it re-syncs the panel.
Making the overlay one point shorter takes it out of the path and both flickers
stop. `Overlay::reaching_the_edge` snaps a drag that reaches the bottom edge
back out to the full height, so the row that costs is not actually lost.

Three other explanations were measured and rejected before this one, recorded
here because each looked plausible and each was wrong:

- **Not a GPU clock transition.** The memory clock does collapse from 10501 MHz
  to 405 MHz after a capture, which looked convincing. But it finishes about
  four seconds after the process exits, and the flicker is at twenty-seven — the
  clocks were flat at P8/405 for thirteen seconds either side of it.
- **Not VRR.** The flicker survived turning it off. It was quieter, not absent.
- **Not a mode or topology change.** Resolution, refresh rate and the adapter
  list were byte-identical across 780 samples spanning five captures. A virtual
  display adapter was present throughout and never re-enumerated.

The lesson worth keeping is about the shape of the search rather than the
answer. A symptom half a minute downstream of its cause invites a timer-shaped
theory, and two of the three rejected explanations were chosen because their
timing sounded about right rather than because anything had been measured.

### Idle memory on Windows

Private working set starts around 42 MB, rises while the overlay is up, and
settles roughly 30 MB above launch. It then stays flat over repeated captures,
so it is not a leak.

It is also not any of the obvious candidates, and each was measured rather than
argued. Opening no editor at all still retains it, so it is not the editor.
Running the capture three times in a process with no window returns the buffer
completely, so it is not the image or the allocator. Shrinking the overlay
texture from 29.5 MB to 3.3 MB does not move it, so it is not the texture.
Shrinking the overlay window does not move it either, so it is not the
framebuffer. What is left is one-time renderer and driver warm-up on the first
genuinely rendered frame.

The consequence for anyone tempted to reduce it: downscaling the overlay buys
nothing, and that has been tried. The only approach with a real prospect is
holding no GL context while idle, which the parking rule above forbids inside a
single process.

---

## 14. Tests

Two `cargo test` suites: 23 in `core/`, and 28 in `app/` — 25 for the capture
worker and 3 for the listener.

`core/` covers filename patterns, settings round-trips, rectangle maths,
clipping, cropping, scale normalisation and encoding.

`app/` covers the edits that rewrite pixels — including that a cut out at the
leading edge does not eat 20 px of real image, and that blur destroys
high-frequency detail — the overlay's coordinate reporting, the editor's
history: that adding a shape does not copy the image, that history stays inside
its byte budget, and that undo walks back across a destructive edit, and where
the editor window opens, including that a position on a monitor that is no
longer attached is not reused.

The previous build needed a third layer, because macOS WKWebView has no
WebDriver support and the only way to test the real engine was to run it. The
things it existed to check — a URI scheme, a tainted canvas, a missing
`ctx.filter` — do not exist here.

What is still not covered: anything needing a real screen. Window level, key
focus, activation and the tray icon are checked by running the program.

---

## 15. Things that will bite you

These are the invariants in `AGENTS.md`, stated as prose. Every one is a bug
that happened.

1. Points and physical pixels are different units, and `crop_to_rect` is the
   only converter.
2. `xcap` reports macOS geometry in points and Windows geometry in pixels.
3. Hotkey modifiers are literal, and a modifier-less shortcut is refused.
4. macOS captures must read the framebuffer, not a window list, or the menu bar
   goes missing.
5. A shape is drawn twice and both must agree.
6. Closing the editor must not take the tray with it. The split satisfies this
   by construction; a merge back brings the `CancelClose` with it.
7. Retired by the split: going idle had to shrink the window as well as hide
   it. Reinstate it if anything makes the worker persist.
8. The capture's density travels with it into the editor.
9. Fitting shrinks, never enlarges.
10. Editor history is bounded by bytes, not steps.
11. Blur and pixelate must destroy pixels; test against high-frequency detail.
12. Pointer input is clamped to the image.
13. An accessory app must activate, not merely take focus.
14. On Windows a window is never hidden and then expected to run, or the event
    loop stops and the shortcut goes unanswered.
14a. The overlay is one point short of the screen. An exact match takes DWM's
    independent flip path and re-syncs the whole panel.
15. `ViewportCommand::Focus` is sent last; anything reordering the window
    afterwards carries `SWP_NOACTIVATE` and cancels it.
16. Window styles are not set behind winit's back; it rewrites the extended
    style wholesale.
17. The tray menu needs `tray-icon` 0.24 or newer, or no menu item does
    anything on Windows.
18. Retired by the split: a quit must not be swallowed by the editor's
    `CancelClose`. It returns with any merge back.
19. A release ships both programs or neither. The listener resolves the worker
    from its own path, and a package carrying only the listener has a tray icon
    and two shortcuts that do nothing.
20. macOS refuses a screen capture silently. Without the Screen Recording grant
    `CGDisplayCreateImage` returns the desktop picture with every window
    missing, so the permission is asked for before the read rather than
    diagnosed after it.

---

## 16. Why the webview build was replaced

The first build was Tauri: Rust for capture, three HTML pages for the interface.
It worked. It was replaced anyway, and the reasoning is recorded because it
applies to the next decision of this shape.

Most of its hard-won rules existed to survive the Rust-to-webview boundary
rather than to take a screenshot. Pixels could not cross IPC as text, so they
were parked in a map and served over a custom URI scheme — which then needed a
CORS header *and* `crossOrigin` on the image, or the canvas tainted and saving
threw. WKWebView does not implement `ctx.filter` but pretends to assign it, so
blur had to be hand-written. WebView2 deadlocks if a window is opened or closed
from inside a webview callback, so every such command needed moving off that
thread. Roughly two thirds of the documented invariants came from the boundary,
not from the problem.

Then the numbers. A selection overlay cost about 630 MB across four processes
and did not come back down; three separate leaks were found and fixed and it
still did not. The same overlay is about 130 MB in one process here. Standing up
a WKWebView was also the largest single cost in getting the overlay on screen,
which is why that build ended up pre-warming a hidden webview per monitor — and
paying for a resident browser process tree to do it.

What was kept: `core/` moved across unchanged, with its tests. What was
genuinely lost: nothing that had shipped. What was rebuilt: the overlay, the
editor and the tray, which took less code than the webview versions they
replaced.

The lesson worth carrying: the interface toolkit was chosen before the shape of
the problem was clear. This program is a full-screen overlay, a drawing canvas
and OS window manipulation — the three things a webview is worst at — and its
one document-shaped surface, the settings form, was deleted rather than ported.
