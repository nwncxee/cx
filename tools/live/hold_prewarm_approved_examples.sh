#!/usr/bin/env bash
# Approved examples (approved-spec) for "Grass, particles, the probe and CX windows build under the loading hold", on
# the real client: the docks quicksave loaded by perf-save.sh, whose throwaway home starts with an empty shader and
# pipeline cache, graded from the cx.log it leaves. Exit 0 PASS, 1 FAIL, 2 when a run itself could not be made.
#
#   tools/live/hold_prewarm_approved_examples.sh [--saves DIR] [--only 1|2]   # DIR holds the docks save folder alone
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SAVES="" ONLY=""
while [ $# -gt 0 ]; do
    case "$1" in
        --saves) SAVES="${2:-}"; shift 2 ;;
        --only)  ONLY="${2:-}"; shift 2 ;;
        *) echo "hold-prewarm: unknown argument $1" >&2; exit 2 ;;
    esac
done
if [ -n "$SAVES" ]; then
    [ -d "$SAVES" ] || { echo "hold-prewarm: --saves needs a folder holding the docks save" >&2; exit 2; }
else
    USERDIR="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR)" || exit 2
    SAVE="$USERDIR/saves/000000 - quicksave"
    grep -q Waterdeep "$SAVE/savenfo.txt" 2>/dev/null || { echo "hold-prewarm: $SAVE is not the docks save (savenfo.txt)" >&2; exit 2; }
    SAVES="$(mktemp -d "${TMPDIR:-/tmp}/cx-hold-prewarm-saves-XXXX")"
    cp -a "$SAVE" "$SAVES/" || exit 2
fi

# The docks entered once with an empty cache and the given --cfg overrides; prints the feature lines of its cx.log.
enter_docks() {
    local label="$1"; shift
    local before rc out log
    before="$(date +%s)"
    "$ROOT/tools/live/perf-save.sh" --saves "$SAVES" --soak 30 --label "$label" "$@" >/dev/null
    rc=$?
    [ "$rc" = 3 ] && { echo "hold-prewarm: a client is already running" >&2; return 2; }
    out="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name "cx-perf-$label-*" -newermt "@$before" | sort | tail -1)"
    log="$out/cx.log"
    [ -f "$log" ] || { echo "hold-prewarm: no cx.log from the run ($out)" >&2; return 2; }
    grep -q "LightmapBake: hold released" "$log" || { echo "hold-prewarm: the area never left its loading screen ($log)" >&2; return 2; }
    local lines
    lines="$(grep -a "\[vkpipe\] by feature" "$log")"
    [ -n "$lines" ] || { echo "FAIL: cx.log has no '[vkpipe] by feature' line ($log)"; return 1; }
    echo "$lines"
}

# The sum over feature lines $1 of feature $2's count: "$3" is "under the hold" or "on the frame"; empty when a line
# names no count for it.
total() {
    local sum=0 line n
    while IFS= read -r line; do
        n="$(count "$line" "$2" "$3")"
        [ -n "$n" ] || return 0
        sum=$((sum + n))
    done <<<"$1"
    echo "$sum"
}

# The count for feature $2 in feature line $1: "$3" is "under the hold" or "on the frame".
count() {
    if [ "$3" = "under the hold" ]; then grep -oP "(?<=[:;] )$2 \K\d+(?= under the hold)" <<<"$1" | head -1
    else grep -oP "(?<=[:;] )$2 \d+ under the hold, \K\d+(?= on the frame)" <<<"$1" | head -1; fi
}

status=0
FEATURES=("grass" "particles" "environment probe" "CX windows")

if [ -z "$ONLY" ] || [ "$ONLY" = 1 ]; then
    # Given an empty shader and pipeline cache
    # When the player enters the docks and the loading screen lifts
    # Then cx.log counts zero grass, particle, environment probe and CX window pipelines built on the frame in the first ten seconds
    line="$(enter_docks holdpre1)"; rc=$?
    if [ "$rc" != 0 ]; then [ "$rc" = 2 ] && exit 2; echo "$line"; status=1
    else
        for f in "${FEATURES[@]}"; do
            held="$(total "$line" "$f" "under the hold")"; frame="$(total "$line" "$f" "on the frame")"
            [ -n "$held" ] && [ -n "$frame" ] || { echo "FAIL: the feature line names no count for $f: $line"; status=1; continue; }
            [ $((held + frame)) -gt 0 ] \
                || { echo "hold-prewarm: the docks built no $f pipeline at all, so this run proves nothing: $line" >&2; exit 2; }
            [ "$frame" = 0 ] || { echo "FAIL: $frame $f pipeline(s) built on the frame after the loading screen: $line"; status=1; }
        done
        [ "$status" = 0 ] && echo "PASS 1: no grass, particle, probe or CX window pipeline built on the frame after the loading screen"
    fi
fi

if [ -z "$ONLY" ] || [ "$ONLY" = 2 ]; then
    # Given grass switched off in the settings, and an empty shader and pipeline cache
    # When the player enters the docks
    # Then cx.log counts no grass pipeline built, under the loading hold or after it
    line="$(enter_docks holdpre2 --cfg 'grass.enabled=false')"; rc=$?
    if [ "$rc" != 0 ]; then [ "$rc" = 2 ] && exit 2; echo "$line"; status=1
    else
        held="$(total "$line" grass "under the hold")"; frame="$(total "$line" grass "on the frame")"
        if [ -z "$held" ] || [ -z "$frame" ]; then echo "FAIL: the feature line names no count for grass: $line"; status=1
        elif [ "$held" != 0 ] || [ "$frame" != 0 ]; then echo "FAIL: grass is off, yet $held grass pipeline(s) built under the hold and $frame after it: $line"; status=1
        else echo "PASS 2: grass off, no grass pipeline built under the hold or after it"; fi
    fi
fi
exit "$status"
