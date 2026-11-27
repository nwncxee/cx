#!/usr/bin/env bash
# reaches: managed/* tools/gates/*
# The air's looks are cx_air.glsl's: an air main that stops including it or calling the look it owes fails.
# The rule, its list of mains and its reasons are tools/gates/air-mains.py's.
cd "$(dirname "$0")/../.." || exit 2
python3 -I tools/gates/air-mains.py "$@"
