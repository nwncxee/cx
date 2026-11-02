#!/usr/bin/env bash
# Regression test for the deferred lights walk (it holds on main 65203c28, so it carries no approved-spec mark): the
# docks quicksave facing the horizon, on a base checkout built at that commit and on this one, alternating
# (deferred_walk_ab.sh). Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/deferred_walk_horizon.sh --base DIR --saves DIR [--rounds N]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Given the Waterdeep docks quicksave facing the horizon, with few lamps in view
# When the frame draws
# Then cx.log reports no more GPU time per frame than the previous build on the same save and view
exec "$ROOT/tools/live/deferred_walk_ab.sh" --label "the horizon" --expect no-worse \
    --keys "key:KP_Multiply;sleep:500;hold:Next:900;sleep:1500" "$@"
