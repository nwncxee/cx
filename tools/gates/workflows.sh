#!/usr/bin/env bash
# Every workflow under .github/workflows/ passes actionlint (valid YAML and expressions, known runner labels,
# run: scripts with no shellcheck warning) and zizmor's offline security audits (no cache feeding a release,
# no persisted checkout token, no template injection).
cd "$(dirname "$0")/../.." || exit 1
if ! command -v actionlint >/dev/null || ! command -v shellcheck >/dev/null || ! command -v zizmor >/dev/null; then
  # Outside nix develop (the Claude Code hook), borrow flake.nix's pinned gates shell once.
  [ -z "$GATES_IN_SHELL" ] && command -v nix >/dev/null &&
    GATES_IN_SHELL=1 exec nix develop .#gates --command bash tools/gates/workflows.sh
  echo "!! workflows: actionlint, shellcheck and zizmor must be on PATH; run inside nix develop" >&2
  exit 1
fi
SHELLCHECK_OPTS=--severity=warning actionlint .github/workflows/*.yml ||
  { echo "!! workflows: actionlint refused a workflow (above)" >&2; exit 1; }
zizmor --offline --quiet .github/workflows/ ||
  { echo "!! workflows: zizmor refused a workflow (above)" >&2; exit 1; }
