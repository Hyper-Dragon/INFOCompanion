#!/usr/bin/env bash
#
# Does a checkpoint actually commit, in every game we ship?
#
# This exists because of a real bug: the save sequence waited for `Ok.`, which is what
# Adventure prints, and The Witness prints `O.K.`. The Witness's saves therefore all
# timed out — auto-save never committed, and the recovery line sent to dismiss a filename
# prompt arrived as a junk command, so the player saw
#
#     [system] checkpoint auto timed out
#     What?
#
# The lesson is that every marker in the save sequence is a *per-game* measurement, so a
# test that only ever runs Adventure cannot see the class of bug. This one runs each story
# in the games directory.
#
# Run inside the image:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/checkpoint-per-game.sh

set -uo pipefail

BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
GAMES_DIR="${INFOCOMPANION_GAMES_DIR:-/games}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "FAIL: $*"
    exit 1
}

command -v dfrotz >/dev/null 2>&1 || true

# Every story format the daemon discovers (Core/src/games.rs), found case-insensitively
# because Infocom's own releases are `.DAT`. Moonmist is why this list exists: it writes
# `[Okay.]` where Adventure writes `Ok.`, and every one of its checkpoints timed out
# until the markers were read as a family (issue #19).
STORIES=()
while IFS= read -r story; do
    STORIES+=("$story")
done < <(
    find "$GAMES_DIR" -maxdepth 1 -type f \
        \( -iname '*.z3' -o -iname '*.z4' -o -iname '*.z5' -o -iname '*.z6' \
           -o -iname '*.z7' -o -iname '*.z8' -o -iname '*.zblorb' -o -iname '*.dat' \) \
        | sort
)
[ "${#STORIES[@]}" -gt 0 ] || fail "no story files in $GAMES_DIR"
echo "checkpoint-per-game: ${#STORIES[@]} stories"

# Nothing below knows any story by name. Every rule is a property of the *sequence*,
# checked against what the run recorded, so a story added tomorrow is covered the same way:
#
#   1. a checkpoint is never claimed without the file on disk to show for it;
#   2. once the interpreter has been handed the filename, the checkpoint must commit — the
#      save is decided by the artifact, so there is nothing left for a wording to mis-read;
#   3. the recovery line never reaches the game as a command (the `What?`);
#   4. and at the end at least one story must have committed: none at all is systemic (a
#      changed prompt handshake, say), not a per-game accident.
#
# A story this *fixed three-command drive* never reaches a savable prompt in — a menu with
# nobody to answer it, a question it cannot get past — is reported with the reason the daemon
# gave rather than failed. Driving a game is the driver's job, not this test's: the companion
# reads the same screen and answers, and the core never pretends it saved.
status=0
saved=0
for story in "${STORIES[@]}"; do
    name="$(basename "$story")"
    game_work="$WORK/${name%.*}"
    mkdir -p "$game_work"

    # Three commands with auto-save every 2 turns: the checkpoint falls due mid-session,
    # while the game is sitting at its own prompt.
    {
        printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"t","payload":{"protocol_version":1,"client_name":"checkpoint-per-game"}}'
        sleep 1
        printf '%s\n' '{"v":1,"type":"game_input","seq":2,"ts":"t","payload":{"line":"look"}}'
        sleep 2
        printf '%s\n' '{"v":1,"type":"game_input","seq":3,"ts":"t","payload":{"line":"inventory"}}'
        sleep 3
        printf '%s\n' '{"v":1,"type":"game_input","seq":4,"ts":"t","payload":{"line":"look"}}'
        sleep 3
    } | timeout 90 "$BINARY" --stdio --game "$story" --state-dir "$game_work/state" \
        --autosave-turns 2 --turn-timeout-ms 400 >"$game_work/out.jsonl" 2>"$game_work/err.log"

    session=$(ls -t "$game_work/state/logs/"core-*.jsonl 2>/dev/null | head -1)
    stem="$(basename "${story%.*}")"

    problems=""
    [ -n "$session" ] || problems="no session log"

    committed=no
    grep -q '"event":"checkpoint"' "$session" 2>/dev/null && committed=yes
    reason=$(grep '"event":"checkpoint_failed"' "$session" 2>/dev/null | head -1 |
        grep -o '"detail":"[^"]*"' | head -1 | cut -c11-140)
    deferred=no
    grep -q '"event":"checkpoint_deferred"' "$session" 2>/dev/null && deferred=yes

    # 1. Never claim what is not on disk. One folder per game, so the path says which
    # game the file belongs to as well as where it is.
    if [ -z "$problems" ] && [ "$committed" = yes ] &&
        [ ! -f "$game_work/state/saves/$stem/$stem.sav" ]; then
        problems="it reported a checkpoint with no file on disk"
    fi
    # 2. Anything the interpreter was asked to write must have been committed. "Asked" is the
    # staging filename on an interpreter with no tool channel, and the host-named path with one
    # (`\t save <path>`, which the core announces like any other machinery) — either way it is
    # the point after which the interpreter has everything it needs.
    answered=no
    if grep -q '"kind":"agent_input","line":"stage"' "$session" 2>/dev/null ||
        grep -qF '"line":"\t ' "$session" 2>/dev/null; then
        answered=yes
    fi
    if [ -z "$problems" ] && [ "$answered" = yes ] && [ "$committed" = no ]; then
        problems="the interpreter was given the filename and nothing was committed"
    fi
    # 3. The recovery line must never reach the game as a command. This is the `What?`.
    if [ -z "$problems" ] && grep -q '"input":"",' "$session" 2>/dev/null &&
        grep -q '";\?What?\|^What?$' "$game_work/out.jsonl" 2>/dev/null; then
        problems="a junk line was sent to the game"
    fi

    if [ -n "$problems" ]; then
        echo "  $name: FAIL — $problems"
        echo "    --- checkpoint lines from the log ---"
        grep -i checkpoint "$session" 2>/dev/null | cut -c1-160 | sed 's/^/    /'
        echo "    --- what frotz said, per the daemon ---"
        grep -i "frotz last said" "$game_work/err.log" | tail -2 | cut -c1-200 | sed 's/^/    /'
        status=1
    elif [ "$committed" = yes ]; then
        echo "  $name: ok — checkpoint committed"
        saved=$((saved + 1))
    else
        if [ "$deferred" = yes ]; then
            echo "  $name: not saved — the game is waiting for an answer, so the save is still held back"
        else
            echo "  $name: not saved — this fixed drive never reached a prompt it could save at"
        fi
        [ -n "$reason" ] && echo "    reason: $reason"
    fi
done

[ "$status" -eq 0 ] || fail "a checkpoint invariant was broken"
[ "$saved" -gt 0 ] || fail "no story could be saved at all: that is systemic, not per-game"
echo "PASS: $saved of ${#STORIES[@]} stories committed a checkpoint; the rest said why not, and no junk line reached a parser"
