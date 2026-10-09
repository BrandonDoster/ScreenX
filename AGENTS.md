# Agent Instructions

Read this before changing anything. It is the map and the rules; `README.md`
is for users.

## Orientation

ScreenX is a Windows 11 screenshot tool: C# on .NET 10, WinForms, one
resident process. There is no network code in the app, and none may be
added; `installer.ps1`, which downloads releases from GitHub, is the only
exception.

The design goal is that it is unnoticeable until it is needed: **0 CPU at
idle** (no timers, no polling, workstation GC with no background thread) and
a small idle footprint (about 11 MB private, under 10 MB working set). Check
both after any change that could touch them:

```powershell
$p = Get-Process ScreenX; $c = $p.TotalProcessorTime; Start-Sleep 10; $p.Refresh()
($p.TotalProcessorTime - $c).TotalMilliseconds   # must be 0
$p.PrivateMemorySize64 / 1MB
```

This is a full rewrite; the earlier Rust/egui build is retired and its
history is gone from the repository. Nothing here should refer to Rust, egui,
Tauri, `core/` or `app/`.

## File map

| File | Holds |
| --- | --- |
| `Program.cs` | `Main`, `--edit`, `Images` (safe image opening), `Settings`, `HotkeyWindow` |
| `Tray.cs` | The resident part: tray menu, hotkeys, `Choose`, region/screen/scrolling captures, `Deliver`, startup entry |
| `Capture.cs` | `Capture` (BitBlt, window list, memory trim), `Frame` (DIB section), `Clip` (CF_DIB), `Gdi` P/Invoke |
| `Overlay.cs` | Selection overlay: window snapping, drag, magnifier, nudging |
| `Scrolling.cs` | Scrolling capture driver: wheel input, settling, border |
| `Stitcher.cs` | Joins scrolling frames. Pure, fully tested |
| `Editor.cs` | Annotation editor form and its `Canvas` |
| `SettingsWindow.cs` | Settings window, hotkey recording, `--pick-folder` |
| `Shapes.cs` | `Shape`: geometry, hit testing, handles, drawing, redaction; `History` (undo) |
| `tests/ScreenX.Tests` | xunit tests for everything that needs no screen |
| `installer.ps1` | Installs or updates from the latest release; Windows PowerShell 5.1, ASCII only |
| `.github/workflows/build.yml` | Tests and publishes every push; a `v*` tag makes the release |

## Rules — each one was a real bug

1. **Read the screen into a `Frame` (DIB section), not a GDI+ `Bitmap`.**
   `Graphics.GetHdc` on a GDI+ bitmap goes through a temporary copy and
   `DrawImage` converts format on every paint; the overlay took 300-600 ms to
   appear that way, and 85-150 ms with DIB sections and `BitBlt`.
   `Graphics.CopyFromScreen` also rejects `CaptureBlt`, which layered
   windows (menus, tooltips) need.

2. **Never put an image on the clipboard with `Clipboard.SetImage` or
   `SetDataObject`.** Both keep a GDI bitmap of the whole image in this
   process: three full-screen copies took private memory from 11 MB to 97 MB.
   `Clip.Copy` hands over a CF_DIB as an HGLOBAL instead.

3. **The virtual screen is read once per capture and passed through.**
   `Frame.ToBitmap` wraps a raw pointer; a selection made against a layout
   that changed in between (monitor plugged in) read past the pixels. It is
   bounds-checked too, but do not reintroduce a second reading.

4. **Everything is physical pixels.** The process is Per-Monitor V2 DPI
   aware, so monitors, window rects, the capture and the overlay agree with no
   scaling anywhere. Windows that span monitors must swallow `WM_DPICHANGED`,
   or WinForms resizes them to one monitor's scale.

5. **The hotkey grants foreground rights.** `RegisterHotKey` delivery lets
   the overlay `Activate()`; nothing else about focus is needed. Do not add
   `SetForegroundWindow` tricks.

6. **No back buffer the size of the screen.** The overlay paints dim,
   bright and magnifier into disjoint clip regions instead of double
   buffering, and the editor canvas is a `ScrollableControl` the size of the
   window: a control double-buffers at its own size, and an image zoomed 8x
   would allocate hundreds of MB per paint.

7. **Text in shapes is GDI+ `DrawString`, not `TextRenderer`.** GDI ignores
   the zoom transform and corrupts alpha in 32 bpp bitmaps.

8. **Redaction pixelates from the original capture.** It must destroy
   detail, so test it against a one-pixel checkerboard; and it samples the
   untouched source so nothing drawn on top leaks through.

9. **Scrolling capture cannot rely on exact row matches.** DirectWrite and
   WinUI draw text at fractional positions: Notepad scrolled 178 px for three
   notches and the best exact match was 70% of rows. `Stitcher` tries exact
   hashes first and then brightness profiles. Frames are compared only after
   they stop changing (smooth scrolling, lazy loading).

9a. **Scrolling capture cannot rely on whole rows either.** A whole VS Code
    window has an activity bar that never moves and a minimap that moves at
    its own rate, so no row matched and the first step was lost. Frames are
    matched in 48 px strips that vote. A strip that fits several offsets (the
    same words on every line) must not vote: Notepad locked onto one line
    height that way while the real scroll was six times larger. Header and
    footer are found per strip, with tolerance, or a notification is copied
    into every added strip. Strips that never move are repeated, as ShareX
    does, never painted over: painting a sidebar with its background erased
    the content that shared a strip with it. Repeats can be cropped in the
    editor; lost content cannot. Verify any change here live against
    Notepad, an Edge page with a sticky header, and VS Code both as a text
    area and as a whole window, long and short files.

10. **Settings are written only from the settings window's Save.**
    `screenx.json` is created once with the defaults; a file that fails to
    parse gives an error and the defaults for that run, and is left as it
    was. It is not `settings.json`, which the retired Rust build rewrote whole
    in the same folder; machines that ran it still have one there.

10a. **The settings window is modeless.** `DialogResult` closes only modal
     forms; it calls `Close()`. Hotkeys are unregistered while it is open, or
     recording a hotkey fires it.

10b. **Anything that loads shell extensions runs in a child process.** The
     folder picker is `ScreenX.exe --pick-folder`; Save As is a task dialog,
     not the common file dialog. Shell extensions loaded into the resident
     process never unload.

11. **Closing forms: no `BeginInvoke` from `OnFormClosed`.** It posts to the
    window being destroyed and is lost; the editor gives its memory back from
    `Dispose`. Quit asks each editor to close before `ExitThread`, which would
    otherwise drop unsaved annotations without the prompt.

12. **Opened files are PNG, JPEG, BMP or GIF by signature** (`Images.Open`).
    `--edit` makes this reachable from downloaded files, and GDI+ would also
    parse EMF, WMF and TIFF.

13. **Shell paths go through `explorer.exe` as one quoted argument**, not
    `UseShellExecute` on a user-supplied path, which the shell would
    interpret (a folder named like a CLSID junction). Always quoted, not
    `ArgumentList`: that quotes only around spaces, and explorer splits an
    unquoted argument at commas ("a,b" opened Documents).

## Commands

```powershell
dotnet build -c Release                     # a few seconds
dotnet test tests\ScreenX.Tests -c Release
dotnet publish -c Release                   # ReadyToRun; needs the .NET 10 Desktop Runtime
```

To release, push a tag: `git tag v2.0.1; git push origin v2.0.1`. The tag
sets the version (the csproj's is for local builds), and the release gets
`ScreenX-2.0.1-win-x64.zip` and `installer.ps1`.

Stop the running copy before building, or the copy step fails on a locked
`ScreenX.exe`. Create `%TEMP%\screenx.log` to log capture timings, scrolling
steps and errors.

## Testing

`dotnet test` covers what needs no screen. What needs one is checked by
running it: overlay, window snapping, focus, the tray, scrolling capture in
real applications (Notepad and Edge were used: a 300-line file and a page with
a sticky header both came out whole and in order). Synthetic input from a
script works for all of these; leave the mouse alone while it runs.

## Style

Match the surrounding code. Comments say why, and most record a bug that was
hit; do not remove one without understanding what it protects. Keep the
dependency list at zero NuGet packages for the app. Prefer deleting code to
adding it.
