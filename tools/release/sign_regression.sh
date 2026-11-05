#!/usr/bin/env bash
# A regression test, not an approved one: example E2 already held before sign.sh signed anything, so it guards
# that a draft whose version.dll CI already signed is left alone. Oracle: the Certum certificate's SHA-256,
# worked out by hand from the issued certificate.
cd "$(dirname "$0")/../.." || exit 1
. tools/release/sign_harness.sh

c=$(plant e2 "SIGNED-LEAF:$certum")
if run "$c" /dev/null; then
  [ ! -f "$c/ssign-calls" ] || fail "E2: sign.sh ran ssign on a signed version.dll"
  [ "$(cat "$c/uploaded" 2> /dev/null)" = SHA256SUMS.sig ] ||
    fail "E2: sign.sh uploaded [$(paste -sd' ' "$c/uploaded" 2> /dev/null)], expected [SHA256SUMS.sig]"
else
  fail "E2: sign.sh failed -- $(tail -3 "$c/out")"
fi

[ $rc -eq 0 ] && echo ">> sign.sh regression: a signed build is left alone"
exit $rc
