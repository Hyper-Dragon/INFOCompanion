#!/usr/bin/env bash
#
# INFOCompanion — the interpreter's tool channel (02_game_boundary.md §2 (`G5`–`G7`)).
#
# Run inside the image:
#
#   docker compose run --rm --no-deps --entrypoint /bin/bash infocompanion /tests/interpreter-tools.sh
#
# This drives the interpreter directly, the way the daemon does, and asserts the facts the core
# now relies on instead of inferring. Each check names what it would mean if it failed:
#
#   1. without `-T` the channel does not exist, and the screen is unchanged by its presence;
#   2. `hello` says which interpreter this is and what it offers;
#   3. `input_wanted` arrives after everything the story printed has been flushed, and counts the
#      bytes it has written — the fact that lets a turn be framed by arithmetic instead of by
#      waiting for silence;
#   4. `parse` is the story's own parser answering: which words its dictionary has, and which it
#      does not (a zero dictionary address is the Z-machine's way of saying so);
#   5. a save goes where the host named it, with no filename prompt and no overwrite question;
#   6. the story's own `restore` loads from the host's path, silently;
#   7. `-R` still confines a filename the story or the player supplies — and no longer truncates it.
#
# The last one is a bug fixed in `dFrotzTooled`: upstream appends a basename with a length that is
# a difference of lengths rather than the room left in the destination, so a restriction of the
# right length cut the name in half.

set -uo pipefail

DFROTZ="${INFOCOMPANION_DFROTZ:-/usr/games/dfrotz}"
GAMES_DIR="${INFOCOMPANION_GAMES_DIR:-/games}"

# Which story: `stories.sh` picks one this image can actually see, rather than naming Adventure,
# which this repository does not carry. These checks are about the tool channel, not about a story —
# the parse, save, restore and restriction checks are the same on any plain parser game.
. "$(dirname "${BASH_SOURCE[0]}")/stories.sh"
STORY="$(story_for "${1:-}")"
V3="$GAMES_DIR/zork1.z3"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

failed=0
check() {
    local message="$1" got="$2" want="$3"
    if [ "$got" = "$want" ]; then
        printf '  ok    %s\n' "$message"
    else
        printf '  FAIL  %s (got %q, wanted %q)\n' "$message" "$got" "$want"
        failed=1
    fi
}

# "At least one", for the checks where the exact number is not the point and a count that
# happens to be three is still a pass.
check_any() {
    local message="$1" count="$2"
    if [ "$count" -gt 0 ] 2>/dev/null; then
        printf '  ok    %s (%s)\n' "$message" "$count"
    else
        printf '  FAIL  %s (none found)\n' "$message"
        failed=1
    fi
}

[ -x "$DFROTZ" ] || { echo "FAIL: no interpreter at $DFROTZ"; exit 1; }
[ -f "$STORY" ] || { echo "FAIL: no story at $STORY"; exit 1; }

echo "== the tool channel on $(basename "$DFROTZ") =="
echo "== story under test: $STORY"

# --- 1. Without -T there is no channel, and the screen does not change --------------
printf '%s\n' 'look' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p "$STORY" 2>frames-off.txt >screen-off.txt
check "no -T means no frames" "$(wc -c <frames-off.txt | tr -d ' ')" "0"
check "and nothing on stderr either" "$(grep -c . frames-off.txt || true)" "0"

printf '%s\n' 'look' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T "$STORY" 2>frames-on.txt >screen-on.txt
check "the screen is byte for byte the same with -T" \
    "$(cmp -s screen-off.txt screen-on.txt && echo same || echo different)" "same"

# --- 2. The handshake --------------------------------------------------------------
grep -q '"event":"hello"' frames-on.txt
check "there is a handshake" "$?" "0"
check "it names the build" \
    "$(grep -o '"build":"dfrotztooled[^"]*"' frames-on.txt | head -1 | cut -d: -f2)" \
    '"dfrotztooled-2.54"'
check "it names the tools it offers" \
    "$(grep -c '"tools":"[^"]*save' frames-on.txt)" "1"

# --- 3. The prompt says how much screen it is owed ---------------------------------
# A line prompt, counted in bytes, is the turn boundary: the core closes a turn when that many
# bytes have arrived, which is why nothing here waits for the output to go quiet.
check_any "the channel reports a prompt" "$(grep -c '"event":"input_wanted"' frames-on.txt)"
check_any "the prompt counts its bytes" "$(grep -c '"out_bytes":[0-9]*' frames-on.txt)"
check_any "and says whether it wants a line or a key" "$(grep -c '"kind":"line"' frames-on.txt)"

# --- 4. The parser's answer --------------------------------------------------------
# `frobnicate` rather than `xyzzy`: the Infocom dictionaries really do hold the magic words, and a
# test word that is secretly in the dictionary proves nothing about unknown ones.
printf '%s\n' 'look' 'frobnicate plugh' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T "$STORY" 2>frames-parse.txt >/dev/null
check "the parser answered once per line" "$(grep -c '"event":"parse"' frames-parse.txt)" "4"
check "a word the dictionary has is marked known" \
    "$(grep -o '"known":true' frames-parse.txt | head -1)" '"known":true'
check_any "an invented word is marked unknown, with a zero address" \
    "$(grep -c '"dict":0,"known":false' frames-parse.txt)"
check "the words themselves come back" \
    "$(grep -o '"text":"frobnicate"' frames-parse.txt | head -1)" '"text":"frobnicate"'

# --- 5. A save at the path the host named ------------------------------------------
SAVE="$WORK/named.qzl"
printf '%s\n' '\t save '"$SAVE" 'save' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T "$STORY" 2>frames-save.txt >screen-save.txt
check "the command was accepted" "$(grep -c '"verb":"save"' frames-save.txt)" "1"
check "the save was reported written" "$(grep -c '"event":"saved".*"ok":true' frames-save.txt)" "1"
check "and it is the file we named" \
    "$(grep -o '"path":"[^"]*"' frames-save.txt | tail -1 | cut -d: -f2)" "\"$SAVE\""
check "no filename prompt was printed" "$(grep -c 'enter a filename' screen-save.txt)" "0"
check "and the save exists" "$([ -s "$SAVE" ] && echo yes || echo no)" "yes"

# --- 6. A restore from the path the host named -------------------------------------
rm -f frames-restore.txt
printf '%s\n' '\t restore '"$SAVE" 'restore' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T "$STORY" 2>frames-restore.txt >screen-restore.txt
check "the restore was reported" "$(grep -c '"event":"restored".*"ok":true' frames-restore.txt)" "1"
check "no filename prompt was printed for it either" \
    "$(grep -c 'enter a filename' screen-restore.txt)" "0"

# --- 7. The restricted directory, with the truncation gone -------------------------
mkdir -p "$WORK/restricted" "$WORK/elsewhere"
LONGNAME="a-name-far-longer-than-the-restricted-path"
printf '%s\n' '\t clear' 'save' "$WORK/elsewhere/$LONGNAME.qzl" 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T -R "$WORK/restricted" "$STORY" 2>/dev/null >/dev/null
check "a save under -R lands in the restricted directory" \
    "$([ -s "$WORK/restricted/$LONGNAME.qzl" ] && echo yes || echo no)" "yes"
check "and not where it was pointed" \
    "$([ -e "$WORK/elsewhere/$LONGNAME.qzl" ] && echo exists || echo absent)" "absent"

# --- Version 3: the numbers the status line is made of ----------------------------
printf '%s\n' 'look' 'quit' 'y' \
    | timeout 30 "$DFROTZ" -m -q -p -T "$V3" 2>frames-v3.txt >/dev/null
check_any "a V3 prompt reports the status line's own numbers" \
    "$(grep -c '"event":"input_wanted".*"location":[0-9]*' frames-v3.txt)"

# --- 8. The screen, which is not the output ----------------------------------------
# A story that wants a key is drawing something, and the dumb interface prints a row only when it
# holds a cell that has changed *and* is not blank — so a form drawn once and left alone is on the
# screen for the whole game and in the output for none of it. Bureaucracy's licence form is the
# measured case (issue #19); its labels are the assertion.
BUREAUCRACY="$GAMES_DIR/bureaucracy.z4"
if [ -f "$BUREAUCRACY" ]; then
    # The first Return starts the story; the second is the "press any key" that draws the form.
    # Until that key is pressed there is nothing but the preamble, so asking earlier would test
    # the timing rather than the channel.
    printf '%s\n' '' '' '\t screen' '\t screen' \
        | timeout 40 "$DFROTZ" -m -q -p -T "$BUREAUCRACY" 2>frames-screen.txt >screen-bureau.txt
    check "the screen was handed over" "$(grep -c '"event":"screen"' frames-screen.txt)" "2"
    check "it says how big it is" \
        "$(grep -o '"rows":24,"cols":80' frames-screen.txt | head -1)" '"rows":24,"cols":80'
    check "it says where the cursor is" \
        "$(grep -c '"cursor_row":[0-9]*,"cursor_col":[0-9]*' frames-screen.txt)" "2"
    check_any "the form's labels are in the screen" \
        "$(grep -c 'SOFTWARE LICENCE APPLICATION' frames-screen.txt)"
    check "and they were never in the printed stream" \
        "$(grep -c 'SOFTWARE LICENCE APPLICATION' screen-bureau.txt)" "0"
    check_any "a key prompt is reported as a key prompt, not a line" \
        "$(grep -c '"kind":"char"' frames-screen.txt)"
else
    echo "  .. no $BUREAUCRACY, so the screen check is skipped"
fi

echo
if [ "$failed" -eq 0 ]; then
    echo "PASS: the tool channel reports what the core needs, and saves go where it says"
else
    echo "FAIL: the tool channel did not behave as specified"
fi
exit "$failed"
