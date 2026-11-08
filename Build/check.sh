#!/usr/bin/env bash
# Every check this project has, in one command.
#
#   Build/check.sh              the source checks, the image, and the container suite
#   Build/check.sh --fast       the source checks alone (no image build, no containers)
#   Build/check.sh webui seed   the source checks, the image, and only those container tests
#   Build/check.sh --logs DIR   the source checks, plus a read of a pulled state directory
#
# The source checks are the three test suites plus formatting, lints, the markdown lint, and the
# checks that read the prose rather than the code:
#
#   markdownlint  every markdown file at the root, the document set, and the two READMEs,
#                 against .markdownlint-cli2.jsonc
#   *-prose       the four that read what was written rather than what was compiled: the generated
#                 phrase tables, the reproduced engine prompt, the prompts' tool names (`C63`),
#                 and the README's own map of the set and the suite (`D15.2`)
#   core-*        Source/Core      fmt, clippy, cargo test   (the daemon)
#   webui-*       Source/WebUI     fmt, clippy, cargo test   (the browser bridge)
#   agent-test    Source/Agent     dotnet test               (the companion)
#
# The .NET SDK is not installed in WSL, so the agent suite runs from a container — the same
# arrangement Documents/11_operations.md describes. Everything else runs on the host.
#
# The container suite is the nine scripts the image carries, in the order they are worth
# running (Build/tests/*.sh plus the web interface's own smoke test, which the Dockerfile copies
# into /tests/webui.sh). It needs the image built and the stack up, which is what this does:
# those tests measure the shipped thing, not the source tree. A story file has to be present in
# Assets/games/ for most of them — story files are not in the repository (Assets/games/.gitignore).
#
# Exit status is 0 only if every check passed. Full logs land in /tmp/check-<name>.log, and a
# failing check prints its last lines here.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
root=$(cd "$here/.." && pwd)
cd "$root" || exit 1

# The container suite in the order it is worth running: the two that only exercise the
# interpreter, then the session's own behaviour, then the two slow ones, then the web interface.
all_tests=(interpreter-tools screen-bureaucracy turn-number-per-game player-commands stdio-smoke
    session-acceptance quit-confirmation checkpoint-per-game seed webui)

fast=0
only=()
logs=()
while [ $# -gt 0 ]; do
    arg=$1
    shift
    case "$arg" in
        --fast) fast=1 ;;
        --logs) logs+=("$1"); shift ;;
        -h|--help) sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        -*) echo "check: unknown option '$arg'" >&2; exit 2 ;;
        *) only+=("$arg") ;;
    esac
done

# A file *stored* with the wrong line endings fails these checks in confusing ways — a shell
# script that arrives with CRLF cannot even be executed in the container. Only the stored form
# matters: what a checkout looks like on Windows is the editor's business, and `.gitattributes`
# names the two paths that are meant to be the exception. (`git ls-files --eol` prints the index
# form in the first column and the worktree form in the second; this reads the first.)
odd=$(git ls-files --eol | awk '$1 == "i/crlf" || $1 == "i/mixed" { print $4 }' \
    | grep -vi '^assets/games/' | grep -v '^Source/dFrotzTooled/' || true)
if [ -n "$odd" ]; then
    echo "check: these files are not stored as LF, which .gitattributes says they should be:"
    printf '       %s\n' $odd
fi

missing=()
for tool in cargo docker git; do
    command -v "$tool" >/dev/null || missing+=("$tool")
done
if [ "$fast" -eq 0 ]; then
    docker compose version >/dev/null 2>&1 || missing+=("docker compose")
fi
if [ ${#missing[@]} -gt 0 ]; then
    echo "check: not found on PATH: ${missing[*]}" >&2
    echo "       cargo lives in ~/.cargo/bin (a login shell has it; 'wsl -e bash' does not)" >&2
    exit 2
fi

pass=0
failed=()

run() { # run <name> <command...>
    local name=$1; shift
    local log="/tmp/check-${name//[^A-Za-z0-9_.-]/_}.log"
    printf '%-30s' "$name"
    if "$@" >"$log" 2>&1; then
        printf 'PASS\n'
        pass=$((pass + 1))
    else
        printf 'FAIL\n'
        failed+=("$name")
        sed 's/^/      /' "$log" | tail -n 12
    fi
}

# The suite rides in the image (Build/tests/), so it runs in the container the tests are about,
# with the compose stack's mounts — the same way a person would run one by hand.
container_test() { # container_test <name>
    timeout 900 docker compose run --rm -T --no-deps \
        --entrypoint /bin/bash infocompanion "/tests/$1.sh" </dev/null
}

# The log checklist reads a state directory a person pulled off the machine after playing. It is not
# part of the container suite: it is about a session that has already happened, and the commands
# that test some of its items belong to a player seat this transport cannot open. So it runs when a
# directory is named on the command line, and reports on its own terms when one is.
if [ ${#logs[@]} -gt 0 ]; then
    echo "== pulled logs =="
    for dir in "${logs[@]}"; do
        run "logs:$(basename "$dir")" python3 "$here/tests/log-checklist.py" "$dir"
    done
    echo
fi

# `C63`: the engine and the personalities MUST NOT name a tool, and a backticked name with a call on it
# — `saves()` — is exactly that defect, which is why this check exists rather than a test. No container
# suite can read these files: each mounts one source tree, and the prompts are not in it.
prompts_named_a_tool() {
    local hits
    hits=$(grep -rEn '`[A-Za-z_][A-Za-z0-9_]*\(' "$root/Assets/prompts" || true)
    if [ -n "$hits" ]; then
        echo "$hits"
        echo "C63: the prompts must not name a tool"
        return 1
    fi
    return 0
}

echo "== source =="
run markdownlint npx --yes markdownlint-cli2 "Documents/*.md" "*.md" "Source/WebUI/README.md"
run prompts-no-tool-names prompts_named_a_tool
# The appendix's phrase tables are generated from the code (`Build/render-vocabulary.py`), so this is
# what stops them drifting back into a hand-written copy — which is how the delegation forms and the
# brief table went missing in the first place.
run vocabulary-tables python3 "$here/render-vocabulary.py" --check
# And §1 reproduces the engine prompt verbatim, which is a copy too. It had drifted: one bullet updated
# and no two-part block added at all (item 15). A diff is the only check that can see that.
run engine-reproduction python3 "$here/check-engine-reproduction.py"
# And the README carries the set's range, a row per document, a count for the container suite and a
# name per test — none of which anything read, so it went on saying `00–15` and *these eight* while
# the tree said otherwise (`D15.2`). A claim that stopped counting, which no differ can see.
run readme-map python3 "$here/check-readme-map.py"
run core-fmt     cargo fmt --manifest-path Source/Core/Cargo.toml -- --check
run core-clippy  cargo clippy --manifest-path Source/Core/Cargo.toml --all-targets --quiet
run core-test    cargo test --manifest-path Source/Core/Cargo.toml --quiet
run webui-fmt    cargo fmt --manifest-path Source/WebUI/Cargo.toml -- --check
run webui-clippy cargo clippy --manifest-path Source/WebUI/Cargo.toml --all-targets --quiet
run webui-test   cargo test --manifest-path Source/WebUI/Cargo.toml --quiet
# The whole repository, not just the project: two checks in the agent suite read files the *tree* owns
# rather than the project — the shipped `agent-config.json`, whose ceilings are held to a floor
# (`C23.7`), and `AgentLoop.cs`, whose purposes have to match the role table (`C23.6`). Mounted at the
# root the suite finds both by walking up from its own assembly, which is also what it does under a
# local `dotnet test`.
run agent-test   docker run --rm -v "$root:/repo" -w /repo/Source/Agent \
    mcr.microsoft.com/dotnet/sdk:10.0 dotnet test --nologo -v q

if [ "$fast" -eq 0 ]; then
    echo
    echo "== image =="
    run image-build docker compose build
    run image-up    docker compose up -d --wait

    echo
    echo "== container suite =="
    tests=("${all_tests[@]}")
    [ ${#only[@]} -gt 0 ] && tests=("${only[@]}")
    for t in "${tests[@]}"; do
        run "container:$t" container_test "$t"
    done
fi

echo
if [ ${#failed[@]} -eq 0 ]; then
    echo "check: $pass passed"
    exit 0
fi
echo "check: $pass passed, ${#failed[@]} failed"
for f in "${failed[@]}"; do
    printf '  - %-28s /tmp/check-%s.log\n' "$f" "${f//[^A-Za-z0-9_.-]/_}"
done
exit 1
