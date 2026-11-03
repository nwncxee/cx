#!/usr/bin/env python3
"""Lane-indexed record reads per file outside managed/Framework/ -- the CX-403 census, for the ratchet.

    python3 tools/gates/lane-index.py       # "<count> <file>" lines
    python3 tools/gates/lane-index.py -v    # ...and every site, file:line

`a[i * N + k]` is a record flattened into lanes: every reader has to know the stride and which lane is which,
and a wrong k stays inside the array, so nothing catches it. A record type with named fields (Vector3,
Vector4, Matrix4x4, Rgba32, a named struct) and an array of it says the same thing once, and
MemoryMarshal.Cast reinterprets a byte buffer that arrives from outside. An allocation length (`new T[n * 4
+ 1]`) is a size, not a lane, and is not counted.
"""
import re
import subprocess
import sys

LANE = re.compile(r"\[[^\[\]\n]*?\*\s*(?:\d+|[A-Za-z_]\w*)\s*\+[^\[\]\n]*?\]")
SIZE = re.compile(r"(?:\bnew\b|\bstackalloc\b)[\w.<>,?\s]*$")


def sites(line):
    """Each lane-indexed read in `line`, as its match; an allocation length is not one."""
    for m in LANE.finditer(line):
        if SIZE.search(line[:m.start()]):
            continue
        yield m


files = subprocess.run(["git", "ls-files", "--cached", "--others", "--exclude-standard", "managed"],
                       capture_output=True, text=True).stdout.split()
for f in sorted(files):
    if not f.endswith(".cs") or f.startswith("managed/Framework/"):
        continue
    n = 0
    for ln, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        for _ in sites(line):
            n += 1
            if "-v" in sys.argv:
                print(f"  {f}:{ln}: {line.strip()[:110]}", file=sys.stderr)
    if n:
        print(f"{n} {f}")
