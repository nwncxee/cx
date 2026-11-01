#!/usr/bin/env python3
"""Shapes that restate a shipped declaration instead of reading it, per file, for the ratchet.

    python3 tools/gates/golden-glsl.py       # "<count> <file>" lines
    python3 tools/gates/golden-glsl.py -v    # ...and every site, file:line

- A golden under managed/Alloy/Golden/ declaring a CX_* constant a shipped shader declares: the golden must
  splice the shipped line with GpuGolden.Declaration, or it measures a stage that does not ship.
- A golden indexing uIn/uOut with a literal stride: CaseBlocks declares CX_GOLD_IN/CX_GOLD_OUT for that.
- A shader comparing a light record's w against 128 by hand: CxLightIsStatic (cx_lightfrag) is the one test.
"""
import glob
import re
import sys

SHADERS = "managed/Alloy/Shaders"
DECL = re.compile(r"^\s*(?:const\s+\w+\s+|#define\s+)(CX_[A-Z0-9_]+)")
CONST_LINE = re.compile(r"\bconst\s+\w+\s+(.*)")
NAME = re.compile(r"\b(CX_[A-Z0-9_]+)\s*=")
STRIDE = re.compile(r"\bu(?:In|Out)\[[^\]]*\*\s*\d+u")
RECORD = re.compile(r"\b(?:w|int\(cs\.w\))\s*(?:>=|<=|<|>)\s*128\b")

shipped = set()
for f in glob.glob(f"{SHADERS}/*.glsl"):
    for line in open(f, encoding="utf-8", errors="replace"):
        m = DECL.match(line)
        if m:
            shipped.add(m.group(1))
            for n in NAME.findall(line):
                shipped.add(n)


def census(f, test):
    n = 0
    for ln, line in enumerate(open(f, encoding="utf-8", errors="replace"), 1):
        hits = test(line)
        n += hits
        if hits and "-v" in sys.argv:
            print(f"  {f}:{ln}: {line.strip()[:110]}", file=sys.stderr)
    if n:
        print(f"{n} {f}")


def golden(line):
    hits = len(STRIDE.findall(line))
    m = CONST_LINE.search(line)
    if m:
        hits += sum(1 for n in NAME.findall(m.group(1)) if n in shipped)
    return hits


def shader(line):
    code = line.split("//", 1)[0]
    return 0 if "return w >= 128;" in code else len(RECORD.findall(code))


for f in sorted(glob.glob("managed/Alloy/Golden/*.cs")):
    census(f, golden)
for f in sorted(glob.glob(f"{SHADERS}/*.glsl")):
    census(f, shader)
