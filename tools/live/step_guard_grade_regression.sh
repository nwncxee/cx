#!/usr/bin/env bash
# Regression for step_guard_grade.py, in seconds and with no client: four recorded runs (the grader's input lines
# only) must grade as they did live. branch is the shipped guard; no-refusal, shipped-radius and refuse-all are the
# guard with no refusal, with the old personal-space radius, and refusing every step. Run it after any grader edit.
# Exit 0 when every run grades as recorded, 1 otherwise.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail=0
expect() {
    got="$(python3 -I "$HERE/step_guard_grade.py" "$HERE/step_guard_fixtures/$1" | grep -v _WHY | tr '\n' ' ')"
    if [ "$got" = "$2 " ]; then echo "ok   $1"; else echo "FAIL $1: $got"; fail=1; fi
}
expect branch         "SG_E1=1 SG_E2=1 SG_E3=1 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
expect no-refusal     "SG_E1=1 SG_E2=0 SG_E3=0 SG_E4=1 SG_E5=1 SG_E6=1 SG_E7=1"
expect shipped-radius "SG_E1=0 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=1 SG_E6=1 SG_E7=0"
expect refuse-all     "SG_E1=0 SG_E2=1 SG_E3=0 SG_E4=0 SG_E5=0 SG_E6=0 SG_E7=0"
exit $fail
