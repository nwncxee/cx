#!/usr/bin/env bash
# The leg-replay tool's approved example. It builds its fixture itself (replay.sh --fixture) because a recording
# is a struct dump and a committed one would go stale with the next field the leg solve gains. The expected lines
# are worked by hand from the fixture's construction, not read back from the grader:
#   body 1 is the synthetic walk (GaitClip.SyntheticFoot) at its own pace on flat floors: no correction is needed,
#     and every foot on the ground stands still in the world, so all three grades are zero.
#   body 3 is that walk entered mid-stride by a body the layer was already tracking, facing a world heading other than 0:
#     nothing turns the hips, so all three grades are zero.
#   body 2 stands a foot 0.1 above the other on a floor step, thigh 0.77 above its ankle, the animated ankle sliding
#     0.5 units/s while in contact: the ankle is raised 0.1, so the leg shortens by 0.1 / 0.77 = 0.1299 of its
#     hip-to-ankle distance, the foot slides at 0.500 units/s, and the hips never turn.
cd "$(dirname "$0")/.." || exit 2
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT

# Given a recording of a play session made with the client
# When a developer replays it with the replay tool
# Then the tool prints, for each character, the most extra knee bend, the most foot slide and the fastest hip turn, and prints the same numbers every time
tools/anim/replay.sh --fixture "$T/session.cxlegs" >"$T/make.log" 2>&1 \
  || { cat "$T/make.log"; echo "!! anim replay: could not write the fixture recording"; exit 1; }
tools/anim/replay.sh "$T/session.cxlegs" >"$T/one.txt" 2>"$T/one.err" \
  || { cat "$T/one.err"; echo "!! anim replay: the replay failed"; exit 1; }
tools/anim/replay.sh "$T/session.cxlegs" >"$T/two.txt" 2>&1 \
  || { echo "!! anim replay: the second replay failed"; exit 1; }
cmp -s "$T/one.txt" "$T/two.txt" || { diff "$T/one.txt" "$T/two.txt"; echo "!! anim replay: two replays of one recording differ"; exit 1; }
want=$(printf '%s\n' \
  'body 1 most extra knee bend 0.0000 of hip-to-ankle distance' \
  'body 1 most foot slide 0.000 units/s' \
  'body 1 fastest hip turn 0.00 deg/s' \
  'body 2 most extra knee bend 0.1299 of hip-to-ankle distance' \
  'body 2 most foot slide 0.500 units/s' \
  'body 2 fastest hip turn 0.00 deg/s' \
  'body 3 most extra knee bend 0.0000 of hip-to-ankle distance' \
  'body 3 most foot slide 0.000 units/s' \
  'body 3 fastest hip turn 0.00 deg/s')
[ "$(cat "$T/one.txt")" = "$want" ] \
  || { diff <(echo "$want") "$T/one.txt"; echo "!! anim replay: the grades are not the hand-worked ones"; exit 1; }
echo "anim replay: approved examples passed"
