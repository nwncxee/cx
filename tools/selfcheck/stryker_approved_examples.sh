#!/usr/bin/env bash
# Stryker.NET mutates the engine-free core (CX-392, E1 to E3). Heavy -- a Stryker run, which compiles and tests every
# mutant of the changed lines -- so build.sh does not run it; run it by hand inside nix develop. It works in a scratch
# worktree of the committed HEAD, so commit the work first, and never touches this checkout. There it changes two
# lines of HexColour.cs: one rewritten the same, and one whose every mutant changes nothing, so a survivor is certain.
# Oracle: the lines it changed, and HexColour's hand-written self-check. Exit 0 PASS, 1 FAIL (a missing tools/stryker.sh
# is the failure this script starts red on), 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
repo=$PWD
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-stryker-examples.XXXXXX")
wt=$tmp/wt
trap 'git -C "$repo" worktree remove --force "$wt" >/dev/null 2>&1; rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
[ -x tools/stryker.sh ] || { echo "!! no tools/stryker.sh: the repo's Stryker command does not exist"; echo "!! Stryker approved examples: FAILED (above)" >&2; exit 1; }
F=$(src HexColour.cs) || exit 2
case "$F" in managed/Core/*) ;; *) echo "!! $F is not in the core project"; exit 1 ;; esac
git worktree add -q --detach "$wt" HEAD || exit 2
ln -s "$repo/build" "$wt/build"
cp "$wt/$F" "$tmp/before.cs"
sed -i -e 's/if (d < 0) return false;/if (d < 0) { return false; }/' -e 's/^\( *\)uint v = 0;$/&\n\1_ = s.Length * 2;/' "$wt/$F"
[ "$(diff "$tmp/before.cs" "$wt/$F" | grep -c '^>')" = 2 ] || { echo ">> the two lines E1 changes are gone from $F" >&2; exit 2; }
read -ra changed <<< "$(diff --unchanged-line-format= --old-line-format= --new-line-format='%dn ' "$tmp/before.cs" "$wt/$F")"
git -C "$wt" commit -qam "the lines the Stryker examples mutate" || exit 2

# E1
# Given a branch that changed lines in the core assembly
# When the developer runs the repo's Stryker command
# Then Stryker mutates only those lines and reports killed, survived and timed-out counts per file, inside an hour
s=$(date +%s)
(cd "$wt" && timeout 3600 tools/stryker.sh --since HEAD~1 --output "$tmp/report") > "$tmp/e1.out" 2>&1
e1=$?; took=$(( $(date +%s) - s ))
echo ">> the Stryker run took ${took}s"
[ "$e1" != 124 ] || fail "E1: the Stryker run passed an hour"
report=$tmp/report/reports/mutation-report.json
[ -f "$report" ] || fail "E1: no mutation-report.json under the output folder -- $(tail -3 "$tmp/e1.out")"
if [ -f "$report" ]; then
  python3 -I - "$report" "$F" "${changed[@]}" > "$tmp/e1.check" <<'PY' || fail "E1: $(cat "$tmp/e1.check")"
import json, sys
r = json.load(open(sys.argv[1])); want, lines = sys.argv[2], {int(x) for x in sys.argv[3:]}
ms = [(f, m) for f, v in r['files'].items() for m in v['mutants']]
tested = [(f, m) for f, m in ms if m['status'] not in ('Ignored', 'NoCoverage', 'CompileError')]
if not tested: sys.exit(print('no mutant was tested') or 1)
off = [(f, m['location']['start']['line']) for f, m in tested if not f.endswith(want) or m['location']['start']['line'] not in lines]
if off: sys.exit(print(f'mutants outside the changed lines: {off[:5]}') or 1)
PY
fi
grep -qE "HexColour\.cs: [0-9]+ killed, [0-9]+ survived, [0-9]+ timed out" "$tmp/e1.out" || fail "E1: no per-file counts line for HexColour.cs"

# E2
# Given the core's self-checks
# When Stryker's test project runs them
# Then each self-check is one test, and a FAIL line fails that test
tests=$(cd "$wt" && git ls-files 'managed/Core*/*.Tests.csproj' | head -1)
[ -n "$tests" ] || fail "E2: no core test project"
if [ -n "$tests" ]; then
  (cd "$wt" && dotnet test "$tests" --list-tests -nologo) > "$tmp/list.out" 2>&1
  grep -qE '^ +.*HexColour' "$tmp/list.out" || fail "E2: HexColour's self-check is not a test of its own"
  cp "$wt/$F" "$tmp/clean.cs"
  sed -i "s/s\[0\] != '#'/s[0] == '#'/" "$wt/$F"
  cmp -s "$tmp/clean.cs" "$wt/$F" && { echo ">> E2's edit no longer applies to $F" >&2; exit 2; }
  (cd "$wt" && dotnet test "$tests" -nologo) > "$tmp/e2.out" 2>&1 && fail "E2: a FAIL in HexColour's self-check passed the test run"
  grep -qE 'Failed .*HexColour' "$tmp/e2.out" || fail "E2: the test that failed is not HexColour's"
  grep -q '#RRGGBB is opaque' "$tmp/e2.out" || fail "E2: the failed test does not carry the FAIL line"
  cp "$tmp/clean.cs" "$wt/$F"
fi

# E3
# Given a mutant Stryker reports survived
# When the developer reads the report
# Then it names the file, the line and the mutation
if [ -f "$report" ]; then
  python3 -I - "$report" > "$tmp/e3.want" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
for f, v in r['files'].items():
    for m in v['mutants']:
        if m['status'] == 'Survived':
            print(f"{f.split('/')[-1]}:{m['location']['start']['line']} {m['mutatorName']}")
PY
  [ -s "$tmp/e3.want" ] || fail "E3: the line every mutant of which changes nothing left no survivor"
  while read -r where what; do
    grep -F "SURVIVED $where" "$tmp/e1.out" | grep -qF "$what" || fail "E3: the survivor at $where ($what) is not named file, line and mutation"
  done < "$tmp/e3.want"
fi

[ $rc = 0 ] && echo ">> Stryker approved examples: E1 to E3 pass" || echo "!! Stryker approved examples: FAILED (above)" >&2
exit $rc
