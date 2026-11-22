#!/usr/bin/env bash
# CX-195/CX-198: a name this codebase retired may not survive in managed/, docs/ or check-shaders.py, which mirrors managed/'s shader build. The list and the format
# are tools/gates/retired-terms.txt; retiring a stage, mode or knob adds its name there in the same change.
# One git grep over every term finds the candidate lines (PCRE when git has it, the same lines and faster);
# each term is then matched against their text alone.
cd "$(dirname "$0")/../.." || exit 2
list=tools/gates/retired-terms.txt
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
awk -F'\t' '$1 != "" && $1 !~ /^#/ { print $1 }' "$list" > "$tmp/terms"
kind=-E; git grep -qP x -- "$list" 2>/dev/null; [ $? -le 1 ] && kind=-P
git grep --untracked -niI "$kind" -f "$tmp/terms" -- managed docs check-shaders.py 2>/dev/null \
  | grep -v '^docs/adr/' | grep -v '^docs/handoff/' > "$tmp/hits"
[ -s "$tmp/hits" ] || exit 0
sed -E 's/^[^:]*:[0-9]+://' "$tmp/hits" > "$tmp/text"
rc=0
while IFS=$'\t' read -r term ticket allow; do
  case "$term" in ''|'#'*) continue ;; esac
  hits=$(grep -niE -- "$term" "$tmp/text" | cut -d: -f1 | awk 'NR == FNR { want[$1]; next } FNR in want' - "$tmp/hits" \
         | { [ -n "$allow" ] && grep -viE -- "$allow" || cat; })
  [ -z "$hits" ] && continue
  echo "!! retired-terms: \"$term\" was retired by $ticket and still appears:" >&2
  echo "$hits" | sed 's/^/  /' >&2
  [ -n "$allow" ] && echo "  (a line that means something else must say so: /$allow/)" >&2
  rc=1
done < "$list"
exit $rc
