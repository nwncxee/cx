#!/usr/bin/env bash
# The device is the backend's, so Vulkan and VMA entry points live under managed/Graphics/ and VMA's virtual
# blocks keep books in a named set of files. The RULE is CX0005 (tools/analyzers/VulkanDevice.cs), which the
# compiler checks: it knows an entry point from an enum member and a virtual block from an allocator, which a
# grep cannot. This gate does the one thing the compiler cannot -- refuse a name in that list that no file in
# the tree answers to, so a deleted or renamed book-keeper leaves no standing permission behind it. The list is
# read out of VulkanDevice.cs, never restated here.
cd "$(dirname "$0")/../.."
python3 -I - <<'EOF' || exit 1
import re, subprocess, sys

src = open("tools/analyzers/VulkanDevice.cs").read()
block = re.search(r"private static readonly string\[\] Allowed =\s*\{(.*?)\};", src, re.S)
if not block:
    sys.exit("!! vma-confined: CX0005's Allowed list did not parse -- did VulkanDevice.cs change shape?")
listed = re.findall(r'"(/managed/[^"]+)"', block.group(1))
if not listed:
    sys.exit("!! vma-confined: CX0005's Allowed list is empty -- that permits a virtual block nowhere, which is "
             "not the rule")
tree = set(subprocess.run(["git", "ls-files", "managed"], capture_output=True, text=True).stdout.split())
stale = [f for f in listed if f.lstrip("/") not in tree]
if stale:
    for f in stale:
        print(f"{f}: listed in CX0005's Allowed but not in the tree", file=sys.stderr)
    print("!! vma-confined: a VMA virtual-block permission outlived its file (above) -- drop the line from "
          "tools/analyzers/VulkanDevice.cs's Allowed", file=sys.stderr)
    sys.exit(1)
EOF
