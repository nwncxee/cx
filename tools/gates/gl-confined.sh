#!/usr/bin/env bash
# Alloy is Vulkan-only, so GL survives in a named set of files and nowhere else. The RULE is CX0003
# (tools/analyzers/EngineGl.cs), which the compiler checks: it knows a constant from an entry point and an Init
# guard from a call, which a grep cannot. This gate does the one thing the compiler cannot -- refuse a name in
# that list that no file in the tree answers to, so a deleted or renamed file leaves no standing permission
# behind it. The list is read out of EngineGl.cs, never restated here.
cd "$(dirname "$0")/../.."
python3 -I - <<'EOF' || exit 1
import re, subprocess, sys

src = open("tools/analyzers/EngineGl.cs").read()
block = re.search(r"private static readonly string\[\] Allowed =\s*\{(.*?)\};", src, re.S)
if not block:
    sys.exit("!! gl-confined: CX0003's Allowed list did not parse -- did EngineGl.cs change shape?")
listed = re.findall(r'"(/managed/[^"]+)"', block.group(1))
if not listed:
    sys.exit("!! gl-confined: CX0003's Allowed list is empty -- that permits GL nowhere, which is not the rule")
tree = set(subprocess.run(["git", "ls-files", "managed"], capture_output=True, text=True).stdout.split())
stale = [f for f in listed if f.lstrip("/") not in tree]
if stale:
    for f in stale:
        print(f"{f}: listed in CX0003's Allowed but not in the tree", file=sys.stderr)
    print("!! gl-confined: a GL permission outlived its file (above) -- drop the line from "
          "tools/analyzers/EngineGl.cs's Allowed", file=sys.stderr)
    sys.exit(1)
EOF
