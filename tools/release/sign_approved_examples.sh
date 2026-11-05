#!/usr/bin/env bash
# sign.sh's approved examples (approved-spec, E1 and E3 to E6), run on a copy of sign.sh against the draft
# release sign_harness.sh fakes. E4 checks the zip E1's run signed. Oracle: the Certum certificate's SHA-256, worked out by hand from the issued
# certificate, and the fixture zips plant() builds.
cd "$(dirname "$0")/../.." || exit 1
. tools/release/sign_harness.sh

# Given a draft release whose Windows zip holds an unsigned version.dll
# When the maintainer runs sign.sh and types a SimplySign code
# Then the draft's Windows zip holds version.dll signed by the Certum certificate, and the draft's SHA256SUMS lists that zip and SHA256SUMS.sig signs it
c=$(plant e1 "")
e1=$c
if run "$c" "$tmp/code"; then
  python3 -m zipfile -e "$c/draft/cx-windows-x64-$tag.zip" "$c/got"
  grep -q "SIGNED-LEAF:$certum" "$c/got/version.dll" || fail "E1: the draft's version.dll is not signed by the Certum certificate"
  (cd "$c/draft" && grep -q "  cx-windows-x64-$tag.zip\$" SHA256SUMS && sha256sum -c --quiet SHA256SUMS) ||
    fail "E1: the draft's SHA256SUMS does not list the signed Windows zip"
  ssh-keygen -Y verify -f "$c/tree/packaging/allowed_signers" -I cx-releases -n file \
    -s "$c/draft/SHA256SUMS.sig" < "$c/draft/SHA256SUMS" > /dev/null 2>&1 ||
    fail "E1: SHA256SUMS.sig does not sign the draft's SHA256SUMS"
  [ -f "$c/published" ] || fail "E1: the draft was not published"
else
  fail "E1: sign.sh failed -- $(tail -3 "$c/out")"
fi

# Given a SimplySign account that signs with a certificate other than the Certum one
# When the maintainer runs sign.sh and types a code
# Then it stops with an error and the draft stays unpublished
c=$(plant e3 "")
if run "$c" "$tmp/code" STUB_LEAF=$other; then
  fail "E3: sign.sh succeeded with a signature from another certificate"
fi
grep -q "not signed by the Certum certificate" "$c/out" ||
  fail "E3: the error does not say the signature is not the Certum certificate's -- $(tail -2 "$c/out")"
[ ! -f "$c/published" ] || fail "E3: the draft was published"
[ ! -f "$c/uploaded" ] || fail "E3: sign.sh uploaded [$(paste -sd' ' "$c/uploaded")]"

# Given a draft release whose Windows zip holds an unsigned version.dll
# When sign.sh signs it
# Then the new zip lists the same files at its top level as CI's zip did
names() { python3 -c "import sys, zipfile; print('\n'.join(sorted(zipfile.ZipFile(sys.argv[1]).namelist())))" "$1"; }
[ -d "$e1/got" ] && [ "$(names "$e1/ci-windows.zip")" = "$(names "$e1/draft/cx-windows-x64-$tag.zip")" ] ||
  fail "E4: the signed zip lists [$(names "$e1/draft/cx-windows-x64-$tag.zip" | paste -sd' ')], CI's listed [$(names "$e1/ci-windows.zip" | paste -sd' ')]"

# Given a maintainer with no ssign installed
# When they run sign.sh on a draft with an unsigned version.dll
# Then it stops with an error that says what to install, before it asks for a code
c=$(plant e5-missing "")
mkdir -p "$tmp/no-ssign" && for s in gh osslsigncode; do ln -sf "$tmp/stubs/$s" "$tmp/no-ssign/$s"; done
bare=$tmp/no-ssign
IFS=: read -ra dirs <<< "$PATH"
for d in "${dirs[@]}"; do [ -x "$d/ssign" ] || bare+=":$d"; done
if env CASE="$c" PATH="$bare" RELEASE_SIGN_KEY="$tmp/release-key" CERTUM_EMAIL=dev@example.com \
  bash "$c/tree/tools/release/sign.sh" "$tag" < /dev/null > "$c/out" 2>&1; then
  fail "E5: sign.sh succeeded with no ssign"
fi
grep -q "cargo install ssign" "$c/out" || fail "E5: with no ssign, the error does not say what to install -- $(tail -2 "$c/out")"
[ ! -f "$c/published" ] || fail "E5: the draft was published with no ssign"

# Given a maintainer whose ssign is a different version from the one sign.sh needs
# When they run sign.sh on a draft with an unsigned version.dll
# Then it stops with an error that says which version to install, before it asks for a code
c=$(plant e6 "")
if run "$c" /dev/null STUB_SSIGN_VERSION=0.1.9; then fail "E6: sign.sh succeeded with ssign 0.1.9"; fi
grep -q "cargo install ssign --version 0.1.8" "$c/out" ||
  fail "E6: with ssign 0.1.9, the error does not say to install 0.1.8 -- $(tail -2 "$c/out")"
[ ! -f "$c/published" ] || fail "E6: the draft was published with ssign 0.1.9"

[ $rc -eq 0 ] && echo ">> sign.sh approved examples: E1 and E3 to E6 pass"
exit $rc
