# ScreenX

A screen capture and annotation tool for macOS (Intel) and Windows. Everything
stays on your machine — no uploading, no account, no network code.

Built with Tauri: a Rust core does the capture, encoding and file handling, and
three small webviews provide the selection overlay, the editor and the settings
form.

## Features

- **Capture the entire screen** — whichever display the pointer is on.
- **Capture a region or a window, from one tool.** Drag a rectangle for an
  arbitrary area, or rest the pointer on a window for a moment and it lights up
  so a single click captures exactly that window.
- **Annotation editor** — rectangles, ellipses, arrows, lines, freehand,
  highlighter, blur, pixelate, text, numbered steps, crop, and cut-out (remove a
  full-width or full-height band and close the gap), with undo and redo.
- **Settings** for the storage folder, filename patterns, global hotkeys, image
  format and the highlight delay — all in one JSON file you can also edit by hand.

GIF recording is not in this version. It was in the earlier Electron build and is
parked until the screenshot side is finished.

## Why it was rewritten

Version 0.1 was Electron. Measured on the same Intel Mac, against the same
3584×2240 Retina display:

| | 0.1 (Electron) | 0.2 (Rust + Tauri) |
| --- | --- | --- |
| macOS app bundle | 281 MB | **6.9 MB** |
| Installer | — | **3.0 MB** (dmg) |
| Full-screen capture | ~600 ms | **114–236 ms** |
| PNG encode | — | **35 ms** |
| Window list with bounds | 518 ms (helper subprocess) | **0.6 ms** |

The window list is the one that changed the feel of the product: at 0.6 ms the
region overlay can hit-test the pointer against real window rectangles on every
mouse move, which is what makes hover-to-highlight work.

## Building and running

Needs [Rust](https://rustup.rs) and Node (for the Tauri CLI only).

```sh
npm install
npm run dev      # run it
npm run build    # produce an installer
```

On Windows the build has to be run on Windows; there is no cross-compilation path
from macOS for the MSVC target.

## Default hotkeys

| Action | Shortcut |
| --- | --- |
| Capture entire screen | `Ctrl + Alt + F` |
| Capture region or window | `Ctrl + Alt + A` |

Change them under Settings → Hotkeys. Modifiers are recorded exactly as pressed:
if you press Control, Control is what gets registered, on every platform.
Combinations another application already owns are reported after saving.

**Escape always cancels a selection.** While the overlay is up it covers the menu
bar, so Escape is claimed globally for exactly as long as the overlay is on
screen, and released the moment it closes.

## Using the region tool

- **Drag** anywhere for a custom rectangle.
- **Rest the pointer on a window** for the highlight delay (400 ms by default,
  configurable, 0 for instant) and that window lights up with its title. One
  click captures it.
- **Hold Ctrl or Cmd** while moving to suppress highlighting entirely.
- **Enter** takes the whole display, **Escape** or right-click cancels.

## Editor

| Key | Action |
| --- | --- |
| `1`–`9` | switch to the first nine tools |
| `Ctrl/Cmd + Z` / `+ Shift + Z` | undo / redo |
| `Ctrl/Cmd + C` | copy image to clipboard |
| `Ctrl/Cmd + S` / `+ Shift + S` | save / save as… |
| `Delete` | delete the selected annotation |
| `Esc` | close |

Hold `Shift` while dragging a rectangle or ellipse to keep it square.

**Cut out** removes a band and butts the remaining pieces together. Drag wider
than tall to take out a full-height column; drag taller than wide to take out a
full-width row. The band to be removed is shown in red while you drag.

## Filename patterns

`ScreenX_%y-%mo-%d_%h-%mi-%s` produces `ScreenX_2026-08-07_14-32-05.png`.

| Token | Meaning |
| --- | --- |
| `%y` `%yy` | year (2026 / 26) |
| `%mo` `%mon` `%mon2` | month (08 / Aug / August) |
| `%d` | day of month |
| `%w` `%w2` | weekday (Fri / Friday) |
| `%wy` | ISO week number |
| `%h` `%h12` `%pm` | hour, 12-hour hour, AM/PM |
| `%mi` `%s` `%ms` | minute, second, millisecond |
| `%unix` | Unix timestamp |
| `%i` | auto-increment counter |
| `%ra` `%rn` `%rx` | random letters / digits / hex |
| `%guid` | random GUID |
| `%t` | window or screen title |
| `%width` `%height` | image size |
| `%un` `%cn` | user name / computer name |
| `%pn` | application name |

`%i{4}` gives `0007`, `%ra{6}` gives six random characters. Unknown tokens are
left in the name so typos are visible. Characters no filesystem accepts are
stripped, and an existing file is never overwritten — ` (2)`, ` (3)` and so on
are appended instead.

## Settings file

Everything lives in one JSON file, shown and openable from Settings → General:

- macOS: `~/Library/Application Support/ScreenX/settings.json`
- Windows: `%APPDATA%\ScreenX\settings.json`

Missing keys fall back to defaults and unknown keys are ignored, so hand-editing
it is safe.

## Platform notes

### macOS

- Grant **Screen Recording** permission under System Settings → Privacy &
  Security. Without it captures come back blank.
- macOS only reports windows that are actually visible. A minimised or fully
  covered window cannot be highlighted — bring it to the front first.
- Builds target Intel (x64) and run on Apple Silicon under Rosetta.

### Windows

- No extra permissions are needed.
- Hardware-accelerated fullscreen games may capture as a black frame; run them in
  borderless-window mode instead.

## Tests

```sh
cd src-tauri && cargo test
```

Covers filename patterns, settings round-tripping, rectangle maths, monitor
clipping, scale-factor cropping and image encoding. The webviews are not covered
automatically and need a real run.

## Licence

MIT
