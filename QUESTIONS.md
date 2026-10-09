# Questions for Brandon

## Round 5 (2026-10-08 afternoon)

Your answers to the review-notes questions, and where each stands. None of
it is committed: you said no commits yet.

| Item | Your answer | Done |
| --- | --- | --- |
| Network folder | Warn, don't block | Save shows a warning once for a share, WebDAV path or mapped network drive; Save again keeps it |
| Sticky-panel repeats | Fix it | Done, and live-tested in Notepad, Edge (sticky header, and a page with a highlighting contents panel) and VS Code (text area and whole window, long and short files) |
| Mixed DPI | Change the scale | Done with the main display at 125%; the 12.3" panel only allows 100%. Both are back at 100% |
| `Local\ScreenX` name | Leave it | Left |
| Comma in folder | (asked whether it is real) | It is: a folder `a,b` opened Documents and `end,` opened This PC. Commas are legal in folder names ("Smith, John"), so nothing is refused; the path is now always quoted, which opens the right folder in every case tried |
| `dotnet ScreenX.dll` | Leave it | Left |
| Whole-width "end" | Do with the sidebar fix | Done |

Questions:

12. **Your 12.3" display dropped off during the DPI test.** It read black in
    screenshots from the start, and Windows now lists it as not present. Its
    scale is back at 100%. Is it switched off or asleep? If it is still gone
    after you power-cycle it, tell me.
12A. Switched the display inputs to the work PC, which is likely why it
    dropped off. Untested parts (the editor moved between scales) wait.

13. **Commit.** Everything from this round is uncommitted on `windows`. One
    commit, or one per item? Say when.
13A. One per batch of work: change, test, confirm, commit, next. Done for
    round 5 as six commits.

14. **Per-column sidebars.** The last review left one medium: where a
    48 px strip straddles a pane edge, repeated list cells beside the pane
    can still be painted over. The fix is to decide "sidebar" per column
    instead of per strip; about 30 lines plus a full live rerun. Do it next?
14A. Not now. ShareX repeats the bottom of VS Code's sidebar on every step;
    repeats are acceptable because the editor can crop them. Brandon will use
    it for a while to settle the flow. Left open in REVIEW-NOTES.md.

## Future features (not day one)

- **Capture from the command line.** For agents and scripts:
  `ScreenX.exe --capture screen | window "<title>" | region x,y,w,h`, saving
  to the screenshots folder by default, with a parameter for another output
  folder, printing the saved path. No setting: anything on the machine can
  already read the screen or drive ScreenX with synthetic input, so a toggle
  would not protect anything. A capture started this way exits once it is
  done and must not touch the copy of ScreenX the user is running.

## Where your answers stand (2026-10-08)

| # | Your answer | Done |
| --- | --- | --- |
| 1 | Editor by default, copy-only in it | Default is `editor`; the editor's **Copy** puts it on the clipboard and saves nothing |
| 2 | Same name if never saved; ask if already saved | First save uses the usual name and later saves update it; an opened file asks *Overwrite* or *Save alongside* |
| 3 | Region Ctrl+Shift+Q, no screen/scrolling hotkey | Those are the defaults now, and your `screenx.json` has been set to them |
| 4 | Start where the page is | Unchanged |
| 5 | Don't stop on mouse moves | Done: it scrolls the area wherever the mouse is, and stops at the end, on Esc, or after 3 minutes |
| 6 | Guess | Done: repeating content is guessed using the expected scroll |
| 7 | Transparent rounded corners | Done for whole-window captures; PNG on the clipboard keeps them |
| 8 | Don't push yet | Nothing pushed |
| 9 | Prompt for the runtime | The .NET launcher already shows Windows' download dialog when the runtime is missing; it also runs on newer .NET now |
| 10 | Settings GUI | **Settings...** in the tray, or start ScreenX a second time |
| 11 | Recycle | `node_modules` and `src-tauri` are in the Recycle Bin |

Also: the VS Code scrolling failure is fixed (whole window and text area,
long and short files), and your displays are both at 100%, so mixed DPI is
still untested; see `REVIEW-NOTES.md`.

Decisions I made overnight that you may want to change, and things I could
not decide without you. Each has the default I went with. Answer inline or
just tell me; nothing here blocks using the build.

## Behaviour

1. **What a capture does by default.** `AfterCapture` is `both`: copy to the
   clipboard and save a PNG, then a notification you can click to annotate.
   The other choices are `copy`, `save` and `editor` (open the editor
   straight away, nothing saved until you save). Is `both` right for you, or
   do you want the editor to open every time?
1A. editor, with a copy to cliipboard only option being available in editor

2. **Editor Save.** Ctrl+S writes `<name> (edited).png` beside the capture so
   the original is never overwritten. Would you rather it overwrite the
   capture it was opened from (one file per screenshot), or get a Save As
   dialog? The dialog loads shell extensions into the process and they stay
   loaded, which costs idle memory, so I left it out.
2A. should same as name if editor is opened before save, if editing something already saved then it should prompt to overwrite ot save alongside.

3. **Default hotkeys.** `Control+Alt+A` region, `Control+Alt+F` screen,
   `Control+Alt+S` scrolling — the old build's two plus one. Do you want
   PrintScreen for region? It needs Windows' own PrintScreen-opens-Snipping-
   Tool setting turned off, which ScreenX cannot do for you.
3A. screen and scrolling doesn't have a default hotkey, region should be ctrl+shift+q

4. **Scrolling capture start position.** It starts from wherever the page is
   scrolled to, like ShareX's default. Should it scroll to the top first?
   That needs Home or Ctrl+Home sent to the window, which does not work in
   every app.
4A. Starts where the page is when the capture is started

5. **Scrolling capture stop on mouse move.** Moving the mouse stops it,
   because the wheel goes to whatever is under the pointer and moving would
   scroll something else. One ShareX complaint is exactly this. Keep it?
5A. I wouldn't stop on mouse moving. Just when it stops scrolling, when esc is hit or timeout

6. **Scrolling capture of repeating content.** On the first step, a page
   whose rows repeat exactly (an empty grid, a list of identical lines) fits
   several offsets and ScreenX stops rather than guess, because a guess
   silently duplicates or drops rows. After the first step it uses the scroll
   amount it has seen. Acceptable, or would you rather it guess?
6A. guessing, better to have a slightly messed up capture that can be edited in the editor than no capture at all.

7. **Window snapping and rounded corners.** Clicking a window takes its
   visible frame, so Windows 11's rounded corners show a sliver of whatever
   is behind. ShareX can make those corners transparent. Worth doing?
7A.yes

## Shipping

8. **Push and release.** Everything is committed on the local `windows`
   branch only; nothing is pushed. Want it pushed to GitHub? And a release
   workflow (zip of the published build on a `v*` tag, like the old one)?
8A. not yet

9. **Runtime.** The build needs the .NET 10 Desktop Runtime installed. The
   alternative is a self-contained build that carries its own runtime (much
   larger download, no install needed). You said size does not matter; idle
   memory is about the same either way. Which?
9A. it shouldn't carry it but isn't there a way during an install or maybe when launched to prompt the user to install it through native windows tooling? I remember seeing "native" windows prompts to install .net when an app needed it beore.

10. **Settings window.** Settings are still a JSON file opened from the tray,
   with Reload. Worth a small native settings dialog (hotkey capture,
   folder picker, after-capture choice)?
10A. yeah settings GUI is worth since we are handling the window system in a better way.

## Housekeeping

11. The old checkout (`ScreenX`, branch `master`) has untracked `node_modules/`
   and `src-tauri/` folders left over from the webview build. I did not touch
   them. Recycle them?
11A. yeah recycle is fine.
