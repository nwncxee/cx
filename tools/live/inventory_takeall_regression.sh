#!/usr/bin/env bash
# Regression: Take all empties a chest at a player's pace on the Linux client, against the module's own server. The
# showcase invtakeall empties the PC's backpack and has it open a chest 8 s in (five items, plus whatever treasure the
# chest rolls); the press lands on the loot pane's Take all at 15 s. The grade reads the server's record of the chest
# (inc_take marks every change) and the window's takes in cx.log. --full runs invtakefull, whose backpack is full of
# rings: the window stops before a take, or after the one the server refuses. Exit 0 when every check holds, 1 when one does not, 2
# when the run could not be made.
#
#   tools/live/inventory_takeall_regression.sh [--full] [--out DIR]
# Given an open chest and the CX inventory, with an empty backpack (or, with --full, a full one)
# When the player presses Take all in the loot pane's title bar
# Then the server empties the chest one take at a time, no two within half a second (or, full, nothing leaves the chest and the window stops)
set -u
# shellcheck disable=SC2016 # each check is a condition check() evals
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
# shellcheck source=tools/live/inventory_key_run.sh
. "$ROOT/tools/live/inventory_key_run.sh"
show=invtakeall out="$ROOT/.scratch/inventory-takeall"
while [ $# -gt 0 ]; do
    case "$1" in
        --full) show=invtakefull out="$ROOT/.scratch/inventory-takefull"; shift ;;
        --out) out="${2:?--out needs a folder}"; shift 2 ;;
        *) echo "inventory-takeall: unknown argument $1" >&2; exit 2 ;;
    esac
done
# Take all at the harness's 1280x720, the loot pane beside the centred window.
button="1028,139"
keys="sleep:13000;shot:$out/loot.png;move:$button;sleep:400;down:$button;sleep:150;up:$button;sleep:1200;shot:$out/taking.png;sleep:12000;shot:$out/after.png"
inventory_run "$ROOT/tools/live/run-live.sh" "$out" "$keys" --showcase "$show" --cfg 'chat.enabled=0' || exit $?
markers="$out/showcase-markers.txt"
counts="$(grep -oE '^CXSHOW: count t=[0-9]+ chest=[0-9]+' "$markers" | sort)"
held="$(printf '%s\n' "$counts" | head -1 | grep -oE '[0-9]+$')"
final="$(printf '%s\n' "$counts" | tail -1 | grep -oE '[0-9]+$')"
[ -n "$held" ] && [ -n "$final" ] || { echo "the chest was never opened or never counted (see $markers)"; exit 2; }
mapfile -t at < <(grep -a '\[inv\] act Take ' "$INV_LOG" | cut -c1-27 | while read -r t; do date -d "$t" +%s.%N; done)
gap="$(printf '%s\n' "${at[@]}" | awk 'NR>1 { d = $1 - p; if (m == "" || d < m) m = d } { p = $1 } END { printf "%.2f", (m == "" ? 99 : m) }')"
fail=0
check() { if eval "$2"; then echo "ok    $1"; else echo "FAIL  $1"; fail=1; fi; }
if [ "$show" = invtakeall ]; then
    check "the server emptied the chest ($held items, $final left)" '[ "$final" = 0 ]'
    check "one take per item (${#at[@]} for $held)" '[ "${#at[@]}" = "$held" ]'
    check "no two takes within half a second (closest ${gap} s)" 'awk "BEGIN { exit !($gap >= 0.5) }"'
else
    check "the server kept every item in the chest ($held, $final left)" '[ "$final" = "$held" ]'
    check "the window sent at most one take (${#at[@]})" '[ "${#at[@]}" -le 1 ]'
    check "the window said it stopped" 'grep -aq "\[inv\] act Refused" "$INV_LOG"'
fi
echo "server:"; grep -E '^CXSHOW: (before|count|filled)' "$markers" | sed 's/^/  /'
exit "$fail"
