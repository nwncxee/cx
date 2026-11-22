#!/usr/bin/env bash
# Regression check, not an approved test: tools/selfcheck/mutants.py --check <name> judges one self-check from the
# run's cx.log, so a branch whose other red tests fail can still mutate its own. Planted runs; no compile.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
T=$(mktemp -d) && trap 'rm -rf "$T"' EXIT
fail=0

# <name> <check or ""> <run exit> <cx.log text> <wanted exit>
judge() {
  printf '%b' "$4" > "$T/cx.log"
  got=$(python3 -I - "$ROOT/tools/selfcheck" "$2" "$3" "$T/cx.log" <<'EOF'
import sys
sys.path.insert(0, sys.argv[1])
import mutants
mutants.CHECK = sys.argv[2] or None
print(mutants.judged(int(sys.argv[3]), f">> FAIL: Other.SelfCheck FAIL: E1\n>> {sys.argv[4]}\n")[0])
EOF
)
  [ "$got" = "$5" ] || { echo "!! mutants --check $1: judged $got, not $5" >&2; fail=1; }
}

judge "passes beside another check's FAIL" Mine.SelfCheck 1 '[cx] Other.SelfCheck FAIL: E1\n[cx] Mine.SelfCheck: passed\n' 0
judge "fails on its own FAIL line" Mine.SelfCheck 1 '[cx] Mine.SelfCheck FAIL: E2 broke\n[cx] Mine.SelfCheck: FAILED\n' 1
judge "fails when the check never ran" Mine.SelfCheck 0 '[cx] Other.SelfCheck: passed\n' 1
judge "keeps the run's exit with no --check" "" 1 '[cx] Mine.SelfCheck: passed\n' 1
judge "keeps a timeout" Mine.SelfCheck 124 '[cx] Mine.SelfCheck: passed\n' 124

[ "$fail" = 0 ] && echo "mutants --check: regression check passed"
exit "$fail"
