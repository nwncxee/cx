#!/usr/bin/env bash
# tools/ssl-by-name.sh on planted libraries: a shim that opens libssl.so.3 by name passes, one patched to a store
# path fails and names the path, one naming no libssl at all fails, and a missing file is refused.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
fail() { echo "!! ssl-by-name regression: $*" >&2; exit 1; }
printf 'abc\0libssl.so.3\0libssl.so.1.1\0xyz' > "$t/byname.so"
printf 'abc\0/opt/patched-openssl/lib/libssl.so.3\0libssl.so.3\0' > "$t/fixed.so"
printf 'abc\0nothing here\0' > "$t/none.so"
bash tools/ssl-by-name.sh "$t/byname.so" 2>/dev/null || fail "a shim that opens libssl by name was refused"
out=$(bash tools/ssl-by-name.sh "$t/fixed.so" 2>&1) && fail "a shim patched to a store path passed"
grep -q '/opt/patched-openssl/lib/libssl.so.3' <<<"$out" || fail "the refusal does not name the fixed path"
bash tools/ssl-by-name.sh "$t/none.so" 2>/dev/null && fail "a library naming no libssl passed"
bash tools/ssl-by-name.sh "$t/missing.so" 2>/dev/null; [ $? -eq 2 ] || fail "a missing library did not exit 2"
echo "ssl-by-name regression: passed"
