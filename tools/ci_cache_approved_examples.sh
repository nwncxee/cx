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
[ $rc = 0 ] && echo "ci cache approved examples: E5 passes"
exit $rc
