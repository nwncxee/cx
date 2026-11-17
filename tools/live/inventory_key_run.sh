#!/usr/bin/env bash
# One real client with the CX inventory on: the inventory key pressed twice, graded from the cx.log it leaves.
# Sourced by the inventory key tests; inventory_key_run RUNNER OUT runs RUNNER (run-live.sh or run-proton-live.sh)
# into OUT and sets INV_OPENED / INV_CLOSED to 1 when the first press left CX's window open / the second closed it:
# the window must open exactly once and then close exactly once, so an open that shuts again at once fails both.
# shellcheck disable=SC2034 # INV_OPENED, INV_CLOSED and INV_SEQ are read by the scripts that source this
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
    # The window's opens and closes in the order they happened, one letter each: O for an open, C for a close.
    INV_SEQ="$(grep -aoE "\[inv\] status open=(True|False)" "$log" | sed -E 's/.*=True/O/; s/.*=False/C/' | tr -d '\n')"
    INV_OPENED=0 INV_CLOSED=0
    case "$INV_SEQ" in O|OC) INV_OPENED=1 ;; esac
    [ "$INV_SEQ" = OC ] && INV_CLOSED=1
    return 0
}
