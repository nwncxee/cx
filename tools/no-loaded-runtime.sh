#!/usr/bin/env bash
# Refuses a build that would rewrite the staged runtime a live process has loaded: a running client maps
# libcx.so and cx_managed.so out of cx-linux-x64/, and restaging that folder swaps them under it. The test
# is "does a live process map a file under THIS folder", not "is a client running" -- another checkout or
# worktree stages its own cx-linux-x64/, so building there while a client runs is fine, and on a host with
# nothing of ours mapped (a CI runner) the scan finds nothing and the build goes on.
# Usage: no-loaded-runtime.sh [staged-dir] [proc-root]. Exit 3 names the process that has it loaded.
out=$(realpath -m -- "${1:-$(cd "$(dirname "$0")/.." && pwd)/cx-linux-x64}")
proc=${2:-/proc}

for maps in "$proc"/[0-9]*/maps; do
  # Another user's maps opens EACCES however the mode bits read, and an exited process takes its whole
  # folder with it mid-scan: either way that process holds nothing of ours we could name.
  grep -qsF -- "$out/" "$maps" || continue
  pid=${maps%/maps}
  pid=${pid##*/}
  comm=$(tr -d '\0' < "${maps%maps}comm" 2>/dev/null)
  echo "!! $out is loaded by ${comm:-a live process} (pid $pid)." >&2
  echo "!! Restaging it would swap the libraries out from under that process. Close it, or build in a" >&2
  echo "!! worktree -- each checkout stages its own cx-linux-x64/." >&2
  exit 3
done
