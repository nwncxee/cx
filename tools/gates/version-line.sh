#!/usr/bin/env bash
# One declaration holds the version line every stage composed in C# opens with: VkGlsl.Version. A string literal
# that IS that line is a second declaration of it, so it fails here. The first line of a stage's own text -- a
# .glsl file, or a multi-line GLSL literal -- is GLSL, not a declaration, and is not matched. A self-check's
# hand-written literal is its independent oracle and is exempt, which is why only *SelfCheck* files are skipped.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE '"#version[ \t]+460[ \t]+core(\\n)?"' \
  -- managed ':!managed/Graphics/Vulkan/VkGlsl.cs' ':!managed/**/*SelfCheck*.cs')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! version-line: the version line declared again (above); take it from VkGlsl.Version" >&2
exit 1
