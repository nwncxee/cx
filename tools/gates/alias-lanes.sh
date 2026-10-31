#!/usr/bin/env bash
# An alias lane names memory two render targets SHARE, so it is a member of AliasLanes and never a literal at
# the call site: equal strings alias, and a typo buys a second allocation in silence instead of failing. The
# goldens are exempt -- they invent lanes to test the sharing itself. No baseline: the tree is clean, so any
# hit is new. `python3 tools/gates/alias-lanes.py -v` lists each site.
cd "$(dirname "$0")/../.."
out=$(python3 tools/gates/alias-lanes.py -v) || exit $?
[ -z "$out" ] && exit 0
echo "!! alias-lanes: an alias argument is a string literal rather than an AliasLanes member:" >&2
echo "$out" | sed 's/^/  /' >&2
exit 1
