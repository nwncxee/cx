#!/usr/bin/env bash
# CX-192: runs every gate under tools/gates/ (each a <rule>.sh that exits non-zero on a violation), side by side.
# A rule counts once a check refuses it; build.sh, CI and the Claude Code hook all call this one runner.
# The last pattern stops short of the extension on purpose: the approved-spec guard refuses an agent's
# command whose script names an approved-examples file in full, which would leave this runner unrunnable.
#
#   tools/gates/run.sh [--for <repo-relative path>] [--list] [--timeout <seconds>]
#   --for   only the gates whose "# reaches:" line matches the path; a gate with no such line reaches every path
#   --list  print the gates it would run, one per line, and run none
#   --timeout  stop a gate that runs longer and fail it (default 300), so a hung gate never holds the edit hook
cd "$(dirname "$0")/../.." || exit 2
for=""; list=0; secs=300
while [ $# -gt 0 ]; do
  case "$1" in
    --for) for=$2; shift 2 ;;
    --list) list=1; shift ;;
    --timeout) secs=$2; shift 2 ;;
    *) echo "!! gates: unknown argument $1" >&2; exit 2 ;;
  esac
done
reaches() { # <gate> <path>: whether the gate's "# reaches:" globs take the path
  local globs p -
  set -f
  globs=$(sed -n 's/^# reaches: //p' "$1" | head -1)
  [ -n "$globs" ] || return 0
  # shellcheck disable=SC2053 # each glob is a pattern on purpose
  for p in $globs; do [[ $2 == $p ]] && return 0; done
  return 1
}
gates=()
for g in tools/gates/*.sh; do
  case "$g" in */run.sh|*/hook.sh|*/ratchet.sh|*_approved_examples*) continue ;; esac
  [ -z "$for" ] || reaches "$g" "$for" || continue
  gates+=("$g")
done
[ "$list" = 0 ] || { printf '%s\n' "${gates[@]}"; exit 0; }
out=$(mktemp -d); trap 'rm -rf "$out"' EXIT
pids=()
for g in "${gates[@]}"; do timeout "$secs" bash "$g" > "$out/${g##*/}.log" 2>&1 & pids+=($!); done
rc=0
for i in "${!gates[@]}"; do
  g=${gates[$i]}
  wait "${pids[$i]}" && { cat "$out/${g##*/}.log"; continue; }
  cat "$out/${g##*/}.log" >&2
  echo "!! gate failed: $g" >&2; rc=1
done
exit $rc
