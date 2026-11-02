#!/usr/bin/env bash
# Regression test for the deferred lights walk (it holds on main 65203c28, so it carries no approved-spec mark): the
# docks quicksave facing the horizon, from a base checkout built at that commit and from this one, alternating
# (deferred_walk_ab.sh). "No more" allows two standard errors of the paired difference plus 0.05 ms.
# Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/deferred_walk_horizon.sh --base DIR --saves DIR [--rounds N]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BASE=""; SAVES=""; ROUNDS=3
while [ $# -gt 0 ]; do
    case "$1" in
        --base) BASE="$2"; shift 2 ;;
        --saves) SAVES="$2"; shift 2 ;;
        --rounds) ROUNDS="$2"; shift 2 ;;
        *) echo "deferred-walk: unknown option $1" >&2; exit 2 ;;
    esac
done
[ "$(git -C "$BASE" rev-parse --short=8 HEAD 2>/dev/null)" = 65203c28 ] \
    || { echo "deferred-walk: --base must be a checkout of main 65203c28 with its cx-linux-x64/ built" >&2; exit 2; }
grep -q Waterdeep "$SAVES"/*/savenfo.txt 2>/dev/null || { echo "deferred-walk: --saves must hold the docks save alone" >&2; exit 2; }

# Given the Waterdeep docks quicksave facing the horizon, with few lamps in view
# When the frame draws
# Then cx.log reports no more GPU time per frame than the previous build on the same save and view
out="$("$ROOT/tools/live/deferred_walk_ab.sh" --base "$BASE" --saves "$SAVES" --label dwhorizon --rounds "$ROUNDS" \
        --keys "key:KP_Multiply;sleep:500;hold:Next:900;sleep:1500")" || exit 2
echo "$out"
read -r _ mean sd n <<<"$(tail -1 <<<"$out")"
if python3 -c "import math,sys; m,s,n=$mean,$sd,$n; sys.exit(0 if -m <= 2*s/math.sqrt(n) + 0.05 else 1)"; then
    echo "PASS: facing the horizon this build costs $(python3 -c "print(f'{-$mean:.3f}')") ms GPU per frame against main 65203c28 (sd $sd over $n rounds)"
else
    echo "FAIL: facing the horizon this build costs $(python3 -c "print(f'{-$mean:.3f}')") ms more GPU per frame than main 65203c28 (sd $sd over $n rounds)"
    exit 1
fi
