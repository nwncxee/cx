#!/usr/bin/env bash
# The back-face normal flip is one declaration: a shader that spells it out inline, or a lit main that stops
# calling it, fails. The rule, its allowances and its reasons are tools/gates/face-normal.py's.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/face-normal.py "$@"
