#!/usr/bin/env bash
# reaches: managed/* native/* tools/gates/*
# CX-194: a doc comment states the contract in at most 4 lines and cites no ticket (git and the tracker hold
# history). No baseline: the tree is clean, so any hit fails.
cd "$(dirname "$0")/../.." || exit 2
out=$(python3 tools/gates/comments.py -v)
[ -z "$out" ] && exit 0
echo "$out" >&2
echo "!! comments: doc comment over 4 text lines or citing a ticket (see above)" >&2
exit 1
