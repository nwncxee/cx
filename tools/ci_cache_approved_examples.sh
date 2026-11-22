#!/usr/bin/env bash
# release.yml keeps building every shipped byte from scratch while ci.yml caches FSR (CX-633, E5).
# Oracle: the workflow files themselves, their uses: lines.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
rc=0
# E5
# Given a v* tag
# When release.yml runs
# Then it uses no cache and fetches and builds FSR from scratch
uses=$(grep -E '^\s*(-\s*)?uses:' .github/workflows/release.yml) || { echo "!! ci cache: release.yml has no steps" >&2; exit 1; }
if grep -q 'actions/cache' <<< "$uses"; then echo "!! ci cache E5: release.yml restores a cache" >&2; rc=1; fi
if grep -nE '^\s*cache(-dependency-path)?:' .github/workflows/release.yml; then echo "!! ci cache E5: a setup action in release.yml caches" >&2; rc=1; fi
if grep -nE '^[^#]*\bbuild/(deps|ffx|libcxfsr)' .github/workflows/release.yml; then echo "!! ci cache E5: release.yml names FSR's build folders" >&2; rc=1; fi
# Regression: main saves the cache a pull request's first run restores, so ci.yml and ffx-cache.yml must take
# it through the one shared step, and ffx-cache.yml must run whenever an input of that step's key changes.
act=.github/actions/ffx-cache/action.yml
for w in ci ffx-cache; do
  grep -qE '^\s*(-\s*)?uses: \./\.github/actions/ffx-cache$' ".github/workflows/$w.yml" \
    || { echo "!! ci cache: $w.yml does not restore FSR through $act" >&2; rc=1; }
done
if grep -n 'actions/cache@' .github/workflows/*.yml; then echo "!! ci cache: a workflow caches outside $act" >&2; rc=1; fi
key=$(grep -oE "hashFiles\([^)]*\)" "$act" | grep -oE "'[^']+'" | tr -d "'" | sort)
want=$(printf '%s\n' "$key" "$act" .github/workflows/ffx-cache.yml | sort)
got=$(sed -n '/^  push:/,/^  [a-z_]*:$/p' .github/workflows/ffx-cache.yml | sed -n "s/^ *- '*\([^']*\)'*$/\1/p" | sort)
[ "$got" = "$want" ] || { echo "!! ci cache: ffx-cache.yml's push paths are not the cache key's inputs:" >&2; diff <(echo "$want") <(echo "$got") >&2; rc=1; }
[ $rc = 0 ] && echo "ci cache approved examples: E5 passes"
exit $rc
