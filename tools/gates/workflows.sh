#!/usr/bin/env bash
# Every workflow under .github/workflows/ passes actionlint: valid YAML and expressions, known runner labels
# and run: scripts with no shellcheck warning.
cd "$(dirname "$0")/../.."
if ! command -v actionlint >/dev/null || ! command -v shellcheck >/dev/null; then
  # Outside nix develop (the Claude Code hook), borrow flake.nix's pinned gates shell once.
  [ -z "$GATES_IN_SHELL" ] && command -v nix >/dev/null &&
    GATES_IN_SHELL=1 exec nix develop .#gates --command bash tools/gates/workflows.sh
  echo "!! workflows: actionlint and shellcheck must be on PATH; run inside nix develop" >&2
  exit 1
fi
SHELLCHECK_OPTS=--severity=warning actionlint .github/workflows/*.yml && exit 0
echo "!! workflows: actionlint refused a workflow (above)" >&2
exit 1
