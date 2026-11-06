#!/usr/bin/env bash
#
# INFOCompanion — scripted-session acceptance test (§7, §5.4, issue #4).
#
# Run inside the image:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/session-acceptance.sh
#
# Where the smoke test proves the framing contract on three commands, this proves the
# acceptance criteria of the turn framer over a longer session:
#
#   * the round-trip count equals the commands sent, plus the startup intro;
#   * every command is matched by exactly one turn carrying that input, in order;
#   * no turn is split and none loses its text;
#   * the `>` prompt never leaks into a turn's output;
#   * every turn was framed by the prompt, not by the quiescence fallback;
#   * auto-save actually writes a checkpoint to disk (§5.4);
#   * stdout stays pure protocol throughout.
#
# Checks are done with grep/awk rather than Python or jq: the runtime image stays
# minimal, and every dependency a test needs is a dependency the image has to carry.
#
# It uses its own state directory, so a checkpoint from a real session cannot change
# what it observes.

set -uo pipefail

. "$(dirname "${BASH_SOURCE[0]}")/stories.sh"
GAME="$(story_for "${1:-}")"
BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
AUTOSAVE_TURNS="${INFOCOMPANION_AUTOSAVE_TURNS:-5}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Movement, examination, inventory, score, and refusals: enough shapes to catch
# framing that only works for one kind of output. Written to hold on either story this can resolve
# to — the mailbox is zork1's, the lamp is Adventure's, and both answer `frobnicate plugh` and
# `drop bottle` with a refusal, which is the shape being tested.
COMMANDS=(
    look inventory north look inventory south south look
    east look west look up down look inventory score look
    "examine house" "open mailbox" "take leaflet" "read leaflet" look
    "frobnicate plugh" "take lamp" "take keys" inventory look "drop bottle"
    inventory score look inventory look
)

fail() {
    echo "FAIL: $*"
    echo "--- stdout (first 12 lines) ---"
    head -12 "$WORK/out.jsonl" 2>/dev/null
    echo "--- stderr (last 12 lines) ---"
    tail -12 "$WORK/err.log" 2>/dev/null
    exit 1
}

[ -f "$GAME" ] || fail "no story file at $GAME"

echo "== sending ${#COMMANDS[@]} commands, auto-save every $AUTOSAVE_TURNS turns"
{
    printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"t","payload":{"protocol_version":1,"client_name":"acceptance"}}'
    sleep 1
    sequence=2
    for command in "${COMMANDS[@]}"; do
        printf '{"v":1,"type":"game_input","seq":%d,"ts":"t","payload":{"line":"%s"}}\n' "$sequence" "$command"
        sequence=$((sequence + 1))
        sleep 0.35
    done
    sleep 1
} | timeout 180 "$BINARY" --stdio --game "$GAME" --state-dir "$WORK/state" \
    --autosave-turns "$AUTOSAVE_TURNS" >"$WORK/out.jsonl" 2>"$WORK/err.log"
status=$?
[ "$status" -eq 0 ] || fail "the daemon exited with $status (124 = timed out)"

COMMAND_LIST=$(printf '%s|' "${COMMANDS[@]}")
COMMAND_LIST=${COMMAND_LIST%|}

awk -v cmds="$COMMAND_LIST" '
function field(line, name,    r) {
    if (match(line, "\"" name "\":\"[^\"]*\"")) {
        r = substr(line, RSTART, RLENGTH)
        sub("\"" name "\":\"", "", r)
        sub("\"$", "", r)
        return r
    }
    return ""
}
function number(line, name,    r) {
    if (match(line, "\"" name "\":[0-9]+")) {
        r = substr(line, RSTART, RLENGTH)
        sub("\"" name "\":", "", r)
        return r + 0
    }
    return -1
}
function fail(message) {
    print "FAIL: " message
    failed = 1
}
BEGIN {
    count = split(cmds, expected, "|")
    envelopes = 0; turns = 0; max_ms = 0; leaks = 0; empties = 0; slow = 0; bad = 0
}
{
    envelopes++
    if ($0 !~ /^\{"v":1,"type":"[a-z_]+","seq":[0-9]+,"ts":"[^"]+","payload":/) {
        bad++
        print "BADLINE " substr($0, 1, 110)
        next
    }
    if ($0 !~ /"type":"turn_complete"/) next

    turns++
    input = field($0, "input")
    output = field($0, "output")
    ms = number($0, "elapsed_ms")
    if (ms > max_ms) max_ms = ms

    if (turns == 1) {
        if (input != "") fail("the first turn should be the startup intro with empty input")
        next
    }
    if (turns - 1 > count) { fail("more turns arrived than commands were sent"); next }
    if (input != expected[turns - 1])
        fail("expected input [" expected[turns - 1] "] but the turn carried [" input "]")
    if (output ~ />$/) { leaks++; print "   prompt leaked into turn [" input "]" }
    if (output ~ /^[[:space:]]*$/) empties++
    if (ms > 200) { slow++; print "   slow turn [" input "] " ms "ms -- the prompt was missed" }
}
END {
    printf "   envelopes %d, turns %d (expected %d), max turn %d ms\n", envelopes, turns, count + 1, max_ms
    printf "   empty turns %d (legitimate for a move into a room already described)\n", empties
    if (bad) fail(bad " malformed line(s) on stdout")
    if (turns != count + 1) fail("expected " count + 1 " turns, saw " turns)
    if (leaks) fail(leaks " turn(s) carried the prompt into their output")
    if (slow) fail(slow " turn(s) fell back to the quiescence window")
    if (failed) exit 1
    print "   checks passed"
}
' "$WORK/out.jsonl"
[ $? -eq 0 ] || fail "session checks did not pass"

# Auto-save must have written a checkpoint (§5.4).
CHECKPOINTS=$(find "$WORK/state/saves" -name '*.sav' 2>/dev/null | wc -l)
[ "$CHECKPOINTS" -ge 1 ] || fail "no checkpoint was written after ${#COMMANDS[@]} commands"
echo "== auto-save wrote $CHECKPOINTS checkpoint file(s)"

# A resume must behave: boot again on the same state and confirm it restores.
#
# The command goes in **immediately**, with no pause for the daemon to settle, because that is the
# case that matters and the one that hid a bug: a restored boot names the save file as soon as the
# story has opened (through the interpreter's tool channel), and anything queued behind that naming
# is dispatched at once. A sleep here let the naming finish first and the bug went unnoticed while
# every turn after a restore was being swallowed.
{
    printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"t","payload":{"protocol_version":1,"client_name":"acceptance"}}'
    sleep 0.2
    printf '%s\n' '{"v":1,"type":"game_input","seq":2,"ts":"t","payload":{"line":"inventory"}}'
    sleep 2
} | timeout 60 "$BINARY" --stdio --game "$GAME" --state-dir "$WORK/state" >"$WORK/resume.jsonl" 2>"$WORK/resume.err"
grep -q '"game_stem":"' "$WORK/resume.jsonl" || fail "the resumed session did not handshake"
grep -q '"type":"turn_complete".*"input":"inventory"' "$WORK/resume.jsonl" ||
    fail "a command sent straight after a restore was never framed as a turn"
echo "== resume on the same state directory handshakes, restores, and still frames turns"

# The resumed world is *labelled* with where it came from (05_memory.md §5 (`M23`–`M26`)). The autosave runs every
# few turns, so a world that comes back can be several moves behind the conversation — and "saved at
# turn N" is the only fact that says so. It comes from the record the core writes beside the
# checkpoint, so it is checked against that record rather than against a guess.
grep -q '"resumed":true' "$WORK/resume.jsonl" || fail "the resumed session did not say it had resumed"
grep -q '"resumed_from":"' "$WORK/resume.jsonl" || fail "the resumed session did not name the save"
RECORD=$(find "$WORK/state/saves" -name '*.json' 2>/dev/null | head -1)
[ -n "$RECORD" ] || fail "no save record was written beside the checkpoint"
RECORDED_TURN=$(grep -o '"turn": *[0-9]*' "$RECORD" | head -1 | grep -o '[0-9]*')
[ -n "$RECORDED_TURN" ] || fail "the save record does not say which turn it was taken at"
grep -q "\"resumed_at_turn\":$RECORDED_TURN" "$WORK/resume.jsonl" ||
    fail "the resumed session did not label the world with the checkpoint's turn ($RECORDED_TURN)"
echo "== the resumed world is labelled with the turn it went back to ($RECORDED_TURN)"

# stdout is pure protocol, and the logs went to stderr.
grep -qE '(INFO|WARN|ERROR|DEBUG) ' "$WORK/out.jsonl" && fail "log lines leaked onto stdout"
grep -qE 'INFO' "$WORK/err.log" || fail "no log output on stderr"
echo "== stdout was pure protocol; logs came out on stderr"

echo
echo "PASS: ${#COMMANDS[@]} commands framed with no splits and no prompt leaks, checkpoint written, resume verified"
