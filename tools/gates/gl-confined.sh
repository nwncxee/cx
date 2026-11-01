#!/usr/bin/env bash
# Alloy is Vulkan-only, so GL survives in a named set of files and nowhere else. The RULE is CX0003
# (tools/analyzers/EngineGl.cs), which the compiler checks: it knows a constant from an entry point and an Init
# guard from a call, which a grep cannot. This gate does the one thing the compiler cannot -- refuse a name in
# that list that no file in the tree answers to, so a deleted or renamed file leaves no standing permission
# behind it. The list is read out of EngineGl.cs, never restated here.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/confined.py gl-confined tools/analyzers/EngineGl.cs CX0003 "GL"
