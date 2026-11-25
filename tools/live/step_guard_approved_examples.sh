#!/usr/bin/env bash
# Approved examples (approved-spec) for the step guard on the real Linux client: the stepguard showcase click-moves across
# open ground and round a corner, then drives into a straight wall, rams a doorway corner post six times and turns
# along it with W held, then walks and drives through a door that shuts in the player's face, walks on through one
# that shuts on them in the doorway, and holds the client's position to the server's.
# Exit 0 PASS, 1 FAIL, 2 when a run could not be made.
#
#   tools/live/step_guard_approved_examples.sh [--out DIR]
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
OUT="$ROOT/.scratch/step-guard"
case "${1:-}" in
    --out) OUT="${2:?--out needs a folder}" ;;
    "") ;;
    *) echo "step-guard: unknown argument $1" >&2; exit 2 ;;
esac
mkdir -p "$(dirname "$OUT")"
"$ROOT/tools/live/run-live.sh" --out "$OUT" --showcase stepguard --camera-mode 0 --seconds 290 \
    --cfg 'chat.enabled=0' --cfg 'general.steplog=1' > "$OUT.txt" 2>&1
[ $? = 2 ] && { echo "the run could not be made (see $OUT.txt)"; exit 2; }
grep '^CXSHOW: sg-FAILED' "$OUT/showcase-markers.txt" 2>/dev/null && exit 2
graded="$(python3 "$ROOT/tools/live/step_guard_grade.py" "$OUT")"
eval "$graded"
[ -n "${SG_SETUP:-}" ] && { echo "too little to grade: $SG_SETUP (see $OUT)"; exit 2; }
fail=0
check() { if [ "$2" = 1 ]; then echo "PASS $1 $3"; else echo "FAIL $1 $3 ($OUT)"; fail=1; fi; }

# Given the player stands 0.10 m from a wall, the spot a drive into a doorway corner leaves them
# When their next walk step runs along the wall or away from it
# Then the step guard lets it through
check E1 "$SG_E1" "$SG_E1_WHY"

# Given a walk or drive step that would cross a wall or a closed door
# When the step guard tests it
# Then the step is refused, as today
check E2 "$SG_E2" "$SG_E2_WHY"

# Given the step guard refused a step on the player's walk
# When the server's next update for the player arrives
# Then the client draws the player where the server says
check E3 "$SG_E3" "$SG_E3_WHY"

# Given the player drives into the doorway corner and releases the keys
# When the server has stopped them
# Then the client shows the player where everyone else sees them, with no pin left after keyup
check E4 "$SG_E4" "$SG_E4_WHY"

# Given the player click-moves to a point behind a corner the server's path cuts
# When the server arrives there
# Then the client draws the player within 0.1 m of the server's position
check E5 "$SG_E5" "$SG_E5_WHY"

# Given an ordinary click-move the step guard never refuses
# When it arrives
# Then it ends exactly as it does today, with no extra slide or snap
check E6 "$SG_E6" "$SG_E6_WHY"

# Given the player holds W into a wall and the step guard refuses the step
# When they turn along the wall, still holding W
# Then the player walks on along the wall without letting go of the key
check E7 "$SG_E7" "$SG_E7_WHY"
exit $fail
