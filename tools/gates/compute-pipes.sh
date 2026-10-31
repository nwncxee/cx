#!/usr/bin/env bash
# A compute pipeline from GLSL is VkBackend.CreateCompute: it builds the stage, the pipeline and frees the module.
# A hand-rolled ShaderDesc(ShaderStage.Compute ...) anywhere else is a copy of it.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE 'ShaderDesc\(\s*ShaderStage\.Compute' -- managed ':!managed/Graphics/Vulkan/VkBackend.Pipeline.cs')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! compute-pipes: a compute stage built by hand (above); call VkBackend.CreateCompute" >&2
exit 1
