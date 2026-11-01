#!/usr/bin/env bash
# GL's depth (casters, shadow cascades, the GUI) survives in a named set of files. The RULE is CX0006
# (tools/analyzers/DepthConvention.cs), which the compiler checks; this gate refuses a name in that list that no file in
# the tree answers to, so a deleted or renamed file leaves no standing permission behind it. The list is read out
# of GlDepth.cs, never restated here. An argument names another tree to check.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/confined.py depth-confined tools/analyzers/DepthConvention.cs CX0006 "GL depth" "$@"
