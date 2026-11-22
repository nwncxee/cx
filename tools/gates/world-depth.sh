#!/usr/bin/env bash
# reaches: managed/* tools/gates/*
# The world's depth is reversed: a shader or a C# string that reads it the GL way, or turns it into a distance
# without CxDepthViewZ, fails. The rule and its reasons are tools/gates/world-depth.py's.
cd "$(dirname "$0")/../.." || exit 2
python3 -I tools/gates/world-depth.py "$@"
