#!/usr/bin/env bash
# Approved examples (approved-spec) for "The area's pipelines are built under the loading hold", on the real client:
# the docks quicksave loaded by perf-save.sh, whose throwaway home starts with an empty shader and pipeline cache,
# graded from the cx.log it leaves. Exit 0 PASS, 1 FAIL, 2 when the run itself could not be made.
#
#   tools/live/prewarm_approved_examples.sh [--saves DIR]   # DIR holds the docks save folder alone
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
DOCKS_WHO=prewarm
. "$ROOT/tools/live/docks_run.sh"
SAVES=""
[ "${1:-}" = "--saves" ] && SAVES="${2:-}"
docks_saves "$SAVES" || exit 2
OUT="$(docks_run prewarm)" || exit 2
LOG="$OUT/cx.log"

# Given an empty shader and pipeline cache
# When the player enters the docks and the loading screen lifts
# Then cx.log counts zero pipelines built on the frame for the area's own materials in the first ten seconds
lines="$(grep -a "\[vkpipe\] after the loading screen" "$LOG")"
[ -n "$lines" ] || { echo "FAIL: cx.log has no '[vkpipe] after the loading screen' line ($LOG)"; exit 1; }
bad="$(grep -Ev " 0 material pipelines built on the frame in the first 10 s" <<<"$lines")"
[ -z "$bad" ] || { echo "FAIL: the area's materials still built pipelines on the frame after the loading screen:"; echo "$bad"; exit 1; }
echo "PASS: $(wc -l <<<"$lines") area entry(ies), no material pipeline built on the frame in the first 10 s ($LOG)"
