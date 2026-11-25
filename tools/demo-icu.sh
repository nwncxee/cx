#!/usr/bin/env bash
# Prints the folder of ICU libraries the demo host's library path needs, or nothing when the system's own loader finds
# ICU. The NativeAOT runtime opens libicuuc.so by its bare name; the live client finds it in its own runtime, so only
# the demo host's path changes. Where nix exists the folder is ICU from the flake's own pinned nixpkgs.
set -u
cd "$(dirname "$0")/.." || exit 1
ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so' && exit 0
command -v nix >/dev/null || exit 0
out=$(nix build --inputs-from . nixpkgs#icu.out --no-link --print-out-paths 2>/dev/null) || exit 0
[ -e "$out/lib/libicuuc.so" ] && printf '%s\n' "$out/lib"
exit 0
