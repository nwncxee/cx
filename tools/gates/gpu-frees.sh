#!/usr/bin/env bash
# CX-319: a GL object freed on a lifetime edge may still be read by the frames in flight, so it belongs in
# GpuGraveyard.Retire rather than a glDelete* of its own. Alloy's own objects are Vulkan's now (VkBackend holds
# a freed one past the submit ring), so what is left to guard is the GL that stays -- CX's UI windows and the
# engine's model views and texture path, the files CX0003 lists. Counts the remaining immediate frees per file;
# the ratchet lets a file's count fall only. A creation failure and a scratch object inside one bracket are
# legitimate, so a file need not reach zero. `python3 tools/gates/gpu-frees.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/gpu-frees.py | bash tools/gates/ratchet.sh tools/gates/gpu-frees.baseline "$@"
