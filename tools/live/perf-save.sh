#!/usr/bin/env bash
# Frame-rate run on a saved game: boots the real client headless (run-live.sh --menu), loads the save through
# the Load panel, soaks, and prints the steady-state frame, GPU and zone averages from the CX log.
#
#   tools/live/perf-save.sh --saves DIR [--soak N] [--label NAME] [--cfg 'key=value' ...]
#   --keys SCRIPT runs (xtool grammar) once the area has settled, e.g. "key:Return;type:/alloy bench;key:Return"
#
# DIR holds ONE save folder (it is preselected on the Load panel). The user's real cx.cfg is passed through so
# knobs match play; each --cfg replaces that key. Click points are pixel constants for 2544x1378 at the
# copied settings.tml's UI scale. Steady state = every reporting period after the first two in the area.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SAVES=""; SOAK=90; LABEL="run"; OVERRIDES=(); EXTRA=""
while [ $# -gt 0 ]; do
    case "$1" in
        --saves) SAVES="$2"; shift 2 ;;
        --soak)  SOAK="$2"; shift 2 ;;
        --label) LABEL="$2"; shift 2 ;;
        --cfg)   OVERRIDES+=("$2"); shift 2 ;;
        --keys)  EXTRA="$2;"; shift 2 ;;
        *) echo "perf-save: unknown option $1" >&2; exit 2 ;;
    esac
done
[ -d "$SAVES" ] || { echo "perf-save: --saves DIR required" >&2; exit 2; }
pgrep -x nwmain-linux >/dev/null && { echo "perf-save: a client is running" >&2; exit 3; }

USER_CFG="$(python3 "$ROOT/tools/paths.py" NWN_USERDIR 2>/dev/null || echo "$HOME/.local/share/Neverwinter Nights")/cx.cfg"
declare -A over=()
for o in ${OVERRIDES+"${OVERRIDES[@]}"}; do over["${o%%=*}"]=1; done
args=()
if [ -f "$USER_CFG" ]; then
    while IFS= read -r l; do
        [[ -z "$l" || "$l" == \#* ]] && continue
        [ -n "${over["${l%%=*}"]+x}" ] && continue
        args+=(--cfg "$l")
    done < "$USER_CFG"
fi
for o in ${OVERRIDES+"${OVERRIDES[@]}"}; do args+=(--cfg "$o"); done

OUT="${TMPDIR:-/tmp}/cx-perf-$LABEL-$(date +%H%M%S)"
half=$(( SOAK * 500 ))
"$ROOT/tools/live/run-live.sh" --menu --saves "$SAVES" --width 2544 --height 1378 --seconds 6 --out "$OUT" --keep \
    ${args+"${args[@]}"} \
    --keys "click:1269,705;sleep:3000;click:1410,914;sleep:20000;${EXTRA}sleep:$half;shot:$OUT/shot.png;sleep:$half" \
    > "$OUT.harness.log" 2>&1
rc=$?

python3 - "$OUT/cx.log" "$LABEL" "$rc" "$OUT" <<'PY'
import re, sys, statistics as st
log, label, rc, out = sys.argv[1:]
num = lambda s: float(s.replace(',', '.'))
frames, gpus, zones = [], [], []
try:
    lines = open(log, encoding='utf-8', errors='replace').read().splitlines()
except OSError:
    sys.exit(f"perf-save: no cx.log ({out}); harness exit {rc}")
for l in lines:
    if 'UiHostService: [frame]' in l and (m := re.search(r'avg ([\d,]+)ms', l)):
        frames.append(num(m.group(1)))
    elif 'gpu terms total' in l:
        gpus.append(num(re.search(r'gpu terms total ([\d,]+)', l).group(1)))
    elif 'FrameZones: [zones]' in l:
        zones.append({k: num(v) for k, v in re.findall(r'(\w+):(-?[\d,]+)', l.split('[zones]')[1].split(' || worst')[0])})
# The first periods cover the menu and the load; keep only the last periods that follow it.
steady = lambda xs: xs[-max(1, min(len(xs) - 2, 8)):] if xs else []
f, g, z = steady(frames), steady(gpus), steady(zones)
if not f:
    sys.exit(f"perf-save: no frame periods in {log}; harness exit {rc}")
fm = st.median(f)
gpu = f"  gpu {st.median(g):.2f} ms" if g else ""
print(f"[{label}] frame {fm:.2f} ms ({1000/fm:.0f} fps, spread {min(f):.2f}..{max(f):.2f}, n={len(f)}){gpu}")
if z:
    keys = sorted(z[0], key=lambda k: -st.mean(d.get(k, 0) for d in z))
    print("  zones " + " ".join(f"{k}:{st.mean(d.get(k,0) for d in z):.2f}" for k in keys if st.mean(d.get(k,0) for d in z) >= 0.15))
print(f"  out {out}  (shot.png, cx.log)  harness exit {rc}")
PY
