#!/usr/bin/env bash
# The device is the backend's, so Vulkan and VMA entry points live under managed/Graphics/ and VMA's virtual
# blocks keep books in a named set of files. The RULE is CX0005 (tools/analyzers/VulkanDevice.cs), which the
# compiler checks: it knows an entry point from an enum member and a virtual block from an allocator, which a
# grep cannot. This gate does the one thing the compiler cannot -- refuse a name in that list that no file in
# the tree answers to, so a deleted or renamed book-keeper leaves no standing permission behind it. The list is
# read out of VulkanDevice.cs, never restated here.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/confined.py vma-confined tools/analyzers/VulkanDevice.cs CX0005 "a VMA virtual block"
