#!/usr/bin/env bash
# The mutant runner tells a blind mutant from a survivor, and stops a loop in seconds (CX-627, E1 and E2). Heavy -- IL
# compiles and a few boots -- so build.sh does not run it; run it by hand inside nix develop. E1 hides the Vulkan
# driver from the loader (VK_ICD_FILENAMES, the loader's own setting) so the GPU goldens SKIP on any machine. Oracle:
# the goldens' own SKIPPED line, and a loop made endless by hand below. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-blind-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }

# E1
# Given a mutant in a line that only a suite which SKIPPED on this host reaches
# When the developer runs the mutant runner
# Then it reports UNTESTED, naming the skipped suite and its reason, not SURVIVED
# And the run exits non-zero
printf '%s\t%s\t%s\t%s\n' blit_off "$(src GpuGolden.BlitChain.SelfCheck.cs)" \
  'off += (long)MipSide(BlitChainW, l) * MipSide(BlitChainH, l) * 4;' 'off += 0;' > "$tmp/blind.tsv"
VK_ICD_FILENAMES=$tmp/no-driver.json python3 tools/selfcheck/mutants.py "$tmp/blind.tsv" > "$tmp/e1.out" 2>&1
e1=$?
grep -qE '^SURVIVED +blit_off' "$tmp/e1.out" && fail "E1: a mutant only a skipped suite reaches is reported SURVIVED"
grep -qE '^UNTESTED +blit_off +.*GpuGolden\.SelfCheck SKIPPED: .+' "$tmp/e1.out" || fail "E1: blit_off is not UNTESTED with GpuGolden's SKIPPED reason -- $(tail -3 "$tmp/e1.out")"
[ "$e1" != 0 ] || fail "E1: a run with an untested mutant exited 0"
# Found work: the reason is the skipped suite of the mutant's own class, not every suite that skipped.
grep -E '^UNTESTED +blit_off' "$tmp/e1.out" | grep -q VulkanToolchain && fail "found: blit_off's reason names VulkanToolchain, a suite of another class"

# E2
# Given a mutant that makes a loop endless
# When the runner boots it
# Then it is reported killed by a timeout within 1.5 times the unmutated boot plus 3 seconds
printf '%s\t%s\t%s\t%s\n' endless "$(src HexColour.cs)" 'for (int i = 1; i < s.Length; i++)' 'for (int i = 1; i < s.Length; i += 0)' > "$tmp/loop.tsv"
python3 tools/selfcheck/mutants.py "$tmp/loop.tsv" > "$tmp/e2.out" 2>&1
grep -qE '^killed +endless +by a timeout' "$tmp/e2.out" || fail "E2: the endless loop is not killed by a timeout -- $(tail -3 "$tmp/e2.out")"
boot=$(sed -nE 's/^>> the unmutated boot took ([0-9.]+)s; a mutant is stopped at ([0-9.]+)s$/\1/p' "$tmp/e2.out")
cap=$(sed -nE 's/^>> the unmutated boot took ([0-9.]+)s; a mutant is stopped at ([0-9.]+)s$/\2/p' "$tmp/e2.out")
took=$(sed -nE 's/^>> endless: stopped after ([0-9.]+)s$/\1/p' "$tmp/e2.out")
[ -n "$boot" ] && [ -n "$cap" ] && [ -n "$took" ] || fail "E2: the runner does not say the boot time, the stop time and when it stopped endless"
awk -v b="${boot:-0}" -v c="${cap:-999}" -v t="${took:-999}" 'BEGIN { exit !(c <= 1.5 * b + 3 && t <= 1.5 * b + 3) }' \
  || fail "E2: endless ran ${took}s against a stop at ${cap}s, past 1.5 x ${boot}s + 3"
# Found work: the stop counts from the boot, so the compile before it spends none of it.
awk -v c="${cap:-0}" -v t="${took:-0}" 'BEGIN { exit !(t >= c - 1) }' || fail "found: endless was stopped after ${took}s, before its boot reached the stop at ${cap}s"

[ $rc = 0 ] && echo ">> blind mutant approved examples: E1 and E2 pass" || echo "!! blind mutant approved examples: FAILED (above)" >&2
exit $rc
