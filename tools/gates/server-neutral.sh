#!/usr/bin/env bash
# reaches: managed/* tools/gates/*
# CX is engine-generic: its code names no one server or its content (a measurement says "a persistent-world
# area", a contract says "the server end"). Product.cs is exempt: it holds where the project is hosted.
cd "$(dirname "$0")/../.."
hits=$(git -c color.grep=never grep --untracked -nIiE '\bsow\b|sow-|\bsow_|westgate|arelith' -- managed ':!managed/Framework/Product.cs')
[ -z "$hits" ] && exit 0
echo "$hits" >&2
echo "!! server-neutral: managed/ names a specific server or its content (above); describe it generically" >&2
exit 1
