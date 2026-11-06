#!/usr/bin/env bash
#
# Which story a container test drives — sourced by the tests that need one.
#
# Every test in this directory was written against **Adventure**, and this repository does not carry
# it: story files are not committed (`Assets/games/.gitignore`), so a checkout has whatever the person
# playing it put there. The image bundles `zork1.z3`, because that is the story the container ships.
#
# A test that hard-codes a story nobody has fails on a machine that *has* the real games on it, which
# is the worse of the two ways to be wrong: the suite looks like it is testing something. So the story
# resolves instead of being named —
#
#   1. a story given as the test's first argument wins, so a caller can always be explicit;
#   2. `INFOCOMPANION_TEST_STORY` does the same without changing the command line;
#   3. otherwise the first story the image can see, from a list that prefers Adventure (so a checkout
#      that has it keeps running every check exactly as it was written) and falls back to the story
#      the image bundles.
#
# The version matters in one place. `interpreter-tools.sh` used to name a V5 for its parser checks and
# a V3 for the status line's numbers; the parser checks are version-agnostic and now run on whatever
# resolves, and the status-line check names zork1 because it is a V3 and V3 is the only version that
# prints those numbers. A V5 story is still worth having in `Assets/games/` — border_zone is one — and
# `checkpoint-per-game.sh` exercises every story it finds there, so the V5 path is covered by that.

GAMES_DIR="${INFOCOMPANION_GAMES_DIR:-/games}"

# The stories this project can drive, best first. `.z8` and `.z5` are the same machine generation as
# the checks care about, and all four are plain parser games — no front matter, no form.
pick_story() {
    local candidate
    if [ -n "${INFOCOMPANION_TEST_STORY:-}" ] && [ -f "$INFOCOMPANION_TEST_STORY" ]; then
        printf '%s\n' "$INFOCOMPANION_TEST_STORY"
        return 0
    fi
    for candidate in "$GAMES_DIR/advent.z5" "$GAMES_DIR/zork1.z3" "$GAMES_DIR/border_zone.z5" \
        "$GAMES_DIR/bureaucracy.z4"; do
        if [ -f "$candidate" ]; then
            printf '%s\n' "$candidate"
            return 0
        fi
    done
    return 1
}

# The story a test runs on: its argument, or the first this image can see.
story_for() { # story_for [given]
    local given="${1:-}"
    if [ -n "$given" ]; then
        printf '%s\n' "$given"
        return 0
    fi
    pick_story || {
        printf '%s\n' "$GAMES_DIR/zork1.z3"
    }
}
