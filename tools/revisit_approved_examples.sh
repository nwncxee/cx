#!/usr/bin/env bash
# The ladder's revisit grade (tools/live/revisit.py) on recorded sessions: the cx.log lines a ladder run writes --
# a [load] line per area transition, a [vkgpu] line and a [frame] line per period -- planted here. No client needed;
# the same grade on a real run is run-live.sh --ladder.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
T=$(mktemp -d) && trap 'rm -rf "$T"' EXIT
fail=0
s=0

# <file> <msg>: one cx.log line, a second after the one before.
line() { s=$((s + 1)); printf '2026-11-21T20:%02d:%02d.1000000+01:00 [cx] %s\n' $((s / 60)) $((s % 60)) "$2" >> "$1"; }
load() { line "$1" "TexStream: [load] LoadArea 0,18 s (0 builds, 0 ms) [offers 0 ms, engine demands 51 ms over 269 (models 0, other 51), cx-load busy 0 ms] | to the send 0,48 s (0 builds, 0 ms) | held 0,88 s (0 builds, 0 ms)"; }
# <file> <gpu ms> <used MiB> <budget MiB>: one reporting period, its [vkgpu] line and its frame line.
period() {
  line "$1" "VulkanFrame: [vkgpu] $2 ms GPU per frame over 300 frames on the graphics queue (bake, cull, prepass, hi-Z, AO, decals, G-buffer, opaque, cutout, sky, the deferred dispatches, blended; not the async froxel build and fog); the CPU waited 0,01 ms/frame on the GPU timeline"
  local over=""; [ "$3" -gt "$4" ] && over=", OVER BUDGET by $(($3 - $4))MiB"
  line "$1" "UiHostService: [frame] avg 6,90ms (145fps) max 12,1ms, >6.9ms 40/900, >16.7ms 0/900, gc 0/0/0 on 12KiB allocated, vram 2900MiB held, $3/$4MiB of the budget used$over; largest arena Static vertices 384MiB"
}
# <file> <used MiB> <budget MiB> <first visit ms> <second area ms> <revisit ms>: a ladder session, three periods an area.
ladder() {
  s=0; : > "$1"
  line "$1" "AlloyRenderer: [vram] VK_EXT_memory_budget, $3MiB device-local, $3MiB free"
  local ms
  for ms in "$4" "$5" "$6"; do
    load "$1"; period "$1" "$ms" "$2" "$3"; period "$1" "$ms" "$2" "$3"; period "$1" "$ms" "$2" "$3"
  done
}
# A frame line with no Alloy part and no [vkgpu] line: the session the stock renderer drew.
stood_down() {
  s=0; : > "$1"
  line "$1" "AlloyRenderer: stood down -- the Vulkan device was refused"
  local i
  for i in 1 2 3; do
    load "$1"
    line "$1" "UiHostService: [frame] avg 7,40ms (135fps) max 13,0ms, >6.9ms 60/900, >16.7ms 0/900, gc 0/0/0 on 10KiB allocated"
    line "$1" "UiHostService: [frame] avg 7,40ms (135fps) max 13,0ms, >6.9ms 60/900, >16.7ms 0/900, gc 0/0/0 on 10KiB allocated"
  done
}
no_gpu_time=$(python3 -I -c 'import sys; sys.path.insert(0, sys.argv[1]); import revisit; print(revisit.NO_GPU_TIME)' "$ROOT/tools/live")
grade() { out=$(python3 -I "$ROOT/tools/live/revisit.py" "$1" 2>&1); rc=$?; }
no() { echo "!! revisit $1: $2 -- exit $rc, said: $out" >&2; fail=1; }

# Given main, before any other star on this chart, and the ladder run with the lowest cap that makes it overflow, recorded in docs/live-harness.md
# When the run returns to its first area
# Then the run FAILS, naming the first visit's GPU time and the revisit's, more than 1.5 times it
ladder "$T/overflow.log" 3400 3072 2,25 4,00 4,10
grade "$T/overflow.log"
[ "$rc" = 1 ] && grep -q '^FAIL' <<<"$out" || no E3 "a revisit at 4.10 ms after a first visit at 2.25 ms does not FAIL"
grep -qE '2[.,]25' <<<"$out" && grep -qE '4[.,]10' <<<"$out" || no E3 "the FAIL does not name the first visit's 2.25 ms and the revisit's 4.10 ms"

# Given the same run after the device-memory, texture-budget and pressure stars
# When the run returns to its first area
# Then it PASSES, the revisit's GPU time within 1.5 times the first visit's
ladder "$T/fixed.log" 2950 3072 2,25 6,00 2,60
grade "$T/fixed.log"
[ "$rc" = 0 ] && grep -q '^PASS' <<<"$out" || no E4 "a revisit at 2.60 ms after a first visit at 2.25 ms (the second area at 6.00) does not PASS"
grep -qE '2[.,]25' <<<"$out" && grep -qE '2[.,]60' <<<"$out" || no E4 "the PASS does not name the first visit's 2.25 ms and the revisit's 2.60 ms"

# Given Alloy stood down
# When the ladder runs
# Then it FAILS, since no GPU time was measured
stood_down "$T/down.log"
grade "$T/down.log"
[ "$rc" = 1 ] && grep -q '^FAIL' <<<"$out" && grep -qF "$no_gpu_time" <<<"$out" || no E9 "a session with no [vkgpu] line does not FAIL saying no GPU time was measured"

# Given alloy.vramcap 0 on main
# When the ladder runs
# Then it PASSES (the control run)
ladder "$T/control.log" 2400 7800 2,25 3,00 2,30
grade "$T/control.log"
[ "$rc" = 0 ] && grep -q '^PASS' <<<"$out" || no E10 "the uncapped control, revisit 2.30 ms after 2.25 ms, does not PASS"
grep -qE '2[.,]25' <<<"$out" && grep -qE '2[.,]30' <<<"$out" || no E10 "the control's PASS does not name the first visit's 2.25 ms and the revisit's 2.30 ms"

# Found, not approved: a stood-down session that wrote no [load] line at all still fails on its missing GPU time.
s=0; : > "$T/bare.log"
line "$T/bare.log" "UiHostService: [frame] avg 7,40ms (135fps) max 13,0ms, >6.9ms 60/900, >16.7ms 0/900, gc 0/0/0 on 10KiB allocated"
grade "$T/bare.log"
[ "$rc" = 1 ] && grep -qF "$no_gpu_time" <<<"$out" || no found "a session with no [load] and no [vkgpu] line does not FAIL saying no GPU time was measured"

# Found, not approved: a session that never returns to the first area fails, rather than grading the second area.
ladder "$T/stuck.log" 2950 3072 2,25 2,30 2,30
sed -i "$(grep -n 'TexStream: \[load\]' "$T/stuck.log" | tail -1 | cut -d: -f1),\$d" "$T/stuck.log"
grade "$T/stuck.log"
[ "$rc" = 1 ] && grep -q '^FAIL' <<<"$out" || no found "a session of two area visits does not FAIL"

# Found, not approved: a session that loaded areas but measured no GPU time says so, before it counts the visits.
sed '/\[vkgpu\]/d' "$T/stuck.log" > "$T/dark.log"
grade "$T/dark.log"
[ "$rc" = 1 ] && grep -qF "$no_gpu_time" <<<"$out" || no found "a session of two area loads and no [vkgpu] line does not FAIL saying no GPU time was measured"

# Found, not approved: a load spike in the first visit's first period does not hide a slow return.
ladder "$T/spike.log" 3400 3072 2,25 4,00 4,10
sed -i "0,/\[vkgpu\] 2,25 ms/s//[vkgpu] 9,00 ms/" "$T/spike.log"
grade "$T/spike.log"
[ "$rc" = 1 ] && grep -qE '2[.,]25' <<<"$out" || no found "a 9 ms spike in the first visit's first period hides a revisit at 4.10 ms after 2.25 ms"

[ "$fail" = 0 ] && echo "revisit: approved examples passed"
exit "$fail"
