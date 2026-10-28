#!/usr/bin/env bash
# CX-316: a non-opaque uniform belongs in a std140 block on a UboSlots point, not in a loose glUniform call --
# Vulkan refuses the GLSL, and the call traffic is per term per program switch. Alloy writes the Vulkan block
# itself, so every site left is CX's own windows and the NUI model view on the engine's renderer. Counts the
# remaining loose sites per file under managed/; the ratchet lets a file's count fall only, and a sampler unit
# is opaque and counts too. `python3 tools/gates/uniform-blocks.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/uniform-blocks.py | bash tools/gates/ratchet.sh tools/gates/uniform-blocks.baseline "$@"
