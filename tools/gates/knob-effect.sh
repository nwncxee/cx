#!/usr/bin/env bash
# CX-503: a knob's declared effect matches how its readers read it -- Restart readers take Boot, Live readers
# take V. No baseline: the tree is clean, so any hit is new. `python3 tools/gates/knob-effect.py -v` adds the
# field names two classes both declare, which the name match cannot tell apart.
cd "$(dirname "$0")/../.."
out=$(python3 tools/gates/knob-effect.py) || exit $?
[ -z "$out" ] && exit 0
echo "!! knob-effect: a knob's declared effect does not match its readers:" >&2
echo "$out" | sed 's/^/  /' >&2
exit 1
