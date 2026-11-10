#!/usr/bin/env python3
"""A back face's shading normal is the vertex normal negated, and that flip is ONE declaration: `CxFaceNormal`
in cx_lightfrag.glsl, which a GPU golden runs on the device. A second copy spelled inline compiles fine and
answers to nothing -- drop the `normalize` from one of them and the two sides of a leaf card disagree with no
error -- so this reads the shipped GLSL and refuses the inline shapes. ALLOWED names the declaration's own file
and the content stand-in, which the GUI twin builds without the light library and so cannot reach the function.
FLIP_CALLERS names the file whose shading frame must come through it; a new one goes on the list.

usage: face-normal.py [root]   (default: the repository)
"""
import os
import re
import sys

SHADERS = os.path.join("managed", "Alloy", "Shaders")
HOME = "cx_lightfrag.glsl"
ALLOWED = (HOME, "cx_content_f.glsl")
FLIP_CALLERS = ("cx_ownlit_synth.glsl",)
FN = "CxFaceNormal("
# `front ? n : -n` and its negated spelling, plus the sign a caller multiplies by
SHAPES = (
    re.compile(r"\bgl_FrontFacing\s*\?\s*([A-Za-z_][\w.]*)\s*:\s*-\s*\1\b"),
    re.compile(r"!\s*gl_FrontFacing\s*\?\s*-\s*([A-Za-z_][\w.]*)\s*:\s*\1\b"),
    re.compile(r"\bgl_FrontFacing\s*\?\s*-?1\.0\s*:\s*-?1\.0"),
)


def code_of(line):
    return line.split("//")[0]


def scan(root):
    shaders = os.path.join(root, SHADERS)
    bad = []
    if not os.path.isdir(shaders):
        return [f"!! face-normal: no {SHADERS} to read"]
    for name in sorted(os.listdir(shaders)):
        if not name.endswith(".glsl"):
            continue
        rel = os.path.join(SHADERS, name)
        text = open(os.path.join(shaders, name), errors="replace").read()
        if name in FLIP_CALLERS and FN not in text:
            bad.append(f"{rel}: the lit main's shading normal does not come through {FN[:-1]} ({HOME})")
        if name in ALLOWED:
            continue
        for i, line in enumerate(text.splitlines(), 1):
            if any(s.search(code_of(line)) for s in SHAPES):
                bad.append(f"{rel}:{i}: a back face's normal flipped inline -- call {FN[:-1]} ({HOME})")
    return bad


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    bad = scan(root)
    for b in bad:
        print(b, file=sys.stderr)
    if bad:
        print(f"!! face-normal: {len(bad)} site(s) that do not answer to the one flip (above)", file=sys.stderr)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
