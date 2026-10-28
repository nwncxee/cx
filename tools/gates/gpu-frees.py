#!/usr/bin/env python3
"""Immediate GL frees per file under managed/ -- the CX-319 census, for the ratchet.

    python3 tools/gates/gpu-frees.py       # "<count> <file>" lines
    python3 tools/gates/gpu-frees.py -v    # ...and every site, file:line

A GL object freed on a lifetime edge -- a resize, an area change, an eviction, a stand-down -- may still be
read by the frames in flight. Alloy's objects are Vulkan's, destroyed through the backend, which holds a freed
one past its submit ring, so this census is over the GL that stays: CX's own windows and the NUI model view on
the engine's renderer, the files CX0003 lists.

Those own their objects for the session and free them where they die, on a scratch target or a creation
failure. So the ratchet is shrink-only, not a target of zero.
"""
import re
import subprocess
import sys

SITE = re.compile(r"\bGl\.Delete(?:Textures|Buffers|Framebuffers|VertexArrays|Renderbuffers|Samplers)\(")

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
