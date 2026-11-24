#!/usr/bin/env bash
# The CI path map's sample table: one path per arm, each with the jobs it must and must not start.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 1
fail=0
row() { # <path> <build> <windows>
  got=$(printf '%s\n' "$1" | bash tools/ci-changes.sh | tr '\n' ' ')
  want="build=$2 windows=$3 "
  [ "$got" = "$want" ] || { echo "!! ci-changes $1: got '$got', want '$want'" >&2; fail=1; }
}
# Given a pull request that changes a path both builds read
# When tools/ci-changes.sh maps its changed paths
# Then it starts the Linux build and the Windows build
row managed/Alloy/AlloyRenderer.cs true true
row native/bootstrap.c true true
row third_party/funchook/LICENSE true true
row build-win-native.sh true true
row tools/ffx/prepare.sh true true
row tools/deps.json true true
row tools/analyzers/EngineGl.cs true true
row packaging/README-win.txt true true
row packaging/allowed_signers true true
row LICENSE true true
row .github/workflows/ci.yml true true
row tools/ci-changes.sh true true
# Given a pull request that changes a path only the Linux build reads
# When tools/ci-changes.sh maps its changed paths
# Then it starts the Linux build and not the Windows build
row build.sh true false
row tools/ssl-by-name.sh true false
row .github/actions/ffx-cache/action.yml true false
row run-demo.sh true false
row tools/live/run-live.sh true false
row tools/live/revisit.py true false
row tools/live/step_guard_grade.py true false
row tools/live/step_guard_fixtures/branch/cx.log true false
row tools/live/step_guard_grade_regression.sh true false
row tools/selfcheck/run.sh true false
row tools/release/sign.sh true false
row tools/gates/comments.sh true false
row packaging/cx-launch true false
row docs/spec/unified-settings.md true false
row README.md true false
# Given a pull request that changes a path no build reads
# When tools/ci-changes.sh maps its changed paths
# Then it starts neither build
row docs/adr/0031-coding-standards.md false false
row .github/workflows/ffx-cache.yml false false
row media/shot.webp false false
row newfolder/thing.txt false false
# A rename reaches the build through either name; two paths, one answer.
got=$(printf 'docs/a.md\nmanaged/X.cs\n' | bash tools/ci-changes.sh | tr '\n' ' ')
[ "$got" = "build=true windows=true " ] || { echo "!! ci-changes two paths: got '$got'" >&2; fail=1; }
# A blank line is skipped, not the end of the list.
got=$(printf '\nmanaged/X.cs\n' | bash tools/ci-changes.sh | tr '\n' ' ')
[ "$got" = "build=true windows=true " ] || { echo "!! ci-changes blank line: got '$got'" >&2; fail=1; }
got=$(printf '' | bash tools/ci-changes.sh | tr '\n' ' ')
[ "$got" = "build=false windows=false " ] || { echo "!! ci-changes no paths: got '$got'" >&2; fail=1; }
[ $fail = 0 ] && echo ">> ci-changes approved examples: every row passes"
exit $fail
