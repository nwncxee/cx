#!/usr/bin/env bash
# tools/selfcheck/run.sh's approved examples (CX-588, E1 to E4). Heavy -- two IL compiles and three boots, about four
# minutes -- so build.sh does not run it; run it by hand, inside nix develop, in a checkout whose staged build is
# CURRENT (tools/where.sh), because E1 and E3 compare against ./run-demo.sh on that build. Oracle: run-demo.sh's own
# log of the same tree, and the hand-written FAIL line below. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-fast-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }

case "$(bash tools/where.sh)" in *"build     CURRENT"*) ;; *) echo ">> the staged build is not CURRENT: run ./build.sh first" >&2; exit 2 ;; esac
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
systemd-run --user --scope -q true 2>/dev/null || { echo ">> no systemd user manager to cap the run under" >&2; exit 2; }

# The cap a deathmatch opponent runs under: a user scope with 2G and no swap, as cap.sh makes one.
capped() { systemd-run --user --scope -q -p MemoryMax=2G -p MemorySwapMax=0 -- "$@"; }

# The suites a log ran, one per line: the name before ".SelfCheck", with how many lines it wrote.
suites() { grep -o '\[cx\] [A-Za-z0-9_.]*SelfCheck' "$1" | sed 's/^\[cx\] //' | sort | uniq -c; }

echo ">> the AOT demo on the staged build..."
CX_NO_BUILD=1 ./run-demo.sh > "$tmp/demo.out" 2>&1 || { echo ">> run-demo.sh failed on the staged build: $tmp/demo.out" >&2; cat "$tmp/demo.out"; exit 2; }
demolog=$(sed -n 's/^>> \(.*cx\.log\) (written from C#):$/\1/p' "$tmp/demo.out")
cp "$demolog" "$tmp/demo.log"
demo_line=$(grep -E '^>> [0-9]+ SelfCheck lines, none failed' "$tmp/demo.out")

# E1
# Given a checkout where one .cs file under managed/ changed since the last run
# When the developer runs the fast self-check command
# Then every SelfCheck suite the stub host runs also runs here
# And it ends with the same line run-demo.sh ends with: >> <n> SelfCheck lines, none failed
# And it finishes in under 2 minutes on this machine, against about 7 for build.sh then run-demo.sh
# E4
# Given a deathmatch opponent running under cap.sh with a 2G memory cap
# When it runs the fast self-check command
# Then the run finishes inside the cap, so an opponent can prove a finding itself instead of handing a mutant back
touch managed/Framework/HexColour.cs
start=$(date +%s)
capped tools/selfcheck/run.sh > "$tmp/fast.out" 2>&1
fast_rc=$?
took=$(( $(date +%s) - start ))
echo ">> the fast run took ${took}s and exited $fast_rc"
[ "$fast_rc" = 0 ] || fail "E4: the fast run under a 2G cap exited $fast_rc -- $(tail -5 "$tmp/fast.out")"
fastlog=build/selfcheck/cx.log
fast_line=$(grep -E '^>> [0-9]+ SelfCheck lines, none failed' "$tmp/fast.out")
[ -n "$fast_line" ] || fail "E1: the fast run did not end on '>> <n> SelfCheck lines, none failed'"
[ "$took" -lt 120 ] || fail "E1: the fast run took ${took}s, not under 2 minutes"

# E3
# Given a suite that cannot run without the AOT build
# When the fast self-check command runs
# Then that suite is listed as SKIPPED with its reason
# And the number of SelfCheck lines equals run-demo.sh's, less the suites it lists as skipped
skipped_here=$(grep -o '\[cx\] [A-Za-z0-9_.]*SelfCheck SKIPPED: .*' "$fastlog" | sed 's/^\[cx\] //' | sort)
skipped_demo=$(grep -o '\[cx\] [A-Za-z0-9_.]*SelfCheck SKIPPED: .*' "$tmp/demo.log" | sed 's/^\[cx\] //' | sort)
only_here=$(comm -23 <(echo "$skipped_here") <(echo "$skipped_demo") | sed '/^$/d')
while IFS= read -r s; do
  [ -n "$s" ] || continue
  case "$s" in *"SKIPPED: "?*) ;; *) fail "E3: a suite skipped only here gives no reason: $s" ;; esac
  grep -F ">>   " "$tmp/fast.out" | grep -qF "$s" || fail "E3: the verdict does not list $s"
done <<< "$only_here"
drop=$(echo "$only_here" | sed -n 's/ SKIPPED:.*//p' | sed 's/[.[\*^$]/\\&/g' | paste -sd'|')
if [ -n "$drop" ]; then
  n_demo=$(grep 'SelfCheck' "$tmp/demo.log" | grep -cvE "\[cx\] ($drop)")
  n_here=$(grep 'SelfCheck' "$fastlog" | grep -cvE "\[cx\] ($drop)")
else
  n_demo=$(grep -c 'SelfCheck' "$tmp/demo.log"); n_here=$(grep -c 'SelfCheck' "$fastlog")
fi
[ "$n_here" = "$n_demo" ] || fail "E3: $n_here SelfCheck lines here, $n_demo in run-demo.sh's log, less the suites skipped here only"
# ...and E1's every-suite line: each suite the demo ran, the fast run ran as often, unless it is skipped here.
diff <(suites "$tmp/demo.log" | grep -vE " (${drop:-^$})\$") <(suites "$fastlog" | grep -vE " (${drop:-^$})\$") > "$tmp/suites.diff" \
  || fail "E1: the suites differ from run-demo.sh's (< demo, > fast): $(head -10 "$tmp/suites.diff")"
[ -n "$drop" ] || [ "$fast_line" = "$demo_line" ] || fail "E1: the fast run ended '$fast_line', run-demo.sh '$demo_line'"

# E2
# Given a change that makes a self-check fail under run-demo.sh
# When the developer runs the fast self-check command
# Then it exits non-zero
# And it prints the same FAIL line run-demo.sh prints for that check
mkdir -p "$tmp/overlay/managed/Framework"
sed 's/s\[0\] != '"'"'#'"'"'/s[0] == '"'"'#'"'"'/' managed/Framework/HexColour.cs > "$tmp/overlay/managed/Framework/HexColour.cs"
cmp -s managed/Framework/HexColour.cs "$tmp/overlay/managed/Framework/HexColour.cs" && { echo ">> E2's edit no longer applies to HexColour.cs" >&2; exit 2; }
capped tools/selfcheck/run.sh --out "$tmp/e2" --overlay "$tmp/overlay" > "$tmp/e2.out" 2>&1
e2_rc=$?
[ "$e2_rc" = 1 ] || fail "E2: a failing self-check exited $e2_rc, not 1 -- $(tail -5 "$tmp/e2.out")"
grep -q '^>> FAIL: a SelfCheck suite failed:' "$tmp/e2.out" || fail "E2: no '>> FAIL: a SelfCheck suite failed:' line"
grep -q '\[cx\] HexColour.SelfCheck FAIL: #RRGGBB is opaque$' "$tmp/e2.out" || fail "E2: no 'HexColour.SelfCheck FAIL: #RRGGBB is opaque' line"

# CX-633 E6
# Given a machine with a Vulkan device
# When tools/selfcheck/run.sh boots twice in a row
# Then no gap between two cx.log lines in either boot is over 5 s, and the whole boot stays under 30 s
# CX-633 E7
# Given a fast-path boot that has run once
# When it runs again
# Then cx.log does not say "pipeline cache empty", and a player's cx.cfg still never reaches the suites
# Seconds between cx.log's lines: the widest gap and the whole span, as "<gap> <span> <line after the gap>".
span() { python3 -c 'import sys, datetime as d
ts = []
for l in open(sys.argv[1], errors="replace"):
    try: ts.append((d.datetime.fromisoformat(l.split(" ")[0][:26]), l.strip()[:160]))
    except ValueError: pass
g = max(((b[0] - a[0]).total_seconds(), b[1]) for a, b in zip(ts, ts[1:]))
print(f"{g[0]:.1f} {(ts[-1][0] - ts[0][0]).total_seconds():.1f} {g[1]}")' "$1"; }
if command -v vulkaninfo > /dev/null && vulkaninfo --summary > /dev/null 2>&1; then
  cfg="build/selfcheck/home/.local/share/Neverwinter Nights/cx.cfg"
  trap 'rm -rf "$tmp"; rm -f "$cfg"' EXIT
  vk_ran=1
  for boot in 1 2; do
    if [ "$boot" = 2 ]; then mkdir -p "$(dirname "$cfg")"; echo "# a player's cx.cfg, planted by CX-633 E7" > "$cfg"; fi
    capped tools/selfcheck/run.sh --no-build > "$tmp/boot$boot.out" 2>&1 || fail "E6: boot $boot failed -- $(tail -3 "$tmp/boot$boot.out")"
    [ "$boot" = 2 ] && grep -q 'pipeline cache empty' build/selfcheck/cx.log && fail "E7: the second boot started with an empty pipeline cache"
    read -r gap whole after <<< "$(span build/selfcheck/cx.log)"
    echo ">> boot $boot: widest gap ${gap}s, whole boot ${whole}s"
    awk -v g="$gap" 'BEGIN { exit !(g > 5) }' && fail "E6: boot $boot has a ${gap}s gap before: $after"
    awk -v w="$whole" 'BEGIN { exit !(w >= 30) }' && fail "E6: boot $boot took ${whole}s, not under 30 s"
  done
  grep -qF "CX-633 E7" "$cfg" 2> /dev/null && fail "E7: the planted cx.cfg was still in the suites' home"
else
  vk_ran=0
  echo ">> CX-633 E6 and E7: SKIPPED: no Vulkan device here"
fi

[ $rc = 0 ] && [ "$vk_ran" = 0 ] && echo ">> fast self-check approved examples: E1 to E4 pass, CX-633 E6 and E7 SKIPPED"
[ $rc = 0 ] && [ "$vk_ran" = 1 ] && echo ">> fast self-check approved examples: E1 to E4, CX-633 E6 and E7 pass (${took}s)" || echo "!! fast self-check approved examples: FAILED (above)" >&2
exit $rc
