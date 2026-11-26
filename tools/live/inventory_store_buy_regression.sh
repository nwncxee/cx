#!/usr/bin/env bash
# Regression: a store item dragged onto a free cell of the CX inventory's backpack is bought into that cell, in the Linux
# client. The showcase invstore opens a stock store 25 s after login; the key opens the CX window at 15 s and the drag runs
# at 28 s, from the leather armour in the store's first tab onto the backpack's empty top rows; drag.png is the frame before
# the release, with the cells it is bought into lit. Exit 0 PASS, 1 FAIL, 2 when the run could not be made.
#
#   tools/live/inventory_store_buy_regression.sh [--out DIR]
# Given an open store and the CX inventory window open, with free backpack cells
# When the player drags a store item onto a free backpack cell
# Then the item is bought into that cell: the window names the cell and the server takes the gold
set -u
# shellcheck disable=SC2016 # each check is a condition check() evals
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-store-buy"
[ "${1:-}" = --out ] && out="${2:?--out needs a folder}"
# The harness's 1280x720 with the window centred: the store's leather armour, then a path into the backpack's top rows.
drag="move:180,235;sleep:400;down:180,235;sleep:200;move:190,245;sleep:200;move:300,300;sleep:200;move:450,380;sleep:200"
drag="$drag;move:560,400;sleep:600;shot:$out/drag.png;up:560,400"
inventory_store_run "$out" "sleep:15000;key:i;sleep:13000;$drag;sleep:3000;shot:$out/after.png" --cfg 'chat.enabled=0' || exit $?
buy="$(grep -aoE '\[inv\] act Buy oid=0x[0-9a-f]+ mask=0x[0-9a-f]+ x=[0-9]+ y=[0-9]+' "$INV_LOG" | head -1)"
x="$(echo "$buy" | grep -oE 'x=[0-9]+' | cut -d= -f2)" y="$(echo "$buy" | grep -oE 'y=[0-9]+' | cut -d= -f2)"
gold="$(grep -aoE '\[inv\] worn/stat change gold=[0-9]+' "$INV_LOG" | grep -oE '[0-9]+$')"
first="$(echo "$gold" | head -1)" last="$(echo "$gold" | tail -1)"
fail=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; fail=1; fi; }
check "the window bought the dragged item (${buy:-no act Buy})" '[ -n "$buy" ]'
check "into the cell under the pointer, not one the server picks (x=${x:-?} y=${y:-?})" '[ -n "$x" ] && [ "$x" != 255 ] && [ "$y" -lt 6 ]'
check "the server took the gold (${first:-?} -> ${last:-?})" '[ -n "$first" ] && [ "$last" -lt "$first" ]'
exit "$fail"
