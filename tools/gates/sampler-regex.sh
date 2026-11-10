#!/usr/bin/env bash
# One reader of GLSL sampler and image declarations (VkGlsl.Opaques), and a named set of files that REWRITE one.
# The RULE is CX0008 (tools/analyzers/SamplerRegex.cs), which the compiler checks: it can tell a regex pattern
# argument from any other string, which a grep cannot. This gate does the one thing the compiler cannot -- refuse
# a name in that list that no file in the tree answers to. The list is read out of SamplerRegex.cs, never
# restated here. An argument names another tree to check.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/confined.py sampler-regex tools/analyzers/SamplerRegex.cs CX0008 "a sampler regex" "$@"
