#!/usr/bin/env python3
"""Reads a perf-save run's cx.log: the steady-state frame, GPU and zone averages.

Usage: perf_save_grade.py <cx.log> <label> <harness exit> <out dir>"""
import re, sys, statistics as st
log, label, rc, out = sys.argv[1:]
num = lambda s: float(s.replace(',', '.'))
frames, gpus, zones, over = [], [], [], 0
try:
    lines = open(log, encoding='utf-8', errors='replace').read().splitlines()
except OSError:
    sys.exit(f"perf-save: no cx.log ({out}); harness exit {rc}")
for l in lines:
    if 'UiHostService: [frame]' in l and (m := re.search(r'avg ([\d.,]+)ms', l)):
        frames.append(num(m.group(1)))
        if (e := re.search(r'OVER BUDGET by (\d+)MiB', l)): over = max(over, int(e.group(1)))
    elif '[vkgpu]' in l and (m := re.search(r'\[vkgpu\] ([\d.,]+) ms GPU per frame', l)):
        gpus.append(num(m.group(1)))
    elif 'FrameZones: [zones]' in l:
        zones.append({k: num(v) for k, v in re.findall(r'(\w+):(-?[\d.,]+)', l.split('[zones]')[1].split(' || worst')[0])})
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
if over:
    print(f"  VRAM OVER BUDGET by up to {over} MiB: the card was oversubscribed (other processes hold VRAM) -- not comparable to a run that fits")
print(f"  out {out}  (shot.png, cx.log)  harness exit {rc}")
