#!/usr/bin/env bash
# CX-195/CX-198: a name this codebase retired may not survive in managed/ or docs/. The list and the format
# are tools/gates/retired-terms.txt; retiring a stage, mode or knob adds its name there in the same change.
cd "$(dirname "$0")/../.."
list=tools/gates/retired-terms.txt
rc=0
while IFS=$'\t' read -r term ticket allow; do
  case "$term" in ''|'#'*) continue ;; esac
  hits=$(git grep --untracked -niIE -- "$term" managed docs 2>/dev/null \
         | grep -v '^docs/adr/' | grep -v '^docs/handoff/' \
         | { [ -n "$allow" ] && grep -viE -- "$allow" || cat; })
  [ -z "$hits" ] && continue
  echo "!! retired-terms: \"$term\" was retired by $ticket and still appears:" >&2
  echo "$hits" | sed 's/^/  /' >&2
  [ -n "$allow" ] && echo "  (a line that means something else must say so: /$allow/)" >&2
  rc=1
done < "$list"
exit $rc
