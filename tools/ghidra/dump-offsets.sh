#!/usr/bin/env bash
# Run OffsetDump.java over one program (headless.sh picks its Ghidra project).
#
#   tools/ghidra/dump-offsets.sh nwmain-linux out/lin.tsv all
#   tools/ghidra/dump-offsets.sh nwmain.exe   out/win.tsv all addr:1407a41a0 ...
#
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

prog="${1:?program name (nwmain-linux | nwmain.exe)}"; shift
out="${1:?output tsv path}"; shift

GHIDRA_HEADLESS_MAXMEM="${GHIDRA_HEADLESS_MAXMEM:-8G}" "$HERE/headless.sh" "$prog" OffsetDump.java "$out" "$@" \
  2>&1 | grep -E 'OffsetDump\.java>|ERROR|error:' || true
