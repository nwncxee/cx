#!/usr/bin/env bash
# A golden splices the shipped constant (GpuGolden.Declaration) and strides its cases by CX_GOLD_IN/OUT, and a shader
# asks CxLightIsStatic rather than compare a light record with 128. Counts the sites left per file; the ratchet lets a
# file's count fall only. `python3 tools/gates/golden-glsl.py -v` lists each site.
cd "$(dirname "$0")/../.."
python3 tools/gates/golden-glsl.py | bash tools/gates/ratchet.sh tools/gates/golden-glsl.baseline "$@"
