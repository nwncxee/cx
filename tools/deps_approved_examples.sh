#!/usr/bin/env bash
# tools/deps_regression.sh keeps its cases and waits on nothing (CX-633, E9). Oracle: the regression's own verdict
# and the wall clock; the waits it checks are the deterministic half.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
# E9
# Given tools/deps_regression.sh
# When it runs
# Then it finishes in under 5 s with the same cases
start=$(date +%s%N)
bash tools/deps_regression.sh || { echo "!! deps E9: the regression failed" >&2; exit 1; }
ms=$(( ($(date +%s%N) - start) / 1000000 ))
[ "$ms" -lt 5000 ] || { echo "!! deps E9: the regression took $ms ms, not under 5 s" >&2; exit 1; }
echo "deps approved examples: E9 passes ($ms ms)"
