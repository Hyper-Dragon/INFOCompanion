#!/usr/bin/env bash
# Seed the bundled assets into their working paths, once per container start.
#
# The rule, in one line: copy what is missing, never overwrite, and treat a
# destination that cannot be written as a hard failure. Assets/games/, Assets/prompts/ and
# state/ must be writable; do not mount them read-only (12_shipped_container.md §2 (`P4`)).
#
# The pack lives at /opt/infocompanion/assets. Deleting that directory is the
# removal path: the seeder then reports that there is nothing to seed and the
# container boots without it, exactly as it did before the pack existed.
#
# Overrides, for the test suite and for odd deployments: INFOCOMPANION_ASSETS_DIR,
# INFOCOMPANION_GAMES_DIR, INFOCOMPANION_STATE_DIR, INFOCOMPANION_PROMPTS_DIR.
set -u

ASSETS="${INFOCOMPANION_ASSETS_DIR:-/opt/infocompanion/assets}"
GAMES="${INFOCOMPANION_GAMES_DIR:-/games}"
STATE="${INFOCOMPANION_STATE_DIR:-/state}"
PROMPTS="${INFOCOMPANION_PROMPTS_DIR:-/prompts}"

copied=0
kept=0

fail() {
    echo "seed-assets: $1" >&2
    echo "seed-assets: Assets/games/, Assets/prompts/ and state/ must be writable; do not mount them read-only" >&2
    exit 1
}

seed_one() { # $1 = source file, $2 = destination path
    local src="$1" dest="$2"
    if [ -e "$dest" ]; then
        if [ -d "$dest" ]; then
            echo "seed-assets: note: $dest is a directory (a bind source that does not exist?); leaving it alone"
        fi
        kept=$((kept + 1))
        return 0
    fi
    mkdir -p "$(dirname "$dest")" 2>/dev/null || fail "cannot create $(dirname "$dest")"
    cp -p "$src" "$dest" 2>/dev/null || fail "cannot write $dest"
    copied=$((copied + 1))
}

seed_dir() { # $1 = source directory, $2 = destination directory
    [ -d "$1" ] || return 0
    local src
    while IFS= read -r -d '' src; do
        seed_one "$src" "$2/${src#"$1"/}"
    done < <(find "$1" -type f -print0)
}

if [ -d "$ASSETS" ]; then
    seed_dir "$ASSETS/games" "$GAMES"
    seed_dir "$ASSETS/prompts" "$PROMPTS"
    [ -f "$ASSETS/agent-config.json" ] && seed_one "$ASSETS/agent-config.json" "$STATE/agent-config.json"
    # The daemon's and the bridge's own tuning, seeded the same way: a container with no mounts is
    # still a tuned one, and the copy here is what an operator finds when they go looking for the
    # settings (10_delivery.md §2 (`D5`), 07_webui.md §7 (`W44`–`W47`)).
    [ -f "$ASSETS/core-config.json" ] && seed_one "$ASSETS/core-config.json" "$STATE/core-config.json"
    [ -f "$ASSETS/webui-config.json" ] && seed_one "$ASSETS/webui-config.json" "$STATE/webui-config.json"
fi

if [ ! -d "$ASSETS" ]; then
    echo "assets: no pack at $ASSETS (nothing to seed)"
elif [ "$copied" -eq 0 ]; then
    echo "assets: nothing to seed ($kept file(s) already present)"
else
    echo "assets: seeded $copied file(s), kept $kept existing"
fi

exit 0
