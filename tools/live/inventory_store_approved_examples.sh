#!/usr/bin/env bash
# Approved examples (approved-spec): a store opened while the CX inventory window is open, in the Linux client. The
# showcase invstore opens a stock store 25 s after login; the key opens the CX window at 15 s. Exit 0 PASS, 1 FAIL, 2
# when the run could not be made.
#
#   tools/live/inventory_store_approved_examples.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-store-open"
[ "${1:-}" = --out ] && out="${2:?--out needs a folder}"
inventory_store_run "$out" "sleep:15000;key:i;sleep:2500;shot:$out/opened.png;sleep:12000;shot:$out/store.png" || exit $?
fail=0

# IK3
# Given the CX inventory window open
# When the player opens a store
# Then the window stays open beside the store
if [ "$INV_SEQ" = O ] && [ "$INV_STORE" = 1 ] && [ "$INV_STOCK" = 0 ]; then echo "PASS IK3 the CX window stayed open when the store opened"
else echo "FAIL IK3 the window went ${INV_SEQ:-nowhere}, store call at the hook: $INV_STORE, stock panel shown: $INV_STOCK ($INV_LOG)"; fail=1; fi
exit $fail
