#!/usr/bin/env bash
# Regression check, not an approved test: it held before the change it guards, so it cannot go red first. The
# example (CX-436): given an empty shader and pipeline cache, when the player enters the docks, then the loading
# screen lifts no later than it does on the build before this change. The hold varies by seconds run to run (the
# bake), so it compares medians: --rounds docks runs here against the --before logs, each a docks run's cx.log on
# the build before. Exit 0 PASS, 1 FAIL, 2 when a run itself could not be made.
#
#   tools/live/hold_length_regression.sh --before LOG [--before LOG ...] [--rounds N] [--saves DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BEFORE=() SAVES="" ROUNDS=3
while [ $# -gt 0 ]; do
    case "$1" in
        --before) BEFORE+=("${2:-}"); shift 2 ;;
        --rounds) ROUNDS="${2:-}"; shift 2 ;;
        --saves)  SAVES="${2:-}"; shift 2 ;;
        *) echo "hold-length: unknown argument $1" >&2; exit 2 ;;
    esac
done
[ "${#BEFORE[@]}" -gt 0 ] || { echo "hold-length: --before needs the build-before runs' cx.log" >&2; exit 2; }
if [ -z "$SAVES" ]; then
    USERDIR="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR)" || exit 2
    SAVE="$USERDIR/saves/000000 - quicksave"
    grep -q Waterdeep "$SAVE/savenfo.txt" 2>/dev/null || { echo "hold-length: $SAVE is not the docks save (savenfo.txt)" >&2; exit 2; }
    SAVES="$(mktemp -d "${TMPDIR:-/tmp}/cx-hold-length-saves-XXXX")"
    trap 'rm -rf "$SAVES"' EXIT
    cp -a "$SAVE" "$SAVES/" || exit 2
fi
# The log writes seconds in the machine's locale, so "19,7" as often as "19.7".
held() { grep -aoP "LightmapBake: hold released after \K[0-9]+[.,][0-9]+(?= s)" "$1" | tail -1 | tr , .; }
median() { sort -g | awk '{ v[NR] = $1 } END { print (NR % 2) ? v[(NR + 1) / 2] : (v[NR / 2] + v[NR / 2 + 1]) / 2 }'; }
was=()
for log in "${BEFORE[@]}"; do
    s="$(held "$log")"
    [ -n "$s" ] || { echo "hold-length: $log has no hold release line" >&2; exit 2; }
    was+=("$s")
done
now=()
for _ in $(seq "$ROUNDS"); do
    before="$(date +%s)"
    "$ROOT/tools/live/perf-save.sh" --saves "$SAVES" --soak 30 --label holdlen >/dev/null
    rc=$?
    [ "$rc" = 0 ] || { echo "hold-length: perf-save.sh exited $rc" >&2; exit 2; }
    OUT="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name 'cx-perf-holdlen-*' -newermt "@$before" | sort | tail -1)"
    s="$(held "$OUT/cx.log" 2>/dev/null)"
    [ -n "$s" ] || { echo "hold-length: the run left no hold release line ($OUT)" >&2; exit 2; }
    now+=("$s")
done
a="$(printf '%s\n' "${now[@]}" | median)" b="$(printf '%s\n' "${was[@]}" | median)"
if awk -v a="$a" -v b="$b" 'BEGIN { exit !(a <= b) }'; then echo "PASS: the hold lifted after a median $a s (${now[*]}), the build before $b s (${was[*]})"
else echo "FAIL: the hold lifted after a median $a s (${now[*]}), later than the build before's $b s (${was[*]})"; exit 1; fi
