#!/usr/bin/env bash
# Regression: a split stack shows both halves in the CX inventory window with no reopen, in the Linux client. The
# showcase invsplit empties the PC's backpack and gives it one tagged stack of 10 arrows (bottom-left cell); the key
# opens the CX window at 15 s and a shift-click splits the stack at 27 s. --full runs invsplitfull, whose first page is
# filled so the new half lands on another page, and turns to the next page after the split. The grade reads the
# server's own stacks (the module's marks, 40 s after login) and prints the adds the client's window was handed
# (cx.log); the window's halves are judged on the screenshots. Exit 0 once the server holds two stacks of 5, 2 when
# the run could not be made.
#
#   tools/live/inventory_split_regression.sh [--full] [--out DIR]
# Given a stack of 10 arrows in the backpack
# When the player splits it
# Then a stack of 5 and a stack of 5 show within a second, with no reopen
set -u
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
showcase=invsplit turn=""
out="$ROOT/.scratch/inventory-split"
while [ $# -gt 0 ]; do
    case "$1" in
        --full) showcase=invsplitfull turn=";key:Next;sleep:2500;shot:@OUT@/page2.png"; out="$out-full"; shift ;;
        --out) out="${2:?--out needs a folder}"; shift 2 ;;
        *) echo "inventory-split: unknown argument $1" >&2; exit 2 ;;
    esac
done
# The stack's cell at the harness's 1280x720 with the window centred (the layout's own default placement).
stack="476,500"
keys="sleep:15000;key:i;sleep:12000;move:$stack;sleep:400;shot:$out/before.png;keydown:Shift_L;sleep:300;click:$stack;sleep:300;keyup:Shift_L"
keys="$keys;sleep:1000;shot:$out/split1s.png;sleep:3000;shot:$out/split4s.png${turn//@OUT@/$out};sleep:12000"
inventory_run "$ROOT/tools/live/run-live.sh" "$out" "$keys" --showcase "$showcase" --cfg 'chat.enabled=0' || exit $?
markers="$out/showcase-markers.txt"
grep -q '^CXSHOW: item ' "$markers" 2>/dev/null || { echo "the arrows were never given (see $markers)"; exit 2; }
grep -aq '\[inv\] act Split' "$INV_LOG" || { echo "no split was sent from the window ($INV_LOG)"; exit 2; }
fives="$(grep -c '^CXSHOW: stack .* 5$' "$markers")"
[ "$fives" = 2 ] || { echo "the server holds no two stacks of 5 ($(grep '^CXSHOW: stack' "$markers" | tr '\n' ' '))"; exit 2; }
echo "server: two stacks of 5; window adds after the split:"
sed -n '/\[inv\] act Split/,$p' "$INV_LOG" | grep -a '\[inv\] add' || echo "  none"
