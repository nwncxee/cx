#!/usr/bin/env bash
# tools/selfcheck/mutants.py's approved examples (CX-589, E1 to E3). Heavy -- an IL compile and five boots -- so
# build.sh does not run it; run it by hand inside nix develop, in a checkout whose staged build is CURRENT
# (tools/where.sh), which E3 reads. Oracle: HexColour.cs's hand-written self-check, which kills the three mutants
# below that change what it parses and not the fourth, which changes nothing. Exit 0 PASS, 1 FAIL, 2 setup.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
rc=0
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-mutant-examples.XXXXXX")
runner=""
trap '[ -z "$runner" ] || kill -9 -- -"$runner" 2>/dev/null; rm -rf "$tmp"' EXIT
fail() { echo "!! $1"; rc=1; }
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }

# What the tree is: every tracked and untracked file's status, and the bytes of every tracked one.
tree_state() { git status --porcelain=v1 --untracked-files=all; git ls-files -z | xargs -0 sha1sum 2>/dev/null | sha1sum; }

F=managed/Framework/HexColour.cs
printf '%s\t%s\t%s\t%s\n' \
  "hash-check" "$F" "s[0] != '#'" "s[0] == '#'" \
  "digit-guard" "$F" "if (d < 0) return false;" "" \
  "opaque" "$F" "v << 8 | 0xFF" "v << 8" \
  "same-shift" "$F" "v = v << 4 | (uint)d;" "v = (v << 4) | (uint)d;" > "$tmp/four.tsv"

# E1
# Given a file listing four mutants, each an exact-text edit to a changed line
# When the developer runs the mutant runner on it
# Then the code is compiled once for all four, not once per mutant
# And the runner reports each mutant as killed, with the FAIL line that killed it, or as survived
before=$(tree_state)
python3 tools/selfcheck/mutants.py "$tmp/four.tsv" > "$tmp/e1.out" 2>&1
e1_rc=$?
[ "$e1_rc" = 1 ] || fail "E1: a list with a survivor exited $e1_rc, not 1 -- $(tail -5 "$tmp/e1.out")"
n=$(grep -c '^>> compiling managed/ to IL' "$tmp/e1.out")
[ "$n" = 1 ] || fail "E1: the runner compiled $n times for four mutants, not once"
# The lines a mutant's report holds: its killed line and the also lines under it.
report() { awk -v n="$2" '$1 == "killed" || $1 == "SURVIVED" || $1 == "ERROR" { on = ($2 == n) } on' "$1"; }
for m in hash-check digit-guard opaque; do
  report "$tmp/e1.out" "$m" | grep -qE "^killed +$m +.*SelfCheck FAIL: " || fail "E1: $m is not reported killed with its FAIL line"
done
report "$tmp/e1.out" opaque | grep -q 'HexColour\.SelfCheck FAIL: #RRGGBB is opaque' || fail "E1: opaque's report does not hold '#RRGGBB is opaque'"
grep -qE '^SURVIVED +same-shift' "$tmp/e1.out" || fail "E1: same-shift is not reported as survived"
[ "$(tree_state)" = "$before" ] || fail "E3: a whole run changed the tree"

# E2
# Given a mutant whose text does not appear exactly once in its file
# When the developer runs the mutant runner
# Then it refuses that mutant by name before compiling anything
printf '%s\t%s\t%s\t%s\n' \
  "fine" "$F" "s[0] != '#'" "s[0] == '#'" \
  "twice" "$F" "return false;" "return true;" \
  "nowhere" "$F" "this text is not in the file" "x" > "$tmp/bad.tsv"
python3 tools/selfcheck/mutants.py "$tmp/bad.tsv" > "$tmp/e2.out" 2>&1
e2_rc=$?
[ "$e2_rc" = 2 ] || fail "E2: a list with a bad mutant exited $e2_rc, not 2"
grep -q 'twice' "$tmp/e2.out" || fail "E2: the refusal does not name 'twice'"
grep -q 'nowhere' "$tmp/e2.out" || fail "E2: the refusal does not name 'nowhere'"
grep -q '^>> compiling' "$tmp/e2.out" && fail "E2: the runner compiled before refusing"

# E3
# Given a normal ./build.sh
# When it stages cx-linux-x64/
# Then cx_managed.so contains no mutant switch and reads no mutant setting
# And the tree is left exactly as it was before the runner started, even when the runner is killed partway
case "$(bash tools/where.sh)" in
  *"build     CURRENT"*)
    grep -qa 'CxMutantSwitch' cx-linux-x64/cx_managed.so && fail "E3: the staged cx_managed.so carries CxMutantSwitch"
    grep -qa 'cx-mutant-active' cx-linux-x64/cx_managed.so && fail "E3: the staged cx_managed.so names the mutant setting" ;;
  *) echo ">> the staged build is not CURRENT: run ./build.sh first" >&2; exit 2 ;;
esac
before=$(tree_state)
mkdir -p "$tmp/rt"
TMPDIR="$tmp/rt" setsid python3 tools/selfcheck/mutants.py "$tmp/four.tsv" > "$tmp/e3.out" 2>&1 &
runner=$!
# Killed once its overlay -- the mutated copy -- exists, which is the window a kill must leave the tree untouched in.
for _ in $(seq 1 600); do ls -d "$tmp"/rt/cx-mutants.*/managed >/dev/null 2>&1 && break; sleep 0.1; done
ls -d "$tmp"/rt/cx-mutants.*/managed >/dev/null 2>&1 || { echo ">> E3: the runner made no overlay within 60s" >&2; exit 2; }
kill -9 -- -"$runner" 2>/dev/null; wait "$runner" 2>/dev/null; runner=""
[ "$(tree_state)" = "$before" ] || fail "E3: a runner killed partway left the tree changed: $(git status --porcelain | head -5)"

[ $rc = 0 ] && echo ">> mutant runner approved examples: E1 to E3 pass" || echo "!! mutant runner approved examples: FAILED (above)" >&2
exit $rc
