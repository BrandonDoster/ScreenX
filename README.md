# ScreenX

A lightweight screenshot tool for Windows 11. It sits in the tray, uses no
CPU while idle, and never touches the network.

## Install and update

```powershell
irm https://github.com/BrandonDoster/ScreenX/releases/latest/download/installer.ps1 | iex
```

This installs ScreenX to `%LOCALAPPDATA%\Programs\ScreenX`, starts it, and
sets it to start with Windows. Run the same line again to update. Close any
ScreenX editor windows first, since updating restarts ScreenX.

You can also download `ScreenX-<version>-win-x64.zip` from
[Releases](https://github.com/BrandonDoster/ScreenX/releases) and unzip it
anywhere.

ScreenX needs the .NET 10 Desktop Runtime. If it is missing, Windows offers
to download it the first time ScreenX starts.

To uninstall, turn off *Start with Windows* in Settings, quit ScreenX from
the tray, and delete the install folder.

## Capturing

- **Region** (`Ctrl+Shift+Q`): hover to pick a window or monitor and click,
  or drag a rectangle. Arrow keys nudge by a pixel (Shift for ten). Enter
  takes the selection; Esc or right-click cancels.
- **Scrolling capture**: pick an area as for a region, and ScreenX scrolls it
  and joins the result into one tall image. Esc stops it early.
- **Screen**: the whole monitor under the pointer, from a hotkey you set.

The tray menu has all of these, plus a 3-second delayed capture (for open
menus), *Edit last screenshot* and *Open screenshots folder*.

A capture opens in the editor by default. Screenshots save to
`Pictures\ScreenX`.

## Editor

| Key | Tool |
| --- | --- |
| V | Select, move, resize. Delete removes; double-click text to edit it |
| A / R / E / L / P | Arrow, box, ellipse, line, pen |
| H | Highlight |
| T | Text. Enter places it, Shift+Enter adds a line |
| N | Numbered step |
| X | Redact (pixelates) |
| C | Crop |
| U | Cut out: drag across a strip, then Enter to remove it and close the gap |

`Ctrl+Z` / `Ctrl+Y` undo and redo, `Ctrl+C` copies, `Ctrl+S` saves,
`Ctrl+wheel` zooms, `Ctrl+0` fits to the window.

To open an existing image in the editor, run `ScreenX.exe --edit <file>` or
use *Open with*.

## Settings

Open *Settings...* from the tray, or launch ScreenX again while it is
running. Click a hotkey box and press the combination; Backspace clears it.
To use `PrintScreen`, first turn off *Settings > Accessibility > Keyboard >
Use the Print screen key to open screen capture* in Windows.

Settings live in `%APPDATA%\ScreenX\screenx.json`.

## Building

```powershell
dotnet build -c Release
dotnet test tests\ScreenX.Tests -c Release
```

Pushing a `v*` tag builds the release on GitHub Actions.
