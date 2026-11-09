#!/usr/bin/env bash
# CX-484: every declared knob has its row in docs/spec/unified-settings.md, and every key the spec rows is
# declared. No baseline: the tree is clean, so any hit is new. `python3 tools/gates/knob-spec-rows.py -v`
# prints the whole census, including the factory calls no registered group claims.
cd "$(dirname "$0")/../.."
out=$(python3 tools/gates/knob-spec-rows.py) || exit $?
[ -z "$out" ] && exit 0
echo "!! knob-spec-rows: the settings spec and the declarations disagree:" >&2
echo "$out" | sed 's/^/  /' >&2
exit 1
