#!/usr/bin/env bash
# One real client with the CX inventory on: the inventory key pressed twice, graded from the cx.log it leaves.
# Sourced by the inventory key tests; inventory_key_run RUNNER OUT runs RUNNER (run-live.sh or run-proton-live.sh)
# into OUT and sets INV_OPENED / INV_CLOSED to 1 when CX's window took the first press / the second.
inventory_key_run() {
    local runner="$1" out="$2" log status
    "$runner" --out "$out" --cfg 'inventory.enabled=1' \
        --keys "sleep:15000;shot:$out/before.png;key:i;sleep:2500;shot:$out/opened.png;key:i;sleep:2000;shot:$out/closed.png" \
        > "$out.txt" 2>&1
    status=$?
    log="$(find "$out" -name cx.log -print -quit)"
    [ -n "$log" ] || { echo "no cx.log under $out (runner exit $status, see $out.txt)"; return 2; }
    grep -aq "InventoryService: armed (replacing the stock panel)" "$log" \
        || { echo "the CX inventory never armed with inventory.enabled=1 ($log)"; return 2; }
    INV_OPENED=0 INV_CLOSED=0
    grep -aq "\[inv\] status open=True" "$log" && INV_OPENED=1
    grep -aq "\[inv\] status open=False" "$log" && INV_CLOSED=1
    return 0
}
