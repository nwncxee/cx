#!/usr/bin/env bash
# Links the do-nothing host the self-checks boot in, shared by ./run-demo.sh and tools/selfcheck/run.sh: it carries a
# known client build's id, because the rot gate refuses an unknown host before discovery, and it links the system's
# sqlite3 and zstd, which NwSyncReader resolves out of the global scope (where they are missing, its check SKIPs, and
# this says so). The key comes from the build files, never a second copy. Linux-only, like the rest of the tooling.
# Usage: tools/stub-host.sh <out> [C source]   (no source: an empty main)
set -u
out=${1:?usage: stub-host.sh <out> [C source]}
src=${2:-}
cd "$(dirname "$0")/.." || exit 1
BID=$(awk -F'\t' '$1 == "linux" { print $2; exit }' managed/Framework/Builds/*.tsv)
[ -n "$BID" ] || { echo ">> FAIL: no linux key in managed/Framework/Builds/*.tsv"; exit 1; }
link() {
  if [ -n "$src" ]; then cc -O1 "$src" -o "$out" -Wl,--build-id=0x"$BID" -ldl "$@"
  else echo 'int main(void){return 0;}' | cc -x c - -o "$out" -Wl,--build-id=0x"$BID" "$@"; fi
}
if ! link -Wl,--no-as-needed -lsqlite3 -lzstd 2>/dev/null; then
  echo ">> no libsqlite3/libzstd to link: NwSyncReader.SelfCheck will SKIP (install them to run it)"
  link || { echo ">> FAIL: could not link the stub host $out"; exit 1; }
fi
