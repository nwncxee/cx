#!/usr/bin/env python3
"""The air's looks are cx_air.glsl's three functions, which the air goldens run on the device. An air main that
stops calling one composes its own look, compiles fine and answers to no golden, so this reads the shipped GLSL and
refuses a main that does not include the library and call each function MAINS names for it.

MAINS names every air main (a `cx_air*.glsl` file with a `main`) and the calls it owes; a new main goes on the list,
and one that is not on it fails. A main that owes a call may not define that name itself, as a macro or an overload.

usage: air-mains.py [root]   (default: the repository)
"""
import os
import re
import sys

SHADERS = os.path.join("managed", "Alloy", "Shaders")
HOME = "cx_air.glsl"
MAINS = {
    "cx_airdust_cs.glsl": ("CxAirDust", "CxAirLit", "CxAirSky"),
    "cx_airmote_cs.glsl": ("CxAirLit", "CxAirSky"),
    "cx_airwisp_cs.glsl": ("CxAirSky",),
    "cx_airdustup.glsl": (),
}
MAIN_NAME = re.compile(r"^cx_air\w*\.glsl$")
HAS_MAIN = re.compile(r"\bvoid\s+main\s*\(")


def code_of(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return "\n".join(line.split("//")[0] for line in text.splitlines())


def scan(root):
    shaders = os.path.join(root, SHADERS)
    if not os.path.isdir(shaders):
        return [f"!! air-mains: no {SHADERS} to read"]
    bad = []
    names = set(os.listdir(shaders))
    for name in sorted(names):
        if name == HOME or not MAIN_NAME.match(name) or name in MAINS:
            continue
        if HAS_MAIN.search(code_of(open(os.path.join(shaders, name), errors="replace").read())):
            bad.append(f"{SHADERS}/{name}: an air main MAINS does not name -- put it on the list in tools/gates/air-mains.py")
    for name, calls in sorted(MAINS.items()):
        rel = f"{SHADERS}/{name}"
        if name not in names:
            bad.append(f"{rel}: MAINS names it and it is not in the tree")
            continue
        code = code_of(open(os.path.join(shaders, name), errors="replace").read())
        if calls and not re.search(r'#\s*include\s+"' + re.escape(HOME) + '"', code):
            bad.append(f"{rel}: the air main does not include {HOME}")
        for fn in calls:
            if not re.search(r"\b" + fn + r"\s*\(", code):
                bad.append(f"{rel}: the air main does not call {fn} ({HOME})")
            if re.search(r"#\s*define\s+" + fn + r"\b", code) or re.search(r"\b" + fn + r"\s*\([^()]*\)\s*\{", code):
                bad.append(f"{rel}: the air main defines {fn} itself, where {HOME}'s must be the one it calls")
    return bad


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    bad = scan(root)
    for b in bad:
        print(b, file=sys.stderr)
    if bad:
        print(f"!! air-mains: {len(bad)} air main(s) that do not answer to cx_air (above)", file=sys.stderr)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
