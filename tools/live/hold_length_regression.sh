#!/usr/bin/env bash
# Regression check, not an approved test: it held before the change it guards, so it cannot go red first. The
# example (CX-436): given an empty shader and pipeline cache, when the player enters the docks, then the loading
# screen lifts no later than it does on the build before this change. Grades the docks run's hold against the
# cx.log of the same run on the build before. Exit 0 PASS, 1 FAIL, 2 when the run itself could not be made.
#
#   tools/live/hold_length_regression.sh --before LOG [--saves DIR]   # LOG: perf-save's cx.log on the build before
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BEFORE="" SAVES=""
while [ $# -gt 0 ]; do
    case "$1" in
        --before) BEFORE="${2:-}"; shift 2 ;;
        --saves)  SAVES="${2:-}"; shift 2 ;;
        *) echo "hold-length: unknown argument $1" >&2; exit 2 ;;
    esac
done
[ -f "$BEFORE" ] || { echo "hold-length: --before needs the build-before run's cx.log" >&2; exit 2; }
if [ -z "$SAVES" ]; then
    USERDIR="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR 2>/dev/null || echo "$HOME/.local/share/Neverwinter Nights")"
    SAVE="$USERDIR/saves/000000 - quicksave"
    grep -q Waterdeep "$SAVE/savenfo.txt" 2>/dev/null || { echo "hold-length: $SAVE is not the docks save (savenfo.txt)" >&2; exit 2; }
    SAVES="$(mktemp -d "${TMPDIR:-/tmp}/cx-hold-length-saves-XXXX")"
    cp -a "$SAVE" "$SAVES/" || exit 2
fi
# The log writes seconds in the machine's locale, so "19,7" as often as "19.7".
held() { grep -aoP "LightmapBake: hold released after \K[0-9]+[.,][0-9]+(?= s)" "$1" | tail -1 | tr , .; }
was="$(held "$BEFORE")"
[ -n "$was" ] || { echo "hold-length: $BEFORE has no hold release line" >&2; exit 2; }
before="$(date +%s)"
"$ROOT/tools/live/perf-save.sh" --saves "$SAVES" --soak 30 --label holdlen >/dev/null
[ $? = 3 ] && { echo "hold-length: a client is already running" >&2; exit 2; }
OUT="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name 'cx-perf-holdlen-*' -newermt "@$before" | sort | tail -1)"
now="$(held "$OUT/cx.log" 2>/dev/null)"
[ -n "$now" ] || { echo "hold-length: the run left no hold release line ($OUT)" >&2; exit 2; }
if awk -v a="$now" -v b="$was" 'BEGIN { exit !(a <= b) }'; then echo "PASS: the hold lifted after $now s, the build before after $was s"
else echo "FAIL: the hold lifted after $now s, later than the build before's $was s ($OUT/cx.log)"; exit 1; fi
