#!/usr/bin/env sh
# The self-check verdict over one cx.log, shared by ./run-demo.sh and tools/selfcheck/run.sh so both end on the
# same lines. Exit 0 with ">> <n> SelfCheck lines, none failed, <k> SKIPPED." and each SKIPPED line, or exit 1 with
# the FAIL lines.
# Usage: tools/selfcheck/verdict.sh <cx.log>
LOG=${1:?usage: verdict.sh <cx.log>}
# The SelfCheck suites assert real invariants, and until #157 the log could say FAILED all day and CI still went
# green. Six spellings exist across the call sites -- FAILED, FAIL, both colon forms, and "had FAILURES" -- so match
# the suite name and the word loosely rather than enumerating them. A log with no SelfCheck line at all is also a
# failure: the services never ran.
if ! grep -q 'SelfCheck' "$LOG"; then
  echo ">> FAIL: no SelfCheck lines in $LOG -- services never ran"; exit 1
fi
if grep -qE 'SelfCheck.*FAIL' "$LOG"; then
  echo ">> FAIL: a SelfCheck suite failed:"; grep -E 'SelfCheck.*FAIL' "$LOG"; exit 1
fi
# A suite that THROWS takes its service's whole Initialize with it and prints no FAILED line -- #673 lost 29
# SelfCheck lines to an ArgumentOutOfRange and the gate above went green.
if grep -q 'init threw' "$LOG"; then
  echo ">> FAIL: a service's Initialize threw:"; grep 'init threw' "$LOG"; exit 1
fi
# A suite that stood down is neither a pass nor a failure, and it is named here so "none failed" cannot be read as
# "all ran" (CX-200: every skip goes through Checker.Skip and says SKIPPED).
echo ">> $(grep -c 'SelfCheck' "$LOG") SelfCheck lines, none failed, $(grep -c 'SelfCheck.*SKIPPED' "$LOG") SKIPPED."
grep -E 'SelfCheck.*SKIPPED' "$LOG" | sed 's/^/>>   /' || true
