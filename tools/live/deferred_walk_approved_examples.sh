#!/usr/bin/env bash
# Approved examples (approved-spec) for the deferred lights walk, on the real client: the docks quicksave loaded by
# perf-save.sh from a base checkout built at main 65203c28 and from this one, alternating (deferred_walk_ab.sh).
# "Less" is a paired difference two standard errors clear of zero. Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/deferred_walk_approved_examples.sh --base DIR --saves DIR [--rounds N]
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

# Given the Waterdeep docks quicksave at 2544x1378 on the RTX 3070, facing the lit quay
# When the frame draws
# Then cx.log reports less GPU time per frame than the previous build (main 65203c28) on the same save and view
out="$("$ROOT/tools/live/deferred_walk_ab.sh" --base "$BASE" --saves "$SAVES" --label dwquay --rounds "$ROUNDS")" || exit 2
echo "$out"
read -r _ mean sd n <<<"$(tail -1 <<<"$out")"
if python3 -c "import math,sys; m,s,n=$mean,$sd,$n; sys.exit(0 if m > 0 and m > 2*s/math.sqrt(n) else 1)"; then
    echo "PASS: the lit quay costs $mean ms less GPU per frame than main 65203c28 (sd $sd over $n rounds)"
else
    echo "FAIL: the lit quay is not clearly cheaper than main 65203c28: base - this = $mean ms (sd $sd over $n rounds)"
    exit 1
fi
