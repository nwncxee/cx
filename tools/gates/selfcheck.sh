#!/usr/bin/env bash
# CX-200: a self-check assertion must be able to fail. Refuses, inside a *SelfCheck* file or a SelfCheck
# method, an Expect whose condition is a compile-time constant, one comparing a const to its own kind of
# literal (over == != < > <= >=, the const named bare or as Type.CONST), one whose two operands are the
# same text, and a hand-written SKIPPED log (a skip goes through Checker.Skip, which counts it). The
# const-against-literal shape's standing instances are a shrink-only baseline.
# `python3 tools/gates/selfcheck.py -v` prints each hit's expression.
cd "$(dirname "$0")/../.."
python3 tools/gates/selfcheck.py || exit 1
# the const-against-literal shape has standing instances; they may only shrink
python3 tools/gates/selfcheck.py --census | tools/gates/ratchet.sh tools/gates/selfcheck.baseline "$1"
