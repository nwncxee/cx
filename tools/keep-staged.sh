#!/usr/bin/env bash
# Exit 0 when cx-linux-x64/ already holds the build of this tree's code, so build.sh can keep it: its BUILT_FROM
# carries the code fingerprint of the tree it was built from (tools/codehash.py), and only comments in .cs files or
# Markdown changed since. Exit 1 when it must rebuild. Usage: tools/keep-staged.sh [fingerprint]
cd "$(dirname "$0")/.." || exit 1
out=cx-linux-x64
code=${1:-$(python3 tools/codehash.py)} || exit 1
[ -f "$out/BUILT_FROM" ] && [ -f "$out/libcx.so" ] && [ -f "$out/cx_managed.so" ] || exit 1
grep -qx "code $code" "$out/BUILT_FROM" || exit 1
from=$(sed -n 1p "$out/BUILT_FROM")
echo ">> keeping $out/: built from $(git log -1 --format=%h "$from" 2>/dev/null || echo "${from:0:8}") with the same code -- only comments or Markdown changed since"
