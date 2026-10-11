#!/usr/bin/env bash
# CX-196: a .cs file under managed/ past 1,500 lines is split by responsibility -- partials or sibling
# classes, one job each, the way AlloyRenderer's are. Generated files (*.g.cs) and SymbolMap.cs (a flat
# symbol table that grows with every hook, not a responsibility to split) are exempt. Ratchet:
# file-size.baseline lists what is still over with its current length and may only shrink.
cd "$(dirname "$0")/../.."
find managed -name '*.cs' ! -name '*.g.cs' ! -path managed/Framework/SymbolMap.cs -print0 \
  | xargs -0 wc -l \
  | awk 'NF == 2 && $2 ~ /^managed\// && $1 > 1500 { print $1, $2 }' \
  | bash tools/gates/ratchet.sh tools/gates/file-size.baseline "$@"
