#!/usr/bin/env bash
# no-loaded-runtime.sh's approved examples (approved-spec, E1 to E4): a live process holding a file under a
# staged runtime folder, read through the real /proc. Oracle: the pid the shell reports for the process this
# script starts, and the expectations written by hand below.
cd "$(dirname "$0")/.."
repo=$PWD
rc=0
tmp=$(mktemp -d)
held=()
trap '[ ${#held[@]} = 0 ] || kill "${held[@]}" 2>/dev/null; rm -rf "$tmp"' EXIT

# A staged runtime folder $tmp/<case>/cx-linux-x64 holding a copy of `sleep`, which stands in for the
# libraries a client loads out of it: a process running that copy maps a file under the folder.
stage() {
  local dir=$tmp/$1/cx-linux-x64
  mkdir -p "$dir"
  cp -- "$(command -v sleep)" "$dir/sleep" || exit 1
  echo "$dir"
}

# Starts the staged copy in $1 and sets held_pid to its pid, once the kernel lists its image. It sets a
# variable rather than printing, so the pid it must kill on the way out reaches the trap's own shell.
hold() {
  "$1/sleep" 300 >/dev/null 2>&1 &
  held_pid=$!
  held+=("$held_pid")
  local tries=0
  while ! grep -qsF -- "$1/" "/proc/$held_pid/maps"; do
    tries=$((tries + 1))
    [ "$tries" -lt 100 ] || { echo "!! the staged copy in $1 never reached /proc/$held_pid/maps" >&2; exit 1; }
    sleep 0.05
  done
}

# The check over folder $1 must exit 3 and print every text after $2.
refuses() {
  local dir=$1 what=$2 out status
  shift 2
  out=$(bash "$repo/tools/no-loaded-runtime.sh" "$dir" 2>&1) && status=0 || status=$?
  [ "$status" = 3 ] || { echo "!! $what: the check exited $status, not 3 -- it said: $out"; rc=1; }
  for want in "$@"; do
    case "$out" in *"$want"*) ;; *) echo "!! $what: the refusal does not name $want -- it said: $out"; rc=1 ;; esac
  done
}

# The check over folder $1 must pass, reading $3 for /proc when one is given.
passes() {
  local out
  out=$(bash "$repo/tools/no-loaded-runtime.sh" "$1" "${3:-/proc}" 2>&1) \
    || { echo "!! $2: the check refused it -- $out"; rc=1; }
}

# E1
# Given a live process maps a file under this checkout's cx-linux-x64/
# When a build starts here
# Then it refuses, exit 3, naming that process and its pid
loaded=$(stage e1)
hold "$loaded"
refuses "$loaded" "E1" "pid $held_pid" "cx-linux-x64 is loaded by"

# E2
# Given that process maps another checkout's cx-linux-x64/, which is what a build in a worktree faces
# When a build starts in this one
# Then it passes: a staged folder nothing has mapped is the build's to rewrite
idle=$(stage e2)
passes "$idle" "E2"

# E3
# Given a checkout that has never staged a runtime folder
# When its first build starts
# Then it passes
passes "$tmp/e3/cx-linux-x64" "E3"

# E4
# Given a host whose /proc holds nothing of ours to read, as a CI runner's does
# When a build starts
# Then it passes rather than failing the build
passes "$loaded" "E4" "$tmp/no-proc"

[ $rc = 0 ] || echo "!! no-loaded-runtime approved examples: FAILED (above)" >&2
exit $rc
