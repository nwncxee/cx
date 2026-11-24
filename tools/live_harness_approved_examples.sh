#!/usr/bin/env bash
# The live harness's offline examples: typed text keeps its shifted characters, a stage built before the
# harness or the runtime changed is refused by name, a crash report written during shutdown gets its own
# ungraded line, a recipe fault on the frame line is named, and the step guard's grader grades its recorded runs as
# they graded live. None of them starts a client.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fail=0
# Given text the harness types into the client
# When the keymap turns it into key presses
# Then shifted characters keep their shift
python3 -I "$ROOT/tools/live/keymap_test.py" || fail=1
bash "$ROOT/tools/live/step_guard_grade_regression.sh" || fail=1

# shellcheck source=tools/live/harness-lib.sh disable=SC1091 # linted on its own
. "$ROOT/tools/live/harness-lib.sh"
expect() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: wanted [$3], got [$2]"; fail=1; fi; }

t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
git -C "$t" init -q && git -C "$t" config user.name t && git -C "$t" config user.email t@t
mkdir -p "$t/tools/live" "$t/docs" "$t/cx-linux-x64"
echo a > "$t/tools/live/run-live.sh"; echo a > "$t/docs/x.md"
git -C "$t" add -A && git -C "$t" commit -qm one
old=$(git -C "$t" rev-parse HEAD)
git -C "$t" rev-parse HEAD > "$t/cx-linux-x64/BUILT_FROM"
# Given a staged build and the commits made since it was staged
# When the harness checks the stage before it starts a client
# Then a stage of HEAD or a docs-only change runs, and a stage older than the harness or runtime is refused by name
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "a stage of HEAD runs" "$?" 0
echo b > "$t/docs/x.md"; git -C "$t" commit -qam docs
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "a docs-only change since the stage runs" "$?" 0
echo b > "$t/tools/live/run-live.sh"; git -C "$t" commit -qam harness
msg=$(stage_stale "$t/cx-linux-x64" "$t"); expect "a harness change since the stage is refused" "$?" 1
expect "the refusal names the rebuild" "$(grep -c "staged build ${old:0:8} is older than tools/live or the runtime sources" <<<"$msg")$(grep -c 'run ./build.sh' <<<"$msg")" 11
git -C "$t" rev-parse HEAD > "$t/cx-linux-x64/BUILT_FROM"
echo c > "$t/tools/live/run-live.sh"; { git -C "$t" rev-parse HEAD; echo dirty; } > "$t/cx-linux-x64/BUILT_FROM"
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "a stage built from uncommitted edits runs" "$?" 0
touch -d "-1 min" "$t/cx-linux-x64/BUILT_FROM"; git -C "$t" checkout -q tools/live/run-live.sh
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "edits reverted after a dirty build are refused" "$?" 1
git -C "$t" rev-parse HEAD > "$t/cx-linux-x64/BUILT_FROM"; touch -d "-1 min" "$t/cx-linux-x64/BUILT_FROM"; echo n > "$t/tools/live/new.sh"
msg=$(stage_stale "$t/cx-linux-x64" "$t"); expect "a new untracked harness file is refused" "$?" 1
expect "the refusal names the uncommitted file" "$(grep -c 'tools/live/new.sh' <<<"$msg")" 1
rm "$t/tools/live/new.sh" "$t/cx-linux-x64/BUILT_FROM"
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "an unstamped stage is refused" "$?" 1

# A stage that build.sh kept, or built from edits committed since, is the build of the tree when its stamped
# code fingerprint is the tree's own, whatever commit the stamp names; a fingerprint that differs is refused.
cp "$ROOT/tools/codehash.py" "$t/tools/" && echo cx-linux-x64/ > "$t/.gitignore"
git -C "$t" add tools/codehash.py .gitignore && git -C "$t" commit -qm codehash
built=$(git -C "$t" rev-parse HEAD); echo d > "$t/tools/live/run-live.sh"
{ echo "$built"; echo dirty; echo "code $(python3 "$t/tools/codehash.py" "$t")"; } > "$t/cx-linux-x64/BUILT_FROM"
git -C "$t" commit -qam "the edits the stage was built from"
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "a dirty build whose edits were then committed runs" "$?" 0
echo e > "$t/tools/live/run-live.sh"; git -C "$t" commit -qam "a change after the build"
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "a stamped fingerprint the tree no longer has is refused" "$?" 1
rm "$t/cx-linux-x64/BUILT_FROM"

# The real script, not only its helper: a stage stamped with a commit this checkout lacks stops at exit 2,
# however shallow the checkout.
mkdir -p "$t/stage" && touch "$t/stage/libcx.so" "$t/stage/cx-launch"
echo 0000000000000000000000000000000000000000 > "$t/stage/BUILT_FROM"
err=$(STAGE_DIR="$t/stage" timeout 30 bash "$ROOT/tools/live/run-live.sh" 2>&1 >/dev/null); rc=$?
expect "run-live.sh refuses a stale stage with exit 2" "$rc" 2
expect "run-live.sh names the rebuild" "$(grep -c 'run ./build.sh' <<<"$err")" 1

expect "no shutdown crash, no line" "$(shutdown_crash_line 0 0 /c)" ""
expect "a shutdown crash is named, ungraded" "$(shutdown_crash_line 0 1 /c)" "shutdown crash  1 written after grading, not graded"
expect "only the new ones count" "$(shutdown_crash_line 2 3 /c)" "shutdown crash  1 written after grading, not graded"
expect "a count that is not a number prints no line" "$(shutdown_crash_line '' 3 /c 2>&1)" ""
# Given a ladder session's frame lines
# When the harness grades the recipe table
# Then each fault clause the frame line prints is named, and a clean frame line names none
f="$t/cx.log"
keys='[cx] dbfeed: recipe fault keys: RECIPES SPILLED|models evicted for recipes|RECIPES EVICTED IN USE|RECIPES RETIRED TWICE'
printf '%s\n%s\n' "$keys" 'dbfeed: frames 120 recipes 900 retired 69 box-grown 67' > "$f"
expect "a clean frame line names no fault" "$(recipe_fault_lines "$f")" ""
for clause in 'RECIPES SPILLED 3' 'models evicted for recipes 2' 'RECIPES EVICTED IN USE 14' 'RECIPES RETIRED TWICE 1'; do
    printf '%s\n%s\n' "$keys" "dbfeed: frames 240 recipes 900 $clause retired 69" > "$f"
    expect "the frame line's $clause is named" "$(recipe_fault_lines "$f")" "$clause"
done
printf '%s\n%s\n' '[cx] dbfeed: recipe fault keys: TABLE OVERFLOW|models dropped for room' 'dbfeed: frames 240 models dropped for room 5 TABLE OVERFLOW 2' > "$f"
expect "renamed keys are read from the session's own log" "$(recipe_fault_lines "$f")" "TABLE OVERFLOW 2,models dropped for room 5"
printf '%s\n%s\n%s\n' "$keys" "$keys" 'dbfeed: frames 240 RECIPES SPILLED 3' > "$f"
expect "a log that booted twice still names a fault" "$(recipe_fault_lines "$f")" "RECIPES SPILLED 3"
printf '%s\n%s\n%s\n' "$keys" 'dbfeed: frames 120 RECIPES SPILLED 3' 'dbfeed: frames 240 RECIPES SPILLED 3' > "$f"
expect "a count two frame lines repeat is named once" "$(recipe_fault_lines "$f")" "RECIPES SPILLED 3"
echo 'dbfeed: frames 240 RECIPES SPILLED 3' > "$f"
expect "a frame line with no key line is a fault, not a pass" "$(recipe_fault_lines "$f")" "no recipe fault keys in cx.log"
echo '[cx] Alloy stood down: no Vulkan device' > "$f"
expect "a session with no frame line names no fault" "$(recipe_fault_lines "$f")" ""
exit $fail
