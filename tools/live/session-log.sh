#!/usr/bin/env bash
# Usage: session-log.sh <log folder> <out folder>. Writes <out folder>/cx.log from the session's log parts, oldest first.
parts=()
for f in "$1"/cx.{4,3,2,1}.log "$1/cx.log"; do
    [ -f "$f" ] && parts+=("$f")
done
[ ${#parts[@]} -gt 0 ] && cat "${parts[@]}" > "$2/cx.log"
