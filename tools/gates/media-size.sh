#!/usr/bin/env bash
# A file under media/ stays under 300 KB: git keeps every version of a binary forever.
cd "$(dirname "$0")/../.."
limit=307200
rc=0
while IFS= read -r f; do
  [ -f "$f" ] || continue
  size=$(stat -c %s "$f")
  [ "$size" -le "$limit" ] && continue
  echo "!! media-size: $f is $size bytes, over $limit; compress it (WebP; a clip to animated WebP)" >&2
  rc=1
done < <(git ls-files --cached --others --exclude-standard -- media)
exit $rc
