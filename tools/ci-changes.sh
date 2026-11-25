#!/usr/bin/env bash
# The CI path map: reads a pull request's changed paths on stdin, one per line, and prints which jobs they reach.
# build: build.sh and run-demo.sh, the Linux build and its checks. windows: build-win-native.sh, the Windows build.
# A path no list names reaches neither job; it is docs, media or a tool no build reads. When build.sh or a
# script it calls reads a new file, add it to linux; when build-win-native.sh does, add it to windows too.
set -euo pipefail
# Read by both builds.
both=(
  'managed/*'
  'native/*'
  'third_party/*'
  'tools/analyzers/*'
  'tools/ffx/*'
  tools/deps.json
  tools/deps.py
  build-win-native.sh
  packaging/licenses.sh
  packaging/README-win.txt
  packaging/allowed_signers
  LICENSE
  .gitmodules
  .github/workflows/ci.yml
  tools/ci-changes.sh
)
# Read only by the Linux build. LICENSE and third_party/ are above because licenses.sh copies their notices; the
# settings spec is here for the knob-spec-rows gate, and README.md for notice_approved_examples.sh's verify lines.
linux=(
  build.sh
  .github/actions/ffx-cache/action.yml
  run-demo.sh
  flake.nix
  flake.lock
  'tools/engine/*'
  'tools/anim/*'
  'tools/gates/*'
  tools/build-openal.sh
  tools/approved-examples.sh
  tools/no-loaded-runtime.sh
  tools/codehash.py
  tools/keep-staged.sh
  tools/ssl-by-name.sh
  tools/stub-host.sh
  tools/where.sh
  'tools/selfcheck/*'
  tools/live/session-log.sh
  tools/live/docks_run.sh
  tools/live/keymap.py
  tools/live/keymap_test.py
  tools/live/harness-lib.sh
  tools/live/xtool.py
  tools/live/run-live.sh
  tools/live/revisit.py
  tools/live/step_guard_grade.py
  'tools/live/step_guard_fixtures/*'
  tools/vk/report.py
  tools/vk/mutants.sh
  tools/vk/mutate.py
  'tools/*_approved_examples*'
  'tools/*_regression*'
  'tools/release/*'
  tools/paths.py
  docs/spec/unified-settings.md
  packaging/cx-launch
  packaging/README.txt
  README.md
)
matches() { # <path> <pattern>...
  local f=$1 p; shift
  # shellcheck disable=SC2053 # $p is a glob pattern on purpose
  for p in "$@"; do [[ $f == $p ]] && return 0; done
  return 1
}
build=false windows=false
while IFS= read -r f; do
  [ -n "$f" ] || continue
  if matches "$f" "${both[@]}"; then build=true windows=true
  elif matches "$f" "${linux[@]}"; then build=true
  fi
done
echo "build=$build"
echo "windows=$windows"
