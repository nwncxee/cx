#!/usr/bin/env bash
# A `///` block with two <summary> elements is a doc left behind by a declaration that moved or went away, and
# the compiler does not warn. Counts the blocks per file under managed/; the ratchet lets a file's count fall only.
# `python3 tools/gates/doc-stack.py -v` lists each.
cd "$(dirname "$0")/../.."
python3 tools/gates/doc-stack.py | bash tools/gates/ratchet.sh tools/gates/doc-stack.baseline "$@"
