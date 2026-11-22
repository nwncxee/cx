#!/usr/bin/env python3
"""The ladder's revisit grade, read off a session's cx.log: the first area's GPU time per frame on the first visit
against the return to it, the area visits split at TexStream's [load] lines. Exit 0 PASS, 1 FAIL.
Usage: python3 -I tools/live/revisit.py <cx.log>"""
import re
import statistics
import sys

# the marker a FAIL line carries when the session measured no GPU time: what a caller matches, never the prose
NO_GPU_TIME = "[revisit] no-gpu-time"

# how much slower than the first visit the return to the first area may draw before the run fails
SLOWER = 1.5

LOAD = re.compile(r"TexStream: \[load\] ")
GPU = re.compile(r"\[vkgpu\] (\d+(?:[.,]\d+)?) ms GPU per frame")


def visits(lines):
    """The GPU ms per frame of each reporting period, one list per area visit; periods before the first load are
    the main menu's and belong to no visit."""
    out = None
    for line in lines:
        if LOAD.search(line):
            out = (out or []) + [[]]
        elif out and (m := GPU.search(line)):
            out[-1].append(float(m.group(1).replace(",", ".")))
    return out or []


def grade(lines):
    """(passed, the one line that says why)."""
    seen = visits(lines)
    if not any(seen):
        return False, f"FAIL {NO_GPU_TIME}: no [vkgpu] line in the session, so Alloy drew nothing to measure"
    if len(seen) < 3:
        return False, f"FAIL [revisit] the session made {len(seen)} area visits, not the ladder's three (first area, second, back)"
    first, back = seen[0], seen[-1]
    if not first or not back:
        which = "first visit" if not first else "return to the first area"
        return False, f"FAIL {NO_GPU_TIME}: the {which} logged no [vkgpu] period"
    a, b = statistics.median(first), statistics.median(back)
    if b > a * SLOWER:
        return False, f"FAIL [revisit] the return to the first area draws {b:.2f} ms GPU per frame, more than {SLOWER}x the first visit's {a:.2f} ms"
    return True, f"PASS [revisit] the return to the first area draws {b:.2f} ms GPU per frame, within {SLOWER}x the first visit's {a:.2f} ms"


if __name__ == "__main__":
    if len(sys.argv) != 2:
        sys.exit(__doc__)
    with open(sys.argv[1], encoding="utf-8", errors="replace") as f:
        ok, why = grade(f.read().splitlines())
    print(why)
    sys.exit(0 if ok else 1)
