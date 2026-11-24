#!/usr/bin/env bash
# Regression for step_guard_grade.py, in seconds and with no client: the runs recorded in step_guard_fixtures/ (the
# grader's input lines only) must grade as they did live, and a copy of the branch run with one refusal or one offset
# placed just inside a check's window must fail that check. branch is the shipped guard; no-refusal, personal-space
# and refuse-all are the guard refusing nothing, testing at the player's personal-space radius, and refusing every step.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIX="$HERE/step_guard_fixtures"
t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
fail=0
expect() { # <run dir> <name> <verdicts>
    got="$(python3 -I "$HERE/step_guard_grade.py" "$1" | grep -v _WHY | tr '\n' ' ')"
    if [ "$got" = "$3 " ]; then echo "ok   $2"; else echo "FAIL $2: $got"; fail=1; fi
}
edit() { # <name> <mark> <from s> <to s> refuse|shift|freeze|unrefuse: one more refusal from
    # mark+from on, the client 0.2 m off in the span, held at the span's first point, or refused nothing in the span
    mkdir -p "$t/$1" && cp "$FIX/branch/nwclientLog1.txt" "$t/$1/"
    python3 -I - "$HERE" "$FIX/branch" "$t/$1" "$2" "$3" "$4" "$5" <<'PY' || fail=1
import importlib.util, sys
from pathlib import Path
here, src, dst, prefix, a, b, kind = sys.argv[1], Path(sys.argv[2]), Path(sys.argv[3]), sys.argv[4], float(sys.argv[5]), float(sys.argv[6]), sys.argv[7]
spec = importlib.util.spec_from_file_location("grade", Path(here) / "step_guard_grade.py")
g = importlib.util.module_from_spec(spec)
spec.loader.exec_module(g)
marks, _, samples = g.read(src)
t0, _ = g.mark(marks, prefix)
lines = [(m, s) for l in (src / "cx.log").read_text().splitlines() if (m := g.CLI.match(l)) and (s := g.AT.match(m[4]))]
span = [s for s in samples if t0 + a <= s[0] <= t0 + b]
base = g.refused_by(samples, t0 + a)
drop = g.refused_by(samples, t0 + b) - base
out = []
for (m, s), (t, x, y, n) in zip(lines, samples):
    if kind == "refuse" and t >= t0 + a: n += 1
    if kind == "shift" and t0 + a <= t <= t0 + b: x += 0.2
    if kind == "freeze" and t0 + a <= t <= t0 + b: x, y = span[0][1], span[0][2]
    if kind == "unrefuse" and t > t0 + a: n = base if t <= t0 + b else n - drop
    out.append(f"{m.string[:m.start(4)]}at ({x:.2f},{y:.2f},{s[3]}) refused={n}")
(dst / "cx.log").write_text("\n".join(out) + "\n")
PY
}
# Given a recorded live run of each guard
# When the grader grades its log lines
# Then it gives the verdicts the live run gave
expect "$FIX/branch"         branch         "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
expect "$FIX/no-refusal"     no-refusal     "SG_E1=1 SG_E2=0 SG_E3=0 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
expect "$FIX/personal-space" personal-space "SG_E1=0 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=0"
expect "$FIX/refuse-all"     refuse-all     "SG_E1=0 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=0 SG_E6=0 SG_E7=0"
# Given the branch run with one refusal or offset just inside one check's window
# When the grader grades it
# Then that check fails
edit e1 "along3 jump" 8.6 0 refuse; expect "$t/e1" "E1 counts a refusal at the start of the last trial's window" "SG_E1=0 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
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
edit e7f "door1 turn" -1 4.5 freeze; expect "$t/e7f" "E7 holds the walk after the closed door"           "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=0"
exit $fail
