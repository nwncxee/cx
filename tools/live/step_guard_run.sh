#!/usr/bin/env bash
# One real client in the stepguard showcase with general.steplog on, graded by step_guard_grade.py. Sourced by the
# step guard tests: step_guard_run DEFAULT_OUT [--out DIR] runs it and sets SG_E1..SG_E7 (1 pass, 0 fail), each with
# its SG_E<n>_WHY, and SG_OUT. Returns 2 when the run could not be made or holds too little to grade.
# shellcheck disable=SC2034 # the SG_ results are read by the scripts that source this
step_guard_run() {
    local out="$1" root status graded
    root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
    case "${2:-}" in
        --out) out="${3:?--out needs a folder}" ;;
        "") ;;
        *) echo "step-guard: unknown argument $2" >&2; return 2 ;;
    esac
    SG_OUT="$out"
    mkdir -p "$(dirname "$out")"
    "$root/tools/live/run-live.sh" --out "$out" --showcase stepguard --camera-mode 0 --seconds 225 \
        --cfg 'chat.enabled=0' --cfg 'general.steplog=1' > "$out.txt" 2>&1
    status=$?
    [ "$status" = 2 ] && { echo "the run could not be made (see $out.txt)"; return 2; }
    grep -q '^CXSHOW: sg-FAILED' "$out/showcase-markers.txt" 2>/dev/null \
        && { grep '^CXSHOW: sg-FAILED' "$out/showcase-markers.txt"; return 2; }
    graded="$(python3 "$root/tools/live/step_guard_grade.py" "$out")" \
        || { eval "$graded"; echo "too little to grade: ${SG_SETUP:-?} (see $out)"; return 2; }
    eval "$graded"
    return 0
}
