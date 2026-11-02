#!/usr/bin/env bash
# The pipeline tally tells grass, particles, the probe and CX's windows apart by label prefix. A label spelled by hand
# can drift from the prefix and leave the tally counting nothing in silence, so each comes from managed/Rhi/PipeFeatures.cs.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIE '"(vk grass |vk particles |vk probe |vk CX window |CX window )' -- 'managed/*.cs' ':!managed/Rhi/PipeFeatures.cs')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! pipe-features: a label spells a pipeline feature prefix by hand (above); take it from PipeFeatures" >&2
exit 1
