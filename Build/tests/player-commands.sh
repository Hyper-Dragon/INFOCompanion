#!/usr/bin/env bash
#
# The player's own commands, driven by a machine (issue: the checklist's manual items).
#
# Run inside the image:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/player-commands.sh
#
# `/switch`, `/save <name>` and `/restore` are parsed out of a **player's** line, and `--stdio` opens
# an agent-style seat whose `game_input` goes straight to the story. So for as long as this suite has
# existed, the commands a player types to manage their own saves could not be driven by anything but
# a person at a keyboard — four items on the session checklist were manual because of it, and the
# 2026-09-19 UAT found the defect in two of them the hard way.
#
# `player_line` closes that: a line that takes the *same route* an SSH pane's line takes, all the way
# to the same handler. Nothing here re-implements parsing, so a test cannot pass a command the
# interactive path would refuse, and a refusal is as much a result as a success.
#
# What this holds the core to:
#
#   1. `/save <name>` leaves a file at `saves/<stem>/<name>.sav` and says which file it wrote;
#   2. `/saves` lists what is there;
#   3. a name that tries to leave the state directory is refused, and nothing is written outside;
#   4. `/restore <name>` comes back, on the same path;
#   5. `/bar` reports the interpreter's own status line, which no pane can hold (`G23`).
#
# It uses its own state directory, so a save from real play cannot change what it observes.

set -uo pipefail

. "$(dirname "${BASH_SOURCE[0]}")/stories.sh"
GAME="$(story_for "${1:-}")"
STEM="$(basename "$GAME")"; STEM="${STEM%%.*}"
BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "FAIL: $*"
    echo "--- stdout (last 12 lines) ---"
    tail -12 "$WORK/out.jsonl" 2>/dev/null
    echo "--- stderr (last 12 lines) ---"
    tail -12 "$WORK/err.log" 2>/dev/null
    exit 1
}

[ -f "$GAME" ] || fail "no story file at $GAME"
echo "== the player's own commands, driven by a machine"
echo "== story under test: $GAME"

{
    printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"t","payload":{"protocol_version":1,"client_name":"player-commands"}}'
    sleep 1
    # Something to save: a game with no progress has no checkpoint to copy.
    printf '%s\n' '{"v":1,"type":"game_input","seq":2,"ts":"t","payload":{"line":"look"}}'
    sleep 2
    # 1. A named save.
    printf '%s\n' '{"v":1,"type":"player_line","seq":3,"ts":"t","payload":{"text":"/save kitchen"}}'
    sleep 2
    # 2. What is there.
    printf '%s\n' '{"v":1,"type":"player_line","seq":4,"ts":"t","payload":{"text":"/saves"}}'
    sleep 1
    # 3. A name that tries to leave.
    printf '%s\n' '{"v":1,"type":"player_line","seq":5,"ts":"t","payload":{"text":"/save ../escape"}}'
    sleep 2
    # Move on, so the restore has somewhere to come back from.
    printf '%s\n' '{"v":1,"type":"game_input","seq":6,"ts":"t","payload":{"line":"look"}}'
    sleep 2
    # 4. And back.
    printf '%s\n' '{"v":1,"type":"player_line","seq":7,"ts":"t","payload":{"text":"/restore kitchen"}}'
    sleep 3
    # 5. The session report, which is what it always was. The interpreter's own line used to be a
    # paragraph of it and is now `/bar`'s alone, so `/status` is still driven here: what it asserts
    # below is about the real `/status`, and a command nobody sends asserts nothing.
    printf '%s\n' '{"v":1,"type":"player_line","seq":8,"ts":"t","payload":{"text":"/status"}}'
    sleep 2
    # 5b. The interpreter's own status line, which nothing but the interpreter can state: it is drawn
    # into row one by cursor addressing, so it is in no turn's output and no pane has ever held it.
    printf '%s\n' '{"v":1,"type":"player_line","seq":9,"ts":"t","payload":{"text":"/bar"}}'
    sleep 2
} | timeout 120 "$BINARY" --stdio --game "$GAME" --state-dir "$WORK/state" \
    >"$WORK/out.jsonl" 2>"$WORK/err.log"
status=$?
[ "$status" -eq 0 ] || fail "the daemon exited with $status (124 = timed out)"

SAVES="$WORK/state/saves/$STEM"
SESSION=$(ls -t "$WORK/state/logs/"core-*.jsonl 2>/dev/null | head -1)
[ -n "$SESSION" ] || fail "the session log was not written"

# A protocol seat is not a pane, and it is worth being exact about what that means: it receives the
# *facts* — `checkpoint`, `restore` — and not the core's sentences. `/saves` answering "saves for
# zork1…" goes to terminal panes, and a reader looking for that sentence here would be looking in the
# wrong place. So the commands themselves are asserted where the core records them, in the session
# log, and their effects where they land, on disk.

# 1. Every one of the commands reached the command namespace. This is the thing that was
# impossible before `player_line`: `/`, and the parse behind it, existed only for a player's line.
#
# The log writes its keys in alphabetical order, so `args` comes before `verb` on the line — the
# pattern below matches either order rather than the one it happens to be written in today.
reached() { # reached <verb> <args>
    grep -qE "\"verb\":\"$1\".*\"args\":\"$2\"|\"args\":\"$2\".*\"verb\":\"$1\"" "$SESSION"
}
reached save kitchen || fail "/save kitchen never reached the core as a command"
reached saves "" || fail "/saves never reached the core as a command"
reached save "../escape" || fail "/save ../escape never reached the core as a command"
reached restore kitchen || fail "/restore kitchen never reached the core as a command"
reached status "" || fail "/status never reached the core as a command"
reached bar "" || fail "/bar never reached the core as a command"
echo "   /save, /saves, /status, /bar, /restore and the refused name were all parsed as the player's own commands"

# 2. The file is where it should be, with a record beside it. This is the whole of the 2026-09-19
# complaint: `/save incell` reported nothing and wrote `wishbringer.sav`, so a player believed they
# had a checkpoint they did not have.
[ -s "$SAVES/kitchen.sav" ] || fail "no saves/$STEM/kitchen.sav — a named save wrote nothing"
[ -s "$SAVES/kitchen.json" ] || fail "no record beside the save, so it cannot be described later"
echo "   /save kitchen wrote $SAVES/kitchen.sav, with its record beside it"

# 2b. And the interpreter was pointed at *that* file, not at the game's own checkpoint — which is the
# half of the 2026-09-20 complaint that no file can show. The save existed and was correct; the
# screen, which is the only place a save becomes visible, said `zork1.sav`, so a player who had just
# typed `/save kitchen` could not tell that anything had happened. The core names the file it asks
# for in one line of the session log, and that line is the regression this pins: before, it named the
# checkpoint and the name only turned up in a file copy that nothing but the filesystem could see.
# (`\t` is written as two characters in the log — a JSON-escaped backslash and a `t`.)
grep -q "saves/$STEM/kitchen.sav" "$SESSION" \
    || fail "the core never pointed the interpreter at the save the player named"
echo "   and the interpreter was told to write the name the player gave, not the checkpoint"

# 3. The core told the seat what it did, in the form a machine reads.
grep -q '"event":"checkpoint"' "$WORK/out.jsonl" || fail "no checkpoint event reached the seat"
grep -q 'saved kitchen' "$WORK/out.jsonl" || fail "the checkpoint event did not name the save"
grep -q '(manual)' "$WORK/out.jsonl" || fail "the checkpoint event did not say why it was taken"
echo "   and reported it as a checkpoint event naming the save and its reason"

# 4. The escape was refused, and refused by writing nothing anywhere. The prose refusal goes to panes,
# so what is asserted is the part that matters: a name that tries to leave the state directory leaves
# nothing behind, inside or outside it.
if [ -e "$WORK/escape.sav" ] || [ -e "$WORK/state/escape.sav" ]; then
    fail "a name with .. in it wrote outside a game's own folder"
fi
escaped=$(find "$WORK/state" -name 'escape*' 2>/dev/null | wc -l)
[ "$escaped" -eq 0 ] || fail "a name with .. in it wrote $escaped file(s) inside the state directory"
stray=$(find "$WORK/state" -name '*.sav' -not -path "*/saves/*" 2>/dev/null | wc -l)
[ "$stray" -eq 0 ] || fail "$stray save(s) landed outside a game's own folder"
echo "   /save ../escape wrote nothing, anywhere"

# 5. The restore came back and said so — naming the slot and the fork, which is the fact a companion
# has to be told and a player has to be able to see.
grep -q '"event":"restore"' "$WORK/out.jsonl" || fail "/restore reported nothing to the seat"
grep -q 'kitchen' "$WORK/out.jsonl" || fail "the restore event did not name the slot it came from"
grep -q 'fork' "$WORK/out.jsonl" || fail "the restore event did not say the world forked"
echo "   /restore kitchen came back, naming the save and the fork"

# 6. `/bar` reported the interpreter's own line — the numbers a player reads on row one without
# being told to, and which no pane can hold (02_game_boundary.md §4.3 (`G23`)). A protocol seat is given
# facts and not the core's sentences, so the answer is read back out of the core's own trace. Three
# answers are honest and the fourth is a blank: the numbers when the interpreter keeps the bar,
# nothing-to-read when the story draws its own (`G38`), and nothing-read-yet. What must never appear
# is a zero standing in for a number nobody has.
TRACE=$(ls -t "$WORK/state/logs/"core-*.trace 2>/dev/null | head -1)
[ -n "$TRACE" ] || fail "no session trace, so the /bar answer cannot be read back"
grep -q 'status line:' "$TRACE" || fail "/bar said nothing at all about the status line"
grep -qE 'status line: (at object [0-9]+|this story draws its own|nothing has been read yet)' "$TRACE" \
    || fail "/bar answered something that is neither the numbers nor an honest reason there are none"
echo "   /bar reported the interpreter's own line: $(grep -o 'status line:.*' "$TRACE" | tail -1)"

echo
echo "PASS: the player's own commands are reachable from a machine, and a named save is a file"
exit 0
