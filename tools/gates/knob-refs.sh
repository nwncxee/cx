#!/usr/bin/env bash
# reaches: managed/* tools/gates/*
# CX-198: a `<group>.<knob>` key in a string literal under managed/ must name a declared Knob<T>. Removing a
# knob removes the messages and help text that named it, in the same change. No baseline: the tree is clean,
# so any hit is new. `python3 tools/gates/knob-refs.py -v` lists each key.
cd "$(dirname "$0")/../.."
out=$(python3 tools/gates/knob-refs.py -v) || exit $?
[ -z "$out" ] && exit 0
echo "!! knob-refs: a string literal names a knob that is not declared:" >&2
echo "$out" | sed 's/^/  /' >&2
exit 1
