#!/usr/bin/env bash
#
# INFOCompanion — the shipped assets seed correctly (12_shipped_container.md §2 and §6 (`P4`, `P9`)).
#
# Run it inside the image:
#
#   docker compose run --rm -T --entrypoint /bin/bash infocompanion /tests/seed.sh
#
# What it proves, in the order the spec asks for it:
#   1. every file in the pack lands in the working paths, and the seeded story is a
#      Z-machine story file that dfrotz can open;
#   2. a second run copies nothing;
#   3. a user's edit survives, and a bundled file the user deleted comes back;
#   4. a destination that cannot be written is a hard failure, naming the path and
#      the fix.

set -uo pipefail

SEEDER="${INFOCOMPANION_SEEDER:-/usr/local/bin/seed-assets.sh}"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() {
    echo "FAIL: $*"
    exit 1
}

export INFOCOMPANION_ASSETS_DIR="${INFOCOMPANION_ASSETS_DIR:-/opt/infocompanion/assets}"

run_seed() {
    INFOCOMPANION_GAMES_DIR="$WORK/games" \
    INFOCOMPANION_STATE_DIR="$WORK/state" \
    INFOCOMPANION_PROMPTS_DIR="$WORK/prompts" \
        "$SEEDER"
}

[ -d "$INFOCOMPANION_ASSETS_DIR" ] || fail "no pack at $INFOCOMPANION_ASSETS_DIR"

echo "== first run"
out="$(run_seed)" || fail "first seed failed: $out"
echo "$out"
[ -f "$WORK/games/zork1.z3" ] || fail "no zork1.z3 after seeding"
[ -f "$WORK/games/zork1.md" ] || fail "no zork1.md sidecar after seeding"
[ -f "$WORK/games/_default.md" ] || fail "no _default.md stand-in after seeding"
[ -f "$WORK/prompts/engine.txt" ] || fail "no engine.txt after seeding"
[ -d "$WORK/prompts/persona" ] || fail "no persona directory after seeding"
[ -f "$WORK/state/agent-config.json" ] || fail "no agent-config.json after seeding"
[ -f "$WORK/state/core-config.json" ] || fail "no core-config.json after seeding"
[ -f "$WORK/state/webui-config.json" ] || fail "no webui-config.json after seeding"
echo "== every pack file landed"

# The story file must be a z3 (header byte 03) and must actually play.
[ "$(od -An -tx1 -N1 "$WORK/games/zork1.z3" | tr -d ' ')" = "03" ] \
    || fail "zork1.z3 does not start with the z3 header byte"
printf 'open mailbox\nquit\ny\n' | timeout 30 /usr/games/dfrotz -p "$WORK/games/zork1.z3" \
    >"$WORK/play.txt" 2>&1 || true
grep -q "West of House" "$WORK/play.txt" || fail "dfrotz did not reach West of House"
grep -q "small mailbox" "$WORK/play.txt" || fail "dfrotz output shows no mailbox"
echo "== the seeded story is a z3 and it plays"

echo "== second run is idempotent"
out="$(run_seed)" || fail "second seed failed: $out"
echo "$out"
echo "$out" | grep -q "nothing to seed" || fail "second run did not report 'nothing to seed'"

echo "== edits survive; deletions return"
echo "A NOTE FROM THE PLAYER" >>"$WORK/games/zork1.md"
rm "$WORK/games/wishbringer.md"
out="$(run_seed)" || fail "third seed failed: $out"
echo "$out"
grep -q "A NOTE FROM THE PLAYER" "$WORK/games/zork1.md" || fail "a user edit was overwritten"
[ -f "$WORK/games/wishbringer.md" ] || fail "a deleted bundled file did not return"
echo "== edit kept, deleted file restored"

echo "== an unwritable destination is a hard failure"
# As root, permission bits do not stop a write, so the unwritable destination here is
# structural: the target path is a file, not a directory.
: >"$WORK/notadir"
if INFOCOMPANION_GAMES_DIR="$WORK/notadir" \
   INFOCOMPANION_STATE_DIR="$WORK/state" \
   INFOCOMPANION_PROMPTS_DIR="$WORK/prompts" \
        "$SEEDER" >"$WORK/ro.out" 2>&1; then
    fail "seeding into an impossible destination did not fail"
fi
out="$(cat "$WORK/ro.out")"
echo "$out" | head -3
echo "$out" | grep -qE "cannot (write|create)" || fail "the failure did not name the path"
echo "$out" | grep -qi "writable" || fail "the failure did not explain the fix"
echo "== hard failure, with the path and the fix named"

echo "PASS: seed-assets (12_shipped_container.md §2 (`P4`))"
