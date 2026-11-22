#!/usr/bin/env bash
# Claude Code PostToolUse hook (Edit|Write): runs the gates whose paths reach the edited file the moment it changes,
# so a violation surfaces as feedback (exit 2 = stderr shown to Claude) rather than at the end of a session.
# build.sh and CI run every gate.
f=$(jq -r '.tool_input.file_path // empty')
root=$(cd "$(dirname "$0")/../.." && pwd)
case "$f" in "$root"/*) ;; *) exit 0 ;; esac
out=$(bash "$(dirname "$0")/run.sh" --for "${f#"$root"/}" 2>&1) && exit 0
echo "$out" >&2
exit 2
