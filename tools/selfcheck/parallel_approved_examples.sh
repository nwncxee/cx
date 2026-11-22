#!/usr/bin/env bash
# Mutant boots run side by side, and text that cannot switch is refused before the compile (CX-628, E1 to E3). Heavy --
# two runs of twenty mutants and two compiles -- so build.sh does not run it; run it by hand inside nix develop, on a
# machine with a systemd user manager for the cap. Oracle: the one-worker run's own verdicts and time, and mutants cut
# by hand below to fail in the ways each example names. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-parallel-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
systemd-run --user --scope -q true 2>/dev/null || { echo ">> no systemd user manager to cap the run under" >&2; exit 2; }
capped() { systemd-run --user --scope -q -p MemoryMax=5.8G -p MemorySwapMax=0 -- "$@"; }
ms() { echo $(( $(date +%s%N) / 1000000 )); }

# E1
# Given a list of 20 switchable mutants
# When the runner runs them with 3 workers under a 5.8G cap
# Then it reports the same verdicts as one worker, in at most half the time
review_list "$tmp/all.txt"
TWENTY="hash-check opaque digit-guard wrap_ge wrap_space sel_anchor pair_flag icon_known icon_cache icon_plt_side
  tell_pending strip_digits strip_warn fz_dt log_tail store_size hist_missing snip_sp rig_holders b4_slotsafe_kind"
for m in $TWENTY; do awk -F'\t' -v m="$m" '$1 == m { print $1 "\t" $2 "\t" $3 "\t" $4 }' "$tmp/all.txt"; done > "$tmp/twenty.tsv"
[ "$(grep -c . "$tmp/twenty.tsv")" = 20 ] || { echo ">> the list does not hold 20 mutants" >&2; exit 2; }
verdicts() { grep -E '^(killed|SURVIVED|UNTESTED|ERROR) ' "$1" | awk '{ print $1, $2 }' | sort; }
s=$(ms); capped python3 tools/selfcheck/mutants.py --jobs 1 "$tmp/twenty.tsv" > "$tmp/one.out" 2>&1; one=$(( $(ms) - s ))
s=$(ms); capped python3 tools/selfcheck/mutants.py --jobs 3 "$tmp/twenty.tsv" > "$tmp/three.out" 2>&1; three=$(( $(ms) - s ))
echo ">> 20 mutants: ${one} ms with one worker, ${three} ms with three"
grep -q '^>> .*on its own' "$tmp/one.out" && fail "E1: a mutant of the list did not switch"
[ "$(verdicts "$tmp/one.out" | wc -l)" = 20 ] || fail "E1: one worker gave $(verdicts "$tmp/one.out" | wc -l) verdicts, not 20 -- $(tail -3 "$tmp/one.out")"
diff <(verdicts "$tmp/one.out") <(verdicts "$tmp/three.out") > "$tmp/e1.diff" || fail "E1: three workers' verdicts differ from one's: $(head -6 "$tmp/e1.diff")"
[ $(( three * 2 )) -le "$one" ] || fail "E1: three workers took ${three} ms, more than half of one worker's ${one} ms"

# E2
# Given a mutant whose old text ends on an operator or reaches into a string literal
# When the developer runs the runner
# Then it refuses that mutant by name, with the reason, before compiling anything
H=$(src HexColour.cs) || exit 2
{
  printf '%s\t%s\t%s\t%s\n' fine "$H" "s[0] != '#'" "s[0] == '#'"
  printf '%s\t%s\t%s\t%s\n' dangling "$(src PostChainVk.cs)" 'levels < MaxLevels &&' 'levels <= MaxLevels &&'
  printf '%s\t%s\t%s\t%s\n' in-literal "$H" 'c == 0x112233FFu, "#RRGGBB' 'c == 0x112233FFu, "#RRGGBX'
} > "$tmp/cut.tsv"
python3 tools/selfcheck/mutants.py "$tmp/cut.tsv" > "$tmp/e2.out" 2>&1
[ $? = 2 ] || fail "E2: a list with badly cut mutants did not exit 2"
grep -qE 'refusing dangling: .*operator' "$tmp/e2.out" || fail "E2: dangling is not refused for ending on an operator"
grep -qE 'refusing in-literal: .*literal' "$tmp/e2.out" || fail "E2: in-literal is not refused for reaching into a literal"
grep -q 'refusing fine' "$tmp/e2.out" && fail "E2: a well cut mutant was refused"
grep -q '^>> compiling' "$tmp/e2.out" && fail "E2: the runner compiled before refusing"
# Found work: text that ends on a postfix operator, a null-forgiving !, a generic's > or a nullable ? is whole.
for t in 'n--' 'i++' 'value!' 'List<int>' 'int?' 'Dictionary<string, List<int>>'; do
  python3 -c 'import sys; sys.path.insert(0, "tools/selfcheck"); import mutants; sys.exit(mutants.dangling(sys.argv[1]) is not None)' "$t" \
    || fail "found: '$t' is refused as ending on an operator"
done
for t in 'a &&' 'x +' 'p =>' 'b ?' 'c >'; do
  python3 -c 'import sys; sys.path.insert(0, "tools/selfcheck"); import mutants; sys.exit(mutants.dangling(sys.argv[1]) is None)' "$t" \
    || fail "found: '$t' is not refused as ending on an operator"
done

# E3
# Given a switched compile that fails in three files
# When the runner sorts out which mutants compile alone
# Then it compiles the switched set at most twice
# One file's switch is a syntax error and two are type errors, which a compiler holding a syntax error may not report.
{
  printf '%s\t%s\t%s\t%s\n' hash-check "$H" "s[0] != '#'" "s[0] == '#'"
  printf '%s\t%s\t%s\t%s\n' decl_side "$(src IconFrames.cs)" 'private const int MaxSide = 1024;' 'private const int MaxSide = 512;'
  printf '%s\t%s\t%s\t%s\n' type_cap "$(src ChatStore.cs)" 'if (_size < Capacity) _size++;' 'if (_size < "Capacity") _size++;'
  printf '%s\t%s\t%s\t%s\n' type_wrap "$(src TextWrapper.cs)" 'lineStart = lastSpace + 1;' 'lineStart = "lastSpace";'
} > "$tmp/three.tsv"
python3 tools/selfcheck/mutants.py "$tmp/three.tsv" > "$tmp/e3.out" 2>&1
n=$(grep -c '^>> compiling the switched set' "$tmp/e3.out")
[ "$n" -ge 1 ] && [ "$n" -le 2 ] || fail "E3: the switched set was compiled $n times, not once or twice"
for m in decl_side type_cap type_wrap; do grep -qE "^>> $m: .*on its own" "$tmp/e3.out" || fail "E3: $m was not moved to a compile of its own"; done
grep -qE '^(killed|SURVIVED) +hash-check' "$tmp/e3.out" || fail "E3: the switched mutant got no verdict -- $(tail -3 "$tmp/e3.out")"

[ $rc = 0 ] && echo ">> parallel mutant approved examples: E1 to E3 pass" || echo "!! parallel mutant approved examples: FAILED (above)" >&2
exit $rc
