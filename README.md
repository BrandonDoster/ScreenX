# ScreenX (Windows)

Screenshot tool for Windows 11. C# / .NET 10 WinForms, one resident process,
no network code.

Idle is the design goal: between screenshots the process sits in `GetMessage`
with no timers, no polling and no background GC thread. Measured on a
two-monitor desktop (5120x1440 + second display), approximate:

| | |
| --- | --- |
| Idle CPU | 0 ms over 10 s |
| Idle memory | ~11 MB private, under 10 MB working set |
| Hotkey to overlay on screen | ~85-150 ms (screen read ~60 ms of that) |

## Use

- `Control+Alt+A`: region. Hover to pick a window (or the monitor under the
  pointer) and click, or drag a rectangle. A magnifier shows the pixels under
  the pointer, and the arrow keys move it one pixel at a time (ten with
  Shift). Enter takes the highlighted area; Esc or right click cancels.
- `Control+Alt+F`: the monitor under the pointer, no overlay.
- `Control+Alt+S`: scrolling capture. Choose the area as for a region (hover
  picks a window; for a browser, drag just the page so the tabs are left out),
  then ScreenX scrolls it with the mouse wheel and joins the frames into one
  tall image. A sticky header or a status bar appears once. It stops at the
  end of the page, or when you press Esc or move the mouse.

The tray menu has the same captures, plus one on a 3 second delay for
photographing another program's open menu.

The result is copied and saved as PNG to `Pictures\ScreenX`. Click the
notification, or **Edit last screenshot** in the tray menu, to annotate it.
`ScreenX.exe --edit <file>` opens any image in the editor.

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
selected changes that shape. Save writes `<name> (edited).png` beside the
capture, so the original is never overwritten. Closing with changes that were
neither copied nor saved asks first.

## Settings

`%APPDATA%\ScreenX\screenx.json`, opened from the tray menu. Use **Reload
settings** after editing. A file that does not parse is reported and left
alone, never overwritten.

```json
{
  "Folder": "C:\\Users\\you\\Pictures\\ScreenX",
  "RegionHotkey": "Control+Alt+A",
  "ScreenHotkey": "Control+Alt+F",
  "ScrollHotkey": "Control+Alt+S",
  "AfterCapture": "both"
}
```

`AfterCapture` is `both`, `copy` or `save`. Hotkeys are modifiers
(`Control`, `Alt`, `Shift`, `Win`) plus any WinForms `Keys` name: `A`, `F5`,
`D1`, `PrintScreen`. `PrintScreen`, `Pause` and `F13`-`F24` may be used alone.
To use `PrintScreen`, first turn off *Settings > Accessibility > Keyboard >
Use the Print screen key to open screen capture*.

## Build

```powershell
dotnet build -c Release            # a few seconds
dotnet test tests\ScreenX.Tests -c Release
dotnet publish -c Release          # ReadyToRun, needs the .NET 10 desktop runtime
```

The tests cover what does not need a screen: hotkey parsing, settings
loading, the clipboard DIB layout, frames and dimming, shapes and redaction,
and the scrolling-capture stitcher against synthetic pages (sticky header and
footer, a moving scrollbar thumb, text drawn at fractional offsets).

Create `%TEMP%\screenx.log` to record per-capture timing and errors; delete it
to stop.
