# Questions for Brandon

Decisions I made overnight that you may want to change, and things I could
not decide without you. Each has the default I went with. Answer inline or
just tell me; nothing here blocks using the build.

## Behaviour

1. **What a capture does by default.** `AfterCapture` is `both`: copy to the
   clipboard and save a PNG, then a notification you can click to annotate.
   The other choices are `copy`, `save` and `editor` (open the editor
   straight away, nothing saved until you save). Is `both` right for you, or
   do you want the editor to open every time?

2. **Editor Save.** Ctrl+S writes `<name> (edited).png` beside the capture so
   the original is never overwritten. Would you rather it overwrite the
   capture it was opened from (one file per screenshot), or get a Save As
   dialog? The dialog loads shell extensions into the process and they stay
   loaded, which costs idle memory, so I left it out.

3. **Default hotkeys.** `Control+Alt+A` region, `Control+Alt+F` screen,
   `Control+Alt+S` scrolling — the old build's two plus one. Do you want
   PrintScreen for region? It needs Windows' own PrintScreen-opens-Snipping-
   Tool setting turned off, which ScreenX cannot do for you.

4. **Scrolling capture start position.** It starts from wherever the page is
   scrolled to, like ShareX's default. Should it scroll to the top first?
   That needs Home or Ctrl+Home sent to the window, which does not work in
   every app.

5. **Scrolling capture stop on mouse move.** Moving the mouse stops it,
   because the wheel goes to whatever is under the pointer and moving would
   scroll something else. One ShareX complaint is exactly this. Keep it?

## Shipping

6. **Push and release.** Everything is committed on the local `windows`
   branch only; nothing is pushed. Want it pushed to GitHub? And a release
   workflow (zip of the published build on a `v*` tag, like the old one)?

7. **Runtime.** The build needs the .NET 10 Desktop Runtime installed. The
   alternative is a self-contained build that carries its own runtime (much
   larger download, no install needed). You said size does not matter; idle
   memory is about the same either way. Which?

8. **Settings window.** Settings are still a JSON file opened from the tray,
   with Reload. Worth a small native settings dialog (hotkey capture,
   folder picker, after-capture choice)?

## Housekeeping

9. The old checkout (`ScreenX`, branch `master`) has untracked `node_modules/`
   and `src-tauri/` folders left over from the webview build. I did not touch
   them. Recycle them?
