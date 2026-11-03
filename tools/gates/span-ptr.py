#!/usr/bin/env python3
"""Spans built over a raw pointer per file outside managed/Framework/ -- the CX-284 census, for the ratchet.

    python3 tools/gates/span-ptr.py       # "<count> <file>" lines
    python3 tools/gates/span-ptr.py -v    # ...and every site, file:line

`new Span<T>(p, n)` checks nothing: the caller supplies the length, and `.Slice(i * size, size)` on the
result is `p + i * size` one step removed, which ADR 0031 decision 3 refuses outside Framework/. Two
arguments is always the pointer constructor -- the array one takes one argument or three -- so the shape
counts exactly. A view builds its span from its count row and a NativeBuffer<T> from its allocation.
"""
import re
import subprocess
import sys

OPEN = re.compile(r"\bnew\s+(?:ReadOnly)?Span\s*<")


def args_of(text, at):
    """The top-level argument count of the call whose `new ...Span<` starts at `at`, or None if unbalanced."""
    i, depth = at, 0
    while i < len(text):                      # step over the type argument list to the opening paren
        if text[i] == "<":
            depth += 1
        elif text[i] == ">":
            depth -= 1
            if depth == 0:
                break
        i += 1
    i = text.find("(", i)
    if i < 0:
        return None
    depth, commas, i = 0, 0, i
    while i < len(text):
        c = text[i]
        if c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                return commas + 1
        elif c == "," and depth == 1:
            commas += 1
        elif c == '"':                         # a string argument may hold parens or commas
            i += 1
            while i < len(text) and text[i] != '"':
                i += 2 if text[i] == "\\" else 1
        i += 1
    return None


files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
for f in sorted(files):
    if not f.endswith(".cs") or f.startswith("managed/Framework/"):
        continue
    text = open(f, encoding="utf-8", errors="replace").read()
    n = 0
    for m in OPEN.finditer(text):
        if args_of(text, m.start()) != 2:
            continue
        n += 1
        if "-v" in sys.argv:
            ln = text.count("\n", 0, m.start()) + 1
            print(f"  {f}:{ln}: {text.splitlines()[ln - 1].strip()[:110]}", file=sys.stderr)
    if n:
        print(f"{n} {f}")
