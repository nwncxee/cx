#!/usr/bin/env bash
# Regression: the engine's item target cursor fires at an item clicked in the CX inventory window, in the Linux
# client. The showcase invtarget empties the PC's backpack, gives it one tagged longsword (bottom-left cell) and raises
# an item target cursor 25 s after login; the key opens the CX window at 15 s and the click lands on the sword at 27 s.
# The module's own player-target event names what the click picked. Exit 0 PASS, 1 FAIL, 2 when the run could not be made.
#
#   tools/live/inventory_target_regression.sh [--out DIR]
# Given the engine's item target cursor up and the CX inventory window open
# When the player clicks a backpack item in the window
# Then the cursor fires at that item
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-target"
[ "${1:-}" = --out ] && out="${2:?--out needs a folder}"
# The sword's cell at the harness's 1280x720 with the window centred (the layout's own default placement).
sword="476,500"
inventory_run "$ROOT/tools/live/run-live.sh" "$out" \
    "sleep:15000;key:i;sleep:12000;move:$sword;sleep:400;shot:$out/aim.png;click:$sword;sleep:2500;shot:$out/after.png" \
    --showcase invtarget --cfg 'chat.enabled=0' || exit $?
markers="$out/showcase-markers.txt"
grep -q '^CXSHOW: aiming' "$markers" 2>/dev/null || { echo "the target cursor never went up (see $markers)"; exit 2; }
if grep -q '^CXSHOW: targeted CXSHOW_target' "$markers"; then echo "PASS the cursor fired at the sword clicked in the CX window"
else echo "FAIL the cursor fired at $(grep -o 'targeted .*' "$markers" || echo nothing) ($INV_LOG)"; exit 1; fi
