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
# and what build.sh fetches into the cached build/deps, main's job fetches too, so a restored copy is the whole of it
fetches=$(grep -oE 'deps\.py[[:space:]]+[a-z-]+[[:space:]]+build/deps[^[:space:]]*' build.sh | sed -E 's/[[:space:]]+/|/g')
[ -n "$fetches" ] || { echo "!! ci cache: found no tools/deps.py fetch into build/deps in build.sh" >&2; rc=1; }
for d in $fetches; do
  grep -qF "${d//|/ }" .github/workflows/ffx-cache.yml || { echo "!! ci cache: ffx-cache.yml does not fetch ${d//|/ } as build.sh does" >&2; rc=1; }
done
# main's job saves from main alone, on push and on its keep-alive schedule
on=$(sed -n '/^on:/,/^[a-z]/p' .github/workflows/ffx-cache.yml)
if ! grep -qE '^  push:' <<< "$on" || ! grep -qE '^    branches: \[main\]$' <<< "$on"; then
  echo "!! ci cache: ffx-cache.yml does not run on a push to main alone" >&2; rc=1
fi
if ! grep -qE '^  schedule:' <<< "$on" || ! grep -qE "^    - cron: '[^']+'$" <<< "$on"; then
  echo "!! ci cache: ffx-cache.yml has no schedule to keep its cache from eviction" >&2; rc=1
fi
# the cached paths are everything FSR's build writes: the deps, the patched SDK and shaders, the library and its key
paths=$(sed -n '/^ *path: |$/,/^ *key:/p' "$act" | sed '1d;$d' | tr -d ' ')
[ "$paths" = "$(printf '%s\n' build/deps build/ffx build/libcxfsr.a build/libcxfsr.a.key)" ] \
  || { echo "!! ci cache: $act caches ${paths//$'\n'/ }, not build/deps build/ffx build/libcxfsr.a build/libcxfsr.a.key" >&2; rc=1; }
[ $rc = 0 ] && echo "ci cache approved examples: E5 passes"
exit $rc
