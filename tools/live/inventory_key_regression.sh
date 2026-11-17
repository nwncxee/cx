#!/usr/bin/env bash
# Regression for the inventory key on Linux: the real client with the CX inventory on, the key pressed twice. The key
# must reach the CX window through the ToggleDisplay hook (its "[inv] key" line), which is the path Windows takes too,
# and open then close it. Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/inventory_key_regression.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
inventory_key_run "$ROOT/tools/live/run-live.sh" "$ROOT/.scratch/inventory-key-linux" "$@" || exit $?
fail=0
keys="$(grep -ac "\[inv\] key " "$INV_LOG")"
if [ "$keys" -ge 2 ]; then echo "PASS both presses reached the ToggleDisplay hook"; else echo "FAIL $keys of 2 presses reached the ToggleDisplay hook ($INV_LOG)"; fail=1; fi
if [ "$INV_CLOSED" = 1 ] && [ "$INV_STOCK" = 0 ]; then echo "PASS the key opened and closed the CX window"; else echo "FAIL the window went ${INV_SEQ:-nowhere}, stock panel shown: $INV_STOCK ($INV_LOG)"; fail=1; fi
exit $fail
