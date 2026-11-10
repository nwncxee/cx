#!/usr/bin/env bash
# A compute pipeline is VkBackend.CreateCompute, from GLSL or from SPIR-V: it builds the stage, the pipeline and
# frees the module. A compute stage built by hand anywhere else -- a ShaderDesc or the SPIR-V CreateShader
# overload -- is a copy of it, and a copy is what leaks the module.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE '(ShaderDesc|CreateShader)\(\s*ShaderStage\.Compute' -- managed ':!managed/Graphics/Vulkan/VkBackend.Pipeline.cs')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! compute-pipes: a compute stage built by hand (above); call VkBackend.CreateCompute" >&2
exit 1
