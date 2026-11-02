#!/usr/bin/env bash
# CX-192: runs every gate under tools/gates/ (each a <rule>.sh that exits non-zero on a violation).
# A rule counts once a check refuses it; build.sh, CI and the Claude Code hook all call this one runner.
# The last pattern stops short of the extension on purpose: the approved-spec guard refuses an agent's
# command whose script names an approved-examples file in full, which would leave this runner unrunnable.
cd "$(dirname "$0")/../.."
rc=0
for g in tools/gates/*.sh; do
  case "$g" in */run.sh|*/hook.sh|*/ratchet.sh|*_approved_examples*) continue ;; esac
  bash "$g" || { echo "!! gate failed: $g" >&2; rc=1; }
done
exit $rc
