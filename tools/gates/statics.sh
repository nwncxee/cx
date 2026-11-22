#!/usr/bin/env bash
# reaches: managed/* tools/gates/*
# CX-193: a pass's mutable state belongs to one lifetime owner, not to loose `static` fields. Counts the
# non-readonly static fields (and static auto-properties with a setter) per file under managed/Alloy and
# managed/Services; the ratchet lets a file's count fall only. `python3 tools/gates/statics.py -v` lists each.
cd "$(dirname "$0")/../.." || exit 2
python3 tools/gates/statics.py | bash tools/gates/ratchet.sh tools/gates/statics.baseline "$@"
