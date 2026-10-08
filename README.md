# ScreenX (Windows)

Screenshot tool for Windows 11. C# / .NET 10 WinForms, one resident process,
no network code.

Idle is the design goal: between screenshots the process sits in `GetMessage`
with no timers, no polling and no background GC thread. Measured on a
two-monitor desktop (5120x1440 + 1920x720), approximate:

| | |
| --- | --- |
| Idle CPU | 0 ms over 10 s |
| Idle memory | ~11 MB private, under 10 MB working set |
| Hotkey to overlay on screen | ~85-150 ms (screen read ~60 ms of that) |

## Use

- `Control+Shift+Q`: region. Hover to pick a window (or the monitor under the
  pointer) and click, or drag a rectangle. A magnifier shows the pixels under
  the pointer, and the arrow keys move it one pixel at a time (ten with
  Shift). Enter takes the highlighted area; Esc or right click cancels. A
  whole window on Windows 11 keeps its rounded corners, transparent.
- **Capture screen** (tray menu, or set a hotkey): the monitor under the
  pointer, no overlay.
- **Scrolling capture** (tray menu, or set a hotkey): choose the area as for a
  region, then ScreenX scrolls it with the mouse wheel and joins the frames
  into one tall image. A whole window works, VS Code included: a sidebar that
  does not move, a notification or a status bar appear once. It stops at the
  end of the page, after three minutes, or on Esc; the mouse stays yours
  while it runs.

The tray menu also has a capture on a 3 second delay, for photographing
another program's open menu.

By default a capture opens in the editor. **Copy** there puts it on the
clipboard without saving anything; **Save** writes it to `Pictures\ScreenX`.

## Editor

| Key | Tool |
| --- | --- |
| V | Select: drag to move, drag a handle to resize, Delete to remove, double-click text to edit |
| A / R / E / L / P | Arrow, box, ellipse, line, pen |
| H | Highlight |
| T | Text: Enter places it, Shift+Enter for a new line |
| N | Numbered step |
| X | Redact: pixelates from the original capture, so it destroys the detail |
| C | Crop |

Ctrl+Z / Ctrl+Y undo and redo, Ctrl+C copies, Ctrl+S saves, Ctrl+wheel or
Ctrl+plus/minus zooms, Ctrl+0 fits. Picking a colour or size with a shape
selected changes that shape.

Saving a new capture gives it the usual name; saving again updates that same
file. Saving an image that was opened from a file asks whether to overwrite it
or save alongside it as `(edited)`. Closing with changes that were neither
copied nor saved asks first.

`ScreenX.exe --edit <file>` opens a PNG, JPEG, BMP or GIF in the editor.

## Settings

**Settings...** in the tray menu, or start ScreenX again while it is running.
Hotkeys are recorded by pressing them; Backspace clears one. `PrintScreen`,
`Pause` and `F13`-`F24` may be used alone; anything else needs Control, Alt,
Shift or Win. To use `PrintScreen`, first turn off *Settings > Accessibility >
Keyboard > Use the Print screen key to open screen capture*.

Everything is kept in `%APPDATA%\ScreenX\screenx.json`, which can also be
edited by hand. A file that does not parse is reported and left alone; it is
written only when Save is pressed in the settings window.

```json
{
  "Folder": "C:\\Users\\you\\Pictures\\ScreenX",
  "RegionHotkey": "Control+Shift+Q",
  "ScreenHotkey": "",
  "ScrollHotkey": "",
  "AfterCapture": "editor"
}
```

`AfterCapture` is `editor`, `both` (copy and save), `copy` or `save`.

## Install

ScreenX needs the .NET 10 Desktop Runtime (or newer). If it is missing, the
first launch shows Windows' own dialog offering to download it; nothing has
to be bundled.

## Build

```powershell
dotnet build -c Release            # a few seconds
dotnet test tests\ScreenX.Tests -c Release
dotnet publish -c Release          # ReadyToRun
```

The tests cover what does not need a screen: hotkey parsing, settings
loading and saving, the clipboard DIB layout, frames, dimming and rounded
corners, shapes, redaction and undo history, and the scrolling-capture
stitcher against synthetic pages (sticky headers and footers, sidebars and
minimaps, notifications, moving scrollbar thumbs, text drawn at fractional
offsets, repeating rows, blank space past the last line).

Create `%TEMP%\screenx.log` to record capture timings, scrolling steps and
errors; delete it to stop.
