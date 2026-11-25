#!/usr/bin/env bash
# The demo host boots the staged extender on a machine whose loader holds no ICU, with the library path
# ./run-demo.sh gives it, and the runtime starts its globalization instead of aborting.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
B="$PWD/cx-linux-x64"
[ -f "$B/cx_managed.so" ] || { echo "!! demo-icu regression: no staged build (run ./build.sh)" >&2; exit 2; }
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
fail() { echo "!! demo-icu regression: $*" >&2; exit 1; }
bash tools/stub-host.sh "$t/nwmain-demo" >/dev/null || exit 2
# Given a machine whose loader finds no ICU of its own
ldconfig -p 2>/dev/null | grep -q 'libicuuc\.so' && { echo "demo-icu regression: the system holds ICU, nothing to prove"; exit 0; }
# When the demo host boots the extender with the library path ./run-demo.sh gives it
HOME="$t" XDG_DATA_HOME="$t/.local/share" CX_MANAGED="$B/cx_managed.so" LD_LIBRARY_PATH="$B${ICU_LIB:+:$ICU_LIB}" \
  LD_PRELOAD="$B/libcx.so" "$t/nwmain-demo" >"$t/out" 2>&1
code=$?
# Then the runtime finds ICU and the host runs to its end
grep -q 'valid ICU package' "$t/out" && fail "the runtime found no ICU (exit $code): $(head -c 200 "$t/out")"
[ "$code" -eq 0 ] || fail "the demo host exited $code: $(head -c 300 "$t/out")"
echo "demo-icu regression: passed"
