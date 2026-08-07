---
name: screenx-docs
description: Write and update ScreenX documentation - the user README, docs/TECHNICAL.md, and AGENTS.md - and regenerate the screenshots in docs/images with the --docshots debug mode. Use when editing any of those files, adding a screenshot, adding a feature that needs documenting, or when asked about documentation style, ASD-STE100 wording, or how the synthetic screenshots are produced.
---

# ScreenX documentation

Three documents, three readers, three registers. Getting the register wrong is
the most common mistake, so decide who you are writing for first.

| File | Reader | Register |
| --- | --- | --- |
| `README.md` | Someone using the program | ASD-STE100. Plain, short, instructional |
| `docs/TECHNICAL.md` | A developer changing the code | Clear prose. Explains *why*, not just what |
| `AGENTS.md` | An AI assistant starting cold | Dense, scannable, line-anchored |

Never merge them. A user does not need the coordinate-system rules; an agent
does not need install instructions.

---

## 1. README.md — Simplified Technical English

Any technical part of the README follows ASD-STE100. The rules that matter here:

- **One instruction per sentence.** Not "Click the field and press the keys" as
  a single step — split it.
- **Short sentences.** Around 20 words maximum for a procedure, 25 for
  description.
- **Active voice, present tense.** "ScreenX writes the file", not "the file is
  written".
- **Keep the articles.** "Click the field", not "Click field".
- **One word, one meaning.** Pick a term and never vary it. This project uses:
  *screenshot* (not capture/grab/snap), *shortcut* (not hotkey/keybinding, even
  though the settings tab is called Hotkeys), *folder* (not directory),
  *window* (not app/application window), *press* for keys, *click* for the
  mouse.
- **Say "you".** Address the reader directly.
- **Avoid noun stacks.** Three words maximum. "window highlight delay time
  setting" is wrong.
- **Numbered steps for procedures**, tables for reference material.
- **Warning before the step it applies to**, never after.

Banned in the README: jargon, marketing words, "simply", "just", "leverage",
"seamless", "robust", "powerful", em-dash asides, and any sentence a reader has
to read twice.

Numbers and units stay exact. Never soften a measured figure into "fast" or
"small" — write the number.

### Structure to keep

Contents list → Install → First run → the two capture paths → Editor →
Settings → Naming → Shortcuts → Troubleshooting → For developers → What is not
here yet → Licence.

The **"What is not here yet"** section is not optional. It currently records
that GIF recording is parked, that neither build is signed, and that Windows
has never been run. Keep it honest and current; it is the first thing a reader
checks when something is missing.

---

## 2. docs/TECHNICAL.md — the design record

Explains how the program is put together and **why it is put together that
way**. A section that only restates what the code does is not worth writing;
the code already says that.

Every non-obvious decision should record the reason, and where a bug caused the
decision, name the bug. Examples already in the file: why images travel over a
URI scheme instead of IPC, why blur is hand-written, why hotkey modifiers are
stored literally.

Keep the "Things that will bite you" section aligned with the invariants in
`AGENTS.md`. If you add one, add it to both.

Write plainly. No AI-assistant register: no "delve", no "it's worth noting
that", no "in essence", no bulleted lists of adjectives, no tricolons. Say the
thing.

---

## 3. AGENTS.md — the map

Optimised for an agent with no prior context. Three parts do the heavy lifting:

**File map** — every source file, its line count, and one line on what it
holds.

**"Where do I change X?" table** — a task, and the file plus **line number** to
open. This is the highest-value section; keep it accurate.

**Invariants** — numbered, each one a bug that actually happened. Say what
breaks and how it presents. An invariant that is not a real past bug is just
opinion and should not be in the list.

Line numbers go stale. After editing Rust or `ui/`, re-verify them:

```sh
grep -nE "^(pub )?(async )?fn |^(pub )?struct |^const " src-tauri/src/*.rs
grep -n "const TOOLS\|invoke_handler" ui/editor.js src-tauri/src/lib.rs
```

Update the counts in the file map and the "Orientation" bullet (test totals,
bundle size) whenever they change.

---

## 4. Screenshots

### Regenerate them

```sh
npm run docshots
```

This runs the debug-only mode in `src-tauri/src/docshots.rs`. It writes
`docs/images/editor.png`, `settings.png` and `settings-hotkeys.png`, then exits.
A watchdog kills the process after 30 seconds so it can never leave windows on
screen.

Debug builds only — the module is behind `#[cfg(debug_assertions)]` and is not
in a release bundle.

### How it works

1. `sample_image()` **draws a mock application window from scratch** with
   `fill()` rectangles: a title bar with traffic lights, a sidebar with a
   selected row, heading and paragraph bars, and a white panel to blur. It is
   about 900x560. Nothing is captured from the real screen.
2. That image is encoded, put in `State.frames`, and an editor window is opened
   with it through the normal payload path — the same path a real capture uses.
3. After the window loads, Rust calls `window.eval(...)` to push annotation
   objects straight into the editor's `shapes` array through the `window.__editor`
   hook that the tests already use, then calls `render()`. No production code
   knows about the screenshot mode.
4. `shoot(title, file)` finds the window by title with `xcap::Window::all()` and
   captures it with the app's own capture code.

### Two privacy rules — do not remove them

**Never capture the real desktop.** The editor screenshot uses the drawn mock
window. If you need a new screenshot of content, extend `sample_image()`.

**Substitute identifying values before shooting.** The settings screenshot
replaces the home path with `/Users/you/...` through `eval`, and the hotkeys tab
shows the shipped defaults rather than whatever this machine has bound. Both are
**display-only** — no stored setting is written. Look at the `eval` blocks in
`docshots.rs` before adding a screenshot of any screen that shows a path, a user
name or machine-specific configuration.

### Add a new screenshot

1. Open the window in `docshots::run()`, with a payload if it needs one.
2. If it shows anything machine-specific, `eval` a neutral value over it first.
3. Call `shoot("Exact Window Title", "name.png")`. The title must match the
   window title exactly.
4. Reference it in the README with descriptive alt text.

### Screenshots that cannot be generated

Some shots need a real desktop — the selection overlay must show real windows,
and the tray menu is an OS-drawn menu. Do not fake these. Leave a placeholder
the user can fill:

```markdown
> **PLACEHOLDER — screenshot: `docs/images/overlay.png`**
> The selection overlay with a window highlighted: the screen dark, one window
> bright inside a dashed blue outline, and the size and title label above it.
>
> I did not make this screenshot, because it must show a real desktop. Take it
> yourself when your screen shows nothing private.
```

Use a blockquote with a code-span path, **not** an `![](...)` image tag, so
nothing renders as a broken image. Always say what the picture must contain and
why it is missing.

---

## 5. Before you finish

```sh
npm test          # 51 tests; docs changes should not break them, but check
npm run selfcheck # if you touched anything the docs describe as verified
```

Verify the references resolve:

```sh
grep -oE 'docs/images/[a-z-]+\.png' README.md | sort -u | while read f; do
  [ -f "$f" ] && echo "ok   $f" || echo "placeholder $f"
done
```

Confirm each "placeholder" line is genuinely a labelled placeholder and not a
broken image tag.

Check that a claim you wrote is still true. The docs quote measured figures —
6.9 MB bundle, 3.0 MB dmg, 114-236 ms capture, 0.6 ms window list, test counts.
If you changed something that moves one of those, re-measure it or remove it.
Do not carry a stale number forward.

---

## Rules of thumb

- Write the number, not an adjective.
- If a fix was subtle, the reason belongs in the docs *and* in a code comment.
- An invariant in `AGENTS.md` must name a real bug.
- Documentation that claims something works must point at how that was verified.
- If you cannot verify a claim, say so in the document rather than omitting it.
  "Windows has never been run" is more useful than silence.
