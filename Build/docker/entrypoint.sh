#!/usr/bin/env bash
# The image's entry point: the session, and the web interface alongside it.
#
# The daemon is the container's process — it owns the game, the state and the companion, and
# its exit is the container's exit — so it is *exec'd* rather than wrapped by a supervisor.
# The web interface is started behind it because it is a convenience for looking at the panes
# in a browser, never a thing the session depends on: if it cannot start, the session is
# unaffected and the log says so.
set -u

# The shipped assets first (12_shipped_container.md §2 (`P4`)): a fresh container must be a working one, so the
# pack is copied into the working paths before anything else starts — and a seed that
# cannot write (a read-only Assets/games/ or Assets/prompts/) must stop the container rather than
# leave it half-provisioned.
/usr/local/bin/seed-assets.sh || exit 1

# A flag means this is a scripted run (the container tests call the binary directly anyway),
# and a scripted run is not a session anybody is watching in a browser.
if [ "${INFOCOMPANION_NO_WEBUI:-false}" = "true" ]; then
    echo "web interface: off (INFOCOMPANION_NO_WEBUI=true)"
elif [ "$#" -gt 0 ]; then
    echo "web interface: off (running in a mode, not as a session: $*)"
else
    /usr/local/bin/infocompanion-webui &
    echo "web interface: http://<host>:${INFOCOMPANION_WEB_PORT:-8080} (no authentication)"
fi

exec /usr/local/bin/infocompanion "$@"
