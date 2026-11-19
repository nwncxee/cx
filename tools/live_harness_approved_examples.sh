#!/usr/bin/env bash
# The live harness's offline examples: typed text keeps its shifted characters, a stage built before the
# harness or the runtime changed is refused by name, and a crash report written during shutdown gets its own
# ungraded line. None of them starts a client.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fail=0
python3 -I "$ROOT/tools/live/keymap_test.py" || fail=1

. "$ROOT/tools/live/harness-lib.sh"
expect() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: wanted [$3], got [$2]"; fail=1; fi; }

t=$(mktemp -d) && trap 'rm -rf "$t"' EXIT
git -C "$t" init -q && git -C "$t" config user.name t && git -C "$t" config user.email t@t
mkdir -p "$t/tools/live" "$t/docs" "$t/cx-linux-x64"
echo a > "$t/tools/live/run-live.sh"; echo a > "$t/docs/x.md"
git -C "$t" add -A && git -C "$t" commit -qm one
old=$(git -C "$t" rev-parse HEAD)
git -C "$t" rev-parse HEAD > "$t/cx-linux-x64/BUILT_FROM"
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
exit $fail
