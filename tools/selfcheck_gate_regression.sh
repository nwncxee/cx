#!/usr/bin/env bash
# The self-check gate's fifth shape (CX-561): a bool check that feeds Expect and returns true early. Planted trees run through
# tools/gates/selfcheck.py --root. Oracle: the planted files and the refusal text written by hand below.
cd "$(dirname "$0")/.." || exit 2
repo=$PWD
rc=0
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT

# plant <case> <file text>: a tree with one self-check file.
plant() { mkdir -p "$tmp/$1/managed"; printf '%s\n' "$2" > "$tmp/$1/managed/Plant.SelfCheck.cs"; }

plant early 'class C {
    static bool Reads() { if (Env.On) return true; return Compute() == 3; }
    void Run(Checker check) { check.Expect(Reads(), "reads"); }
}'
plant search 'class C {
    static bool Has(int[] a) { foreach (int x in a) if (x == 3) return true; return false; }
    void Run(Checker check) { check.Expect(Has([1, 3]), "has"); }
}'
plant unread 'class C {
    static bool Helper() { if (Env.On) return true; return false; }
    void Run(Checker check) { check.Expect(Compute() == 3, "computes"); }
}'

if out=$(python3 tools/gates/selfcheck.py --root "$tmp/early" 2>&1); then echo "!! selfcheck gate: an early return true feeding Expect passed"; rc=1
else case "$out" in *"Reads returns true early"*) ;; *) echo "!! selfcheck gate: the refusal does not name the method -- it said: $out"; rc=1 ;; esac; fi
python3 tools/gates/selfcheck.py --root "$tmp/search" >/dev/null 2>&1 || { echo "!! selfcheck gate: a search helper's loop return was refused"; rc=1; }
python3 tools/gates/selfcheck.py --root "$tmp/unread" >/dev/null 2>&1 || { echo "!! selfcheck gate: a bool helper no Expect reads was refused"; rc=1; }
[ $rc = 0 ] && echo "selfcheck gate: regression tests passed"
exit $rc
