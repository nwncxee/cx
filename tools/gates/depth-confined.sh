#!/usr/bin/env bash
# GL's depth (casters, shadow cascades, the GUI) survives in a named set of files. The RULE is CX0006
# (tools/analyzers/GlDepth.cs), which the compiler checks; this gate refuses a name in that list that no file in
# the tree answers to, so a deleted or renamed file leaves no standing permission behind it. The list is read out
# of GlDepth.cs, never restated here. An argument names another tree to check.
root=${1:-$(cd "$(dirname "$0")/../.." && pwd)}
cd "$(dirname "$0")/../.."
python3 -I - "$root" <<'PY' || exit 1
import re, subprocess, sys

root = sys.argv[1]
src = open(f"{root}/tools/analyzers/GlDepth.cs").read()
block = re.search(r"private static readonly string\[\] Allowed =\s*\{(.*?)\};", src, re.S)
if not block:
    sys.exit("!! depth-confined: CX0006's Allowed list did not parse -- did GlDepth.cs change shape?")
listed = re.findall(r'"(/managed/[^"]+)"', block.group(1))
if not listed:
    sys.exit("!! depth-confined: CX0006's Allowed list is empty -- that keeps GL depth nowhere, which is not the rule")
tree = set(subprocess.run(["git", "-C", root, "ls-files", "managed"], capture_output=True, text=True).stdout.split())
stale = [f for f in listed if f.lstrip("/") not in tree]
if stale:
    for f in stale:
        print(f"{f}: listed in CX0006's Allowed but not in the tree", file=sys.stderr)
    print("!! depth-confined: a GL-depth permission outlived its file (above) -- drop the line from "
          "tools/analyzers/GlDepth.cs's Allowed", file=sys.stderr)
    sys.exit(1)
PY
