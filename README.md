# ScreenX (Windows)

Screenshot tool for Windows 11. C# / .NET 10 WinForms, one resident process,
no network code.

Idle is the design goal: between screenshots the process sits in `GetMessage`
with no timers, no polling and no background GC thread. Measured on a
two-monitor desktop (5120x1440 + second display), approximate:

| | |
| --- | --- |
| Idle CPU | 0 ms over 10 s |
| Idle memory | ~12 MB private, ~3 MB working set |
| Hotkey to overlay on screen | ~85-150 ms (screen read ~60 ms of that) |

## Use

- `Control+Alt+A`: region. Hover to pick a window (or the monitor under the
  pointer) and click, or drag a rectangle. Enter takes the highlighted area;
  Esc or right click cancels.
- `Control+Alt+F`: the monitor under the pointer, no overlay.

The result is copied and saved as PNG to `Pictures\ScreenX`; a notification
shows the file name, and clicking it shows the file in Explorer.

## Settings

`%APPDATA%\ScreenX\screenx.json`, opened from the tray menu. Use **Reload
settings** after editing. A file that does not parse is reported and left
alone, never overwritten.

```json
{
  "Folder": "C:\\Users\\you\\Pictures\\ScreenX",
  "RegionHotkey": "Control+Alt+A",
  "ScreenHotkey": "Control+Alt+F",
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
dotnet publish -c Release          # ReadyToRun, needs the .NET 10 desktop runtime
bin\Release\net10.0-windows\win-x64\ScreenX.exe --selftest   # exit code = failures
```

Create `%TEMP%\screenx.log` to record per-capture timing and errors; delete it
to stop.
