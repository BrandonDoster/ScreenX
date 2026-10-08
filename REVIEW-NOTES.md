# Review notes: low and medium items to look at later

Findings from code and security reviews and from live testing that were not
fixed, because they are low or medium and fixing them has a cost or needs a
decision. High and critical findings are fixed, not listed. Newest at the top.

## Open

| Severity | Area | Item |
| --- | --- | --- |
| Low-medium | Security | The screenshots folder may be a network path (`\\host\share`, a WebDAV path, a mapped drive), so captures and the folder picker would touch the network and send Windows credentials. No network *code* is involved, and saving to a NAS may be wanted. Refuse UNC and network drives in `Load` and `Validate`? Needs your call. |
| Low | Security | Another program in your session can create `Local\ScreenX` first, and every launch then exits thinking a copy is running. Squatting the settings event is now handled (it just goes without). Adding your user SID to the names would stop the first; any program in your session can already end ScreenX, so it buys little. |
| Low | Security | Explorer splits its command line at commas even inside quotes, so a folder path ending in a comma might open something else. Untested. Refuse `,` in the folder, or open it through the shell's "explore" verb on a parsed path. |
| Low | Launch | Run as `dotnet ScreenX.dll` instead of `ScreenX.exe`, `Environment.ProcessPath` is dotnet.exe, so the startup entry and the folder picker point at the wrong program. The normal launch is unaffected. |
| Medium | Testing | Mixed DPI is untested. Both physical displays are at 100% (5120x1440 and 1920x720); the remote session showed one 125% display. Everything works in physical pixels and was designed for mixed DPI, but no capture has been taken across two different scales. Needs a second scale set in Display settings, which I did not change. |
| Low | Scrolling | A notification that only partly overlaps the bottom edge of a dragged area (VS Code's corner pop-ups) shares a strip with moving text, so its edge is copied into every added strip and the blank tail is not trimmed. Seen in testing only with a dragged area cutting through a notification; whole-window snaps are clean. Fix would need overlay detection finer than a strip. |
| Low | Corners | The window's own corner preference cannot be read back from DWM, so a window that opted out of rounding still gets transparent corners. Windows' default rule is used: framed, not maximised, not snapped, not a remote session. |
| Low | Settings | The settings window keeps about 2 MB of the resident process's private memory after it closes (WinForms code that was loaded for it). |
| Low | Scrolling | The scrollbar thumb and the minimap are copied into every added strip, so the right edge of a tall capture shows a column of thumbs and minimap slices. Filling that margin with the track colour would also erase real content when the area has no scrollbar; it needs scrollbar detection. |
| Low | Scrolling | A page with an empty gap taller than the whole capture area ends the capture at the gap (it reads as scrolling past the last line). The capture so far is kept. |
| Low | Scrolling | Strips that never move are repainted as their most common colour below the first frame. A sidebar with a gradient or a pattern comes out flat. |
| Low | Scrolling | Content scrolled by a fractional amount is joined at the nearest whole row, so each step can be half a pixel off; over many steps text can show a one-pixel seam. |
| Low | Editor | The overwrite / save-alongside question is a native task dialog and stays light in dark mode. |
| Low | Editor | Copy from the editor of a rounded-corner capture lays the plain bitmap over white, since the original background is gone by then; the PNG copy keeps the transparency. |
| Low | Install | The runtime prompt is the .NET launcher's own dialog. It was not seen on this machine, which has the runtime installed. |
| Low | Test harness | `%TEMP%\screenx-drive` holds the live-test scripts. An early version closed Notepad by ending its process, which also closed a tab of yours; Windows 11 Notepad restores tabs on next start. The scripts now close only their own tab. |

## Done

Recorded so a later review does not raise them again.

- Third reviews (2026-10-08). Security: check-then-decode race on opened files, Overwrite failing in --edit because the file stayed locked, stray partial files, bare-name process launches and DLL search order, squatting the settings event, UTF-8 on the picker pipe, clipboard size checked after allocating, decompression bombs. Correctness (one high): blank strips painted over as sidebars when content arrived later; trim cutting a narrow area with an icon column; sidebars turning a failed match into the end; stale tie-break estimate as the step grows; copy-only building a full bitmap; Settings from a second launch opening behind other windows; Settings opening during a capture; hotkeys left off if the window failed; Shift+Enter, Ctrl+A unrecordable; pointer dragged while a button is held; "too small" shown for Esc; corner preference that cannot be read.
- Second review (2026-10-08): slow apps reading as the end, wrong offsets on repetitive content, header fixed from the first pair only, step sizing against the area rather than the band, animated content never settling, Esc taps missed, profile pass on the UI thread, buffer churn, double-drawn retyped text, refused wheel input, cursor pulled back, history and dirty-flag slips, Quit mid-scroll, magnifier past the edge, case-sensitive startup check.
- First review (2026-10-08): raw-pointer read past the capture, DPI scaling of opened images, Quit dropping annotations, text re-edit losing the shape, crash dialogs from tray actions, shell execution of the folder path, GDI+ decoding every format, editor sizing on small screens, leaked handles, unchecked native returns, Esc mid-move, label repaint at high DPI.
