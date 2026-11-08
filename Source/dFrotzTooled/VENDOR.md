# dFrotzTooled — the interpreter, vendored

The daemon drives one interpreter, and the whole game boundary is a consequence of that
interface: how a turn is framed, the save protocol, the parser vocabulary, what
may be matched in the game's output and what never may be. That interpreter came from
Debian's `frotz` package. It lives here now so that it can be **changed** — the point of
this directory is the tool surface it did not have (`G5`–`G7`, now built) — and the first step
is a build of the same interpreter we have been running.

## What this is

| | |
|---|---|
| Upstream | Frotz 2.54, released 2022-04-13 |
| Taken from | Debian's source package `frotz 2.54+dfsg-3` (bookworm) — the version the container has been running |
| Original tarball | `frotz_2.54+dfsg.orig.tar.xz`, sha256 `4202b6545a2278a369ad5b6e7343b11849424b1b05cc7a1416961c5be33dd0b4` |
| Patch applied | `patches/debian-uint32.patch`, sha256 `7dffcc56f87a4318567e467a9f1b7b75c0d8cdda6e400b779e02af92dc6ab577` |
| Licence | GNU General Public License v2 — `COPYING` in this directory |

`src/` is the shared engine; `src/dumb/` is the interface we build; `DUMB` (a file, at the
root) is upstream's own essay on that interface, which is worth reading before touching it.

**The one patch**, kept in `patches/` and applied: *"Use uint32_t instead of ulong"*
(Stephen Kitt), which removes a `ulong` macro that collides with Debian's hardening flags.
It touches four files, all under `src/sdl/`, so it cannot reach the interface we use. It is
carried because the tree here should be the tree that produced the binary we have measured,
not because the dumb build needs it.

## How it is built

`make dumb`, and nothing else. It produces one binary — **`dfrotz`, at the root of this
directory**, not under `src/dumb/` — and it needs a C compiler and make: no ncurses, no
pkg-config, no sound libraries. The dumb interface links the common library, the dumb
library and Blorb (`DFROTZ_LIBS` in the `Makefile`), which is why the binary depends on
libc alone. The `curses`, `sdl` and `x11` targets are not built here; Debian builds all
four and ships three of them, and we need one.

`Dockerfile` Stage 2c does exactly that and copies the result to `/usr/games/dfrotz` — the
path `docker-compose.yml` already names, so nothing else moves. The binary is left
unstripped (about 450 KB against Debian's 120 KB) on purpose: we are about to work on it.

## The policy: no upstream sync

This copy is frozen at the version we run. It is not tracked against upstream and it has no
rebase story. When the interpreter is changed, the change goes in as a patch under
`patches/` — with this tree left as upstream-plus-Debian — so the delta from what we were
running stays visible in one place. Moving to a later upstream release is a deliberate
decision to change versions, never a merge.

## The baseline: what "the same interpreter" means here

Deployed to the live instance on **2026-09-18** and compared against the interpreter it
replaced. Four probes, written on 2026-09-14 to pin this interface, were run before and
after the swap — `ai_scratchspace/probe-*.sh`, with their captures beside them as
`*.out.txt` (they drive the interpreter directly and read nothing but the stories in
`assets/games/`, so they need those files present):

| Probe | What it pins |
|---|---|
| `probe-input-surface.sh` | the version banner, the absence of a `/` command set, a character read answered by an empty line, and what a chapter menu and a licence form print |
| `probe-frotz-commands.sh` | which `\`-prefixed interface lines exist, and that undo is a hotkey rather than a verb |
| `probe-line-types.sh` | the line-type markers, and that they are off by default |
| `probe-undo.sh` | `\U` undoes a turn, and `/undo` does not |

All four came back **byte-for-byte identical** to the capture taken under Debian's package
when the build was first swapped in. `ai_scratchspace/probe-verify.sh` re-runs that comparison
against the interpreter built here, and `ai_scratchspace/probe-tool-channel.sh` plus
`probe-tool-saves.sh` are the same idea for what is new.

**That is the test to run after any change to this source**: re-run the four probes and
compare with the captures, then run everything else (`Build/check.sh` — the three suites, the
image and the container suite; README §The checks). A change
that alters what the daemon sees at the game boundary should change one of them, and should say
so. One capture has changed deliberately since the tool surface landed: `probe-frotz-commands`
prints the usage text, and there is a new `-T` line in it.

If this build ever behaves differently from the package it replaced on the *default* path, the
first suspects are the flags rather than the source: Debian compiles with its hardening set
(`-O2`, `-fstack-protector-strong`, `-D_FORTIFY_SOURCE=2`), this `Makefile` uses its own
(`-O3 -g -Wall -std=c99`), and Debian's `+dfsg` repack removes files that none of the targets we
build would use.

## The tool surface

What the vendoring was for (`G5`–`G7`). It is **additive and opt-in**: with no `-T` the
interface behaves as upstream's does, which is what lets the daemon run beside a stock `dfrotz`
with no special case in the game path.

```text
dfrotz -m -q -p -R <saves>/<stem> -T <story>
```

* **Events go to stderr**, one JSON object per line.
* **Commands come in on stdin**, in the `\` namespace the interface already had: `\t save <path>`,
  `\t restore <path>`, `\t clear`, `\t target`, `\t status`, `\t screen`, `\t stats`, `\t ping`,
  `\t hello`, `\t help`.

Both of those streams already exist for anyone driving the interpreter over pipes, so the channel
needs no extra descriptor, no socket and no platform-specific wiring — and the commands are
typable by hand at a terminal, which is how the interface's own `\`-commands have always worked.

| Event | Says |
|---|---|
| `hello` | which interpreter this is, which switches it was given, which story it holds, and which tools it offers |
| `input_wanted` | the interpreter is at a keyboard read with everything flushed; how many bytes it has written to stdout, whether it wants a line or a key, whether this is the first input since a restore, and — for Versions 1 to 3 — the numbers its status line is made of |
| `input` | the line or key the story was actually handed |
| `parse` | what the story's **own parser** did with the line: its word count, and for each word the dictionary entry or a zero, the letter count, the position, and the word itself |
| `saved` / `restored` | which file was opened, whether it worked, how big it is, and whether the host named it |
| `command` | one command accepted (exactly one per command, so the host can tell a refreshed prompt from a turn boundary) |
| `screen` | the screen itself, on request: every row of cells by position, the cursor, the hidden region, the compression mode — see below |
| `stats` | frames emitted, frames dropped, bytes written — the channel's own health |
| `quit`, `error`, `fatal` | the interpreter is leaving, or refusing something |

Three things this deliberately does **not** do. It never sends the screen **unasked**: a turn's text
still goes to stdout and nowhere else, so the panes, the replay buffer and `screen.rs` are
untouched. It never carries story knowledge: `parse` is the story's own parse buffer read back, and
the status numbers are global variables the interpreter already draws. And it never matches, guesses
at or interprets a word of the game's text in either direction.

**The screen had to be askable, and Bureaucracy is why.** The dumb interface prints a row only when
it holds a cell that has **changed and is not blank** (`dumb_show_screen`, `src/dumb/doutput.c`), so
a form drawn once and left alone is on the screen for the whole game and in the output for none of
it. Measured: Bureaucracy's licence application — `SOFTWARE LICENCE APPLICATION`, `Last name:`,
`Your sex (M/F):`, and the rest — appears in no probe capture at any compression mode, because it is
never printed, and its rows are full of *styled* blanks (a reverse-video space is printable, a plain
one is not, which is where the runs of spaces in the captures come from). `\t screen` is therefore
the only way to answer *what is this prompt about* for a story that draws rather than narrates, and
the cursor is part of the answer: the form's fields sit two to a row, and the cursor's **column** is
what says which one is live.

**The `-R` truncation is fixed here.** Upstream's `os_read_file_name` appends the basename with
`strncat(file_name, tempname, strlen(file_name) - strlen(tempname) - 1)` — a difference of
lengths where the third argument has to be the room left in the destination. Both operands are
`size_t`, so the value wraps when the basename is longer than the directory and cuts the name when
the directory is longer than the basename but shorter than twice it, which is why a save survived
on some paths and not others. It is one line, and it is the reason a save can now be named
anything at all.

## What is not done yet

* The **screen** is exposed as far as the host needs it: `\t screen` reports the rows that hold
  content, by position, with the cursor, the hidden region and the compression mode
  (the screen rules at the game boundary, `G21`–`G22`).
  What is deliberately not taken is the cell model as a second source of truth: the stream stays
  what the turn is, and the screen is what a story which draws rather than narrates had to say.
* **In-memory snapshots** are not exposed as a tool (`T23` — the surface is closed). Undo works and is
  reported (`\U`, as it always did), but the host cannot address a snapshot by index.
and the four probes above are what says so.
