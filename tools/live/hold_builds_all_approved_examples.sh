#!/usr/bin/env bash
# Approved examples (approved-spec) for "the frame builds nothing after the loading screen", on the real client: the
# docks quicksave loaded by perf-save.sh, whose throwaway home starts with an empty shader and pipeline cache, graded
# from the [vkpipe] after-the-loading-screen line of the cx.log it leaves. Exit 0 PASS, 1 FAIL, 2 when the run could
# not be made.
#
#   tools/live/hold_builds_all_approved_examples.sh [--saves DIR]   # DIR holds the docks save folder alone
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SAVES=""
if [ "${1:-}" = "--saves" ]; then
    SAVES="${2:-}"
    [ -d "$SAVES" ] || { echo "hold-builds-all: --saves needs a folder holding the docks save" >&2; exit 2; }
else
    USERDIR="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR)" || exit 2
    SAVE="$USERDIR/saves/000000 - quicksave"
    grep -q Waterdeep "$SAVE/savenfo.txt" 2>/dev/null || { echo "hold-builds-all: $SAVE is not the docks save (savenfo.txt)" >&2; exit 2; }
    SAVES="$(mktemp -d "${TMPDIR:-/tmp}/cx-hold-builds-all-saves-XXXX")"
    trap 'rm -rf "$SAVES"' EXIT
    cp -a "$SAVE" "$SAVES/" || exit 2
fi
before="$(date +%s)"
"$ROOT/tools/live/perf-save.sh" --saves "$SAVES" --soak 30 --label holdall >/dev/null
rc=$?
[ "$rc" = 0 ] || { echo "hold-builds-all: perf-save.sh exited $rc" >&2; exit 2; }
OUT="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name 'cx-perf-holdall-*' -newermt "@$before" | sort | tail -1)"
LOG="$OUT/cx.log"
grep -q "LightmapBake: hold released" "$LOG" 2>/dev/null || { echo "hold-builds-all: the area never left its loading screen ($OUT)" >&2; exit 2; }

# Given an empty shader and pipeline cache
# When the player enters the docks and the loading screen lifts
# Then cx.log counts no pipeline of any kind built on the frame in the first ten seconds
lines="$(grep -a "\[vkpipe\] after the loading screen" "$LOG")"
[ -n "$lines" ] || { echo "FAIL: cx.log has no '[vkpipe] after the loading screen' line ($LOG)"; exit 1; }
bad="$(grep -Ev ": 0 material pipelines built on the frame in the first 10 s; 0 other pipelines" <<<"$lines")"
[ -z "$bad" ] || { echo "FAIL: the frame still built pipelines after the loading screen:"; echo "$bad"; exit 1; }
echo "PASS: $(wc -l <<<"$lines") area entry(ies), no pipeline of any kind built on the frame in the first 10 s ($LOG)"
