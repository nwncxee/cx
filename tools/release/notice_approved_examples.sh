#!/usr/bin/env bash
# The approved examples for the checksums sign.sh writes into the release notice (CX-694 E5) and for the README's
# check of a download against the key an install shows (CX-695 E2, E3), run on a copy of sign.sh against the draft
# release sign_harness.sh fakes. The copied release key is the planted tree's packaging/allowed_signers, the text
# CX embeds and Copy release key copies. Oracle: the zips plant() builds and their sha256sum, ssh-keygen's own
# verify, and the verify command as README.md prints it.
cd "$(dirname "$0")/../.." || exit 1
. tools/release/sign_harness.sh

# CX-694 E5
# Given a release whose Windows zip holds an unsigned version.dll, and whose SHA256SUMS lists the Linux, Windows and Moonglow plugin zips
# When the maintainer runs tools/release/sign.sh and types a SimplySign code
# Then cx-update holds one checksum line per zip, each matching its line in the SHA256SUMS that was signed, the Windows line
# is the SHA-256 of the zip the published draft holds, not CI's, and ssh-keygen verifies cx-update.sig under cx-update
c=$(plant e5 "")
e5=$c
if run "$c" "$tmp/code"; then
  d=$c/draft
  ssh-keygen -Y verify -f "$c/tree/packaging/allowed_signers" -I cx-releases -n file -s "$d/SHA256SUMS.sig" < "$d/SHA256SUMS" > /dev/null 2>&1 ||
    fail "E5: SHA256SUMS.sig does not sign the draft's SHA256SUMS"
  [ "$(grep -c '^sha256 ' "$d/cx-update" 2> /dev/null)" = 3 ] ||
    fail "E5: cx-update holds [$(grep '^sha256 ' "$d/cx-update" 2> /dev/null | paste -sd'|')], expected one checksum line per zip"
  while read -r sum name; do
    grep -qx "sha256 $sum $name" "$d/cx-update" 2> /dev/null || fail "E5: cx-update has no line [sha256 $sum $name] from the signed SHA256SUMS"
  done < "$d/SHA256SUMS"
  win=cx-windows-x64-$tag.zip
  published=$(sha256sum "$d/$win" | cut -c1-64) ci=$(sha256sum "$c/ci-windows.zip" | cut -c1-64)
  [ "$published" != "$ci" ] || fail "E5: the case never repacked the Windows zip, so it proves nothing about the repack"
  grep -qx "sha256 $published $win" "$d/cx-update" 2> /dev/null && ! grep -q "$ci" "$d/cx-update" ||
    fail "E5: cx-update's Windows line is not the published zip's SHA-256 ($published; CI's was $ci)"
  # CX-694 E4's released-parser check reads Fixtures/sums.txt, so that fixture must hold every line kind sign.sh writes.
  extra=$(comm -23 <(cut -d' ' -f1 "$d/cx-update" | sort -u) <(cut -d' ' -f1 managed/Services/Update/Fixtures/sums.txt | sort -u))
  [ -z "$extra" ] || fail "E5: cx-update writes line kinds [$(paste -sd' ' <<< "$extra")] the sums.txt fixture lacks"
  ssh-keygen -Y verify -f "$c/tree/packaging/allowed_signers" -I cx-releases -n cx-update -s "$d/cx-update.sig" < "$d/cx-update" > /dev/null 2>&1 ||
    fail "E5: ssh-keygen does not verify cx-update.sig under cx-update"
else
  fail "E5: sign.sh failed -- $(tail -3 "$c/out")"
fi

# The verify line README.md tells players to run, as it prints it.
verify=$(grep -m1 '^ssh-keygen -Y verify .*SHA256SUMS' README.md)
[ -n "$verify" ] || fail "README.md has no ssh-keygen verify line for SHA256SUMS"
# PowerShell refuses `<`, so the Windows steps run the same line through cmd.
grep -qxF "cmd /c \"$verify\"" README.md || fail "README.md has no Windows line running [$verify] through cmd /c"
# A folder as a player keeps it: the release's SHA256SUMS, its signature $2, and the copied key as allowed_signers.
player() {
  local dir=$tmp/$1
  mkdir -p "$dir"
  cp "$e5/draft/SHA256SUMS" "$dir/" && cp "$2" "$dir/SHA256SUMS.sig" && cp "$e5/tree/packaging/allowed_signers" "$dir/allowed_signers"
  echo "$dir"
}

# CX-695 E2
# Given the player saved the copied release key as allowed_signers
# When they run the README's ssh-keygen verify on a genuine release's SHA256SUMS and SHA256SUMS.sig
# Then it prints Good "file" signature for cx-releases
dir=$(player e2 "$e5/draft/SHA256SUMS.sig")
out=$(cd "$dir" && bash -c "$verify" 2>&1)
grep -qF 'Good "file" signature for cx-releases' <<< "$out" || fail "CX-695 E2: the README's verify printed [$out]"

# CX-695 E3
# Given the player saved the copied release key as allowed_signers, and a SHA256SUMS signed by any other key
# When they run the same verify
# Then it fails
ssh-keygen -q -t ed25519 -N '' -C cx-releases -f "$tmp/other-key"
cp "$e5/draft/SHA256SUMS" "$tmp/resigned" && ssh-keygen -Y sign -f "$tmp/other-key" -n file "$tmp/resigned" 2> /dev/null ||
  fail "CX-695 E3: could not sign with the other key, so the case proves nothing"
dir=$(player e3 "$tmp/resigned.sig")
if (cd "$dir" && bash -c "$verify") > /dev/null 2>&1; then fail "CX-695 E3: the README's verify passed a SHA256SUMS signed by another key"; fi

[ $rc -eq 0 ] && echo ">> release notice approved examples: CX-694 E5, CX-695 E2 and E3 pass"
exit $rc
