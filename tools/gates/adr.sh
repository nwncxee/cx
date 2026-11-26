#!/usr/bin/env bash
# reaches: docs/adr/* bin/check-adr tools/gates/*
# Every decision record passes bin/check-adr, which CI runs too, and a record numbered past the last one written in
# an older shape takes decision-record's current one. An optional folder argument is for the regression test.
cd "$(dirname "$0")/../.." || exit 2
dir=${1:-docs/adr}
# The last record written in an older shape: every record after it takes the current one.
last_older=60
rc=0
bash bin/check-adr "$dir"/*.md || rc=1
for f in "$dir"/[0-9][0-9][0-9][0-9]-*.md; do
  n=$((10#$(basename "$f" | cut -c1-4)))
  [ "$n" -gt "$last_older" ] || continue
  grep -qx '## Status' "$f" && continue
  echo "$f: a new record takes decision-record's current shape (Status, Context, Decision, Consequences, Guard), not an older one"
  rc=1
done
while read -r n; do
  ls "$dir/$n"-*.md > /dev/null 2>&1 && continue
  grep -qE "Retires ADR-$n: " "$dir"/*.md && continue
  echo "$dir: a record is superseded by ADR-$n, which is gone and which no record retires"
  rc=1
done < <(grep -ohE '^Superseded by ADR-[0-9]{4}' "$dir"/*.md | grep -oE '[0-9]{4}' | sort -u)
exit $rc
