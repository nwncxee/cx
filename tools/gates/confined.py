#!/usr/bin/env python3
"""A compiler rule's Allowed list, refused when it names a file the tree no longer holds: a deleted or renamed file
must leave no standing permission behind it. The list is read out of the analyzer, never restated.

usage: confined.py <gate> <analyzer.cs> <rule> <what the permission allows> [root]
"""
import re
import subprocess
import sys

gate, analyzer, rule, what = sys.argv[1:5]
root = sys.argv[5] if len(sys.argv) > 5 else "."
src = open(f"{root}/{analyzer}").read()
block = re.search(r"private static readonly string\[\] Allowed =\s*\{(.*?)\};", src, re.S)
if not block:
    sys.exit(f"!! {gate}: {rule}'s Allowed list did not parse -- did {analyzer} change shape?")
listed = re.findall(r'"(/managed/[^"]+)"', block.group(1))
if not listed:
    sys.exit(f"!! {gate}: {rule}'s Allowed list is empty -- that allows {what} nowhere, which is not the rule")
tree = set(subprocess.run(["git", "-C", root, "ls-files", "managed"], capture_output=True, text=True).stdout.split())
stale = [f for f in listed if f.lstrip("/") not in tree]
for f in stale:
    print(f"{f}: listed in {rule}'s Allowed but not in the tree", file=sys.stderr)
if stale:
    sys.exit(f"!! {gate}: a permission for {what} outlived its file (above) -- drop the line from {analyzer}'s Allowed")
