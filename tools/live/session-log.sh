#!/usr/bin/env bash
# Usage: session-log.sh <log folder> <out folder>. Writes <out folder>/cx.log from the session's log parts, oldest first.
set -uo pipefail
[ $# -eq 2 ] || { echo "usage: session-log.sh <log folder> <out folder>" >&2; exit 2; }
parts=()
for f in "$1"/cx.[0-9].log; do
    [ -f "$f" ] && parts=("$f" "${parts[@]}")
done
[ -f "$1/cx.log" ] && parts+=("$1/cx.log")
[ ${#parts[@]} -gt 0 ] || { echo "session-log: no cx.log in $1" >&2; exit 1; }
cat "${parts[@]}" > "$2/cx.log"
