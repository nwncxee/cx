#!/usr/bin/env python3
"""Loose glUniform* call sites per file under managed/ -- the CX-316 census, for the ratchet.

    python3 tools/gates/uniform-blocks.py       # "<count> <file>" lines
    python3 tools/gates/uniform-blocks.py -v    # ...and every site, file:line

A non-opaque uniform outside a block is GLSL that Vulkan refuses, and one glUniform call per term per
program switch is CPU the frame does not have. The replacement is a std140 block on a UboSlots point, filled
through UniformBlock<T>. Sampler units count too: they are opaque, so they are the part of a file's tally
that may legitimately never reach zero -- the ratchet is shrink-only, not a target of zero.
"""
import re
import subprocess
import sys

SITE = re.compile(r"\bGl\.Uniform[0-9A-Za-z]*\(")

files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
for f in sorted(files):
    if not f.endswith(".cs"):
        continue
    n = 0
    for ln, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for _ in SITE.finditer(line):
            n += 1
            if "-v" in sys.argv:
                print(f"  {f}:{ln}: {line.strip()[:110]}", file=sys.stderr)
    if n:
        print(f"{n} {f}")
