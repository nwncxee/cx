#!/usr/bin/env bash
# CX-340: Alloy passes move onto the RHI seam (managed/Rhi/) one at a time. Counts the direct Gl.* call sites per
# file under managed/Alloy; the ratchet lets a file's count fall only, so a migrated pass cannot grow GL back.
cd "$(dirname "$0")/../.."
grep -rHo '\bGl\.[A-Za-z_][A-Za-z0-9_]*(' --include='*.cs' managed/Alloy | cut -d: -f1 | sort | uniq -c \
  | awk '{ print $1, $2 }' | bash tools/gates/ratchet.sh tools/gates/rhi-seam.baseline "$@"
