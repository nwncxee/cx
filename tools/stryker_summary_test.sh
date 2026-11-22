#!/usr/bin/env bash
# tools/stryker_summary.py reads a Stryker report: a mutant no test covers is UNTESTED and fails the run, as a
# survivor does. Oracle: the hand-written report below. Exit 0 PASS, 1 FAIL.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-stryker-summary.XXXXXX"); trap 'rm -rf "$tmp"' EXIT
rc=0; fail() { echo "!! $1"; rc=1; }
m() { printf '{"status":"%s","mutatorName":"%s","replacement":"x","location":{"start":{"line":%s}}}' "$1" "$2" "$3"; }
printf '{"files":{"managed/Ui/A.cs":{"mutants":[%s,%s]}}}' "$(m Killed Equality 3)" "$(m NoCoverage Boolean 7)" > "$tmp/blind.json"
printf '{"files":{"managed/Ui/A.cs":{"mutants":[%s,%s]}}}' "$(m Killed Equality 3)" "$(m Ignored Boolean 7)" > "$tmp/clean.json"
out=$(python3 -I tools/stryker_summary.py "$tmp/blind.json"); e=$?
[ "$e" = 1 ] || fail "a NoCoverage mutant exited $e, not 1"
grep -qx 'A.cs: 1 killed, 0 survived, 0 timed out, 1 untested' <<<"$out" || fail "no untested count: $out"
grep -qx 'UNTESTED A.cs:7 Boolean x' <<<"$out" || fail "the NoCoverage mutant is not named UNTESTED: $out"
python3 -I tools/stryker_summary.py "$tmp/clean.json" >/dev/null || fail "an all-killed report did not exit 0"
[ $rc = 0 ] && echo ">> stryker summary: PASS" || echo "!! stryker summary: FAILED (above)" >&2
exit $rc
