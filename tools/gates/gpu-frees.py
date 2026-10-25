#!/usr/bin/env python3
"""Immediate GPU frees per file under managed/ -- the CX-319 census, for the ratchet.

    python3 tools/gates/gpu-frees.py       # "<count> <file>" lines
    python3 tools/gates/gpu-frees.py -v    # ...and every site, file:line

A GPU object freed on a lifetime edge -- a resize, an area change, an eviction, a stand-down -- may still be
read by the frames in flight. GL forgives it (the name goes, the object lives until no command names it);
Vulkan does not, and vkDestroyImage on an image a submitted command buffer touches is undefined. The
replacement is GpuGraveyard.Retire, whose drain deletes three frames later.

What is left in the count is the legitimate remainder: a creation failure (the object was never submitted
with), a scratch object made and dropped inside one bracket, and the lightmap page pool, which is freed
precisely to make room for its replacement. So the ratchet is shrink-only, not a target of zero. The
graveyard's own drain is the one exempt file.
"""
import re
import subprocess
import sys

SITE = re.compile(r"\bGl\.Delete(?:Textures|Buffers|Framebuffers|VertexArrays|Renderbuffers)\(")
EXEMPT = ("managed/Graphics/GpuGraveyard.cs",)   # the drain itself: this is where the deletes are supposed to be

files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
for f in sorted(files):
    if not f.endswith(".cs") or f in EXEMPT:
        continue
    n = 0
    for ln, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for _ in SITE.finditer(line):
            n += 1
            if "-v" in sys.argv:
                print(f"  {f}:{ln}: {line.strip()[:110]}", file=sys.stderr)
    if n:
        print(f"{n} {f}")
