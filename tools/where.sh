#!/usr/bin/env bash
# Says where a checkout is, whether its staged runtime is the build of what is checked out, and the exact
# command to run it. Every report of work on a worktree pastes this output, so the reader can test it.
# Usage: tools/where.sh [checkout]   (default: the checkout this script lives in)
dir=$(realpath -- "${1:-$(cd "$(dirname "$0")/.." && pwd)}")
cd "$dir" || exit 1
head=$(git rev-parse HEAD)
out="$dir/cx-linux-x64"

echo "checkout  $dir"
echo "branch    $(git branch --show-current || echo '(detached)')  at $(git log -1 --format='%h %s')"

if [ ! -x "$out/cx-launch" ]; then
  built="NOT BUILT -- run ./build.sh in the checkout first"
elif [ ! -f "$out/BUILT_FROM" ]; then
  built="UNKNOWN -- built before build.sh stamped its commit; rebuild to be sure"
else
  from=$(sed -n 1p "$out/BUILT_FROM")
  was=$(git log -1 --format='%h' "$from" 2>/dev/null || echo "${from:0:8}")
  # A stamped code fingerprint decides, by the same test build.sh keeps a build by: equal means only comments or
  # Markdown moved since the build.
  if bash tools/keep-staged.sh >/dev/null && grep -qx dirty "$out/BUILT_FROM" \
    && [ -n "$(git status --porcelain --untracked-files=no --ignore-submodules=all)" ]; then
    built="UNCOMMITTED -- built from $was plus uncommitted edits, which the tree still holds"
  elif bash tools/keep-staged.sh >/dev/null; then
    built="CURRENT -- built from $was, same code as ${head:0:8}"
  elif grep -q '^code ' "$out/BUILT_FROM"; then
    built="STALE -- built from $was, and the code changed since; rebuild"
  elif [ "$from" != "$head" ]; then
    built="STALE -- built from $was, checkout is at ${head:0:8}; rebuild"
  elif grep -qx dirty "$out/BUILT_FROM"; then
    built="UNCOMMITTED -- built from ${head:0:8} plus uncommitted edits"
  elif [ -n "$(git status --porcelain --untracked-files=no --ignore-submodules=all)" ]; then
    built="STALE -- built from ${head:0:8}, but tracked files changed since; rebuild"
  else
    built="CURRENT -- built from ${head:0:8}, nothing changed since"
  fi
fi
echo "build     $built"

nwn=$(python3 tools/paths.py NWN_BIN 2>/dev/null || echo /path/to/nwmain-linux)
echo "run       NWN_BIN=\"$nwn\" \"$out/cx-launch\""
echo "steam     Launch Options: \"$out/cx-launch\" %command%"
