#!/usr/bin/env bash
# A mutant boots only the suites that reach its file (CX-624, E1 to E4). Heavy -- IL compiles, the baseline's boot and a
# handful of boots -- so build.sh does not run it; run it by hand inside nix develop. Oracle: HexColour's hand-written
# self-check, an equivalent mutant written below, two probe suites this script writes (whose attribute and phase names
# this test sets: SelfCheckSuite, SuitePhase.BeforeServices and AfterServices), and the SelfCheck lines of the boot just
# before the change. Exit 0 PASS, 1 FAIL, 2 setup.
#
#   tools/selfcheck/suites_approved_examples.sh [<before>]   the commit before the registry landed (default BASE_REV)
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-suite-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
before=${1:-$BASE_REV}
F=$(src HexColour.cs) || exit 2

# E1
# Given a mutant in managed/Framework/HexColour.cs
# When the developer runs the mutant runner
# Then the boot runs HexColour's suite and the suites that reach HexColour, and no others
# And that mutant's boot takes under 4 seconds, against about 16 for a whole boot
printf '%s\t%s\t%s\t%s\n' opaque "$F" 'v << 8 | 0xFF' 'v << 8' > "$tmp/one.tsv"
python3 tools/selfcheck/mutants.py "$tmp/one.tsv" > "$tmp/e1.out" 2>&1
if ! grep -qE '^killed +opaque ' "$tmp/e1.out" || ! grep -qE '(^killed +opaque +| also )HexColour\.SelfCheck FAIL: #RRGGBB is opaque' "$tmp/e1.out"; then fail "E1: opaque is not killed with HexColour's own check among its FAIL lines -- $(tail -3 "$tmp/e1.out")"; fi
line=$(grep -E '^>> opaque: booting [0-9]+ of [0-9]+ suites: ' "$tmp/e1.out")
[ -n "$line" ] || fail "E1: the runner does not say which suites the mutant's boot runs"
k=$(sed -E 's/^>> opaque: booting ([0-9]+) of ([0-9]+) .*/\1/' <<< "$line"); n=$(sed -E 's/^>> opaque: booting ([0-9]+) of ([0-9]+) .*/\2/' <<< "$line")
grep -q 'HexColour' <<< "$line" || fail "E1: HexColour's suite is not among the suites booted"
# ponytail: "no others" is held as a bound, a few of the whole set, until a second reach oracle exists to compare with.
if [ -z "$k" ] || [ -z "$n" ] || [ $(( k * 4 )) -ge "$n" ]; then fail "E1: $k of $n suites booted for one HexColour mutant, not the few that reach it"; fi
secs=$(sed -nE 's/^>> opaque: booted in ([0-9.]+)s$/\1/p' "$tmp/e1.out")
if [ -z "$secs" ] || ! awk -v s="$secs" 'BEGIN { exit !(s < 4) }'; then fail "E1: the mutant's boot took '${secs}'s, not under 4"; fi

# E2
# Given a mutant the filtered boot reports as survived
# When the runner finishes
# Then it boots that mutant once more with every suite, and reports the whole boot's verdict
printf '%s\t%s\t%s\t%s\n' same-shift "$F" 'v = v << 4 | (uint)d;' 'v = (v << 4) | (uint)d;' > "$tmp/same.tsv"
python3 tools/selfcheck/mutants.py "$tmp/same.tsv" > "$tmp/e2.out" 2>&1
grep -qE '^>> same-shift: survived the filtered boot, booting it with every suite' "$tmp/e2.out" || fail "E2: a filtered survivor was not booted again whole"
grep -qE '^>> same-shift: booting ([0-9]+) of \1 suites' "$tmp/e2.out" || fail "E2: the recheck did not boot every suite"
[ "$(grep -cE '^SURVIVED +same-shift' "$tmp/e2.out")" = 1 ] || fail "E2: the whole boot's verdict is not the one reported"

# E3
# Given a suite added under managed/ with no registration line anywhere
# When the self-checks boot
# Then it runs, in its phase: before the services start, or after
mkdir -p "$tmp/probe/managed/Framework"
cat > "$tmp/probe/managed/Framework/SuiteProbe.SelfCheck.cs" <<'CS'
namespace CX.Framework;

/// <summary>The approved example's two probe suites, one per phase, registered nowhere.</summary>
internal static class SuiteProbe
{
    /// <summary>Logs its phase, before the services start.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void EarlySelfCheck(ExtenderLog log) => log.Info("SuiteProbe.SelfCheck: passed before the services");

    /// <summary>Logs its phase, after the services start.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void LateSelfCheck(ExtenderLog log) => log.Info("SuiteProbe.SelfCheck: passed after the services");
}
CS
tools/selfcheck/run.sh --out "$tmp/e3" --overlay "$tmp/probe" > "$tmp/e3.out" 2>&1 || fail "E3: the boot with the probe suites failed -- $(tail -3 "$tmp/e3.out")"
log=$tmp/e3/cx.log
at() { grep -nF "$1" "$log" | head -1 | cut -d: -f1; }
early=$(at 'SuiteProbe.SelfCheck: passed before the services'); late=$(at 'SuiteProbe.SelfCheck: passed after the services')
init=$(grep -nE '\[cx\] host: initializing [0-9]+ service\(s\)$' "$log" | head -1 | cut -d: -f1)
seal=$(at '[cx] host: frame Start ->')
[ -n "$init" ] && [ -n "$seal" ] || fail "E3: the boot does not log where the services start ('host: initializing <n> service(s)') and the frame seal"
[ -n "$early" ] && [ "$early" -lt "${init:-0}" ] || fail "E3: the before-services probe ran at line '$early', not before the services start at '$init'"
[ -n "$late" ] && [ "$late" -gt "${init:-999999}" ] && [ "$late" -lt "${seal:-0}" ] || fail "E3: the after-services probe ran at line '$late', not between the services ('$init') and the frame seal ('$seal')"

# E4
# Given the whole self-check boot, through tools/selfcheck/run.sh or ./run-demo.sh
# When it runs after the change
# Then it logs the same SelfCheck lines as before, and ends on the same verdict line
baseline "$before"
tools/selfcheck/run.sh --out "$tmp/e4" > "$tmp/e4.out" 2>&1 || fail "E4: the whole boot failed -- $(tail -3 "$tmp/e4.out")"
suites() { grep -o '\[cx\] [A-Za-z0-9_.]*SelfCheck[ :A-Z]*' "$1" | sort | uniq -c; }
diff <(suites "$BASE_LOG") <(suites "$tmp/e4/cx.log") > "$tmp/e4.diff" || fail "E4: the SelfCheck lines differ from $before's (< before, > now): $(head -8 "$tmp/e4.diff")"
verdict() { sh tools/selfcheck/verdict.sh "$1" | head -1; }
[ "$(verdict "$BASE_LOG")" = "$(verdict "$tmp/e4/cx.log")" ] || fail "E4: the verdict is '$(verdict "$tmp/e4/cx.log")', not $before's '$(verdict "$BASE_LOG")'"

# Found work: a site that ran only outside every unit names no unit to filter to, so its boot runs every unit.
host_only=$(python3 -c "import sys; sys.path.insert(0, 'tools/selfcheck'); import mutants; print(mutants.filtered({mutants.OUTSIDE}), sorted(mutants.filtered({mutants.OUTSIDE, 'A'})))")
[ "$host_only" = "None ['A']" ] || fail "found: a site that ran only in the host does not boot every unit ($host_only)"

[ $rc = 0 ] && echo ">> suite registry approved examples: E1 to E4 pass" || echo "!! suite registry approved examples: FAILED (above)" >&2
exit $rc
