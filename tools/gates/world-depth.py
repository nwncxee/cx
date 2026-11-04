#!/usr/bin/env python3
"""The world's depth is reversed (near 1, far 0) and its projection folded to match, so a world depth read IS the NDC
z. GL's decode of it (`d * 2.0 - 1.0`) or GL's far plane (`d >= 1.0`) is a missed site: the frame goes wrong without
a compile error, so this reads the text -- every Alloy shader, every GLSL string in the C#, and every helper a world
depth reaches through a parameter. Shadow and cover maps keep GL's depth and are not named. A shader also turns world
depth into a distance only through CxDepthViewZ (cx_depthlin.glsl), so the decode its golden holds is every reader's, and
C# takes the same decode's terms from the planes only through WorldDepth.Decode.

usage: world-depth.py [root]   (default: the repository)
"""
import os
import re
import sys

WORLD_DEPTH = r"(?:uDepth|uDepthMs|uSceneDepth|uGbDepth|uFbDepth|uTrigDepth|uOitDepth)"
READ = r"\b(?:texture|textureLod|texelFetch)\s*\(\s*"
GL_DECODE = r"\s*\*\s*2\.0\s*-\s*1\.0"
GL_FAR = r"\s*(?:>=\s*1\.0|<\s*1\.0)"
LINEAR = re.compile(r"\[3\]\s*(?:\.z|\[2\])\s*/\s*\(|\b(\w+)\.y\s*/\s*\([^()]*\+\s*\1\.x\s*\)")
LINEAR_HOME = "cx_depthlin.glsl"
CS_DECODE = re.compile(r"\bnear / \(far - near\)")
CS_DECODE_HOME = os.path.join("Core", "WorldDepth.cs")
FN_HEAD = re.compile(r"^\s*(?:\w+\s+)+?(\w+)\s*\(([^()]*)\)\s*\{?\s*$")
CALL = re.compile(r"\b(\w+)\s*\(")
KEYWORDS = {"if", "for", "while", "switch", "return", "texture", "textureLod", "texelFetch"}


def code_of(line):
    return line.split("//")[0]


def strings_of(line):
    """The text of a C# line's string literals, joined: the GLSL a C# file carries."""
    return " ".join(re.findall(r'"((?:[^"\\]|\\.)*)"', code_of(line)))


def functions(lines):
    """Each function a GLSL text defines: name -> (first body line, last line, [param names])."""
    out = {}
    i = 0
    while i < len(lines):
        m = FN_HEAD.match(code_of(lines[i]))
        if not m or m.group(1) in KEYWORDS:
            i += 1
            continue
        params = [p.split()[-1].split("[")[0] for p in m.group(2).split(",") if p.strip() and p.strip() != "void"]
        depth, j, opened = 0, i, False
        while j < len(lines):
            c = code_of(lines[j])
            depth += c.count("{") - c.count("}")
            opened |= "{" in c
            if opened and depth <= 0:
                break
            j += 1
        if opened:
            out[m.group(1)] = (i, j, params)
        i = j + 1 if opened else i + 1
    return out


def args_at(code, start):
    """The top-level arguments of the call whose '(' is at `start`."""
    depth, cur, out = 0, "", []
    for ch in code[start:]:
        if ch == "(":
            depth += 1
            if depth == 1:
                continue
        elif ch == ")":
            depth -= 1
            if depth == 0:
                out.append(cur.strip())
                return out
        elif ch == "," and depth == 1:
            out.append(cur.strip())
            cur = ""
            continue
        cur += ch
    return out


def misses(path, lines, world):
    """Lines of `lines` that read world depth the GL way; `world` maps a line to the extra world names in scope."""
    out, held = [], set()
    for i, code in enumerate(lines, 1):
        names = WORLD_DEPTH + "".join("|" + re.escape(n) for n in sorted(world(i - 1)))
        read = re.compile(READ + r"\b(?:" + names + r")\b")
        m = re.search(r"\b(?:float\s+)?(\w+)\s*=\s*" + read.pattern, code)
        if m:
            held.add(m.group(1))
        vals = "|".join(sorted(held | world(i - 1)))
        shapes = ([r"\)\s*\.\s*[rx]"] if read.search(code) else []) + ([r"\b(?:" + vals + r")"] if vals else [])
        for gl, how in ((GL_DECODE, "decoded as GL's (* 2.0 - 1.0)"), (GL_FAR, "tested against GL's far plane (1.0)")):
            if any(re.search(shape + gl, code) for shape in shapes):
                out.append(f"{path}:{i}: a world depth {how}")
                break
    return out


def world_params(texts):
    """Every helper parameter a world depth reaches, followed through calls to a fixed point: name -> {param}."""
    fns = {}
    for lines in texts.values():
        fns.update(functions(lines))
    reach = {n: set() for n in fns}
    spans = {path: functions(lines) for path, lines in texts.items()}
    changed = True
    while changed:
        changed = False
        for path, lines in texts.items():
            here = spans[path]
            held = set()
            for i, raw in enumerate(lines):
                code = code_of(raw)
                m = re.search(r"\b(?:float\s+)?(\w+)\s*=\s*" + READ + r"\b" + WORLD_DEPTH, code)
                if m:
                    held.add(m.group(1))
                scope = set(held)
                for name, (a, b, _) in here.items():
                    if a <= i <= b:
                        scope |= reach.get(name, set())
                world = re.compile(r"^(?:" + WORLD_DEPTH + "".join("|" + re.escape(n) for n in scope) + r")$"
                                   r"|" + READ + r"\b" + WORLD_DEPTH)
                for m in CALL.finditer(code):
                    callee = m.group(1)
                    if callee not in fns:
                        continue
                    params = fns[callee][2]
                    for k, arg in enumerate(args_at(code, m.end() - 1)):
                        if k < len(params) and world.search(arg) and params[k] not in reach[callee]:
                            reach[callee].add(params[k])
                            changed = True
    return reach


def scan(root):
    shaders = os.path.join(root, "managed", "Alloy", "Shaders")
    texts, raw = {}, {}
    if os.path.isdir(shaders):
        for f in sorted(os.listdir(shaders)):
            if f.endswith(".glsl"):
                texts[os.path.join("managed", "Alloy", "Shaders", f)] = [code_of(l) for l in
                                                                         open(os.path.join(shaders, f)).read().splitlines()]
    for d, dirs, files in os.walk(os.path.join(root, "managed")):
        dirs[:] = [x for x in dirs if x not in ("bin", "obj")]
        for f in sorted(files):
            if f.endswith(".cs"):
                rel = os.path.relpath(os.path.join(d, f), root)
                raw[rel] = open(os.path.join(d, f), errors="replace").read().splitlines()
                texts[rel] = [strings_of(l) for l in raw[rel]]
    reach = world_params(texts)
    bad = []
    for path, lines in raw.items():
        if path.endswith(CS_DECODE_HOME) or os.path.join("Alloy", "Golden") in path:
            continue
        for i, line in enumerate(lines, 1):
            if CS_DECODE.search(code_of(line)):
                bad.append(f"{path}:{i}: world depth's decode worked out from the planes by hand -- call WorldDepth.Decode")
    for path, lines in texts.items():
        fns = functions(lines)
        spans = [(a, b, reach.get(n, set())) for n, (a, b, _) in fns.items()]

        def world(i, spans=spans):
            for a, b, ps in spans:
                if a <= i <= b:
                    return ps
            return set()
        bad += misses(path, lines, world)
        if path.endswith(".glsl") and not path.endswith(LINEAR_HOME):
            for i, code in enumerate(lines, 1):
                if LINEAR.search(code):
                    bad.append(f"{path}:{i}: world depth turned into a distance by hand -- call CxDepthViewZ ({LINEAR_HOME})")
    return bad


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..")
    bad = scan(root)
    for b in bad:
        print(b, file=sys.stderr)
    if bad:
        print(f"!! world-depth: {len(bad)} read(s) of the reversed world depth the GL way (above)", file=sys.stderr)
    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(main())
