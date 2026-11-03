#!/usr/bin/env bash
# CX-319: a GL object freed while the frames in flight may still read it is a use-after-free. Alloy's objects
# are Vulkan's, destroyed through the backend, which holds a freed one past its submit ring -- so what is left
# to guard is the GL that stays: CX's own text rig on the engine's renderer, the files CX0003 lists, which own
# their objects for the session and free none. The census is empty, so the shrink-only ratchet refuses every
# site. `python3 tools/gates/gpu-frees.py -v` lists any it finds.
cd "$(dirname "$0")/../.."
python3 tools/gates/gpu-frees.py | bash tools/gates/ratchet.sh tools/gates/gpu-frees.baseline "$@"
