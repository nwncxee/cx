#!/usr/bin/env bash
# The A/B behind the deferred walk's live examples: perf-save.sh on the docks save from a BASE checkout of main
# 65203c28 (its staged cx-linux-x64/ built) and from this one, alternating, ROUNDS times, each run's figure the GPU
# time per frame perf-save.sh reads off cx.log's [vkgpu] lines. The verdict is on the paired difference base - this:
# "less" passes when it is two standard errors clear of zero, "no-worse" when the build is at most two standard
# errors plus 0.05 ms slower. Exit 0 PASS, 1 FAIL, 2 when a run could not be made or is not comparable.
#
#   tools/live/deferred_walk_ab.sh --base DIR --saves DIR --label NAME --expect less|no-worse [--keys SCRIPT] [--rounds N]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BASE=""; SAVES=""; LABEL=""; KEYS=""; ROUNDS=3; EXPECT=""
while [ $# -gt 0 ]; do
    case "$1" in
        --base) BASE="$2"; shift 2 ;;
        --saves) SAVES="$2"; shift 2 ;;
        --label) LABEL="$2"; shift 2 ;;
        --keys) KEYS="$2"; shift 2 ;;
        --rounds) ROUNDS="$2"; shift 2 ;;
        --expect) EXPECT="$2"; shift 2 ;;
        *) echo "deferred-walk-ab: unknown option $1" >&2; exit 2 ;;
    esac
done
[ "$(git -C "$BASE" rev-parse --short=8 HEAD 2>/dev/null)" = 65203c28 ] && [ -f "$BASE/cx-linux-x64/cx_managed.so" ] \
    || { echo "deferred-walk-ab: --base must be a checkout of main 65203c28 with its cx-linux-x64/ built" >&2; exit 2; }
[ -f "$ROOT/cx-linux-x64/cx_managed.so" ] || { echo "deferred-walk-ab: this checkout has no built cx-linux-x64/" >&2; exit 2; }
grep -q Waterdeep "$SAVES"/*/savenfo.txt 2>/dev/null || { echo "deferred-walk-ab: --saves must hold the docks save alone" >&2; exit 2; }
case "$EXPECT" in less|no-worse) ;; *) echo "deferred-walk-ab: --expect less or no-worse" >&2; exit 2 ;; esac
[ -n "$LABEL" ] || { echo "deferred-walk-ab: --label NAME is required" >&2; exit 2; }

# One perf-save run from checkout $1, labelled $2; echoes its GPU ms per frame, or fails.
run() {
    local out rc gpu
    if [ -n "$KEYS" ]; then out="$("$1/tools/live/perf-save.sh" --saves "$SAVES" --label "$2" --keys "$KEYS")"
    else out="$("$1/tools/live/perf-save.sh" --saves "$SAVES" --label "$2")"; fi
    rc=$?
    [ "$rc" = 3 ] && { echo "deferred-walk-ab: a client is already running" >&2; return 1; }
    if grep -q "VRAM OVER BUDGET" <<<"$out"; then echo "deferred-walk-ab: run $2 oversubscribed VRAM -- not comparable" >&2; return 1; fi
    # read off the run's own cx.log by perf-save's steady rule: the base checkout's perf-save predates its gpu figure
    gpu="$(python3 - "$(sed -n 's/^  out \([^ ]*\) .*/\1/p' <<<"$out")/cx.log" <<'PY'
import re, statistics, sys
try: lines = open(sys.argv[1], encoding="utf-8", errors="replace").read().splitlines()
except OSError: sys.exit(0)
g = [float(m.group(1).replace(",", ".")) for l in lines if (m := re.search(r"\[vkgpu\] ([\d,]+) ms GPU per frame", l))]
g = g[-max(1, min(len(g) - 2, 8)):] if g else []
if g: print(f"{statistics.median(g):.3f}")
PY
)"
    [ -n "$gpu" ] || { echo "deferred-walk-ab: run $2 reported no GPU time:" >&2; echo "$out" >&2; return 1; }
    echo "$gpu"
}

pairs=()
for r in $(seq 1 "$ROUNDS"); do
    b="$(run "$BASE" "dw-base-$r")" || exit 2
    t="$(run "$ROOT" "dw-this-$r")" || exit 2
    echo "round $r: base $b ms, this $t ms GPU per frame"
    pairs+=("$b" "$t")
done
python3 - "$EXPECT" "$LABEL" "${pairs[@]}" <<'PY'
import math, statistics, sys
expect, label, xs = sys.argv[1], sys.argv[2], [float(x) for x in sys.argv[3:]]
d = [b - t for b, t in zip(xs[::2], xs[1::2])]
m, sd, n = statistics.mean(d), statistics.stdev(d) if len(d) > 1 else 0.0, len(d)
se2 = 2 * sd / math.sqrt(n)
ok = m > se2 if expect == "less" else -m <= se2 + 0.05
print(f"{'PASS' if ok else 'FAIL'}: {label} costs {m:.3f} ms less GPU per frame than main 65203c28 "
      f"(sd {sd:.3f} over {n} rounds, expected {expect})")
sys.exit(0 if ok else 1)
PY
