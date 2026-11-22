#!/usr/bin/env bash
# Boots the fast self-check path on an older commit, for the approved examples that compare against "before".
# Prints the path of that boot's cx.log, then the boot's wall time in seconds on a second line. The older tree is a
# detached worktree of its own, borrowing this checkout's build/ for the native libraries. Its IL build is kept in
# build/selfcheck-baseline-<rev>/ and reused, but every call boots it again, so the time is taken under the same load
# as the caller's own boot. Run it inside nix develop. Linux-only, like the rest of the developer tooling.
# Exit 0, or 2 for a setup problem.
#
#   tools/selfcheck/baseline.sh <rev>
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
repo=$PWD
command -v dotnet >/dev/null || { echo ">> run this inside nix develop" >&2; exit 2; }
rev=$(git rev-parse --short=8 "${1:?usage: baseline.sh <rev>}^{commit}") || exit 2
out=$repo/build/selfcheck-baseline-$rev
wt=$(mktemp -d "${TMPDIR:-/tmp}/cx-baseline.XXXXXX")
trap 'git -C "$repo" worktree remove --force "$wt" >/dev/null 2>&1; rm -rf "$wt"' EXIT
git worktree add -q --detach "$wt" "$rev" || exit 2
ln -s "$repo/build" "$wt/build"
# The staged runtime too: a suite that reads it from the working directory would SKIP in a tree without one.
[ -d "$repo/cx-linux-x64" ] && ln -s "$repo/cx-linux-x64" "$wt/cx-linux-x64"
rm -rf "$wt/third_party/funchook" && ln -s "$repo/third_party/funchook" "$wt/third_party/funchook"
mkdir -p "$out"
if [ ! -f "$out/bin/Release/net10.0/linux-x64/cx_managed.dll" ]; then
  "$wt/tools/selfcheck/run.sh" --out "$out" > "$out/compile.out" 2>&1 || { echo ">> the baseline at $rev failed:" >&2; tail -20 "$out/compile.out" >&2; exit 2; }
fi
start=$(date +%s%N)
"$wt/tools/selfcheck/run.sh" --out "$out" --no-build > "$out/boot.out" 2>&1 || { echo ">> the baseline boot at $rev failed:" >&2; tail -5 "$out/boot.out" >&2; exit 2; }
echo "$out/cx.log"
awk -v n=$(( ($(date +%s%N) - start) / 1000000 )) 'BEGIN { printf "%.1f\n", n / 1000 }'
