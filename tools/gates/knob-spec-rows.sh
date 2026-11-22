#!/usr/bin/env bash
# reaches: managed/* tools/gates/* docs/spec/*
# CX-484: every declared knob has its row in docs/spec/unified-settings.md, and every key the spec rows is
# declared. No baseline: the tree is clean, so any hit is new. `python3 tools/gates/knob-spec-rows.py -v`
# prints the whole census, including the factory calls no registered group claims.
cd "$(dirname "$0")/../.." || exit 2
out=$(python3 tools/gates/knob-spec-rows.py) || exit $?
[ -z "$out" ] && exit 0
echo "!! knob-spec-rows: the settings spec and the declarations disagree:" >&2
while IFS= read -r l; do printf '  %s\n' "$l"; done <<< "$out" >&2
exit 1
