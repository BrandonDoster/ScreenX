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

Numbers that describe *behaviour* stay exact: the default dwell is 400 ms, the
minimum window side is 48 pixels, a token width of `{4}` pads to four digits.
Those are contracts, and rounding them makes the document wrong.

Numbers that describe *artifacts* do not belong in any document. See
[Figures](#figures) below.

### Structure to keep

Contents list → Install → First run → the two capture paths → Editor →
Settings → Naming → Shortcuts → Troubleshooting → For developers → What is not
here yet → Licence.

The **"What is not here yet"** section is not optional. It is the first thing a
reader checks when something is missing, so it has to be current. Read it in
`README.md` rather than trusting any summary of it, including one in this file.

When something on that list starts working, it does not just get deleted.
Replace it with what is now true and what is still not: a limitation you have
retired is exactly the thing a reader needs told.

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

## 5. Facts that live in more than one file

This is where drift comes from. A fact written in three documents gets updated
in one, and the other two keep asserting the old thing for months. Every entry
below has actually diverged at least once.

| Fact | Lives in |
| --- | --- |
| What works on each platform, and what is unverified | `README.md` "What is not here yet"; `docs/TECHNICAL.md` platform sections; `AGENTS.md` "Platform notes" |
| Anything a release produces | Nowhere. Do not write artifact sizes down — see [Figures](#figures) |
| Test and self-check counts | `docs/TECHNICAL.md` section 13; `AGENTS.md` "Orientation" |
| Version number | `package.json`; `src-tauri/Cargo.toml`; `src-tauri/Cargo.lock`; `src-tauri/tauri.conf.json`; installer filenames in `README.md` |
| An invariant | `AGENTS.md` numbered list; `docs/TECHNICAL.md` "Things that will bite you" |
| Source file line counts and anchors | `AGENTS.md` file map and "Where do I change X?" |

**Changing one means grepping for the others.** Do not rely on remembering which
files mention it — search for the old value:

```sh
grep -rn "<the old number or claim>" README.md docs/ AGENTS.md .agents/
```

Retiring a limitation is the case that gets missed most, because the obvious
file gets fixed and the other two read as prose you already skimmed.

---

## 6. Before you finish

Run the checks; do not recall the numbers.

```sh
# Counts. Compare against AGENTS.md "Orientation" and TECHNICAL.md section 13.
node --test --test-reporter=tap tests/*.test.mjs | grep '^# tests'
cargo test --manifest-path src-tauri/Cargo.toml 2>&1 | grep 'test result'
npm run selfcheck            # count the "ok"/"FAIL" lines; that is the total

# Line counts and anchors for the AGENTS.md file map and its tables.
wc -l src-tauri/src/*.rs ui/*.js tests/*.mjs
grep -nE "^(pub )?(async )?fn |^(pub )?struct |^const |^pub static " src-tauri/src/*.rs
grep -n "const TOOLS\|invoke_handler" ui/editor.js src-tauri/src/lib.rs

# Version agreement across every file that carries it.
grep -o '"version": "[^"]*"' package.json src-tauri/tauri.conf.json
grep -m1 '^version' src-tauri/Cargo.toml
grep -oE 'ScreenX_[0-9]+\.[0-9]+\.[0-9]+' README.md | sort -u

# Image references. Every "placeholder" must be a labelled blockquote, not a
# broken image tag.
grep -oE 'docs/images/[a-z-]+\.png' README.md | sort -u | while read f; do
  [ -f "$f" ] && echo "ok   $f" || echo "placeholder $f"
done
```

### Figures

**Artifact sizes are not recorded in any document.** Not the app bundle, the
dmg, the installer or the binary. They move with every release and with the
build shape — a universal macOS binary is roughly twice a single-arch one — so a
written figure is stale on arrival and generates busywork at each release. The
release assets are the source of truth, and a reader who wants a byte count can
look there. Do not link them to it either; it is not interesting enough to
signpost.

"Small", "a few megabytes", "hundreds of megabytes" are fine. That is the whole
claim worth making, and it stays true.

**Performance figures are approximate, and marked as approximate.** Write
"~35 ms", "under a millisecond", "roughly half a second", "100–250 ms". Hard
values like "0.6 ms" and "518 ms" read as benchmarks, which invites someone to
either re-measure before touching a line of prose, or leave a stale number in
place. Orders of magnitude are what the argument actually rests on.

Behavioural constants are the exception and stay exact: a default of 400 ms, a
48-pixel minimum. Those are contracts, not measurements.

### Claims about what works

Anything the docs say is verified must name how. If you fixed something a stub
could not catch, `npm run selfcheck` should gain a line for it, and that is what
the docs point at. A claim with no check behind it is the one that goes stale
without anyone noticing.

---

## Rules of thumb

- Exact for behaviour, approximate for performance, absent for artifact sizes.
- If a fix was subtle, the reason belongs in the docs *and* in a code comment.
- An invariant in `AGENTS.md` must name a real bug.
- Documentation that claims something works must point at how that was verified.
- If you cannot verify a claim, say so in the document rather than omitting it.
  A stated "this is untested on X" is more useful than silence, and it is the
  sentence a later session will come back and retire.
- **This file states rules, not facts about ScreenX.** Every product fact it
  once carried — the bundle size, what was unverified — went stale and then
  taught the wrong thing to the next session, because this is what gets read
  first. If you want to write a number or a status here, write the command that
  prints it instead.
