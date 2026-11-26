#!/usr/bin/env bash
# reaches: docs/adr/* bin/check-adr tools/gates/*
# Every decision record passes bin/check-adr, which CI runs too, and a record numbered past the last one written in
# an older shape takes decision-record's current one. An optional folder argument is for the regression test.
cd "$(dirname "$0")/../.." || exit 2
dir=${1:-docs/adr}
last_older=59
rc=0
bash bin/check-adr "$dir"/*.md || rc=1
for f in "$dir"/[0-9][0-9][0-9][0-9]-*.md; do
  n=$((10#$(basename "$f" | cut -c1-4)))
  [ "$n" -gt "$last_older" ] || continue
  grep -qx '## Status' "$f" && continue
  echo "$f: a new record takes decision-record's current shape (Status, Context, Decision, Consequences, Guard), not an older one"
  rc=1
done
exit $rc
