#!/usr/bin/env bash
# CX-284: `new Span<T>(p, n)` checks nothing -- the caller supplies the length, and `.Slice(i * size, size)` on
# the result is the pointer-plus-offset ADR 0031 refuses, one step removed. Two arguments is always the pointer
# constructor. A view builds its span from its count row, a NativeBuffer<T> from its allocation, and only
# managed/Framework/ builds one from a bare pointer. Counts the sites left per file outside Framework/, a FLOOR
# (a target-typed `new(p, n)` needs the compiler to see); the ratchet lets a file's count fall only.
# `python3 tools/gates/span-ptr.py -v` lists each.
cd "$(dirname "$0")/../.."
python3 tools/gates/span-ptr.py | bash tools/gates/ratchet.sh tools/gates/span-ptr.baseline "$@"
