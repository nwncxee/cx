#!/usr/bin/env bash
# Approved examples (approved-spec) for "The area's pipelines are built under the loading hold", on the real client:
# the docks quicksave loaded by perf-save.sh, whose throwaway home starts with an empty shader and pipeline cache,
# graded from the cx.log it leaves. Exit 0 PASS, 1 FAIL, 2 when the run itself could not be made.
#
#   tools/live/prewarm_approved_examples.sh [--saves DIR]   # DIR holds the docks save folder alone
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SAVES=""
if [ "${1:-}" = "--saves" ]; then
    SAVES="${2:-}"
    [ -d "$SAVES" ] || { echo "prewarm: --saves needs a folder holding the docks save" >&2; exit 2; }
fi
if [ -z "$SAVES" ]; then
    USERDIR="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR 2>/dev/null || echo "$HOME/.local/share/Neverwinter Nights")"
    SAVE="$USERDIR/saves/000000 - quicksave"
    grep -q Waterdeep "$SAVE/savenfo.txt" 2>/dev/null || { echo "prewarm: $SAVE is not the docks save (savenfo.txt)" >&2; exit 2; }
    SAVES="$(mktemp -d "${TMPDIR:-/tmp}/cx-prewarm-saves-XXXX")"
    cp -a "$SAVE" "$SAVES/" || exit 2
fi
before="$(date +%s)"
"$ROOT/tools/live/perf-save.sh" --saves "$SAVES" --soak 30 --label prewarm >/dev/null
rc=$?
[ "$rc" = 3 ] && { echo "prewarm: a client is already running" >&2; exit 2; }
OUT="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name 'cx-perf-prewarm-*' -newermt "@$before" | sort | tail -1)"
LOG="$OUT/cx.log"
[ -f "$LOG" ] || { echo "prewarm: no cx.log from the run ($OUT)" >&2; exit 2; }
grep -q "LightmapBake: hold released" "$LOG" || { echo "prewarm: the area never left its loading screen ($LOG)" >&2; exit 2; }

# Given an empty shader and pipeline cache
# When the player enters the docks and the loading screen lifts
# Then cx.log counts zero pipelines built on the frame for the area's own materials in the first ten seconds
lines="$(grep -a "\[vkpipe\] after the loading screen" "$LOG")"
[ -n "$lines" ] || { echo "FAIL: cx.log has no '[vkpipe] after the loading screen' line ($LOG)"; exit 1; }
bad="$(grep -Ev " 0 material pipelines built on the frame in the first 10 s" <<<"$lines")"
[ -z "$bad" ] || { echo "FAIL: the area's materials still built pipelines on the frame after the loading screen:"; echo "$bad"; exit 1; }
echo "PASS: $(wc -l <<<"$lines") area entry(ies), no material pipeline built on the frame in the first 10 s ($LOG)"
