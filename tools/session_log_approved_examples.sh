#!/usr/bin/env bash
# The harness's view of a session that rolled its log. Plants the parts; no client needed.
cd "$(dirname "$0")/.." || exit 2
T=$(mktemp -d); trap 'rm -rf "$T"' EXIT
mkdir -p "$T/home" "$T/out"
printf 'early SelfCheck FAIL\n' > "$T/home/cx.2.log"
printf 'middle\n' > "$T/home/cx.1.log"
printf 'last\n' > "$T/home/cx.log"

# Given a run-live.sh session that writes more than 10 MB
# When it grades the session
# Then its verdict and its out folder cover every part of that session
tools/live/session-log.sh "$T/home" "$T/out"
[ "$(cat "$T/out/cx.log" 2>/dev/null)" = "$(printf 'early SelfCheck FAIL\nmiddle\nlast')" ] \
  || { echo "!! session log: out/cx.log does not hold every part in order"; exit 1; }
[ "$(grep -cE 'SelfCheck.*FAIL' "$T/out/cx.log")" = 1 ] \
  || { echo "!! session log: the verdict's grep misses a rolled part"; exit 1; }
echo "session log: approved examples passed"
