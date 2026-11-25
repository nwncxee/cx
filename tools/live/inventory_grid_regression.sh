#!/usr/bin/env bash
# Regression: the CX inventory's grid and paper doll act as the stock panel's, in the Linux client. The showcase
# invgrid wears one helmet and gives a bag, a second helmet, a potion and a ring, in the backpack's bottom row. The
# keys drop the helmet on the right hand (refused), then on the worn helmet (a swap), drop the ring on the bag (it
# goes in), double-click the potion (the target cursor), aim it at the PC's portrait (drunk), and double-click the bag (it opens). The grade
# reads the server's own holders (the module's marks, 75 s after login) and the acts the window sent (cx.log). Exit 0
# when every one holds, 1 when one does not, 2 when the run could not be made.
#
#   tools/live/inventory_grid_regression.sh [--out DIR]
# Given a worn helmet, and a bag, a helmet, a potion and a ring in the backpack
# When the player drops and double-clicks them in the CX inventory
# Then each lands where the stock panel puts it
set -u
# shellcheck disable=SC2016 # each check is a condition check() evals
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
out="$ROOT/.scratch/inventory-grid"
while [ $# -gt 0 ]; do
    case "$1" in
        --out) out="${2:?--out needs a folder}"; shift 2 ;;
        *) echo "inventory-grid: unknown argument $1" >&2; exit 2 ;;
    esac
done
# Centres read off the harness's 1280x720 shot at scale 1 (the window's top at y 128): the backpack's bottom row holds the bag
# (columns 1-2), the helmet (3-4), the potion (5) and the ring (6); the doll's head and right-hand slots.
bag="493,527" helm="559,527" potion="609,527" ring="642,543" head="727,197" right="494,257"
drag() { echo "move:$1;sleep:300;down:$1;sleep:200;move:$3;sleep:200;move:$2;sleep:500;up:$2;sleep:2500"; }
dbl() { echo "move:$1;sleep:300;click:$1;sleep:80;click:$1"; }
keys="sleep:15000;key:i;sleep:4000;shot:$out/open.png"
keys="$keys;$(drag "$helm" "$right" "530,390");shot:$out/refused.png"
keys="$keys;$(drag "$helm" "$head" "640,360");shot:$out/swapped.png"
keys="$keys;$(drag "$ring" "$bag" "560,538");shot:$out/bagged.png"
keys="$keys;$(dbl "$potion");sleep:1500;cursor:aim;shot:$out/aim.png;click:1240,50;sleep:2500;shot:$out/drunk.png"
keys="$keys;$(dbl "$bag");sleep:3000;shot:$out/bagopen.png;sleep:40000"
inventory_run "$ROOT/tools/live/run-live.sh" "$out" "$keys" --showcase invgrid --cfg 'chat.enabled=0' || exit $?
markers="$out/showcase-markers.txt"
grep -q '^CXSHOW: before head CXSHOW_worn$' "$markers" 2>/dev/null || { echo "the helmet was never worn (see $markers)"; exit 2; }
grep -q '^CXSHOW: after head ' "$markers" || { echo "the module never reported after the keys (see $markers)"; exit 2; }
fail=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; fail=1; fi; }
act() { grep -aE "\[inv\] act $1" "$INV_LOG" | head -1; }
check "a helmet dropped on the right hand is refused (the sound is a hand check)" '[ -n "$(act Refused)" ]'
check "a helmet dropped on the worn one is worn" 'grep -q "^CXSHOW: after head CXSHOW_helm$" "$markers"'
check "the helmet it replaced goes to the backpack" 'grep -q "^CXSHOW: after CXSHOW_worn backpack$" "$markers"'
check "a ring dropped on the bag goes into it" 'grep -q "^CXSHOW: after CXSHOW_ring in CXSHOW_bag$" "$markers"'
check "a double-click on the potion raises the target cursor" '[ -n "$(act Aim)" ]'
check "the aimed potion is drunk" '! grep -q "^CXSHOW: after CXSHOW_potion " "$markers"'
check "a double-click on the bag opens it" '[ -n "$(act Use)" ]'
echo "acts:"; grep -aE '\[inv\] act ' "$INV_LOG" | sed 's/.*\[inv\]/ /'
grep -h '^cursor ' "$out"/*.txt 2>/dev/null | sed 's/^/  /'
exit "$fail"
