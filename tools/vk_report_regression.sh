#!/usr/bin/env bash
# Regression check, not an approved test: tools/vk/report.py reads a validation-layer log, and these held before the
# change that reuses its parser for message ids. Fixture logs only -- no GPU, no layer, no client -- so build.sh runs
# it. Exit 0 PASS, 1 FAIL.
#
# The first case is the one that cost a run: a layer log carries object names out of the engine and is not text, so
# `grep -oP` over one prints nothing at all, which reads exactly like a clean log.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
R="$ROOT/tools/vk/report.py"
WORK="$(mktemp -d "${TMPDIR:-/tmp}/cx-vk-report-XXXX")"
trap 'rm -rf "$WORK"' EXIT
SYNC=VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION_EXT
fails=0
ok() { echo "  ok: $1"; }
bad() { echo "FAIL: $1" >&2; fails=$((fails + 1)); }

# A log holding the layer's Active notice, $2's Current Enables, and the finding blocks named in $3...; a NUL byte
# goes in an object name, as the layer writes it when the engine's own name holds one.
fixture() {
    local f="$WORK/$1" enables="$2"; shift 2
    {
        printf 'Validation Information: [ WARNING-CreateInstance-status-message ] | MessageID = 0x23dfd876\n'
        printf 'vkCreateInstance(): Khronos Validation Layer Active:\n'
        printf '    Current Enables: %s.\n\n' "$enables"
        for id in "$@"; do
            printf 'Validation Error: [ %s ] | MessageID = 0x779fc816\n' "$id"
            printf 'vkCmdCopyBuffer(): srcBuffer (VkBuffer 0x2e49[a name with a \0 in it]) is wrong.\n'
            printf 'Objects: 1\n\n'
        done
    } > "$f"
    printf '%s\n' "$f"
}

# 1. A log that is not text still yields its ids
log="$(fixture binary "$SYNC" VUID-vkCmdDraw-None-09600 SYNC-HAZARD-WRITE-AFTER-WRITE)"
grep -q . "$log" || bad "the fixture log is empty"
got="$(python3 -I "$R" --ids "$log" | tr '\n' ' ')"
want="SYNC-HAZARD-WRITE-AFTER-WRITE VUID-vkCmdDraw-None-09600 WARNING-CreateInstance-status-message "
if [ "$got" = "$want" ]; then ok "a log with a NUL byte in an object name yields every id"
else bad "the ids of a log with a NUL byte came out as '$got', wanted '$want'"; fi

# ... and the grep that reads as clean is why: this is the shape --ids replaced, kept as the comparison
if [ -z "$(grep -oP '^Validation Error: \[ \K[^ ]+' "$log" 2>/dev/null)" ]; then
    ok "grep -oP over the same log prints nothing, which is the bug --ids exists for"
else bad "grep -oP read the fixture log after all, so this case no longer shows what it was for"; fi

# 2. A clean log is a pass, a log with a finding is not
clean="$(fixture clean "$SYNC")"
python3 -I "$R" "$clean" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 0 ] && ok "a log with the notice, the right Enables and no finding passes" || bad "a clean log did not pass"
python3 -I "$R" "$log" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 1 ] && ok "a log with findings fails" || bad "a log with two findings did not fail"

# 3. The three positive controls: a missing log, a missing Active notice, and the wrong feature all fail
python3 -I "$R" "$WORK/not-here" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 1 ] && ok "a log the layer never wrote fails" || bad "a missing log did not fail"
# no findings in it, so the missing notice is the only thing left to fail on
printf 'something else wrote this file\n' > "$WORK/silent"
python3 -I "$R" "$WORK/silent" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 1 ] && ok "a log with no Active notice fails even with no finding in it" || bad "a log the layer was never in did not fail"
other="$(fixture other VK_VALIDATION_FEATURE_ENABLE_GPU_ASSISTED_EXT)"
python3 -I "$R" "$other" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 1 ] && ok "a log whose Enables names another feature fails" || bad "the wrong pass's log did not fail"

# 4. The layer's own notices are not findings
own="$(fixture own "$SYNC" WARNING-CreateDevice-status-message VALIDATION-SETTINGS WARNING-Setting-Limit-Adjusted)"
python3 -I "$R" "$own" "pass a" "$SYNC" >/dev/null 2>&1
[ $? = 0 ] && ok "the layer talking about itself is not a finding" || bad "the layer's own notices were read as findings"

[ "$fails" = 0 ] && { echo "PASS: report.py answers all 8 cases"; exit 0; }
echo "FAIL: $fails of the report.py cases" >&2
exit 1
