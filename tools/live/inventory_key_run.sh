#!/usr/bin/env bash
# One real client with the CX inventory on: the inventory key pressed twice, graded from the cx.log it leaves.
# Sourced by the inventory key tests: inventory_key_run RUNNER DEFAULT_OUT [--out DIR] runs RUNNER (run-live.sh or
# run-proton-live.sh) into DIR and sets INV_SEQ, the window's opens and closes in order (O, C), INV_OPENED and
# INV_CLOSED (the first press left it open; the second closed it, so the sequence is exactly OC), INV_STOCK (the
# stock panel was shown for the player) and INV_LOG. It returns 2 when the run could not be made.
# shellcheck disable=SC2034 # the INV_ results are read by the scripts that source this
inventory_key_run() {
    local runner="$1" out="$2" status
    shift 2
    case "${1:-}" in
        --out) out="${2:?--out needs a folder}" ;;
        "") ;;
        *) echo "inventory-key: unknown argument $1" >&2; return 2 ;;
    esac
    mkdir -p "$(dirname "$out")"
    "$runner" --out "$out" --cfg 'inventory.enabled=1' \
        --keys "sleep:15000;shot:$out/before.png;key:i;sleep:2500;shot:$out/opened.png;key:i;sleep:2000;shot:$out/closed.png" \
        > "$out.txt" 2>&1
    status=$?
    INV_LOG="$(find "$out" -name cx.log -print -quit)"
    [ -n "$INV_LOG" ] || { echo "no cx.log under $out (runner exit $status, see $out.txt)"; return 2; }
    grep -aq "InventoryService: armed (replacing the stock panel)" "$INV_LOG" \
        || { echo "the CX inventory never armed with inventory.enabled=1 ($INV_LOG)"; return 2; }
    INV_SEQ="$(grep -aoE "\[inv\] status open=(True|False)" "$INV_LOG" | sed -E 's/.*=True/O/; s/.*=False/C/' | tr -d '\n')"
    INV_OPENED=0 INV_CLOSED=0 INV_STOCK=0
    case "$INV_SEQ" in O|OC) INV_OPENED=1 ;; esac
    [ "$INV_SEQ" = OC ] && INV_CLOSED=1
    grep -aq "\[inv\] stock panel shown" "$INV_LOG" && INV_STOCK=1
    return 0
}
