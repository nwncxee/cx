#!/usr/bin/env bash
# CX-316: a non-opaque uniform belongs in a std140 block on a UboSlots point, not in a loose glUniform call --
# Vulkan refuses the GLSL, and the call traffic is per term per program switch. Counts the remaining loose
# sites per file under managed/; the ratchet lets a file's count fall only. A sampler unit is opaque and
# counts here too, so a file need not reach zero -- it may only shrink.
# `python3 tools/gates/uniform-blocks.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/uniform-blocks.py | bash tools/gates/ratchet.sh tools/gates/uniform-blocks.baseline "$@"
