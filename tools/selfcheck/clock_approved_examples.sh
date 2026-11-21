#!/usr/bin/env bash
# The self-checks' own clock and the log flush that says when it gives up (CX-626, E1 to E4). Heavy -- IL compiles,
# the baseline's boot, five mutant runs on a loaded machine and the AOT demo -- so build.sh does not run it; run it by
# hand inside nix develop, in a checkout whose staged build is CURRENT (tools/where.sh), which E4 reads. Oracle: the
# boot of BASE_REV (examples_lib.sh) timed beside this one, the log lines written by hand below, and the AOT image's
# own cx.log. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-clock-examples.XXXXXX")
load=""
stop_load() { [ -z "$load" ] || kill -- -"$load" 2>/dev/null; load=""; }
trap 'stop_load; rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
case "$(bash tools/where.sh)" in *"build     CURRENT"*) ;; *) echo ">> the staged build is not CURRENT: run ./build.sh first" >&2; exit 2 ;; esac
SUITES="KnobFileBootApprovedExamples.SelfCheck TexRegistry.UploadApprovedExamples.SelfCheck TexRegistry.UploadHoldsToday.SelfCheck"

# A suite's span in a boot's log: seconds from the line before its first line to its last.
span() { python3 -I - "$1" "$2" <<'PY'
import sys, datetime
lines = [l for l in open(sys.argv[1], encoding='utf-8') if l[:4].isdigit()]
t = lambda l: datetime.datetime.fromisoformat(l.split(' ')[0][:26])
hits = [i for i, l in enumerate(lines) if '[cx] ' + sys.argv[2] in l]
print('%.2f' % ((t(lines[hits[-1]]) - t(lines[max(hits[0] - 1, 0)])).total_seconds()) if hits else 'none')
PY
}

# E1
# Given the knob file debounce, the texture retry wait and the upload holds self-checks
# When the self-checks boot in the fast path
# Then none of them sleeps on the wall clock, and each still proves its wait by stepping a clock it owns
# And the boot is at least 4 seconds shorter than at main 9e89af8e on the same machine
tools/selfcheck/run.sh --out "$tmp/head" > "$tmp/head.compile" 2>&1 || fail "E1: the fast path fails here: $(tail -5 "$tmp/head.compile")"
baseline
s=$(date +%s%N)
tools/selfcheck/run.sh --out "$tmp/head" --no-build > "$tmp/head.out" 2>&1 || fail "E1: the boot here failed: $(tail -5 "$tmp/head.out")"
head_secs=$(awk -v n=$(( ($(date +%s%N) - s) / 1000000 )) 'BEGIN { printf "%.1f", n / 1000 }')
echo ">> boot: ${BASE_SECS}s at $BASE_REV, ${head_secs}s here"
awk -v b="$BASE_SECS" -v h="$head_secs" 'BEGIN { exit !(h <= b - 4) }' || fail "E1: the boot took ${head_secs}s, not 4 s under $BASE_REV's ${BASE_SECS}s"
for s in $SUITES; do
  grep -q "\[cx\] $s: passed" "$tmp/head/cx.log" || fail "E1: $s does not pass here"
  was=$(span "$BASE_LOG" "$s"); now=$(span "$tmp/head/cx.log" "$s")
  [ "$was" != none ] && [ "$now" != none ] || { fail "E1: $s logged nothing ($was before, $now here)"; continue; }
  awk -v w="$was" -v n="$now" 'BEGIN { exit !(w >= 0.9 && n <= w / 4) }' || fail "E1: $s spans ${now}s of the boot against ${was}s at $BASE_REV: it still waits on the wall clock"
done

# E2
# Given the log writer has not emptied its queue when a self-check flushes the log
# When the flush runs out of time
# Then the self-check that asked fails with a line naming the flush, not with a missing log line
L=$(src ExtenderLog.cs) || exit 2
mkdir -p "$tmp/stall/$(dirname "$L")"
sed 's|^\( *\)_wake.WaitOne();|&\n\1BlockingWait.Sleep(5000, "the approved example stalls the writer");|' "$L" > "$tmp/stall/$L"
cmp -s "$L" "$tmp/stall/$L" && { echo ">> E2's stall no longer applies to $L" >&2; exit 2; }
tools/selfcheck/run.sh --out "$tmp/e2" --overlay "$tmp/stall" > "$tmp/e2.out" 2>&1
[ $? = 1 ] || fail "E2: a stalled writer did not fail the boot -- $(tail -3 "$tmp/e2.out")"
grep -qE 'SelfCheck.*FAIL.*log flush' "$tmp/e2.out" || fail "E2: no FAIL line names the log flush"
grep -E 'SelfCheck.*FAIL.*(holds no line|was not in the file)' "$tmp/e2.out" && fail "E2: a check failed on a missing log line, not on the flush"

# E3
# Given a mutant in a file the texture upload suites never reach, such as the chat tell queue's limit
# When the mutant runner boots it five times on a busy machine
# Then the texture upload suites pass every time
printf '%s\t%s\t%s\t%s\n' tell_pending "$(src TellTarget.cs)" '_pending.Count > MaxPending' '_pending.Count >= MaxPending' > "$tmp/tell.tsv"
# shellcheck disable=SC2016 # the loop is the inner shell's, so its $i and $1 expand there
setsid nice -n 19 timeout 1800 sh -c 'i=0; while [ $i -lt "$1" ]; do (while :; do :; done) & i=$((i + 1)); done; wait' _ "$(nproc)" &
load=$!
for n in 1 2 3 4 5; do
  python3 tools/selfcheck/mutants.py --whole --logs "$tmp/e3.$n" "$tmp/tell.tsv" > "$tmp/e3.$n.out" 2>&1
  grep -qE '^(killed|SURVIVED|UNTESTED) +tell_pending' "$tmp/e3.$n.out" || fail "E3: run $n gave no verdict: $(tail -3 "$tmp/e3.$n.out")"
  for s in TexRegistry.UploadApprovedExamples.SelfCheck TexRegistry.UploadHoldsToday.SelfCheck; do
    grep -q "\[cx\] $s: passed" "$tmp/e3.$n/tell_pending.cx.log" 2>/dev/null || fail "E3: run $n: $s did not pass on a busy machine"
  done
done
stop_load

# E4
# Given a normal ./build.sh
# When it stages cx-linux-x64/
# Then the knob file debounce and the texture retry run on the real clock
CX_NO_BUILD=1 ./run-demo.sh > "$tmp/demo.out" 2>&1 || { echo ">> run-demo.sh failed on the staged build" >&2; exit 2; }
demolog=$(sed -n 's/^>> \(.*cx\.log\) (written from C#):$/\1/p' "$tmp/demo.out")
grep -q '\[cx\] KnobFile: the debounce runs on the system clock' "$demolog" || fail "E4: the staged image's knob file debounce does not run on the system clock"
grep -q '\[cx\] TexRegistry: the upload retry runs on the system clock' "$demolog" || fail "E4: the staged image's texture retry does not run on the system clock"

[ $rc = 0 ] && echo ">> self-check clock approved examples: E1 to E4 pass" || echo "!! self-check clock approved examples: FAILED (above)" >&2
exit $rc
