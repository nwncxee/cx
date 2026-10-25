#!/usr/bin/env bash
# CX-319: a GPU object freed on a lifetime edge may still be read by the frames in flight, so it belongs in
# GpuGraveyard.Retire rather than a glDelete* of its own -- GL forgives the immediate delete, Vulkan does not.
# Counts the remaining immediate frees per file under managed/; the ratchet lets a file's count fall only.
# A creation failure, a scratch object inside one bracket and the lightmap page pool are legitimate, so a file
# need not reach zero -- it may only shrink. `python3 tools/gates/gpu-frees.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/gpu-frees.py | bash tools/gates/ratchet.sh tools/gates/gpu-frees.baseline "$@"
