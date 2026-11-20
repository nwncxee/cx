#!/usr/bin/env bash
# Regression: a backpack item dragged from the CX inventory onto an open store's item box sells, on the first press after
# the store opened (the press an engine GUI object under the window used to take), in the Linux client. The showcase
# invstore opens a stock store 25 s after login; the key opens the CX window at 15 s and the drag runs at 28 s, from the
# bottom-left backpack cell (the starting dagger) onto the store's grid. Exit 0 PASS, 1 FAIL, 2 when the run could not be made.
#
#   tools/live/inventory_store_sell_regression.sh [--out DIR]
# Given an open store and the CX inventory window open
# When the player drags a backpack item from the window onto the store's item box
# Then the item sells: the server raises the gold and lowers the weight
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-store-sell"
[ "${1:-}" = --out ] && out="${2:?--out needs a folder}"
# The harness's 1280x720 with the window centred: the bottom-left backpack cell, then a path out to the store's grid.
drag="move:476,540;sleep:400;down:476,540;sleep:200;move:486,530;sleep:200;move:500,450;sleep:200;move:420,450;sleep:200"
drag="$drag;move:300,350;sleep:200;move:180,250;sleep:600;up:180,250"
inventory_store_run "$out" "sleep:15000;key:i;sleep:13000;$drag;sleep:3000;shot:$out/after.png" || exit $?
gold="$(grep -aoE '\[inv\] worn/stat change gold=[0-9]+' "$INV_LOG" | grep -oE '[0-9]+$')"
first="$(echo "$gold" | head -1)" last="$(echo "$gold" | tail -1)"
if grep -aq '\[inv\] act Drop' "$INV_LOG" && [ -n "$first" ] && [ "$last" -gt "$first" ]; then echo "PASS the dragged item sold: gold $first -> $last"
else echo "FAIL no sale: drop logged $(grep -ac '\[inv\] act Drop' "$INV_LOG") time(s), gold ${first:-?} -> ${last:-?} ($INV_LOG)"; exit 1; fi
