#!/usr/bin/env bash
#
# Does the turn number belong to the game, or to the process?
#
# This exists because of a real evening. The 2026-09-19 UAT ran wishbringer from turn 1 to 183,
# switched to border_zone and watched it come up at turn 183, switched to bureaucracy (198) and
# then back to wishbringer at 203 — while the banner announced "Turn 0" over a story that had just
# been restored at turn 208. The companion, handed "turn 185" at a story it had never started,
# reasoned out loud that it must have "been playing a while".
#
# The turn number is the citation on every observation and the number a checkpoint records, so a
# number that means "turns this process has seen" cannot say which story it is about.
#
# Run inside the image:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/turn-number-per-game.sh
#
# What it proves, without a model and without a story named anywhere:
#
#   1. a game that comes back from its own checkpoint resumes at the number that checkpoint
#      recorded. Under the old rule the counter was a property of the process, so a restore came
#      back at 1 no matter what the record said — which is the assertion that catches it;
#   2. within a game, numbered turns strictly increase. Machinery turns share a number with the
#      turn they belong to on purpose; no two *numbered* turns may collide.
#
# What it cannot prove here, and why it is not a hole: that a `/switch` does not carry the count
# from one story to the next. `/switch` is a core command and a core command comes from a *player*
# seat — the `--stdio` transport this suite runs on is an agent-style seat, and hands every line
# straight to the story (`InputSource::Forced`), so a `/switch` typed here is a `/switch` typed at
# frotz. The switch is therefore checked where a player actually is: the acceptance walkthrough in
# item 6 of the operations walkthrough. This test covers the same rule from the
# side that can be driven without a keyboard.

set -uo pipefail

BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
GAMES_DIR="${INFOCOMPANION_GAMES_DIR:-/games}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "FAIL: $*"
    echo "--- the turns the run reported (session, turn) ---"
    cat "$WORK"/*/turns.txt 2>/dev/null | head -40
    echo "--- stderr (last 12 lines) ---"
    find "$WORK" -name err.log | head -1 | xargs -r tail -12
    exit 1
}

# Every story the image carries, rather than a name: a story added tomorrow is covered the same.
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
echo "turn-number-per-game: ${#STORIES[@]} stories"

# The numbered turns of one session log, in the order they happened. `machinery` turns are left
# out: they carry the number of the turn they belong to, so they are not evidence either way.
numbered_turns() { # numbered_turns <log> > "turn" per line
    awk '
    function field(line, name,    r) {
        if (match(line, "\"" name "\":[0-9]+")) {
            r = substr(line, RSTART, RLENGTH); sub("\"" name "\":", "", r); return r
        }
        if (match(line, "\"" name "\":(true|false)")) {
            r = substr(line, RSTART, RLENGTH); sub("\"" name "\":", "", r); return r
        }
        return ""
    }
    /"kind":"turn"/ {
        if (field($0, "machinery") == "true") next
        t = field($0, "turn")
        if (t != "") print t
    }
    ' "$1"
}

drive() { # drive <state-dir> <story> <lines>
    {
        printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"t","payload":{"protocol_version":1,"client_name":"turn-number"}}'
        sleep 3
        seq=2
        for _ in $(seq 1 "$3"); do
            printf '{"v":1,"type":"game_input","seq":%d,"ts":"t","payload":{"line":"look"}}\n' "$seq"
            seq=$((seq + 1))
            sleep 2
        done
        sleep 3
    } | timeout 120 "$BINARY" --stdio --game "$2" --state-dir "$1" \
        --autosave-turns 1 --turn-timeout-ms 400 >"$1/out.jsonl" 2>"$1/err.log"
}

proved=""
for story in "${STORIES[@]}"; do
    stem="$(basename "${story%.*}")"
    work="$WORK/$stem"
    # The shell redirects the run's output into the state directory, so it has to exist first:
    # the daemon creates its own tree, but not before the shell has opened the file.
    mkdir -p "$work/state"

    drive "$work/state" "$story" 3
    record="$work/state/saves/$stem/$stem.json"
    if [ ! -f "$record" ]; then
        # A story parked on a menu with nobody to answer it never reaches a savable prompt. That
        # is the driver's problem, not this test's, and is reported rather than failed — the same
        # rule `checkpoint-per-game` uses.
        echo "turn-number-per-game: $stem never reached a savable prompt"
        continue
    fi

    # The record beside the checkpoint is pretty-printed, so the colon has a space after it.
    recorded="$(sed -n 's/.*"turn": *\([0-9][0-9]*\).*/\1/p' "$record" | head -1)"
    [ -n "$recorded" ] || fail "$stem wrote a record with no turn in it: $(tr -d '\n' <"$record" | cut -c1-200)"

    first_session="$(ls -t "$work/state/logs/"core-*.jsonl | head -1)"
    numbered_turns "$first_session" >"$work/turns.txt"
    [ -s "$work/turns.txt" ] || fail "$stem reported no numbered turns"

    if [ "$(tail -1 "$work/turns.txt")" -ne "$recorded" ]; then
        fail "$stem saved at turn $recorded but its last numbered turn was $(tail -1 "$work/turns.txt")"
    fi

    if ! awk '{ t = $1 + 0; if (seen && t <= prev) { bad = 1 } prev = t; seen = 1 }
               END { exit bad ? 1 : 0 }' "$work/turns.txt"; then
        fail "$stem numbered its turns out of order: $(tr '\n' ' ' <"$work/turns.txt")"
    fi

    # A second process on the same state directory: what a redeploy, a crash and a switch back
    # all are. The number has to come off the record, not out of the process.
    drive "$work/state" "$story" 2
    second_session="$(ls -t "$work/state/logs/"core-*.jsonl | head -1)"
    [ "$second_session" != "$first_session" ] || fail "$stem's second run wrote no new session log"

    first_after="$(numbered_turns "$second_session" | head -1)"
    [ -n "$first_after" ] || fail "$stem resumed but took no numbered turn"

    if [ "$first_after" -ne "$((recorded + 1))" ]; then
        fail "$stem was saved at turn $recorded and came back numbered $first_after — the count did not resume from its own record"
    fi

    numbered_turns "$second_session" >"$work/turns-second.txt"
    if ! awk '{ t = $1 + 0; if (seen && t <= prev) { bad = 1 } prev = t; seen = 1 }
               END { exit bad ? 1 : 0 }' "$work/turns-second.txt"; then
        fail "$stem numbered its turns out of order after resuming: $(tr '\n' ' ' <"$work/turns-second.txt")"
    fi

    echo "turn-number-per-game: $stem saved at turn $recorded, resumed at $first_after"
    proved="$stem"
    break
done

[ -n "$proved" ] || fail "no story in $GAMES_DIR could be saved and resumed"

echo "PASS"
