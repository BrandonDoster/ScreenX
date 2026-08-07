# ScreenX

A cross-platform screen capture and GIF recording tool for macOS (Intel) and Windows.
Everything stays on your machine — there is no uploading, no account, and no network code.

## Features

- **Capture entire screen** — the display the mouse is on.
- **Capture region** — drag a rectangle on a frozen copy of the screen, with a
  pixel magnifier and live size readout.
- **Capture window** — pick from a grid of live window thumbnails.
- **Record region as GIF** and **record window as GIF**, with a floating
  stop/cancel bar and a red outline around the recorded area.
- **Built-in editor** — crop, rectangle, ellipse, line, arrow, freehand,
  highlighter, pixelate, text and numbered step markers, with undo/redo.
- **Settings** for storage folders, filename patterns, global hotkeys, GIF
  quality, and switching individual features off.

## Install and run

```sh
npm install
npm start
```

ScreenX lives in the menu bar (macOS) or notification area (Windows). The first
launch opens the settings window.

### Building installers

```sh
npm run dist:mac    # dmg + zip, x64
npm run dist:win    # nsis installer + portable exe, x64
```

## Default hotkeys

| Action | Shortcut |
| --- | --- |
| Capture entire screen | `Ctrl/Cmd + Alt + F` |
| Capture region | `Ctrl/Cmd + Alt + A` |
| Capture window | `Ctrl/Cmd + Alt + W` |
| Record region as GIF | `Ctrl/Cmd + Alt + R` |
| Record window as GIF | `Ctrl/Cmd + Alt + E` |
| Stop recording | `Ctrl/Cmd + Alt + S` |

All of them can be changed or cleared under Settings → Hotkeys. Combinations
already claimed by another application are reported after saving.

## Filename patterns

Auto-save names are built from tokens, for example
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

A number in braces sets the width: `%i{4}` gives `0007`, `%ra{6}` gives six
random characters. Unknown tokens are left in the name as-is so typos are
visible. Characters no filesystem accepts are stripped, and an existing file is
never overwritten — ` (2)`, ` (3)` and so on are appended instead.

## Editor shortcuts

| Key | Action |
| --- | --- |
| `1`–`9` | switch to the first nine tools |
| `Ctrl/Cmd + Z` | undo |
| `Ctrl/Cmd + Shift + Z` | redo |
| `Ctrl/Cmd + C` | copy image to clipboard |
| `Ctrl/Cmd + S` | save |
| `Ctrl/Cmd + Shift + S` | save as… |
| `Delete` | delete the selected annotation |
| `Esc` | close the editor |

Hold `Shift` while dragging a rectangle or ellipse to keep it square/circular.

## Platform notes

### macOS

- Grant **Screen Recording** permission under System Settings → Privacy &
  Security. Without it every capture comes back blank; ScreenX offers to open
  the right settings pane when it detects this.
- macOS only reports windows that are actually visible on screen. A window that
  is minimised or completely covered by another window will not appear in the
  window picker — bring it to the front first.
- Builds target Intel (x64). They run on Apple Silicon under Rosetta.

### Windows

- No extra permissions are needed.
- Hardware-accelerated fullscreen games may capture as a black frame; run them
  in borderless-window mode instead.

## GIF quality and size

GIFs are limited to 256 colours by the format itself. ScreenX keeps recordings
reasonable by encoding a region at the size you dragged rather than at the
display's physical pixel count, and by capping window recordings at 800 px wide
by default (Settings → Recording).

Larger areas and higher frame rates cost proportionally more CPU per frame; if
the encoder cannot keep up, frames are dropped rather than queued, and playback
speed stays correct because each frame stores its measured duration.

## Tests

```sh
npm test          # filename pattern rules, plain node
npm run test:ui   # drives every renderer with synthetic events
npm run test:e2e  # real screen capture, cropping, saving and GIF recording
```

`test:e2e` needs Screen Recording permission on macOS. If the macOS capture
daemon gets stuck (every capture, including the system `screencapture` tool,
starts failing) restart it with `killall replayd`.

## Licence

MIT
