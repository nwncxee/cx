#!/usr/bin/env bash
# The A/B behind the deferred walk's live examples: perf-save.sh on the docks save from a BASE checkout (its staged
# cx-linux-x64/ built from the commit it is checked out at) and from this one, alternating, ROUNDS times. Each run's
# figure is the median "[vkgpu] N ms GPU per frame" in its cx.log over the steady periods (every one after the first
# two once the loading hold lifts). Prints one line per run, then the paired difference base - this.
# Exit 0 with "AB <mean> <sd> <n>" as the last line, 2 when a run could not be made or is not comparable.
#
#   tools/live/deferred_walk_ab.sh --base DIR --saves DIR --label NAME [--keys SCRIPT] [--rounds N]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
BASE=""; SAVES=""; LABEL=""; KEYS=""; ROUNDS=3
while [ $# -gt 0 ]; do
    case "$1" in
        --base) BASE="$2"; shift 2 ;;
        --saves) SAVES="$2"; shift 2 ;;
        --label) LABEL="$2"; shift 2 ;;
        --keys) KEYS="$2"; shift 2 ;;
        --rounds) ROUNDS="$2"; shift 2 ;;
        *) echo "deferred-walk-ab: unknown option $1" >&2; exit 2 ;;
    esac
done
[ -x "$BASE/tools/live/perf-save.sh" ] && [ -f "$BASE/cx-linux-x64/cx_managed.so" ] \
    || { echo "deferred-walk-ab: --base needs a checkout with a built cx-linux-x64/" >&2; exit 2; }
[ -f "$ROOT/cx-linux-x64/cx_managed.so" ] || { echo "deferred-walk-ab: this checkout has no built cx-linux-x64/" >&2; exit 2; }
[ -d "$SAVES" ] && [ -n "$LABEL" ] || { echo "deferred-walk-ab: --saves DIR and --label NAME are required" >&2; exit 2; }

# One perf-save run from checkout $1, labelled $2; echoes its steady median GPU ms, or nothing.
run() {
    local before out
    before="$(date +%s)"
    if [ -n "$KEYS" ]; then "$1/tools/live/perf-save.sh" --saves "$SAVES" --label "$2" --keys "$KEYS" >/dev/null
    else "$1/tools/live/perf-save.sh" --saves "$SAVES" --label "$2" >/dev/null; fi
    [ $? = 3 ] && { echo "deferred-walk-ab: a client is already running" >&2; return 1; }
    out="$(find "${TMPDIR:-/tmp}" -maxdepth 1 -type d -name "cx-perf-$2-*" -newermt "@$before" | sort | tail -1)"
    [ -f "$out/cx.log" ] || { echo "deferred-walk-ab: no cx.log from run $2" >&2; return 1; }
    if grep -aq "VRAM EVICTED" "$out/cx.log"; then echo "deferred-walk-ab: run $2 had VRAM evicted -- not comparable ($out)" >&2; return 1; fi
    python3 - "$out/cx.log" <<'PY'
import re, statistics, sys
lines = open(sys.argv[1], encoding="utf-8", errors="replace").read().splitlines()
start = next((i for i, l in enumerate(lines) if "LightmapBake: hold released" in l), None)
if start is None: sys.exit(0)
g = [float(m.group(1).replace(",", ".")) for l in lines[start:]
     if (m := re.search(r"\[vkgpu\] ([\d,.]+) ms GPU per frame", l))][2:]
if len(g) >= 3: print(f"{statistics.median(g):.3f}")
PY
}

diffs=()
for r in $(seq 1 "$ROUNDS"); do
    b="$(run "$BASE" "$LABEL-base-$r")" && [ -n "$b" ] || { echo "deferred-walk-ab: base round $r gave no figure" >&2; exit 2; }
    t="$(run "$ROOT" "$LABEL-this-$r")" && [ -n "$t" ] || { echo "deferred-walk-ab: this round $r gave no figure" >&2; exit 2; }
    echo "round $r: base $b ms, this $t ms GPU per frame"
    diffs+=("$(python3 -c "print(f'{$b - $t:.3f}')")")
done
python3 - "${diffs[@]}" <<'PY'
import statistics, sys
d = [float(x) for x in sys.argv[1:]]
print(f"AB {statistics.mean(d):.3f} {statistics.stdev(d) if len(d) > 1 else 0.0:.3f} {len(d)}")
PY
