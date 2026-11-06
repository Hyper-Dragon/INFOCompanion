#!/usr/bin/env bash
#
# INFOCompanion — a story that wants a key, and the screen it is drawn on (issue #19).
#
# Run it inside the image, where frotz and a story file already exist:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/screen-bureaucracy.sh
#
# What it proves, and why it is bureaucracy:
#
#   1. a story can be waiting for something that is *not* a command, and the turn says so
#      (`awaiting_command: false`) — the flag that asks the companion to answer;
#   2. the thing it is waiting in front of is on the interpreter's **screen**, and the dumb
#      interface never printed it: it prints a row only when the row holds a cell that has changed
#      *and* is not blank, and a licence form drawn once and left alone satisfies neither. So the
#      companion was asked to answer a form it had never been shown;
#   3. with the tool channel the core asks for the screen itself and the turn carries it — which is
#      the whole of the fix.
#
# The form's labels are the assertion, because they are the text that never reaches stdout: if
# "SOFTWARE LICENCE APPLICATION" is in the turn, the core is seeing the screen.

set -uo pipefail

GAME="${1:-/games/bureaucracy.z4}"
BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "FAIL: $*"
    echo "--- turns (payloads, first 400 chars each) ---"
    grep -o '"type":"turn_complete".*' "$WORK/out.jsonl" 2>/dev/null | cut -c1-400
    echo "--- stderr (last 15 lines) ---"
    tail -15 "$WORK/err.log" 2>/dev/null
    exit 1
}

echo "== game under test: $GAME"
[ -f "$GAME" ] || fail "no story file at $GAME"

echo "== starting the daemon in --stdio mode"
{
    printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"2026-09-11T00:00:00Z","payload":{"protocol_version":1,"client_name":"screen"}}'
    sleep 3
    # The opening prompt: anything but RESTORE starts the story anew. A line, not a key.
    printf '%s\n' '{"v":1,"type":"game_input","seq":2,"ts":"2026-09-11T00:00:01Z","payload":{"line":""}}'
    sleep 3
    # `[Press any key to begin.]` — a key prompt. An empty line here would be dropped as a
    # mistake: the `key` flag is what says a Return was meant.
    printf '%s\n' '{"v":1,"type":"game_input","seq":3,"ts":"2026-09-11T00:00:02Z","payload":{"line":"","key":true}}'
    sleep 4
    # That keystroke draws the licence form — the thing that is on the screen and nowhere else.
    printf '%s\n' '{"v":1,"type":"game_input","seq":4,"ts":"2026-09-11T00:00:03Z","payload":{"line":"","key":true}}'
    sleep 4
} | timeout 120 "$BINARY" --stdio --game "$GAME" --state-dir "$WORK/state" >"$WORK/out.jsonl" 2>"$WORK/err.log"
status=$?

[ "$status" -eq 0 ] || fail "the daemon exited with $status (124 = timed out)"

grep -q '"type":"welcome"' "$WORK/out.jsonl" || fail "no welcome envelope after hello"

turns=$(grep -c '"type":"turn_complete"' "$WORK/out.jsonl" || true)
[ "$turns" -gt 0 ] || fail "no framed turns at all"
echo "== $turns framed turn(s)"

# --- 1. A key prompt is not a command prompt --------------------------------------
grep -q '"awaiting_command":false' "$WORK/out.jsonl" ||
    fail "no turn said the story was waiting for something that is not a command"
echo "== a turn said: not a command — the story wants a key"

# --- 2. The form is in the screen --------------------------------------------------
grep -q 'SOFTWARE LICENCE APPLICATION' "$WORK/out.jsonl" ||
    fail "the licence form never reached the core: the screen was not asked for, or not carried"
echo "== the licence form reached the core, in the turn's screen"

grep -q '"screen":"' "$WORK/out.jsonl" || fail "the turn carried no screen field"
echo "== and it is labelled as a screen rather than as output"

# --- 3. And it was never in the output ---------------------------------------------
if grep -q 'SOFTWARE LICENCE APPLICATION' <(grep -o '"output":"[^"]*"' "$WORK/out.jsonl"); then
    fail "the form was in the output after all: this test no longer proves what it claims"
fi
echo "== the form was not in the output, which is the whole point"

# --- 4. One screen per turn, not one per keystroke ---------------------------------
screens=$(grep -o '"screen":"' "$WORK/out.jsonl" | wc -l || true)
[ "$screens" -ge 1 ] || fail "no screen was carried at all"
[ "$screens" -le "$turns" ] ||
    fail "$screens screens for $turns turns: a screen is being attached to the wrong turns"
echo "== $screens screen(s) across $turns turn(s) — attached to turns, not to keystrokes"

echo
echo "PASS: a key prompt's screen reaches the companion, and the form is nowhere in the output"
