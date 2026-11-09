#!/usr/bin/env bash
# CX-503: a knob's declared effect matches how its readers read it -- Restart readers take Boot, Live readers
# take V. No baseline on the rules: the tree is clean, so any hit is new. A bare read of a field several
# classes declare names no one declaration, and such a read can hide a Restart knob read through V, so the
# sites left are counted per file against a shrink-only baseline. `python3 tools/gates/knob-effect.py -v`
# lists each of them.
cd "$(dirname "$0")/../.."
out=$(python3 tools/gates/knob-effect.py) || exit $?
if [ -n "$out" ]; then
  echo "!! knob-effect: a knob's declared effect does not match its readers:" >&2
  echo "$out" | sed 's/^/  /' >&2
  exit 1
fi
python3 tools/gates/knob-effect.py --census | bash tools/gates/ratchet.sh tools/gates/knob-effect.baseline "$@"
