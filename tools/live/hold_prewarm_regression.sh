#!/usr/bin/env bash
# Regression checks, not approved tests: two CX-436 examples approved after the change that made them hold, so they
# could not go red first. On the docks quicksave with an empty cache, graded from the [vkpipe] by feature lines:
#   1. Given an empty shader and pipeline cache, when the player enters the docks, then cx.log counts at least one
#      CX window pipeline built under the loading hold.
#   2. Given particles switched off in the settings and an empty cache, when the player enters the docks, then cx.log
#      counts no particle pipeline built, under the loading hold or after it.
# Exit 0 PASS, 1 FAIL, 2 when a run itself could not be made.
#
#   tools/live/hold_prewarm_regression.sh [--saves DIR] [--only 1|2]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOCKS_WHO=hold-regression
. "$ROOT/tools/live/docks_run.sh"
SAVES="" ONLY=""
while [ $# -gt 0 ]; do
    case "$1" in
        --saves) SAVES="$(docks_saves_arg "${2:-}")" || exit 2; shift 2 ;;
        --only)  ONLY="${2:-}"; shift 2 ;;
        *) echo "hold-regression: unknown argument $1" >&2; exit 2 ;;
    esac
done
docks_saves "$SAVES" || exit 2

# The by-feature lines of one docks run with the given --cfg overrides.
enter_docks() {
    local label="$1"; shift
    local out
    out="$(docks_run "$label" "$@")" || return 2
    grep -a "\[vkpipe\] by feature" "$out/cx.log" || { echo "FAIL: cx.log has no '[vkpipe] by feature' line ($out)"; return 1; }
}

# The sum over lines $1 of feature $2's count "under the hold" or "on the frame" ($3).
total() {
    local sum=0 line n
    while IFS= read -r line; do
        if [ "$3" = "under the hold" ]; then n="$(grep -oP "(?<=[:;] )$2 \K\d+(?= under the hold)" <<<"$line" | head -1)"
        else n="$(grep -oP "(?<=[:;] )$2 \d+ under the hold, \K\d+(?= on the frame)" <<<"$line" | head -1)"; fi
        [ -n "$n" ] || return 0
        sum=$((sum + n))
    done <<<"$1"
    echo "$sum"
}

status=0
if [ -z "$ONLY" ] || [ "$ONLY" = 1 ]; then
    lines="$(enter_docks holdreg1)"; rc=$?
    if [ "$rc" = 2 ]; then exit 2; elif [ "$rc" != 0 ]; then echo "$lines"; status=1
    else
        held="$(total "$lines" "CX windows" "under the hold")"
        if [ -z "$held" ]; then echo "FAIL 1: the feature line names no count for CX windows: $lines"; status=1
        elif [ "$held" -lt 1 ]; then echo "FAIL 1: no CX window pipeline built under the loading hold: $lines"; status=1
        else echo "PASS 1: $held CX window pipeline(s) built under the loading hold"; fi
    fi
fi
if [ -z "$ONLY" ] || [ "$ONLY" = 2 ]; then
    lines="$(enter_docks holdreg2 --cfg 'particles.enabled=false')"; rc=$?
    if [ "$rc" = 2 ]; then exit 2; elif [ "$rc" != 0 ]; then echo "$lines"; status=1
    else
        held="$(total "$lines" particles "under the hold")"; frame="$(total "$lines" particles "on the frame")"
        if [ -z "$held" ] || [ -z "$frame" ]; then echo "FAIL 2: the feature line names no count for particles: $lines"; status=1
        elif [ "$held" != 0 ] || [ "$frame" != 0 ]; then echo "FAIL 2: particles are off, yet $held particle pipeline(s) built under the hold and $frame after it: $lines"; status=1
        else echo "PASS 2: particles off, no particle pipeline built under the hold or after it"; fi
    fi
fi
exit "$status"
