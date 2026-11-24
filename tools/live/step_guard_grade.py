#!/usr/bin/env python3
"""Grades a `--showcase stepguard` run: the server's positions (`CXSHOW: sg` lines in the hosted module's log) against
the client's (`StepGuard: at` samples in cx.log, written under general.steplog), lined up by wall clock.

    step_guard_grade.py <run dir>

Prints one shell assignment per example, SG_E<n>=1|0 and SG_E<n>_WHY='...', for step_guard_run.sh to eval. Exit 2
when the run holds too little to grade (no scene, a trial mark missing, too few samples in a window)."""
import math
import re
import shlex
import sys
from pathlib import Path

NEAR = 0.10
PAST_WALL = 0.30
ALONG_MIN = 1.0
RAM_TRIALS = 6
ALONG_TRIALS = 4

SRV = re.compile(r"^\[\w{3} \w{3} +\d+ (\d+):(\d+):(\d+)\] CXSHOW: sg (.*)$")
CLI = re.compile(r"^\d{4}-\d\d-\d\dT(\d+):(\d+):(\d+(?:\.\d+)?)\S* \[cx\] StepGuard: (.*)$")
AT = re.compile(r"^at \((-?[\d.]+),(-?[\d.]+),(-?[\d.]+)\) driving=(-?\d+) waypoints=(\d+) refused=(\d+)$")


class Setup(Exception):
    pass


def read(run):
    marks, srv = [], []
    for line in (run / "nwclientLog1.txt").read_text(errors="replace").splitlines():
        m = SRV.match(line)
        if not m:
            continue
        t = int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3])
        body = m[4]
        if body.startswith("srv "):
            x, y, _ = map(float, body.split()[1:4])
            srv.append((t, x, y))
        else:
            marks.append((t, body))
    samples, refusals = [], []
    for line in (run / "cx.log").read_text(errors="replace").splitlines():
        m = CLI.match(line)
        if not m:
            continue
        t = int(m[1]) * 3600 + int(m[2]) * 60 + float(m[3])
        a = AT.match(m[4])
        if a:
            samples.append((t, float(a[1]), float(a[2]), int(a[6])))
        elif m[4].startswith("refused "):
            refusals.append((t, m[4]))
    return marks, srv, samples, refusals


def mark(marks, prefix):
    for t, body in marks:
        if body.startswith(prefix):
            return t + 0.5, body
    raise Setup(f"no '{prefix}' mark")


def window(samples, t0, a, b, need=2):
    got = [s for s in samples if t0 + a <= s[0] <= t0 + b]
    if len(got) < need:
        raise Setup(f"{len(got)} client sample(s) between +{a}s and +{b}s")
    return got


def rest(srv, t0, a, b):
    got = sorted((x, y) for t, x, y in srv if t0 + a <= t + 0.5 <= t0 + b)
    if not got:
        raise Setup(f"no server position between +{a}s and +{b}s")
    return got[len(got) // 2]


def refused_by(samples, t):
    seen = [s[3] for s in samples if s[0] <= t]
    return seen[-1] if seen else 0


def dist(s, p):
    return math.hypot(s[1] - p[0], s[2] - p[1])


def worst(got, p):
    return max(dist(s, p) for s in got)


def srv_at(srv, t):
    return min(srv, key=lambda r: abs(r[0] + 0.5 - t))[1:]


def keyup(samples, srv, t0, up):
    """One W drive let go at t0 + up: how near the client comes to the server's rest in the 3 s after keyup, how far
    it is at worst once still, and how many steps were refused after the keyup."""
    p = rest(srv, t0, up + 3, up + 7.5)
    first = window(samples, t0, up + 0.5, up + 3, need=1)
    still = window(samples, t0, up + 4, up + 7.5)
    after = refused_by(samples, t0 + up + 7.5) - refused_by(samples, t0 + up + 2.5)
    return min(dist(s, p) for s in first), worst(still, p), after


def grade(run):
    marks, srv, samples, refusals = read(run)
    _, scene = mark(marks, "wall S ")
    f = scene.split()
    sx, sy, d, wall, side = float(f[2]), float(f[3]), math.radians(float(f[6])), float(f[8]), math.radians(float(f[10]))
    out = {}

    t0, _ = mark(marks, "wall jump")
    hold = window(samples, t0, 1, 15)
    reach = max((s[1] - sx) * math.cos(d) + (s[2] - sy) * math.sin(d) for s in hold)
    out["E2"] = (reach <= wall + PAST_WALL,
                 f"furthest client point {reach:.2f} m along the drive, the wall at {wall:.2f} m; "
                 f"{refused_by(samples, t0 + 15) - refused_by(samples, t0)} refusal(s) in the trial")
    ups = [("wall", keyup(samples, srv, t0, 8))]
    for i in range(RAM_TRIALS):
        t, _ = mark(marks, f"ram{i} jump")
        ups.append((f"ram{i}", keyup(samples, srv, t, 7)))
    late = [n for n, (near, _, _) in ups if near > NEAR]
    out["E3"] = (not late, f"{len(late)} of {len(ups)} drives never came within {NEAR} m of the server in the 3 s after "
                 f"keyup ({' '.join(n for n in late) or 'none'}); closest per drive: "
                 + " ".join(f"{n}={near:.2f}" for n, (near, _, _) in ups))
    pinned = [n for n, (_, far, after) in ups if far > NEAR or after]
    out["E4"] = (not pinned, f"{len(pinned)} of {len(ups)} drives left the client off the server at rest "
                 f"({' '.join(pinned) or 'none'}); worst per drive: "
                 + " ".join(f"{n}={far:.2f}/{after}" for n, (_, far, after) in ups))

    _, corner = mark(marks, "corner S ")
    c = corner.split()
    side = math.atan2(float(c[7]) - float(c[3]), float(c[6]) - float(c[2]))
    refused, walked = [], []
    for i in range(ALONG_TRIALS):
        t1, _ = mark(marks, f"along{i} jump")
        a, b = window(samples, t1, 7.5, 8.5, need=1)[0], window(samples, t1, 10.5, 11.5, need=1)[-1]
        walked.append((b[1] - a[1]) * math.cos(side) + (b[2] - a[2]) * math.sin(side))
        refused.append(refused_by(samples, t1 + 11) - refused_by(samples, t1 + 8.5))
    out["E1"] = (not any(refused), "refusals while driving along the wall, per trial: " + " ".join(map(str, refused)))
    out["E7"] = (min(walked) >= ALONG_MIN, "metres walked along the wall with W held after the turn, per trial: "
                 + " ".join(f"{w:.2f}" for w in walked))

    t2, _ = mark(marks, "corner jump")
    p = rest(srv, t2, 12, 17)
    still = window(samples, t2, 12, 17)
    out["E5"] = (worst(still, p) <= NEAR, f"after the corner move the client is up to {worst(still, p):.2f} m from the server")

    t3, _ = mark(marks, "open jump")
    p = rest(srv, t3, 10, 14)
    still = window(samples, t3, 10, 14)
    moved = refused_by(samples, t3 + 14) - refused_by(samples, t3 + 1)
    out["E6"] = (worst(still, p) <= NEAR and moved == 0,
                 f"after the open move the client is up to {worst(still, p):.2f} m from the server, {moved} refusal(s)")
    return out


def main():
    run = Path(sys.argv[1])
    try:
        out = grade(run)
    except (Setup, OSError, IndexError, ValueError) as e:
        print(f"SG_SETUP={shlex.quote(str(e))}")
        return 2
    for k, (ok, why) in sorted(out.items()):
        print(f"SG_{k}={1 if ok else 0}")
        print(f"SG_{k}_WHY={shlex.quote(why)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
