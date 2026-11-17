#!/usr/bin/env bash
# Regression for the inventory key on Linux: the real client with the CX inventory on, the key pressed twice. The key
# must reach the CX window through the ToggleDisplay hook (its "[inv] key" line), which is the path Windows takes too,
# and open then close it. Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/inventory_key_regression.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
OUT="${2:-$ROOT/.scratch/inventory-key-linux}"
[ "${1:-}" = "--out" ] || [ $# -eq 0 ] || { echo "inventory-key: unknown argument $1" >&2; exit 2; }
mkdir -p "$(dirname "$OUT")"
inventory_key_run "$ROOT/tools/live/run-live.sh" "$OUT" || exit $?
log="$(find "$OUT" -name cx.log -print -quit)"
fail=0
keys="$(grep -ac "\[inv\] key " "$log")"
if [ "$keys" -ge 2 ]; then echo "PASS both presses reached the ToggleDisplay hook"; else echo "FAIL $keys of 2 presses reached the ToggleDisplay hook ($log)"; fail=1; fi
if [ "$INV_OPENED" = 1 ] && [ "$INV_CLOSED" = 1 ]; then echo "PASS the key opened and closed the CX window"; else echo "FAIL opened=$INV_OPENED closed=$INV_CLOSED ($OUT)"; fail=1; fi
exit $fail
