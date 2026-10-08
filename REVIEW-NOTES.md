# Review notes: low and medium items to look at later

Findings from code and security reviews and from live testing that were not
fixed, because they are low or medium and fixing them has a cost or needs a
decision. High and critical findings are fixed, not listed. Newest at the top.

## Open

| Severity | Area | Item |
| --- | --- | --- |
| Medium | Testing | Mixed DPI is untested. Both physical displays are at 100% (5120x1440 and 1920x720); the remote session showed one 125% display. Everything works in physical pixels and was designed for mixed DPI, but no capture has been taken across two different scales. Needs a second scale set in Display settings, which I did not change. |
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

- Second review (2026-10-08): slow apps reading as the end, wrong offsets on repetitive content, header fixed from the first pair only, step sizing against the area rather than the band, animated content never settling, Esc taps missed, profile pass on the UI thread, buffer churn, double-drawn retyped text, refused wheel input, cursor pulled back, history and dirty-flag slips, Quit mid-scroll, magnifier past the edge, case-sensitive startup check.
- First review (2026-10-08): raw-pointer read past the capture, DPI scaling of opened images, Quit dropping annotations, text re-edit losing the shape, crash dialogs from tray actions, shell execution of the folder path, GDI+ decoding every format, editor sizing on small screens, leaked handles, unchecked native returns, Esc mid-move, label repaint at high DPI.
