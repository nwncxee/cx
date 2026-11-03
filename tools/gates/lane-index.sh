#!/usr/bin/env bash
# CX-403: `a[i * N + k]` is a record flattened into lanes -- every reader has to know the stride and which lane
# is which, and a wrong k stays inside the array, so nothing catches it. A record type with named fields and an
# array of it says the layout once, and MemoryMarshal.Cast reinterprets a byte buffer from outside. Counts the
# lane reads left per file outside Framework/; the ratchet lets a file's count fall only. An allocation length
# is a size, not a lane, and is not counted. `python3 tools/gates/lane-index.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/lane-index.py | bash tools/gates/ratchet.sh tools/gates/lane-index.baseline "$@"
