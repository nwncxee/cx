#!/usr/bin/env bash
# Signs and publishes the draft release that release.yml built for a tag. It downloads the zips and the
# SHA256SUMS CI wrote, checks each zip's provenance and checksum, Authenticode-signs version.dll in the
# Windows zip when CI left it unsigned, signs SHA256SUMS with the release key, uploads what changed and
# publishes the draft. Run it on the maintainer's machine, never in CI, so a compromised CI can't sign. The
# key is RELEASE_SIGN_KEY (tools/paths.py). Its public half is packaging/allowed_signers, which players
# verify against. The Authenticode key is Certum's, in SimplySign's cloud: the script asks for a 6-digit code
# from the SimplySign app, and CERTUM_EMAIL names the account (it asks for that too when it is unset).
#
# A published release is immutable. Check the draft's notes before you run this.
#
# Usage: tools/release/sign.sh vX.Y.Z   (gh logged in as an account with write access to nwncxee/cx)
# First time: ssh-keygen -t ed25519 -f ~/.ssh/cx-release -C cx-releases
#             echo "cx-releases $(cat ~/.ssh/cx-release.pub)" > packaging/allowed_signers   (and commit it)
#             cargo install ssign --version 0.1.8 --locked
# Needs osslsigncode on PATH (nix shell nixpkgs#osslsigncode).
set -euo pipefail
tag="${1:?usage: tools/release/sign.sh vX.Y.Z}"
repo=nwncxee/cx
ssign_version=0.1.8
# SHA-256 of the Certum code signing certificate's DER. It renews every year: put the new one here and
# in sign_harness.sh's certum.
cert_sha256=6c1137379e841cac08898b423b1e8805aaff6f95ff5927a29b2678a4a8a99aa6
command -v osslsigncode > /dev/null || { echo "osslsigncode is not on PATH (see the header of this script)" >&2; exit 1; }
root=$(cd "$(dirname "$0")/../.." && pwd)
key=$(python3 "$root/tools/paths.py" RELEASE_SIGN_KEY)
signers="$root/packaging/allowed_signers"
[[ -f "$key" ]] || { echo "no release key at $key (see the header of this script)" >&2; exit 1; }
[[ -s "$signers" ]] || { echo "packaging/allowed_signers is missing (see the header of this script)" >&2; exit 1; }

draft=$(gh release view "$tag" -R "$repo" --json isDraft --jq .isDraft)
[[ "$draft" == true ]] || { echo "$tag is already published, and a published release can't change" >&2; exit 1; }
# Publishing freezes every asset, so an extra one would ship unchecked.
assets=$(gh release view "$tag" -R "$repo" --json assets --jq '[.assets[].name] | sort | join(" ")')
expected="SHA256SUMS cx-linux-x64-$tag.zip cx-moonglow-plugin-$tag.zip cx-windows-x64-$tag.zip"
[[ "$assets" == "$expected" ]] || { echo "$tag holds [$assets], expected [$expected]" >&2; exit 1; }

work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
gh release download "$tag" -R "$repo" -D "$work" -p '*.zip' -p SHA256SUMS
cd "$work"
[[ -f SHA256SUMS ]] || { echo "$tag has no SHA256SUMS asset" >&2; exit 1; }
for z in *.zip; do
  grep -q "  $z\$" SHA256SUMS || { echo "$z is not listed in SHA256SUMS" >&2; exit 1; }
  gh attestation verify "$z" -R "$repo" --signer-workflow "$repo/.github/workflows/release.yml" \
    --source-ref "refs/tags/$tag" --deny-self-hosted-runners > /dev/null
  echo "$z: built by release.yml from $tag"
done
sha256sum -c SHA256SUMS

win="cx-windows-x64-$tag.zip"
uploads=(SHA256SUMS.sig)
python3 -m zipfile -e "$win" stage
if osslsigncode verify -in stage/version.dll -require-leaf-hash "sha256:$cert_sha256" > /dev/null 2>&1; then
  echo "$win: version.dll already carries the Certum signature"
else
  [[ "$(ssign --version 2> /dev/null)" == "ssign $ssign_version" ]] ||
    { echo "needs ssign $ssign_version: cargo install ssign --version $ssign_version --locked" >&2; exit 1; }
  [[ -n "${CERTUM_EMAIL:-}" ]] || read -rp "Certum account e-mail: " CERTUM_EMAIL
  read -rsp "SimplySign 6-digit code: " code; echo
  CERTUM_EMAIL="$CERTUM_EMAIL" CERTUM_TOKEN="$code" ssign -n "NWNCX:EE" -u "https://github.com/$repo" stage/version.dll
  osslsigncode verify -in stage/version.dll -require-leaf-hash "sha256:$cert_sha256" > /dev/null ||
    { echo "version.dll is not signed by the Certum certificate after ssign" >&2; exit 1; }
  # make_archive, as build-win-native.sh packs it: the zip holds the folder's contents, flat.
  rm "$win"
  python3 -c "import shutil, sys; shutil.make_archive(sys.argv[1], 'zip', 'stage')" "${win%.zip}"
  sha256sum -- *.zip > SHA256SUMS
  uploads+=("$win" SHA256SUMS)
  echo "$win: signed version.dll"
fi

ssh-keygen -Y sign -f "$key" -n file SHA256SUMS
ssh-keygen -Y verify -f "$signers" -I cx-releases -n file -s SHA256SUMS.sig < SHA256SUMS

gh release upload "$tag" -R "$repo" "${uploads[@]}" --clobber
gh release edit "$tag" -R "$repo" --draft=false
echo "signed and published $tag"
