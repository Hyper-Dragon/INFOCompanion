#!/usr/bin/env bash
#
# INFOCompanion — stdio transport smoke test (issue #3, §8.1).
#
# Run it inside the image, where frotz and a story file already exist:
#
#   docker compose run --rm --entrypoint /bin/bash infocompanion /tests/stdio-smoke.sh
#
# What it proves, in the order the spec asks for it:
#   1. `--stdio` speaks the seat protocol on stdin/stdout;
#   2. framed turns arrive, with no split or missing text;
#   3. **stdout carries nothing but protocol** — every line is a JSON object and no
#      log line leaks into it (logs go to stderr).
#
# Parsing is done with grep rather than jq on purpose: the runtime image stays
# minimal, and an exact-match regex on `{"v":1,"type":"…"` is a stronger statement
# about framing than a tolerant parse would be.

set -uo pipefail

. "$(dirname "${BASH_SOURCE[0]}")/stories.sh"
GAME="$(story_for "${1:-}")"
BINARY="${INFOCOMPANION_BIN:-/usr/local/bin/infocompanion}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Its own state directory, always: a checkpoint left behind by a real session would
# otherwise be restored, adding a refresh `look` turn and changing the turn count.

fail() {
    echo "FAIL: $*"
    echo "--- stdout (first 20 lines) ---"
    head -20 "$WORK/out.jsonl" 2>/dev/null
    echo "--- stderr (first 20 lines) ---"
    head -20 "$WORK/err.log" 2>/dev/null
    exit 1
}

echo "== game under test: $GAME"
[ -f "$GAME" ] || fail "no story file at $GAME"

echo "== starting the daemon in --stdio mode"
{
    # The handshake first (the core answers with `welcome`), then two commands.
    printf '%s\n' '{"v":1,"type":"hello","seq":1,"ts":"2026-09-11T00:00:00Z","payload":{"protocol_version":1,"client_name":"smoke"}}'
    sleep 1
    printf '%s\n' '{"v":1,"type":"game_input","seq":2,"ts":"2026-09-11T00:00:01Z","payload":{"line":"look"}}'
    sleep 1
    printf '%s\n' '{"v":1,"type":"game_input","seq":3,"ts":"2026-09-11T00:00:02Z","payload":{"line":"inventory"}}'
    sleep 1
} | timeout 60 "$BINARY" --stdio --game "$GAME" --state-dir "$WORK/state" >"$WORK/out.jsonl" 2>"$WORK/err.log"
status=$?

# The daemon exits cleanly on stdin EOF; 124 would mean it hung.
[ "$status" -eq 0 ] || fail "the daemon exited with $status (124 = timed out)"

total=$(grep -c '' "$WORK/out.jsonl" || true)
[ "$total" -gt 0 ] || fail "nothing was written to stdout"
echo "== stdout carried $total protocol lines"

# --- 1. stdout is pure protocol -------------------------------------------------
bad=$(grep -cvE '^\{"v":1,"type":"[a-z_]+","seq":[0-9]+,"ts":"[^"]+","payload":' "$WORK/out.jsonl" || true)
[ "$bad" -eq 0 ] || fail "$bad stdout line(s) are not well-formed protocol envelopes"

if grep -qE '(INFO|WARN|ERROR|DEBUG) ' "$WORK/out.jsonl"; then
    fail "log lines leaked onto stdout"
fi

# Logs must exist, and must be on stderr.
grep -qE 'INFO' "$WORK/err.log" || fail "no log output on stderr"
echo "== logs went to stderr, as required"

# --- 2. the handshake -----------------------------------------------------------
grep -q '"type":"welcome"' "$WORK/out.jsonl" || fail "no welcome envelope after hello"
grep -q '"game_stem":"' "$WORK/out.jsonl" || fail "welcome carried no game_stem"
echo "== handshake: hello -> welcome"

# --- 3. framing ----------------------------------------------------------------
grep -q '"type":"game_output"' "$WORK/out.jsonl" || fail "no game_output chunks"
grep -q '"type":"turn_complete"' "$WORK/out.jsonl" || fail "no framed turns"

turns=$(grep -c '"type":"turn_complete"' "$WORK/out.jsonl" || true)
echo "== framed turns: $turns"

# The startup intro is a turn with an empty input (§7 edge case).
grep -q '"type":"turn_complete".*"input":""' "$WORK/out.jsonl" \
    || fail "the startup intro was not framed as a turn with empty input"

# Each command we sent produced a turn carrying that input…
for command in look inventory; do
    grep -q "\"input\":\"$command\"" "$WORK/out.jsonl" \
        || fail "\`$command\` produced no framed turn"
done
echo "== every command round-tripped into a framed turn"

# …and rounds trips must equal commands sent (+1 for the intro): the acceptance
# criterion of §7 is that no turn is split or lost.
[ "$turns" -eq 3 ] || fail "expected 3 turns (intro, look, inventory), saw $turns"

# The prompt itself is not part of a turn's output: the last character of the output
# of a command turn should be real text, not the `>` prompt.
grep -q '"input":"look"[^}]*"output":"[^"]*>' "$WORK/out.jsonl" \
    && fail "the \`>\` prompt leaked into a turn's output"

echo
echo "PASS: stdio transport framed $turns turns with stdout carrying nothing but protocol"
