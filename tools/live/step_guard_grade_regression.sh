#!/usr/bin/env bash
# Regression for step_guard_grade.py, in seconds and with no client: four recorded runs (the grader's input lines
# only) must grade as they did live, and copies of the branch run edited across one check's boundary must fail it.
# branch is the shipped guard; no-refusal, personal-space and refuse-all are the guard with no refusal, at the
# radius it had before CX-698, and refusing every step. Exit 0 when every run grades as expected, 1 otherwise.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FIX="$HERE/step_guard_fixtures"
t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
fail=0
expect() { # <run dir> <name> <verdicts>
    got="$(python3 -I "$HERE/step_guard_grade.py" "$1" | grep -v _WHY | tr '\n' ' ')"
    if [ "$got" = "$3 " ]; then echo "ok   $2"; else echo "FAIL $2: $got"; fail=1; fi
}
edit() { # <name> <mark> <from s> <to s> refuse|shift: one more refusal from mark+from on, or the client 0.2 m off in the span
    mkdir -p "$t/$1" && cp "$FIX/branch/nwclientLog1.txt" "$t/$1/"
    python3 -I - "$FIX/branch" "$t/$1" "$2" "$3" "$4" "$5" <<'PY'
import re, sys
from pathlib import Path
src, dst, mark, a, b, kind = Path(sys.argv[1]), Path(sys.argv[2]), sys.argv[3], float(sys.argv[4]), float(sys.argv[5]), sys.argv[6]
hms = lambda h, m, s: int(h) * 3600 + int(m) * 60 + float(s)
t0 = next(hms(*m.groups()) + 0.5 for m in (re.match(r"^\[\w+ \w+ +\d+ (\d+):(\d+):(\d+)\] CXSHOW: sg " + mark + " ", l)
          for l in (src / "nwclientLog1.txt").read_text().splitlines()) if m)
out = []
for l in (src / "cx.log").read_text().splitlines():
    m = re.match(r"^(\S*T(\d+):(\d+):([\d.]+)\S* \[cx\] StepGuard: at \()(-?[\d.]+)(,.*refused=)(\d+)$", l)
    t = hms(m[2], m[3], m[4]) - t0
    x, n = float(m[5]), int(m[7])
    if kind == "refuse" and t >= a: n += 1
    if kind == "shift" and a <= t <= b: x += 0.2
    out.append(f"{m[1]}{x:.2f}{m[6]}{n}")
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
# Given the branch run with one change that crosses one check's line
# When the grader grades it
# Then that check fails
edit e1-last-trial "along3 jump" 10 0 refuse;  expect "$t/e1-last-trial" "E1 counts a refusal in the last trial"  "SG_E1=0 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
edit e4-after-still "ram0 jump" 13 0 refuse;   expect "$t/e4-after-still" "E4 counts a refusal once still"        "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=1"
edit e5-off "corner jump" 12 17 shift;         expect "$t/e5-off" "E5 holds the client to 0.1 m"                  "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=0 SG_E6=1 SG_E7=1"
edit e6-refused "open jump" 5 0 refuse;        expect "$t/e6-refused" "E6 counts a refusal on the open move"      "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=0 SG_E7=1"
exit $fail
