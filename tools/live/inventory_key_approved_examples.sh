#!/usr/bin/env bash
# Approved examples (approved-spec) for the inventory key on Windows: nwmain.exe under Proton with the cross-built
# version.dll, the key pressed twice in the harness's own area. Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/inventory_key_approved_examples.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
OUT="${2:-$ROOT/.scratch/inventory-key-win}"
[ "${1:-}" = "--out" ] || [ $# -eq 0 ] || { echo "inventory-key: unknown argument $1" >&2; exit 2; }
mkdir -p "$(dirname "$OUT")"
inventory_key_run "$ROOT/tools/live/run-proton-live.sh" "$OUT" || exit $?
fail=0

# Given the CX inventory switched on and its window closed, in the Windows client
# When the player presses the inventory key
# Then the CX inventory window opens and the stock inventory panel does not
if [ "$INV_OPENED" = 1 ]; then echo "PASS IK1 the key opened the CX window"; else echo "FAIL IK1 the key did not reach the CX window ($OUT/opened.png)"; fail=1; fi

# Given the CX inventory window open in the Windows client
# When the player presses the inventory key
# Then the window closes
if [ "$INV_OPENED" = 1 ] && [ "$INV_CLOSED" = 1 ]; then echo "PASS IK2 the second press closed it"; else echo "FAIL IK2 the second press did not close the CX window ($OUT/closed.png)"; fail=1; fi
exit $fail
