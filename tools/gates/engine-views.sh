#!/usr/bin/env bash
# reaches: managed/* tools/gates/* tools/engine/*
# CX-199: engine memory is read through the typed views, and `not-an-offset:` never marks a read of it.
# Ratchet: engine-views.baseline may only shrink. `python3 tools/gates/engine-views.py -v` lists each line.
cd "$(dirname "$0")/../.." || exit 2
python3 tools/gates/engine-views.py | bash tools/gates/ratchet.sh tools/gates/engine-views.baseline "$@"
