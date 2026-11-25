#!/usr/bin/env bash
# Regression for step_guard_grade.py, in seconds and with no client: the shipped guard's live run, recorded in
# step_guard_fixtures/branch (the grader's input lines only), must grade as it did live, and a copy of it with one
# refusal or one offset placed just inside a check's window must fail that check.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIX="$HERE/step_guard_fixtures"
t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
fail=0
expect() { # <run dir> <name> <verdicts>
    got="$(python3 -I "$HERE/step_guard_grade.py" "$1" | grep -v _WHY | tr '\n' ' ')"
    if [ "$got" = "$3 " ]; then echo "ok   $2"; else echo "FAIL $2: $got"; fail=1; fi
}
edit() { # <name> <mark>[@<up>,<end>] <from s> <to s> <kind> [by]: one change to a copy of the branch run (or of $SRC), timed from
    # the mark, or with @ from when the server stops after that drive's keyup (the grader's own stopped()). Kinds:
    # refuse (one more refusal from +from on), unrefuse (none in the span), shift (the client by m in x, 0.13 -- the
    # approved examples' 0.1 m plus a margin -- unless given), scale (the client's path from the span's first point, times by; 0 holds it), srv (the server, in x)
    mkdir -p "$t/$1"
    python3 -I - "$HERE" "${SRC:-$FIX/branch}" "$t/$1" "$2" "$3" "$4" "$5" "${6:-}" <<'PY' || fail=1
import importlib.util, sys
from pathlib import Path
here, src, dst, prefix, a, b, kind, by = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3]), sys.argv[4], float(sys.argv[5]), float(sys.argv[6]), sys.argv[7], sys.argv[8]
spec = importlib.util.spec_from_file_location("grade", Path(here) / "step_guard_grade.py")
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)
by = float(by) if by else 0.13
marks, srv, samples = g.read(src)
prefix, _, keys = prefix.partition("@")
t0, _ = g.mark(marks, prefix)
if keys:
    up, stop = map(float, keys.split(","))
    t0, _ = g.stopped(srv, t0, up - 3, stop)
server = []
for l in (src / "nwclientLog1.txt").read_text(errors="replace").splitlines():
    m = g.SRV.match(l)
    f = m[4].split() if m else []
    if kind == "srv" and f[:1] == ["srv"] and t0 + a <= int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3]) + 0.5 <= t0 + b:
        l = f"{l[:m.start(4)]}srv {float(f[1]) + by:.2f} {' '.join(f[2:])}"
    server.append(l)
(dst / "nwclientLog1.txt").write_text("\n".join(server) + "\n")
lines = [(m, s) for l in (src / "cx.log").read_text().splitlines() if (m := g.CLI.match(l)) and (s := g.AT.match(m[4]))]
span = [s for s in samples if t0 + a <= s[0] <= t0 + b]
base = g.refused_by(samples, t0 + a)
drop = g.refused_by(samples, t0 + b) - base
out = []
for (m, s), (t, x, y, n) in zip(lines, samples):
    inside = t0 + a <= t <= t0 + b
    if kind == "refuse" and t >= t0 + a: n += 1
    if kind == "unrefuse" and t > t0 + a: n = base if t <= t0 + b else n - drop
    if kind == "shift" and inside: x += by
    if kind == "scale" and inside: x, y = span[0][1] + (x - span[0][1]) * by, span[0][2] + (y - span[0][2]) * by
    out.append(f"{m.string[:m.start(4)]}at ({x:.2f},{y:.2f},{s[3]}) refused={n}")
(dst / "cx.log").write_text("\n".join(out) + "\n")
PY
}
# Given the recorded live run of the shipped guard
# When the grader grades its log lines
# Then it gives the verdicts that run gave live
expect "$FIX/branch"         branch         "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
# Given the branch run with one refusal or offset just inside one check's window
# When the grader grades it
# Then that check fails
edit e1 "along3 jump" 8.6 0 refuse; expect "$t/e1" "E1 counts a refusal at the start of the last trial's window" "SG_E1=0 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e1z "along3 jump" 10.5 0 refuse; expect "$t/e1z" "E1 counts a refusal at the end of the last trial's window" "SG_E1=0 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e4 "ram0 jump" 13 0 refuse;    expect "$t/e4" "E4 counts a refusal once still"                            "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
edit e5 "corner jump" 12 12.9 shift; expect "$t/e5" "E5 holds the client to the bound in the window's first second" "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=0 SG_E6=1 SG_E7=1"
edit e6 "open jump" 1.1 0 refuse;   expect "$t/e6" "E6 counts a refusal at the start of the open move's window" "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=0 SG_E7=1"
# Given the branch run with one clause of a check crossed and every other clause of it held
# When the grader grades it
# Then that check fails, so no clause of it rests on another
edit e2u "door2 jump" 1 11 unrefuse; expect "$t/e2u" "E2 holds every door trial to a refusal"           "SG_E1=1 SG_E2=0 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e3s "door0 jump" 4 14.5 shift;  expect "$t/e3s" "E3 holds a refused drive to the server's rest"      "SG_E1=1 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
edit e4s "ram0 jump" 12 14 shift;    expect "$t/e4s" "E4 holds the client to the server once still"       "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
edit e6s "open jump" 10 11 shift;    expect "$t/e6s" "E6 holds the client to the server after the open move" "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=0 SG_E7=1"
edit e7f "door1 turn" -1 4.5 scale 0; expect "$t/e7f" "E7 holds the walk after the closed door"           "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=0"
# Given the branch run with one drive refused once it is still, for each drive E4 grades
# When the grader grades it
# Then E4 fails, so every drive is graded
for d in "wall jump" "ram1 jump" "ram2 jump" "ram3 jump" "ram4 jump" "ram5 jump" "door2 jump"; do
    n="${d%% *}"; edit "e4-$n" "$d" 13 0 refuse
    expect "$t/e4-$n" "E4 grades the $n drive" "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
done
# Given the branch run with one change at the closing edge of a window, or one that a check's measure must ride out
# When the grader grades it
# Then the check fails at the edge, and holds where its measure is meant to absorb the change
edit e5z "corner jump" 16.1 17 shift;    expect "$t/e5z" "E5 holds the client to the bound in the window's last second" "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=0 SG_E6=1 SG_E7=1"
edit e6z "open jump" 13.1 14 shift;      expect "$t/e6z" "E6 holds the client in the window's last second"      "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=0 SG_E7=1"
edit e6r "open jump" 13 0 refuse;        expect "$t/e6r" "E6 counts a refusal at the end of its window"          "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=0 SG_E7=1"
edit e7s "door1 turn" -1 4.5 scale 0.5;  expect "$t/e7s" "E7 holds the walk to a metre"                          "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=0"
edit e7m "door1 turn" -1 4.5 scale -1;   expect "$t/e7m" "E7 takes the door walk either way along the wall"     "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e7a "along3 jump" 7.5 9.6 scale 0; expect "$t/e7a" "E7 reads the along walk at its end, not before"        "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e3l "door0 jump@7,14.5" 0 3 shift; expect "$t/e3l" "E3 reads the approach in the 3 s after the stop only"   "SG_E1=1 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
edit e5o "corner jump" 12 12.9 srv -0.5; expect "$t/e5o" "E5 rides out one stray server position"                "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e5e "corner jump" 15.6 17 srv 0.5;  expect "$t/e5e" "E5 takes the server's rest over the whole window"     "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e6e "open jump" 12.6 14 srv 0.5;    expect "$t/e6e" "E6 takes the server's rest over the whole window"       "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e7t "door1 turn" -1 2.6 scale 0;  expect "$t/e7t" "E7 reads the door walk at its end, not before"        "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e3n "wall jump" 0 400 unrefuse; expect "$t/e3n" "E3 fails a run where no drive is refused" "SG_E1=1 SG_E2=0 SG_E3=0 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit w1 "wall jump" 2 0 refuse; SRC="$t/w1" edit w2 "wall jump@8,15" 0 3 shift
expect "$t/w2" "E3 grades the wall drive once it is refused" "SG_E1=1 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
exit $fail
