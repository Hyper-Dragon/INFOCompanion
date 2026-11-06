#!/usr/bin/env bash
# The web interface on its own: the pages it serves, the guard on its paths, and a pane with no
# session behind it. No daemon, no game, no API key — the bridge is tested as a bridge.
#
#   docker compose run --rm -T --entrypoint /bin/bash infocompanion /tests/webui.sh </dev/null
#
# The `</dev/null` is not decoration: compose attaches the new container to this script's stdin,
# and when that never closes the run never returns — every check passes and then it sits there.
# `-T` says the same thing on the tty side.
#
# Written in bash, deliberately: the thing under test is a Rust program, and a harness in a
# third language is a third language to keep alive. Everything here is a socket, a byte count
# and a string compare, and `/dev/tcp` plus `dd` are enough for both halves of an HTTP request —
# including the WebSocket handshake, whose accept key is computed here rather than asked of the
# server. A test that calls the implementation it is checking proves only that the
# implementation is self-consistent.
set -uo pipefail

PORT="${WEBUI_TEST_PORT:-8099}"
export INFOCOMPANION_WEB_PORT="$PORT"
export INFOCOMPANION_WEB_BIND="127.0.0.1"
export INFOCOMPANION_WEB_ROOT="${INFOCOMPANION_WEB_ROOT:-/opt/webui/public}"
export INFOCOMPANION_DAEMON_HOST="127.0.0.1"
# A set-then-withheld model key. Set for the server rather than for this script, because the
# bundle reads the environment of the process that was launched — and the two halves of the rule
# can then both be checked: the *name* must be in the manifest, the *value* must be nowhere.
export INFOCOMPANION_LLM_API_KEY="sk-smoke-must-not-travel"
# And the older name, which the companion still reads for one release: neither value may travel.
export DEEPSEEK_API_KEY="sk-old-name-must-not-travel"
# Nothing is listening here, and that is the point of the last check.
export INFOCOMPANION_DAEMON_PORT="9"
export INFOCOMPANION_PANE_MULTI="game,companion,me"
export INFOCOMPANION_PERSONA="james"
export INFOCOMPANION_PLAYER_NAME="Peter"
# The test owns the **endpoint** for the same reason it owns the persona: a deployment may quite
# properly have a voice configured, and the last voice check below is about what a page does when
# there is nowhere to speak (`08_webui_tts.md` §7 (`V32`)). Left inherited from whatever container
# this runs in, that check stops testing anything — with the reference endpoint up, the route is a
# route and the endpoint answers, so it fails for being right about the wrong situation.
export INFOCOMPANION_VOICE_URL=""

# Fixtures for /stats.json (09_stats.md): a library holding everything that is not a game as well
# as two that are, and a state directory with a pointer. Temporary directories on purpose —
# pointed at the real /games, the assertions would depend on what happens to be mounted.
FIXTURES="$(mktemp -d)"
GAMES="$FIXTURES/games"
STATE="$FIXTURES/state"
# The smoke test owns the persona it runs with rather than reading the shipped one: the voice a
# deployment has is the `voice:` key in *this* file (04_companion.md §11 (`C61`)), so a check that
# read the real character would change meaning every time somebody edited a persona.
PROMPTS="$FIXTURES/prompts"
mkdir -p "$GAMES" "$STATE" "$PROMPTS/persona"
: >"$GAMES/advent.z5"
: >"$GAMES/wishbringer.DAT"    # the extension list is case-insensitive
: >"$GAMES/wishbringer.md"     # a sidecar, not a story
: >"$GAMES/notes.txt"
mkdir "$GAMES/folder.z5"       # a directory is not a game, whatever it is called
printf 'advent\n' >"$STATE/current-game"
export INFOCOMPANION_GAMES_DIR="$GAMES"
export INFOCOMPANION_STATE_DIR="$STATE"
export INFOCOMPANION_PROMPTS_DIR="$PROMPTS"
# The first persona names no voice at all, which is the case worth checking first: no switch and no
# route, rather than a button that could not do anything (`V15`).
printf -- '---\nname: James\n---\n\nYou are James.\n' >"$PROMPTS/persona/james.txt"

WEBUI_BIN="${WEBUI_BIN:-/usr/local/bin/infocompanion-webui}"
WEBUI_LOG="$(mktemp)"
"$WEBUI_BIN" >"$WEBUI_LOG" 2>&1 &
SERVER=$!
trap 'kill $SERVER 2>/dev/null; rm -f "$WEBUI_LOG"; rm -rf "$FIXTURES"' EXIT

FAILURES=0
check() { # check <name> <ok:0|1> [detail]
    if [ "$2" -eq 0 ]; then
        echo "  ok    $1"
    else
        echo "  FAIL  $1   [$3]"
        FAILURES=$((FAILURES + 1))
    fi
}

# -- sockets ---------------------------------------------------------------------------------

# A GET whose path reaches the server exactly as written. `curl` would normalise `..` on the
# way out, which would have this test checking its own client rather than the guard.
STATUS=""
BODY=""
HEADERS=""
get() { # get <path>
    # In a group so the redirect applies to the connect attempt alone: bash prints a line to
    # stderr for a refused connection, and the readiness probe below *expects* to be refused
    # until the server is listening.
    { exec 3<>"/dev/tcp/127.0.0.1/$PORT"; } 2>/dev/null || return 1
    printf 'GET %s HTTP/1.1\r\nHost: 127.0.0.1:%s\r\nConnection: close\r\n\r\n' "$1" "$PORT" >&3
    local response
    response="$(cat <&3)"
    exec 3<&- 3>&-
    STATUS="$(printf '%s' "$response" | head -1 | cut -d' ' -f2)"
    # Split at the end of the headers: `%%` with a pattern ending in `*` removes the longest
    # suffix, which is everything from the *first* blank line — the same split the body uses.
    HEADERS="${response%%$'\r\n\r\n'*}"
    BODY="${response#*$'\r\n\r\n'}"
}

# A POST with a body, the same way round as `get` — raw, so this is not the test checking its own
# client. Nothing here streams: the answers it is used for are short and closed.
post() { # post <path> <json>
    { exec 3<>"/dev/tcp/127.0.0.1/$PORT"; } 2>/dev/null || return 1
    local body="$2"
    printf 'POST %s HTTP/1.1\r\nHost: 127.0.0.1:%s\r\nContent-Type: application/json\r\nContent-Length: %s\r\nConnection: close\r\n\r\n%s' \
        "$1" "$PORT" "${#body}" "$body" >&3
    local response
    response="$(cat <&3)"
    exec 3<&- 3>&-
    STATUS="$(printf '%s' "$response" | head -1 | cut -d' ' -f2)"
    HEADERS="${response%%$'\r\n\r\n'*}"
    BODY="${response#*$'\r\n\r\n'}"
}

# The RFC 6455 accept key, in hex.
#
# Hex rather than the base64 the wire carries, because the digest has NUL bytes in it about a
# third of the time and bash silently *drops* NUL from a command substitution — a key computed
# that way is wrong whenever it matters to be right.
accept_key() { # accept_key <client-key>
    printf '%s258EAFA5-E914-47DA-95CA-C5AB0DC85B11' "$1" | sha1sum | cut -d' ' -f1
}

# The same thing, decoded from what the server said, so the two can be compared byte for byte.
accept_key_from_header() { # accept_key_from_header <base64 from the server>
    printf '%s' "$1" | base64 -d | od -An -tx1 | tr -d ' \n'
}

# -- the server ------------------------------------------------------------------------------

ready=1
for _ in $(seq 1 50); do
    if get /healthz && [ "$STATUS" = "200" ]; then
        ready=0
        break
    fi
    sleep 0.1
done
if [ "$ready" -ne 0 ]; then
    check "the interface starts" 1 "$(tail -3 "$WEBUI_LOG" | tr '\n' ' ')"
    exit 1
fi
check "the interface starts and answers on /healthz" 0

echo "-- the pages"
page() { # page <path> <marker>
    get "$1"
    case "$BODY" in
        *"$2"*) check "$1 served" 0 ;;
        *) check "$1 served" 1 "status=$STATUS marker=$2" ;;
    esac
}
page / multiterm
page /term xterm.js
page /multiterm xterm.js
page /config.js seat
page /app.css --phosphor
page /app.css crt
page /app.js WebSocket
page /app.js fullscreen
# The three session controls reach the page: the switches, the pace, and the danger level's own
# segmented housing (07_webui.md §5.1 (`W22`–`W25`)). A marker check rather than a behaviour one — this test is
# about what is served, and the control's behaviour is checked in a browser.
page /app.js riskhigh
page /app.js data-risk
page /app.css segmented
page /assets/frame.svg '<svg'
page /assets/xterm.js xterm
page /assets/xterm.css xterm
page /assets/xterm-addon-fit.js FitAddon
page /healthz ok

echo "-- paths it must refuse"
for path in "/../etc/passwd" "/assets/../../etc/passwd" "/assets/..%2fpasswd" \
            "/etc/passwd" "/assets/" "/nope" "/app.css/../app.js"; do
    get "$path"
    check "$path refused" "$([ "$STATUS" = "404" ] && echo 0 || echo 1)" "status=$STATUS"
done

echo "-- what the pages are told"
get /config.js
CONFIG="$BODY"
# Each pane, on one line, so every check is about a pane rather than about key order.
objects() { # objects <single|multi>
    printf '%s\n' "$CONFIG" \
        | awk -v key="\"$1\": [" 'index($0, key) { inside = 1 } inside { print } inside && /^ *\]/ { exit }' \
        | tr -d '\n' | sed 's/},/}\n/g'
}
check "config.js is javascript" \
      "$([ "${CONFIG:0:12}" = "window.IC = " ] && echo 0 || echo 1)" "${CONFIG:0:40}"
seats="$(objects multi | grep -o '"seat": "[a-z]*"' | cut -d'"' -f4 | tr '\n' ' ')"
check "three panes, in order" \
      "$([ "$seats" = "game companion me " ] && echo 0 || echo 1)" "$seats"
check "the game gets the monitor, your hands the strip, and the companion the plain panel" \
      "$(case "$(objects multi | grep '"seat": "game"')" in *'"shape": "monitor"'*) \
            case "$(objects multi | grep '"seat": "companion"')" in *'"shape": "panel"'*) \
              case "$(objects multi | grep '"seat": "me"')" in *'"shape": "strip"'*) echo 0 ;; *) echo 1 ;; esac ;; \
            *) echo 1 ;; esac ;; \
          *) echo 1 ;; esac)" \
      "game: $(objects multi | grep '"seat": "game"') / companion: $(objects multi | grep '"seat": "companion"') / me: $(objects multi | grep '"seat": "me"')"
check "the pane you type in takes the cursor" \
      "$([ "$(objects multi | grep -c '"focus": true')" = 1 ] && echo 0 || echo 1)" \
      "$(objects multi | grep '"focus": true')"
check "the companion pane is named after the personality" \
      "$(case "$(objects multi | grep '"seat": "companion"')" in *'"label": "JAMES"'*) echo 0 ;; *) echo 1 ;; esac)" \
      "$(objects multi | grep '"seat": "companion"')"
check "the pane you type in is named after the player in the config" \
      "$(case "$(objects multi | grep '"seat": "me"')" in *'"label": "PETER"'*) echo 0 ;; *) echo 1 ;; esac)" \
      "$(objects multi | grep '"seat": "me"')"
check "one-screen page defaults to the session view" \
      "$([ "$(objects single | grep -o '"seat": "[a-z]*"' | cut -d'"' -f4)" = "player" ] && echo 0 || echo 1)" \
      "$(objects single)"

echo "-- the instance's numbers (09_stats.md)"
contains() { # contains <haystack> <needle>
    case "$1" in *"$2"*) echo 0 ;; *) echo 1 ;; esac
}
get /stats.json
check "/stats.json is served" "$([ "$STATUS" = "200" ] && echo 0 || echo 1)" "status=$STATUS"
check "it is a JSON document, and never cached" \
      "$(printf '%s' "$HEADERS" | grep -qi '^content-type: application/json' \
         && printf '%s' "$HEADERS" | grep -qi '^cache-control: no-store' \
         && echo 0 || echo 1)" \
      "$(printf '%s' "$HEADERS" | tr -d '\r' | grep -iE '^(content-type|cache-control)' | tr '\n' ' ')"
check "and a browser on another page may read it" \
      "$(printf '%s' "$HEADERS" | grep -qi '^access-control-allow-origin: [*]' && echo 0 || echo 1)" \
      "$(printf '%s' "$HEADERS" | grep -i '^access-control' || true)"
check "the player comes from the configuration" "$(contains "$BODY" '"player":"Peter"')" "$BODY"
check "the persona too" "$(contains "$BODY" '"persona":"james"')" "$BODY"
check "the game is the state directory's pointer, trimmed of its newline" \
      "$(contains "$BODY" '"game":"advent"')" "$BODY"
check "the library is the two stories, and nothing else" \
      "$([ "$(contains "$BODY" '"games_available":2')" = 0 ] \
         && [ "$(contains "$BODY" '"games":["advent","wishbringer"]')" = 0 ] && echo 0 || echo 1)" "$BODY"
check "the schema is declared" "$(contains "$BODY" '"schema":1')" "$BODY"

echo "-- the voice (08_webui_tts.md)"
page /app.js voiceControl
page /app.js ic.tts
page /app.js voice/say
# One window speaks and the others stay quiet (`V1.1`): the lease is how the pages agree.
page /app.js ic.voice.speaker
get /config.js
check "a persona that names no voice says so to the page" \
      "$(contains "$BODY" '"voice": null')" \
      "$(printf '%s' "$BODY" | grep -o '"voice":[^,]*' || true)"
get /voice
check "and there is no voice to listen to" \
      "$([ "$STATUS" = "404" ] && echo 0 || echo 1)" "status=$STATUS"
post /voice/say '{"text":"the grate looks loose"}'
check "and nothing to ask to speak it" \
      "$([ "$STATUS" = "404" ] && echo 0 || echo 1)" "status=$STATUS"

# The other half of the rule: a persona that names one. There is a switch because there is
# something behind it, and the page is told which voice it is (`V18`).
kill "$SERVER" 2>/dev/null
wait "$SERVER" 2>/dev/null
printf -- '---\nname: James\nvoice: bm_fable\n---\n\nYou are James.\n' >"$PROMPTS/persona/james.txt"
"$WEBUI_BIN" >"$WEBUI_LOG" 2>&1 &
SERVER=$!
ready=1
for _ in $(seq 1 50); do
    if get /healthz && [ "$STATUS" = "200" ]; then
        ready=0
        break
    fi
    sleep 0.1
done
check "the interface comes back with a different persona" "$ready" "$(tail -3 "$WEBUI_LOG" | tr '\n' ' ')"
get /config.js
check "a persona that names a voice is a page with a switch" \
      "$(contains "$BODY" '"voice": "bm_fable"')" \
      "$(printf '%s' "$BODY" | grep -o '"voice":[^,]*' || true)"
# A voice with nowhere to be spoken is still not a route: the page falls back to the browser's own
# voice rather than asking something that is not there (08_webui_tts.md §7 `V32`).
post /voice/say '{"text":"the grate looks loose"}'
check "and a named voice with no endpoint is still not a route" \
      "$([ "$STATUS" = "404" ] && echo 0 || echo 1)" "status=$STATUS"

echo "-- the evening's numbers (07_webui.md)"
# Fixtures for /evening.txt: one evening's two logs, small enough to check by eye. The report
# reads the newest of each by name, so one of each is enough — and the numbers below are the
# ones the report is for.
mkdir -p "$STATE/logs"
cat >"$STATE/logs/agent-20260916T222446Z.jsonl" <<'JSONL'
{"kind":"session_start","ts":"2026-09-16T22:24:46Z"}
{"kind":"llm_call","purpose":"mission-step","cost_usd":0.01,"cost_total_usd":0.01,"latency_ms":4000,"truncated":false,"ts":"2026-09-16T22:25:00Z"}
{"kind":"llm_call","purpose":"act-missed","cost_usd":0.03,"cost_total_usd":0.04,"latency_ms":2000,"truncated":true,"ts":"2026-09-16T22:26:00Z"}
{"kind":"act_missed","purpose":"mission-step","after_another_miss":false,"ts":"2026-09-16T22:26:00Z"}
{"kind":"warn","msg":"the game's parser refused a command; that verb will not be sent again","ts":"2026-09-16T22:31:00Z"}
JSONL
cat >"$STATE/logs/core-20260916T222445Z.jsonl" <<'JSONL'
{"kind":"turn","ts":"2026-09-16T22:25:00Z"}
{"kind":"agent_game_input","ts":"2026-09-16T22:25:30Z"}
JSONL
get /evening.txt
check "/evening.txt is served" "$([ "$STATUS" = "200" ] && echo 0 || echo 1)" "status=$STATUS"
check "it is plain text, and never cached" \
      "$(printf '%s' "$HEADERS" | grep -qi '^content-type: text/plain' \
         && printf '%s' "$HEADERS" | grep -qi '^cache-control: no-store' \
         && echo 0 || echo 1)" \
      "$(printf '%s' "$HEADERS" | tr -d '\r' | grep -iE '^(content-type|cache-control)' | tr '\n' ' ')"
check "and a browser on another page may read it" \
      "$(printf '%s' "$HEADERS" | grep -qi '^access-control-allow-origin: [*]' && echo 0 || echo 1)" \
      "$(printf '%s' "$HEADERS" | grep -i '^access-control' || true)"
check "it names the log it read" "$(contains "$BODY" 'agent-20260916T222446Z.jsonl')" "$BODY"
check "it counts the calls and the turns" \
      "$([ "$(contains "$BODY" 'calls        2')" = 0 ] \
         && [ "$(contains "$BODY" 'turns        1')" = 0 ] && echo 0 || echo 1)" "$BODY"
check "it shows the spend and the span" \
      "$([ "$(contains "$BODY" '$0.0400')" = 0 ] \
         && [ "$(contains "$BODY" '6m 14s')" = 0 ] && echo 0 || echo 1)" "$BODY"
check "and the finding that costs the money" \
      "$([ "$(contains "$BODY" 'act_missed        1 events, 1 repair calls, $0.0300')" = 0 ] \
         && [ "$(contains "$BODY" '(75.0% of the spend)')" = 0 ] && echo 0 || echo 1)" "$BODY"

# Single quotes, because backticks inside a double-quoted string are command substitutions: this
# line was trying to run `W31` and `W35` and printing their names as nothing at all.
echo '-- the games, and the write-up (07_webui.md §6.1 (`W31`–`W35`))'
# The home page offers it too: a link nobody can find is a feature nobody has (07_webui.md §4 (`W12`–`W17`)).
get /
check "the home page offers the games" "$(contains "$BODY" 'href="/games"')" "$BODY"
# Fixtures: a game with played data (a turn record and a playback), a game that only has a story
# file, and a write-up for the first one. Everything the page lists comes from here, so the
# assertions cannot depend on what happens to be in a real state directory.
mkdir -p "$STATE/observations" "$STATE/playback" "$STATE/catchup"
printf '{"turn":1,"input":"look","output":"A well house."}\n' >"$STATE/observations/advent.jsonl"
printf '=== advent, attempt 1, generation 1 ===\n>look\n' >"$STATE/playback/advent.log"
printf '{"stem":"advent","generated_at":"2026-09-19T02:22:31+00:00","model":"deepseek-flash","turns":20,"cost_usd":0.00175}\n' \
    >"$STATE/catchup/advent.json"
printf '<!doctype html><html><body><h1>a report</h1></body></html>\n' >"$STATE/catchup/advent.html"

get /games
check "/games is served" "$([ "$STATUS" = "200" ] && echo 0 || echo 1)" "status=$STATUS"
check "it lists the game that has played data" "$(contains "$BODY" '/catchup/advent')" "$BODY"
check "with the date the report claims, not the file's" "$(contains "$BODY" '2026-09-19 02:22 UTC')" "$BODY"
check "and does not list a game nobody has played" \
      "$([ "$(contains "$BODY" 'wishbringer')" = 0 ] && echo 1 || echo 0)" "$BODY"

# A report that exists is served as the file it is.
get /catchup/advent
check "a write-up is served" "$([ "$STATUS" = "200" ] && echo 0 || echo 1)" "status=$STATUS"
check "and it is the page the companion wrote" "$(contains "$BODY" 'a report')" "$BODY"

# One that does not exist is a page saying so, not a 404 and not a blank screen.
get /catchup/wishbringer
check "no write-up yet says so" \
      "$([ "$STATUS" = "200" ] && [ "$(contains "$BODY" 'No write-up yet')" = 0 ] && echo 0 || echo 1)" \
      "status=$STATUS $BODY"

# A stem from a URL is kept to what a file name can be: a traversal is a game that does not
# exist, which is the same answer and one less branch.
get "/catchup/..%2F..%2Fetc%2Fpasswd"
check "a path in a stem cannot reach a file" \
      "$([ "$STATUS" = "200" ] && [ "$(contains "$BODY" 'No write-up yet')" = 0 ] && echo 0 || echo 1)" \
      "status=$STATUS $BODY"

echo '-- the debug bundle, and that it is a zip a real tool will open (07_webui.md §6.2 (`W36`–`W43`))'
# The page has to point at it: the whole point was not having to open a shell to fetch files.
get /games
check "the games page offers the debug bundle" \
      "$(contains "$BODY" 'href="/debug/bundle"')" "$BODY"
check "as a button rather than small print" \
      "$(contains "$BODY" 'class="button" href="/debug/bundle"')" "$BODY"

# A set-then-withheld key, so the two halves of the rule can both be checked: the *name* must be
# in the manifest and the *value* must not be anywhere in the file.
: >"$STATE/observations/wishbringer.jsonl"

# Fetched straight onto disk rather than into a variable: this body is a zip, and a command
# substitution silently drops NUL bytes — the same trap the WebSocket key above documents. Of
# everything in the zip, the one thing that has to be looked for byte for byte is the key.
BUNDLE="$(mktemp)"
{ exec 3<>"/dev/tcp/127.0.0.1/$PORT"; } 2>/dev/null || true
printf 'GET /debug/bundle HTTP/1.1\r\nHost: 127.0.0.1:%s\r\nConnection: close\r\n\r\n' "$PORT" >&3
cat <&3 >"$BUNDLE"
exec 3<&- 3>&-

STATUS_LINE="$(head -1 "$BUNDLE" | tr -d '\r')"
check "the debug bundle is served" \
      "$([ "$STATUS_LINE" = 'HTTP/1.1 200 OK' ] && echo 0 || echo 1)" "$STATUS_LINE"
check "as a zip, not a page" "$(grep -a -q 'application/zip' "$BUNDLE" && echo 0 || echo 1)" ""
check "as a download, with a name that carries the moment" \
      "$(grep -a -q 'attachment; filename="ic-debug-2' "$BUNDLE" && echo 0 || echo 1)" ""
check "and it is not cached" "$(grep -a -q 'no-store' "$BUNDLE" && echo 0 || echo 1)" ""

# The signatures are checked in hex, because a stored zip's entries are literal and the local
# header is the first thing every reader looks for. `od` rather than `grep -P` for the same
# reason the WebSocket digest is compared in hex: what is being asserted is bytes.
HEX="$(od -An -tx1 "$BUNDLE" | tr -d ' \n')"
check "the local file header is there" \
      "$(printf '%s' "$HEX" | grep -q '504b0304' && echo 0 || echo 1)" ""
check "the end of the central directory is there" \
      "$(printf '%s' "$HEX" | grep -q '504b0506' && echo 0 || echo 1)" ""

# Nothing is compressed, so the manifest can be read straight out of the file — which is also
# how a person receiving it by mail will read it.
check "the manifest is inside, uncompressed" \
      "$(grep -a -q '# INFOCompanion debug bundle' "$BUNDLE" && echo 0 || echo 1)" ""
check "and it lists the state directory it read" \
      "$(grep -a -q -- "- state directory: \`$STATE\`" "$BUNDLE" && echo 0 || echo 1)" ""
check "it names the turn record it carries" \
      "$(grep -a -q 'observations/wishbringer.jsonl' "$BUNDLE" && echo 0 || echo 1)" ""

# The manifest is stored rather than deflated, so the zip is not text but the manifest in it is:
# every byte that is not printable becomes a newline and the line can then be read normally. The
# line itself is the check's detail, because a check that fails without saying what it saw costs
# ten minutes every time it happens — which is how this one was written.
KEY_LINE="$(tr -c '[:print:]\n' '\n' <"$BUNDLE" | grep -a 'INFOCOMPANION_LLM_API_KEY' | head -2)"
check "it says whether a model key was set" \
      "$(printf '%s' "$KEY_LINE" | grep -a -q '= set' && echo 0 || echo 1)" "$KEY_LINE"
check "and the key itself never travels" \
      "$(grep -a -q 'sk-smoke-must-not-travel' "$BUNDLE" && echo 1 || echo 0)" "$KEY_LINE"
check "nor the value under the older name" \
      "$(grep -a -q 'sk-old-name-must-not-travel' "$BUNDLE" && echo 1 || echo 0)" "$KEY_LINE"
rm -f "$BUNDLE"

echo "-- a pane with no session to talk to"
KEY="$(head -c 16 /dev/urandom | base64 -w0)"
RAW="$(mktemp)"

# In a group, so the stderr redirect lasts for the connect attempt alone: bash prints a line of
# its own for a refused connection, and this test does not want to explain itself twice.
{ exec 3<>"/dev/tcp/127.0.0.1/$PORT"; } 2>/dev/null
printf 'GET /ws?seat=game&cols=80&rows=24 HTTP/1.1\r\nHost: 127.0.0.1:%s\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: %s\r\nSec-WebSocket-Version: 13\r\n\r\n' \
       "$PORT" "$KEY" >&3
# Everything the pane will ever send, until it hangs up — which, with nothing behind it, it does
# immediately. Reading it in one go and parsing the file is both faster and more careful than
# taking a byte at a time out of the socket: `head -c` can read past what it returns.
timeout 5 cat <&3 >"$RAW"
exec 3<&- 3>&-

# Split at the end of the headers, by counting bytes. The pattern cannot be handed to grep —
# a pattern containing a newline is two patterns — and it must be byte-exact because a pane's
# output is not required to be text. `od -v -tu1` is the only tool here that will tell you what
# each byte actually was, without dropping the ones that look like an end of string.
SPLIT="$(od -An -v -tu1 "$RAW" | awk '
    { for (i = 1; i <= NF; i++) { value = $i; seen++
        if (value == 13) { carriage = 1 }
        else if (value == 10 && carriage) { carriage = 0; ends++ }
        else { carriage = 0; ends = 0 }
        if (ends == 2) { print seen; exit } } }')"
if [ -z "$SPLIT" ]; then
    check "the pane opens a websocket" 1 "no header terminator in $(wc -c <"$RAW") bytes"
else
    HEAD="$(dd bs=1 count="$SPLIT" if="$RAW" 2>/dev/null)"
    STATUS_LINE="$(printf '%s' "$HEAD" | head -1 | tr -d '\r')"
    SENT="$(printf '%s' "$HEAD" | tr -d '\r' | sed -n 's/^[Ss]ec-[Ww]ebsocket-[Aa]ccept: *//p')"
    check "the pane opens a websocket" \
          "$([ "$STATUS_LINE" = "HTTP/1.1 101 Switching Protocols" ] && echo 0 || echo 1)" "$STATUS_LINE"
    check "and proves it understood the handshake" \
          "$([ -n "$SENT" ] && [ "$(accept_key_from_header "$SENT")" = "$(accept_key "$KEY")" ] && echo 0 || echo 1)" \
          "server said ${SENT:-nothing}, expected $(accept_key "$KEY")"

    # The server's frames are never masked and this one is short, so the length is one byte and
    # the first byte should be 0x82: a final, binary frame.
    HEADER="$(dd bs=1 skip="$SPLIT" count=2 if="$RAW" 2>/dev/null | od -An -tu1 | tr -s ' ' | tr -d '\n')"
    OPCODE="$(printf '%s' "$HEADER" | awk '{ print $1 }')"
    LENGTH="$(printf '%s' "$HEADER" | awk '{ print $2 }')"
    check "the notice arrives as one whole binary frame" \
          "$([ "$OPCODE" = "130" ] && [ -n "$LENGTH" ] && [ "$LENGTH" -gt 20 ] && echo 0 || echo 1)" \
          "opcode=$OPCODE length=$LENGTH"
    PAYLOAD="$(dd bs=1 skip=$((SPLIT + 2)) count="${LENGTH:-0}" if="$RAW" 2>/dev/null)"
    check "the pane is not left blank" "$([ ${#PAYLOAD} -gt 20 ] && echo 0 || echo 1)" "${#PAYLOAD} bytes"
    check "a pane that cannot reach the session says so" \
          "$(case "$PAYLOAD" in *"[webui]"*"cannot reach"*) echo 0 ;; *) echo 1 ;; esac)" "${PAYLOAD:0:80}"
    check "and says it in the panes' own colour" \
          "$(case "$PAYLOAD" in *$'\x1b[33m'*) echo 0 ;; *) echo 1 ;; esac)" "${PAYLOAD:0:40}"
fi
rm -f "$RAW"

echo
if [ "$FAILURES" -ne 0 ]; then
    echo "  $FAILURES failure(s)"
    exit 1
fi
echo "  every check passed"
