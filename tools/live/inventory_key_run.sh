#!/usr/bin/env bash
# One real client with the CX inventory on, graded from the cx.log it leaves. Sourced by the inventory tests:
# inventory_run RUNNER OUT KEYS [RUNNER ARGS...] runs RUNNER (run-live.sh or run-proton-live.sh) into OUT with the --keys
# script KEYS and sets INV_SEQ, the window's opens and closes in order (O, C; a close by the window's own close action,
# which the harness's quit makes, is left out), INV_STOCK (the stock panel was shown for
# the player) and INV_LOG. inventory_key_run RUNNER DEFAULT_OUT [--out DIR] presses the inventory key twice and adds
# INV_OPENED and INV_CLOSED (the first press left it open; the second closed it, so the sequence is exactly OC).
# inventory_store_run OUT KEYS [harness args] runs the showcase invstore (a stock store opened 25 s after login) on the Linux client
# and adds INV_STORE (the store's show call reached the CX window's hook).
# Each returns 2 when the run could not be made.
# shellcheck disable=SC2034 # the INV_ results are read by the scripts that source this
inventory_run() {
    local runner="$1" out="$2" keys="$3" status started
    shift 3
    INV_SEQ="" INV_STOCK="" INV_LOG=""
    mkdir -p "$(dirname "$out")"
    started="$(mktemp)"
    "$runner" --out "$out" --cfg 'inventory.enabled=1' --keys "$keys" "$@" > "$out.txt" 2>&1
    status=$?
    # A runner that refuses before boot leaves an earlier run's cx.log in OUT; only one written by this run is graded.
    INV_LOG="$(find "$out" -name cx.log -newer "$started" -print -quit 2>/dev/null)"
    rm -f "$started"
    [ -n "$INV_LOG" ] || { echo "no cx.log from this run under $out (runner exit $status, see $out.txt)"; return 2; }
    grep -aq "InventoryService: armed (replacing the stock panel)" "$INV_LOG" \
        || { echo "the CX inventory never armed with inventory.enabled=1 ($INV_LOG)"; return 2; }
    INV_SEQ="$(grep -aoE "\[inv\] (act Close|status open=(True|False))" "$INV_LOG" \
        | sed -E 's/.*act Close/X/; s/.*=True/O/; s/.*=False/C/' | tr -d '\n' | sed 's/XC//g; s/X//g')"
    INV_STOCK=0
    grep -aq "\[inv\] stock panel shown" "$INV_LOG" && INV_STOCK=1
    return 0
}

inventory_key_run() {
    local runner="$1" out="$2"
    shift 2
    case "${1:-}" in
        --out) out="${2:?--out needs a folder}" ;;
        "") ;;
        *) echo "inventory-key: unknown argument $1" >&2; return 2 ;;
    esac
    inventory_run "$runner" "$out" \
        "sleep:15000;shot:$out/before.png;key:i;sleep:2500;shot:$out/opened.png;key:i;sleep:2000;shot:$out/closed.png" || return $?
    INV_OPENED=0 INV_CLOSED=0
    case "$INV_SEQ" in O|OC) INV_OPENED=1 ;; esac
    [ "$INV_SEQ" = OC ] && INV_CLOSED=1
    return 0
}

inventory_store_run() {
    local out="$1" keys="$2" root
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    inventory_run "$root/tools/live/run-live.sh" "$out" "$keys" --showcase invstore "${@:3}" || return $?
    grep -q '^CXSHOW: store open ' "$out/showcase-markers.txt" 2>/dev/null \
        || { echo "the store never opened (see $out/showcase-markers.txt)"; return 2; }
    INV_STORE=0
    grep -aq "\[inv\] stock show call reaches the CX window" "$INV_LOG" && INV_STORE=1
    return 0
}
