#!/usr/bin/env bash
# Every survivor the 2026-11-21 review proved is killed by an assertion (CX-629, E1 to E3). Heavy -- the review's
# mutants in one runner pass, with the Vulkan driver hidden from the loader (VK_ICD_FILENAMES, the loader's own
# setting) so no GPU suite can kill one -- so build.sh does not run it; run it by hand inside nix develop. Oracle: the
# review's mutant list on CX-623 and, per mutant, the word its killing FAIL line has to name, both in
# review_mutants.tsv. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
. tools/selfcheck/examples_lib.sh
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-survivor-examples.XXXXXX")
trap 'rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
export VK_ICD_FILENAMES=$tmp/no-driver.json
review_list "$tmp/all.txt"
awk -F'\t' '$6 == "survivor"' "$tmp/all.txt" > "$tmp/survivors.txt"
cut -f1-4 "$tmp/survivors.txt" > "$tmp/survivors.tsv"

# E1
# Given each survivor the 2026-11-21 review proved, except those it marked equivalent or engine-bound
# When the developer runs the mutant runner on that list
# Then every one is killed by a FAIL line that names the boundary it moved
# E3
# Given the render-pass colour limit, the post-chain level count and floor, the UBO fit and the attach-layer check
# When a self-check asks each one about a value exactly at its limit
# Then it gets the answer the limit's declaration states, with no GPU
python3 tools/selfcheck/mutants.py "$tmp/survivors.tsv" > "$tmp/e1.out" 2>&1
while IFS= read -r row; do
  name=$(cut -f1 <<< "$row"); word=$(cut -f5 <<< "$row")
  [ -n "$word" ] || { fail "E1: $name has no word for its FAIL line to name"; continue; }
  line=$(grep -E "^[A-Za-z]+ +$name( |$)" "$tmp/e1.out")
  said=$(sed -E 's/^[A-Za-z]+ +[^ ]+ +//' <<< "$line")
  case "$line" in
    killed*"by a timeout"*) fail "E1: $name is killed by a timeout, not by a FAIL line" ;;
    killed*SelfCheck*FAIL*) grep -qiF -- "$word" <<< "$said" || fail "E1: $name's FAIL line does not name '$word': $said" ;;
    *) fail "E1: $name is not killed by a FAIL line: ${line:-no verdict}" ;;
  esac
  case "$name" in a2_*|a3_*|a4_*|a5_*|b19_*) grep -q 'GpuGolden' <<< "$said" && fail "E3: $name is killed only by a GPU golden" ;; esac
done < "$tmp/survivors.txt"

# E2
# Given the extender's services
# When the host initializes them
# Then they initialize in InitOrder, then by name, with the seam first
printf '%s\t%s\t%s\t%s\n' by_name "$(src ServiceHost.cs)" '.OrderBy(t => t.Name)' '.OrderByDescending(t => t.Name)' > "$tmp/order.tsv"
python3 tools/selfcheck/mutants.py "$tmp/order.tsv" > "$tmp/e2.out" 2>&1
grep -qE '^killed +by_name +.*SelfCheck FAIL' "$tmp/e2.out" || fail "E2: initializing in reverse name order fails no check -- $(tail -2 "$tmp/e2.out")"
tools/selfcheck/run.sh --out "$tmp/e2b" > "$tmp/e2b.out" 2>&1 || fail "E2: the unmutated boot failed"
order=$(sed -n 's/.*\[cx\] host: init order: //p' "$tmp/e2b/cx.log" | head -1)
[ "${order%%,*}" = SeamService ] || fail "E2: the first service to initialize is '${order%%,*}', not SeamService"

[ $rc = 0 ] && echo ">> review survivor approved examples: E1 to E3 pass" || echo "!! review survivor approved examples: FAILED (above)" >&2
exit $rc
