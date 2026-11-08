#!/usr/bin/env bash
# Read a pulled state directory and say whether the session behaved.
#
#   Build/tests/log-checklist.sh /mnt/d/feelies/logs
#
# A thin wrapper so this sits with the other tests and can be pointed at a directory the same way.
# The reading is in log-checklist.py, because parsing four kinds of JSON line in shell is a way to
# spend an afternoon on quoting. It runs against a *pulled* directory rather than the live state,
# so it needs no container and no stack — a person can run it on a laptop with the logs on disk.
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)

python3 "$here/log-checklist.py" "$@"
