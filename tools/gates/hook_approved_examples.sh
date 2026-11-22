#!/usr/bin/env bash
# The edit hook runs only the gates whose paths reach the edited file; build.sh and CI run every gate (CX-633, E8).
# Oracle: the gate scripts in tools/gates/, listed by the shell, and the wall clock. The --list assertions are the
# deterministic proof; the clock bound is the example's own line. Needs a tree the gates pass on.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
rc=0
fail() { echo "!! hook: $*" >&2; rc=1; }

# E8
# Given an Edit to one file under managed/
# When the PostToolUse hook runs the gates
# Then it finishes in under 10 s, and build.sh and CI still run every gate
edited=$PWD/managed/Framework/HexColour.cs
start=$(date +%s%N)
printf '{"tool_input":{"file_path":"%s"}}' "$edited" | timeout 120 bash tools/gates/hook.sh > /dev/null 2>&1
hook_rc=$?
ms=$(( ($(date +%s%N) - start) / 1000000 ))
[ "$hook_rc" = 0 ] || fail "E8: the hook exited $hook_rc on a clean tree"
[ "$ms" -lt 10000 ] || fail "E8: the hook took $ms ms on an edit under managed/, not under 10 s"
all=$(for g in tools/gates/*.sh; do case "$g" in */run.sh|*/hook.sh|*/ratchet.sh|*_approved_examples*) ;; *) echo "$g" ;; esac; done | sort)
listed=$(bash tools/gates/run.sh --list | sort)
[ "$listed" = "$all" ] || fail "E8: run.sh no longer runs every gate: $(diff <(echo "$all") <(echo "$listed") | head -5)"
mine=$(bash tools/gates/run.sh --list --for managed/Framework/HexColour.cs)
grep -qx tools/gates/alias-lanes.sh <<< "$mine" || fail "E8: an edit under managed/ skips alias-lanes, which reads managed/"
grep -qx tools/gates/adr.sh <<< "$mine" && fail "E8: an edit under managed/ runs adr, which reads only docs/adr/"

[ $rc = 0 ] && echo "hook approved examples: E8 passes (${ms} ms)"
exit $rc
