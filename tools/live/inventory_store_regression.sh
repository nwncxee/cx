#!/usr/bin/env bash
# Regression, IK4 (it held before its test was written, so it is no approved-spec test): a store opened while the CX
# inventory window is closed opens the CX window and not the stock panel, in the Linux client. Exit 0 PASS, 1 FAIL, 2
# when the run could not be made.
#
#   tools/live/inventory_store_regression.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-store-closed"
[ "${1:-}" = --out ] && out="${2:?--out needs a folder}"
inventory_store_run "$out" "sleep:29500;shot:$out/store.png" || exit $?

# IK4
# Given the CX inventory window closed
# When the player opens a store
# Then the CX inventory window opens beside the store and the stock inventory panel does not
if [ "$INV_SEQ" = O ] && [ "$INV_STORE" = 1 ] && [ "$INV_STOCK" = 0 ]; then echo "PASS IK4 the store opened the CX window and not the stock panel"; exit 0; fi
echo "FAIL IK4 the window went ${INV_SEQ:-nowhere}, store call at the hook: $INV_STORE, stock panel shown: $INV_STOCK ($INV_LOG)"; exit 1
