#!/usr/bin/env bash
# Regression check, not an approved test: tools/codehash.py lexes every .cs file on every build, so a shape that
# makes it quadratic or recursive stops the build. Each case below once ran for minutes, or crashed, on input this
# size. Oracle: each run must finish inside a bound far past the linear time (seconds, not minutes) and print a
# fingerprint. Exit 0 PASS, 1 FAIL.
cd "$(dirname "$0")/.." || exit 1
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
rc=0
case_of() {
  local name=$1 d=$tmp/$1
  mkdir -p "$d/managed"
  python3 -c "$2" > "$d/managed/X.cs"
  (cd "$d" && git init -q && git add -A) || exit 1
  local out
  out=$(timeout 30 python3 tools/codehash.py "$d" 2>&1) && [ ${#out} = 64 ] \
    || { echo "!! $name: no fingerprint inside 30s -- $(echo "$out" | tail -1)"; rc=1; }
}
case_of dollars "print('\$' * 100000)"
case_of blanks "print('x' + ' ' * 400000, end='')"
case_of nesting "print('\$\"{' * 3000 + '}\"' * 3000)"
case_of long "print('int a = 1;\n' * 400000)"
[ $rc = 0 ] && echo ">> codehash runaway regression: 4 cases finish" || echo "!! codehash runaway regression: FAILED (above)" >&2
exit $rc
