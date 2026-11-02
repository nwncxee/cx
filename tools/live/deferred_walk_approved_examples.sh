#!/usr/bin/env bash
# Approved examples (approved-spec) for the deferred lights walk, on the real client: the docks quicksave loaded by
# perf-save.sh from a base checkout built at main 65203c28 and from this one, alternating (deferred_walk_ab.sh, which
# also holds the verdict). Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/deferred_walk_approved_examples.sh --base DIR --saves DIR [--rounds N]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# Given the Waterdeep docks quicksave at 2544x1378 on the RTX 3070, facing the lit quay
# When the frame draws
# Then cx.log reports less GPU time per frame than the previous build (main 65203c28) on the same save and view
exec "$ROOT/tools/live/deferred_walk_ab.sh" --label "the lit quay" --expect less "$@"
