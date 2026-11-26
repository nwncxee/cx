#!/usr/bin/env bash
# Offline: inventory_run (tools/live/inventory_key_run.sh) grades only the run it made. A runner that refuses before the
# client boots leaves the previous run's cx.log in the same folder, and grading that one reported a stale PASS.
# Exit 0 PASS, 1 FAIL.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
work="$(mktemp -d)"; trap 'rm -rf "$work"' EXIT
out="$work/out"
mkdir -p "$out"
printf '%s\n' '[cx] InventoryService: armed (replacing the stock panel)' '[cx] [inv] status open=True' > "$out/cx.log"
touch -d '2000-01-01' "$out/cx.log"
printf '#!/bin/sh\necho "run-live: staged build is stale: run ./build.sh"\nexit 2\n' > "$work/refuse.sh"
chmod +x "$work/refuse.sh"

# Given a folder holding an earlier run's cx.log, in which the CX inventory armed and opened
# When the runner refuses before the client boots and writes nothing
# Then inventory_run reports that no run was made, and grades nothing
inventory_run "$work/refuse.sh" "$out" "sleep:1" > "$work/said.txt"; got=$?
if [ "$got" = 2 ] && [ -z "${INV_SEQ:-}" ]; then echo "PASS a refused run is not graded from the old cx.log"
else echo "FAIL a refused run returned $got and graded '${INV_SEQ:-}' from the old cx.log ($(cat "$work/said.txt"))"; exit 1; fi
