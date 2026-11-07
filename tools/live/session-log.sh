#!/usr/bin/env bash
# Usage: session-log.sh <log folder> <out folder>. Writes <out folder>/cx.log from the session's log parts.
cp -f "$1/cx.log" "$2/cx.log" 2>/dev/null
