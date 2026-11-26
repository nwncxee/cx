#!/usr/bin/env bash
# Regression: a chest opened before the CX inventory ever opened leaves the worn helmet drawn, in the Linux client. The
# showcase invchest wears one helmet, puts a spare in the backpack and has the PC open a chest 8 s in; no key is pressed,
# so the chest is what first shows the window. The grade reads the window's own log (cx.log: the loot opened and the window
# was shown) and the server's marks; loot.png keeps the doll's head slot for a look. Exit 0 when every one holds, 1 when one does not, 2 when the run could not be made.
#
#   tools/live/inventory_chest_regression.sh [--out DIR]
# Given a worn helmet and a chest the PC opened before the inventory ever opened
# When the chest's loot shows the CX inventory
# Then the helmet is drawn in the head slot
set -u
# shellcheck disable=SC2016 # each check is a condition check() evals
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-chest"
while [ $# -gt 0 ]; do
    case "$1" in
        --out) out="${2:?--out needs a folder}"; shift 2 ;;
        *) echo "inventory-chest: unknown argument $1" >&2; exit 2 ;;
    esac
done
keys="sleep:12000;shot:$out/loot.png;sleep:50000"
inventory_run "$ROOT/tools/live/run-live.sh" "$out" "$keys" --showcase invchest --cfg 'chat.enabled=0' || exit $?
markers="$out/showcase-markers.txt"
grep -q '^CXSHOW: open ' "$markers" 2>/dev/null || { echo "the PC never opened the chest (see $markers)"; exit 2; }
grep -q '^CXSHOW: after head ' "$markers" || { echo "the module never reported after the keys (see $markers)"; exit 2; }
fail=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; fail=1; fi; }
first() { grep -anE "$1" "$INV_LOG" | head -1 | cut -d: -f1; }
loot="$(first '\[inv\] loot open ')" shown="$(first '\[inv\] status open=True')"
check "the chest opened its loot" '[ -n "$loot" ]'
check "the chest showed the window, with no key pressed" '[ -n "$shown" ]'
check "the server still has the helmet worn" 'grep -q "^CXSHOW: after head CXSHOW_worn$" "$markers"'
echo "log:"; grep -aE '\[inv\] (loot open|status open|worn/stat)' "$INV_LOG" | sed 's/.*\[inv\]/ /' | head -8
exit "$fail"
