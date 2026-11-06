#!/usr/bin/env python3
"""Read a pulled state directory and say whether the session behaved.

    Build/tests/log-checklist.py /mnt/d/feelies/logs

This is the reader the UAT checklist was missing. A person plays a session, pulls the state
directory off the machine, and this says what happened — the same questions a person would ask
while reading the log by hand, asked in the same order, with the answer and the evidence.

It reads four things:

    _session/agent-*.jsonl    what the companion thought, said, and sent
    _session/core-*.jsonl     what the game was asked, and what it answered
    <stem>/playback.log       the game's own record of the attempt
    <stem>/saves/<stem>.json  the checkpoint, and the turn it was taken at

Every check is a statement that should be true of a well-behaved session, and the interesting ones
are the ones a broken session fails. Each prints PASS or FAIL with a count, and a failure prints
the lines that caused it — a log checker that only says "failed" sends you back to the log.

Three items on the UAT checklist do not appear here, and it is worth saying which and why rather
than letting a run of green ticks imply more than it proves. The commands that test them
(`/switch`, `/save <name>`, `/restore`) are parsed only out of a *player's* line, and `--stdio` opens
an agent-style seat whose input goes straight to the story — so for a long time nothing but a person
at a keyboard could exercise them. `Build/tests/player-commands.sh` closed that, by way of a
`player_line` message on the development transport, and it asserts the mechanics there: the verb
reached the core, the file is on disk, the fork was recorded. What a log still cannot settle is how
it *read* — whether the companion's sentence after a restore made sense, or whether the halt landed
at a screen boundary — and those stay on the manual list rather than being quietly dropped.
"""

from __future__ import annotations

import json
import pathlib
import re
import sys
from collections import defaultdict

# ---------------------------------------------------------------- reading the directory


class Logs:
    """Every line in the pulled directory, parsed, and indexed by what it is."""

    def __init__(self, root: pathlib.Path) -> None:
        self.root = root
        self.malformed: list[str] = []
        self.agent: list[dict] = []
        self.core: list[dict] = []
        self.files: list[pathlib.Path] = []
        for path in sorted(root.rglob("*.jsonl")):
            if path.name.startswith(("agent-", "core-")):
                self.files.append(path)
        for path in self.files:
            into = self.agent if path.name.startswith("agent-") else self.core
            for n, line in enumerate(path.read_text(errors="replace").splitlines(), 1):
                if not line.strip():
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    self.malformed.append(f"{path.name}:{n}")
                    continue
                obj["_file"] = path.name
                into.append(obj)

    def kinds(self, side: list[dict], kind: str) -> list[dict]:
        return [o for o in side if o.get("kind") == kind]

    def game_dirs(self) -> list[pathlib.Path]:
        """The games this directory holds — a directory with a playback log is a game."""
        return sorted(p.parent for p in self.root.rglob("playback.log"))


# ---------------------------------------------------------------- the checks

# The two answers dFrotz gives when a game wants a key rather than a sentence. A story that draws
# its own menu — border_zone's chapter list, zork0's instructions — never says anything else, and a
# companion that reads only the story output sees an empty screen and types a verb at it.
KEY_WANTED = re.compile(r"press\s+[\"']?\d|press\s+[\"']?[A-Z][\"']?\s*[,)]|type\s+a\s+(number|digit)", re.I)
VERB = re.compile(r"^[a-z]{3,}\b", re.I)


def check_lines(logs: Logs) -> tuple[bool, str, list[str]]:
    """Every line is a complete JSON object."""
    if logs.malformed:
        return False, f"{len(logs.malformed)} line(s) are not JSON", logs.malformed[:5] + ["..."]
    return True, f"{len(logs.agent) + len(logs.core)} lines, all JSON", []


def check_screen(logs: Logs) -> tuple[bool, str, list[str]]:
    """Every turn carries the interpreter's screen, not only the story's words.

    A story that paints rather than prints produces output that looks empty, so a companion given
    only the output is blind. The screen is the interpreter's own view of the display, and it is the
    thing that makes a painted game legible. This is the regression guard for the field going
    missing between the core that sends it and the agent that reads it.
    """
    turns = logs.kinds(logs.core, "turn")
    if not turns:
        return False, "no turns in the log at all", []
    blank = [f'turn {t.get("turn")} {t.get("_file")}' for t in turns if not (t.get("screen") or "").strip()]
    if blank:
        return False, f"{len(blank)} of {len(turns)} turn(s) carry no screen", blank[:5]
    return True, f"all {len(turns)} turn(s) carry the screen", []


def check_acts_landed(logs: Logs) -> tuple[bool, str, list[str]]:
    """The companion never speaks into a screen that is not listening.

    `act_missed` is written when the model produced words but called no tool — it answered without
    acting. Each one is a turn the player paid for and the game never saw, and they cluster exactly
    where the screen was still scrolling: the companion talked over the game.
    """
    missed = logs.kinds(logs.agent, "act_missed")
    acts = logs.kinds(logs.agent, "act")
    if not missed:
        return True, f"every turn acted, none answered with words alone ({len(acts)} act(s))", []
    evidence = [
        f'{m.get("ts")}  purpose={m.get("purpose")}  said {m.get("speech_chars")} char(s), '
        f'thought {m.get("thinking_chars")}, called nothing'
        for m in missed[:5]
    ]
    return False, f"{len(missed)} turn(s) answered with words and no action, against {len(acts)} act(s)", evidence


def check_wait_for_the_screen(logs: Logs) -> tuple[bool, str, list[str]]:
    """Nothing is typed at the game while it is still painting.

    The turn payload says whether the story is at a prompt (`awaiting_command`). While it is not,
    the screen is still arriving and anything typed is either lost or lands in the middle of an
    animation. This is the machine half of the same complaint `act_missed` measures from the
    companion's side.
    """
    bad = []
    for obj in logs.core:
        kind = obj.get("kind")
        if kind == "turn":
            last = obj
        elif kind == "agent_game_input" and obj.get("source") == "agent":
            if last is not None and not last.get("awaiting_command", True):
                bad.append(f'{obj.get("ts")}  typed "{obj.get("line")}" while the screen was still arriving')
    if bad:
        return False, f"{len(bad)} command(s) sent before the screen had settled", bad[:5]
    return True, "every command waited for the prompt", []


def check_menus(logs: Logs) -> tuple[bool, str, list[str]]:
    """A menu is answered with a key, not a word.

    When the game answers an input by *explaining how to answer it*, the input was the wrong shape.
    border_zone's chapter menu takes 1, 2, 3 or R; a companion that replies "xyzzy" gets the
    instructions back and has spent a turn to learn nothing.
    """
    bad = []
    for p in logs.kinds(logs.agent, "game_prompt"):
        output = p.get("output") or ""
        sent = (p.get("input") or "").strip()
        if KEY_WANTED.search(output) and VERB.match(sent) and not sent.isdigit():
            bad.append(
                f'turn {p.get("turn")}: sent "{sent}", the game replied '
                f'"{output.strip()[:60]}" — it wanted a key'
            )
    if bad:
        return False, f"{len(bad)} menu(s) answered with a word", bad[:5]
    return True, "no menu was answered with a word", []


def check_errand_budget(logs: Logs) -> tuple[bool, str, list[str]]:
    """An errand the player handed over is not capped at a handful of steps.

    A mission with a small budget stops in the middle of the job and hands the keyboard back, which
    reads as the companion losing its thread: it gets most of the way somewhere and then stops for
    no reason the player can see. A delegated errand is bounded by cost, not by step count.
    """
    small = [
        f'{m.get("ts")}  "{str(m.get("goal"))[:50]}..."  budget {m.get("budget")}'
        for m in logs.kinds(logs.agent, "mission_start")
        if str(m.get("source", "")).startswith("delegated") and (m.get("budget") or 0) < 100
    ]
    if small:
        return False, f"{len(small)} delegated errand(s) capped below the cost bound", small[:5]
    return True, "no delegated errand was capped by step count", []


def check_errands_end_well(logs: Logs) -> tuple[bool, str, list[str]]:
    """An errand ends by reporting back, not by running out."""
    ends = [m for m in logs.kinds(logs.agent, "mission_end") if not (m.get("reason") or "").startswith("stand down")]
    lost = [
        f'{m.get("ts")}  {m.get("steps")}/{m.get("budget")} step(s)  reason: {m.get("reason")}'
        for m in ends
        if m.get("reason") != "the companion reported back"
    ]
    if lost:
        return False, f"{len(lost)} of {len(ends)} errand(s) ended without reporting back", lost[:5]
    return True, f"all {len(ends)} errand(s) ended by reporting back", []


def check_nothing_unread(logs: Logs) -> tuple[bool, str, list[str]]:
    """Nothing the core sent was dropped on the floor.

    A `warn` line is the protocol saying it did not understand something. One is a bug; five is a
    payload that has never once been read, which is how the screen went missing for a whole UAT.
    """
    warns = logs.kinds(logs.agent, "warn")
    if warns:
        return False, f"{len(warns)} payload(s) arrived and were not understood", [
            f'{w.get("ts")}  {w.get("msg")}' for w in warns[:5]
        ]
    return True, "every payload the core sent was understood", []


def check_saves_are_named(logs: Logs) -> tuple[bool, str, list[str]]:
    """Every save the player asked for by name exists on disk.

    `/save kitchen` that reports nothing and writes nothing is worse than a refusal: the player
    believes they have a checkpoint. A save is either a file you can point at or a sentence saying
    why not.
    """
    slots = {p.stem for p in logs.root.rglob("saves/*/*.sav")}
    asked = [
        (c.get("args") or "").strip()
        for c in logs.kinds(logs.core, "core_command")
        if c.get("verb") == "save" and (c.get("args") or "").strip()
    ]
    missing = [f'save "{name}" was asked for and no {name}.sav exists' for name in asked if name not in slots]
    if missing:
        return False, f"{len(missing)} named save(s) went nowhere", missing[:5]
    return True, f"{len(asked)} named save(s) asked for, all present", []


def check_saves_stay_home(logs: Logs) -> tuple[bool, str, list[str]]:
    """A save never leaves the state directory, and never belongs to another game.

    Two ways this goes wrong and both have: a name with `..` in it writes outside the state
    directory, and a checkpoint taken during a switch can be written to the *previous* game's path,
    which silently overwrites a different game's progress.
    """
    bad = []
    for path in logs.root.rglob("playback.log"):
        stem = path.parent.name
        for line in path.read_text(errors="replace").splitlines():
            # A playback line is the host tool call the core made, echoed onto the screen:
            # `\t save /state/saves/<stem>/<stem>.sav>save` — the last `>` is the echoed prompt.
            parts = line.split()
            if len(parts) < 3 or parts[0] != "\\t" or parts[1] not in ("save", "restore"):
                continue
            target = parts[2].split(">")[0]
            if ".." in target:
                bad.append(f"{path.parent.name}/playback.log: {line[:70]} — escapes the state directory")
            elif "/saves/" in target:
                owner = target.split("/saves/")[-1].split("/")[0]
                if owner and owner != stem:
                    bad.append(
                        f"{path.parent.name}/playback.log: {line[:70]} — that is {owner}'s checkpoint"
                    )
    if bad:
        return False, f"{len(bad)} save or restore went to the wrong place", bad[:6]
    return True, "every checkpoint stayed inside its own game", []


def check_turn_belongs_to_game(logs: Logs) -> tuple[bool, str, list[str]]:
    """A checkpoint knows its own game, its own turn, and why it was taken."""
    bad = []
    for path in logs.root.rglob("saves/*.json"):
        try:
            record = json.loads(path.read_text())
        except (OSError, json.JSONDecodeError) as exc:
            bad.append(f"{path.relative_to(logs.root)}: {exc}")
            continue
        stem = path.parent.parent.name
        if record.get("game") != stem:
            bad.append(f'{path.relative_to(logs.root)}: says it is {record.get("game")!r}, it is {stem!r}')
        elif not isinstance(record.get("turn"), int) or record["turn"] < 1:
            bad.append(f'{path.relative_to(logs.root)}: turn is {record.get("turn")!r}')
        elif not record.get("reason"):
            bad.append(f"{path.relative_to(logs.root)}: does not say why it was taken")
    if bad:
        return False, f"{len(bad)} checkpoint record(s) are wrong", bad[:5]
    return True, "every checkpoint names its game, its turn and its reason", []


def check_notebooks(logs: Logs) -> tuple[bool, str, list[str]]:
    """Every game that was played has a notebook, and the write-up was written if it was asked for."""
    bad = []
    played = [d for d in logs.game_dirs() if d.name != "_session"]
    for d in played:
        notebook = d / "notebook.md"
        if not notebook.exists() or not notebook.read_text(errors="replace").strip():
            bad.append(f"{d.name}: played, but no notebook")
    asked = {r.get("stem") for r in logs.kinds(logs.agent, "report_asked")}
    written = {r.get("stem") for r in logs.kinds(logs.agent, "report_written")}
    for stem in sorted(asked - written):
        bad.append(f"{stem}: a write-up was asked for and never finished")
    if bad:
        return False, f"{len(bad)} record(s) missing", bad[:6]
    return True, f"{len(played)} game(s) played, each with a notebook", []


CHECKS = [
    ("lines", check_lines, "every log line is a complete record"),
    ("screen", check_screen, "the interpreter's screen reaches the companion"),
    ("acts-landed", check_acts_landed, "the companion acts rather than talking over the game"),
    ("waited", check_wait_for_the_screen, "no command is typed while the screen is arriving"),
    ("menus", check_menus, "a menu is answered with a key, not a word"),
    ("budget", check_errand_budget, "a delegated errand is not capped by step count"),
    ("errands", check_errands_end_well, "an errand ends by reporting back"),
    ("protocol", check_nothing_unread, "nothing the core sent was dropped"),
    ("named-saves", check_saves_are_named, "a save the player named is on disk"),
    ("saves-home", check_saves_stay_home, "a checkpoint stays inside its own game"),
    ("checkpoints", check_turn_belongs_to_game, "a checkpoint knows its game, turn and reason"),
    ("notebooks", check_notebooks, "a game that was played has a notebook"),
]

# The checklist items a pulled log cannot settle. Printed, never silently dropped: a run that says
# "all checks passed" should also say what that does not cover.
MANUAL = [
    ("forms", "a form is filled in one field at a time, which needs somebody watching the screen"),
    ("fork-note", "whether the companion's own sentence after a restore reads well, which is judgement"),
    ("halt", "a line beginning with a space stops the companion at a screen boundary, and no seat "
             "can send a keystroke signal over this transport yet"),
    ("front-door", "the pages are visited in a browser, not by the core"),
]


def main(argv: list[str]) -> int:
    if len(argv) < 2:
        print(__doc__.strip())
        return 2
    root = pathlib.Path(argv[1])
    if not root.is_dir():
        print(f"log-checklist: {root} is not a directory")
        return 2

    logs = Logs(root)
    print(f"reading {root}")
    print(f"  {len(logs.files)} log file(s), {len(logs.agent) + len(logs.core)} lines")
    games = [d.name for d in logs.game_dirs() if d.name != "_session"]
    print(f"  game(s): {', '.join(games) if games else 'none'}")
    print()

    failed = []
    for name, fn, statement in CHECKS:
        ok, detail, evidence = fn(logs)
        print(f"  {'PASS' if ok else 'FAIL'}  {name:<13} {statement}")
        print(f"        {detail}")
        if not ok:
            failed.append(name)
            for line in evidence:
                print(f"          {line}")
        print()

    print("  These are on the checklist and a pulled log cannot settle them. Printed rather than")
    print("  passed quietly, because a run of green ticks should say what it does not cover:")
    for name, what in MANUAL:
        print(f"    manual  {name:<12} {what}")
    print()

    if failed:
        print(f"log-checklist: {len(CHECKS) - len(failed)} passed, {len(failed)} failed")
        for name in failed:
            print(f"  - {name}")
        return 1
    print(f"log-checklist: {len(CHECKS)} passed")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
