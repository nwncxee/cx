#!/usr/bin/env bash
# tools/gates/workflows.sh in a scratch tree: it passes clean workflows and refuses a template injection planted in a
# workflow's run: step or in a composite action under .github/actions/.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
if ! command -v actionlint >/dev/null || ! command -v zizmor >/dev/null; then
  [ -z "${GATES_IN_SHELL:-}" ] && command -v nix >/dev/null &&
    GATES_IN_SHELL=1 exec nix develop .#gates --command bash tools/workflows_regression.sh
  echo "!! workflows regression: actionlint and zizmor must be on PATH; run inside nix develop" >&2; exit 1
fi
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
fail() { echo "!! workflows regression: $*" >&2; exit 1; }
mkdir -p "$t/tools/gates" "$t/.github/workflows" "$t/.github/actions/a"
cp tools/gates/workflows.sh "$t/tools/gates/"
cat > "$t/.github/workflows/w.yml" <<'YML'
name: w
on: pull_request
permissions: {}
jobs:
  j:
    runs-on: ubuntu-24.04
    steps:
      - uses: ./.github/actions/a
YML
cat > "$t/.github/actions/a/action.yml" <<'YML'
name: a
description: a step
runs:
  using: composite
  steps:
    - shell: bash
      run: echo hello
YML
gate() { (cd "$t" && bash tools/gates/workflows.sh) >/dev/null 2>&1; }
gate || fail "refused a clean workflow and action"
cp "$t/.github/actions/a/action.yml" "$t/clean.yml"
# shellcheck disable=SC2016 # the expression is the planted text, never expanded here
sed -i 's/echo hello/echo "${{ github.event.pull_request.title }}"/' "$t/.github/actions/a/action.yml"
! gate || fail "passed a template injection in a composite action"
cp "$t/clean.yml" "$t/.github/actions/a/action.yml"
# shellcheck disable=SC2016
printf '      - run: echo "${{ github.event.pull_request.title }}"\n' >> "$t/.github/workflows/w.yml"
! gate || fail "passed a template injection in a workflow"
echo "workflows regression: passes"
