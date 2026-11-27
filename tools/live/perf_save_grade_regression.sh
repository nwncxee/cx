#!/usr/bin/env bash
# Regression for perf_save_grade.py, in seconds and with no client: a cx.log whose frame, GPU and zone lines carry
# decimals must average to those decimals, whether the client wrote them with a dot or with a comma.
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
fail=0
expect() { # <name> <decimal mark>
    : > "$t/cx.log"
    for _ in 1 2 3 4 5 6 7 8 9 10; do
        printf 'UiHostService: [frame] avg 8%s25ms (121fps) max 9%s0ms\n' "$2" "$2" >> "$t/cx.log"
        printf 'VulkanFrame: [vkgpu] 4%s50 ms GPU per frame over 300 frames\n' "$2" >> "$t/cx.log"
        printf 'FrameZones: [zones] 8%s2ms = Seam:1%s5 Alloy:3%s7 other:0%s4  (Anim:0%s6) || worst frame 20%s0ms = Seam:9%s9\n' \
            "$2" "$2" "$2" "$2" "$2" "$2" "$2" >> "$t/cx.log"
    done
    got="$(python3 -I "$HERE/perf_save_grade.py" "$t/cx.log" run 0 "$t" | head -2 | tr '\n' ' ')"
    want="[run] frame 8.25 ms (121 fps, spread 8.25..8.25, n=8)  gpu 4.50 ms   zones Alloy:3.70 Seam:1.50 Anim:0.60 other:0.40 "
    if [ "$got" = "$want" ]; then echo "ok   $1"; else echo "FAIL $1: $got"; fail=1; fi
}
expect "a dot decimal keeps its fraction" .
expect "a comma decimal keeps its fraction" ,
exit $fail
