---
name: screenx-docs
description: Write and update ScreenX documentation - the user README, docs/TECHNICAL.md, and AGENTS.md - and the screenshots in docs/images. Also carries the pre-release checklist and the list of files that need a version bump. Use when editing any of those files, adding a screenshot, adding a feature that needs documenting, cutting a release or tagging one, or when asked about documentation style, ASD-STE100 wording, or which files carry the version.
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

Line numbers go stale. After editing any Rust, re-verify them:

```sh
wc -l app/src/*.rs core/src/*.rs app/build.rs
grep -nE "^(pub )?fn |^(pub )?struct |^const |^enum |^pub enum " app/src/*.rs core/src/*.rs
```

Update the counts in the file map and the "Orientation" bullet (test totals)
whenever they change. Editing a file near its top moves every anchor below it,
so re-derive the numbers rather than adjusting the ones you remember changing.

---

## 4. Screenshots

**There is no generator. They are taken by hand, with ScreenX.**

The webview build had a `--docshots` mode that drew a mock window and shot it
unattended. It was deleted with the rest of that build after v1.0.0, along with
`package.json` and every `npm` script. Do not resurrect the mode for one
picture; the three images in `docs/images/` took less time to take than the mode
took to describe.

Current images: `editor.png`, `overlay.png`, `tray-menu.png`.

### Take one — a job for a human, not an agent

Every image is a photograph of somebody's real desktop, and only the person at
the machine can see what is on it. An agent asks for the picture and leaves the
placeholder below until it arrives.

1. Put something on screen that is safe to publish. Check it against the privacy
   rules below; the old generator enforced them and nothing does now.
2. Run ScreenX and capture the thing you want to show.
3. Save it into `docs/images/` under the existing name.
4. Reference it in the README with descriptive alt text.

The overlay and the tray menu can only be photographed, not generated: the
overlay must show real windows to mean anything, and the tray menu is drawn by
the OS.

### Two privacy rules — do not remove them

These used to be enforced by the generator. Nothing enforces them now, so they
are the reason to look at an image twice before committing it.

**Publish nothing private.** Every image in `docs/images/` is a photograph of a
real desktop. Read what is in the window, the tab bar, the sidebar and the
notification area before you save it.

**Substitute identifying values.** No home path with a real user name, no
machine-specific configuration, no shortcut that is not the shipped default.
`/Users/you/...` is the form the documentation already uses.

### The placeholder to leave

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
| Test counts | `docs/TECHNICAL.md` section 14; `AGENTS.md` "Orientation" |
| Version number | Four files, and they must agree. See [Releasing](#7-releasing) |
| Release artifact names | `.github/workflows/release.yml` (build steps *and* the notes heredoc); `Casks/screenx.rb`; `README.md` install steps |
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

Nothing here uses `npm`. There is no `package.json` any more — it went with the
webview build after v1.0.0, so any instruction naming an `npm` script is stale.

```sh
# Counts. Compare against AGENTS.md "Orientation" and TECHNICAL.md section 14.
cargo test --manifest-path core/Cargo.toml 2>&1 | grep 'test result'
cargo test --manifest-path app/Cargo.toml 2>&1 | grep 'test result'

# Line counts and anchors for the AGENTS.md file map and its tables.
wc -l app/src/*.rs core/src/*.rs app/build.rs
grep -nE "^(pub )?fn |^(pub )?struct |^const |^enum |^pub enum " app/src/*.rs core/src/*.rs

# Version agreement across every file that carries it.
grep -m1 '^version' app/Cargo.toml core/Cargo.toml
grep -m1 -A1 'name = "screenx"' app/Cargo.lock
grep -m1 '^  version' Casks/screenx.rb

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

Anything the docs say is verified must name how. A claim with no check behind it
is the one that goes stale without anyone noticing. There is no `selfcheck` any
more — it belonged to the webview build — so a claim points at a `cargo test`
case, or at the sentence saying it was checked by hand and on what.

---

## 7. Releasing

**Run this section before every tag.** Not after, and not "when something looks
wrong": a tag is public the moment it is pushed, and the release workflow fires
on it.

### Bump the version in four places, and they must agree

| File | How |
| --- | --- |
| `app/Cargo.toml` | Edit `version` |
| `core/Cargo.toml` | Edit `version` — the two crates are released together |
| `app/Cargo.lock` | Not by hand. `cargo build` rewrites it |
| `core/Cargo.lock` | Not by hand. Needs its **own** build — see below |

`app/Cargo.lock` carries `screenx` and `screenx-core`; `core/Cargo.lock` carries
only `screenx-core`. They are separate lockfiles, so building the app updates
one and leaves the other stale. A stale lockfile is not caught by any test.

```sh
cargo build --release --manifest-path app/Cargo.toml
cargo build --manifest-path core/Cargo.toml
```

`Casks/screenx.rb` also carries a version, but **do not edit it**. The workflow
rewrites it and commits to master, and only for a tag with no `-` in it, so a
release candidate never becomes the `brew` version. Editing it by hand fights
the workflow.

`app/bundle.sh` reads the version out of `app/Cargo.toml` rather than repeating
it. It was written there once and was still `0.3.0` long after the crate moved
on, which is why it is derived now. Do not reintroduce a literal.

```sh
# Everything that carries the version, in one place. All must match.
grep -m1 '^version' app/Cargo.toml core/Cargo.toml
grep -A1 -E 'name = "screenx(-core)?"' app/Cargo.lock core/Cargo.lock | grep version
```

### Then, in order

1. `cargo test` both manifests. Green, and **no warnings** — a warning that says
   "will become a hard error in a future release" is a countdown, not noise.
2. `./app/bundle.sh` and check the bundle: `Contents/MacOS/` holds **both**
   `screenx` and `screenx-capture`, and `codesign --verify --strict` passes.
3. Verify by hand on macOS the way `AGENTS.md` "Verifying a macOS build by hand"
   says — launched with `open -a`, never from a terminal.
4. Re-read the README install steps against what the workflow actually uploads.
   Artifact names live in three files and have drifted before: the README
   described a `.dmg` for months while the workflow produced only a `.zip`.
5. Check the tree is clean of things that break `actions/checkout`:
   `git ls-files -s | awk '$1=="160000"'` must print nothing.
6. Tag, push, and **then read the run log** — not just its green tick. A job can
   succeed with warnings that matter:
   `gh run view <id> --log | grep -iE '##\[(error|warning)\]|^warning'`
7. Download what was published and open it. The workflow succeeding is not
   evidence the artifact is right.

### The release notes are documentation

They live in the `notes.md` heredoc inside `.github/workflows/release.yml`, and
nothing else validates them. They are the install instructions most people
actually read, so they follow the README's register — ASD-STE100, one
instruction per sentence — and they must name the current artifacts.

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
