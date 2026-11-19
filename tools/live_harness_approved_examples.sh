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
rm "$t/cx-linux-x64/BUILT_FROM"
stage_stale "$t/cx-linux-x64" "$t" >/dev/null; expect "an unstamped stage is refused" "$?" 1

expect "no shutdown crash, no line" "$(shutdown_crash_line 0 0 /c)" ""
expect "a shutdown crash is named, ungraded" "$(shutdown_crash_line 0 1 /c)" "shutdown crash  1 written after grading, not graded (/c)"
expect "only the new ones count" "$(shutdown_crash_line 2 3 /c)" "shutdown crash  1 written after grading, not graded (/c)"
exit $fail
