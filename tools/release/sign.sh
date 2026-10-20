#!/usr/bin/env bash
# Signs and publishes the draft release that release.yml built for a tag. It downloads the zips and the
# SHA256SUMS CI wrote, checks each zip's provenance and checksum, signs SHA256SUMS with the release key,
# uploads SHA256SUMS.sig and publishes the draft. Run it on the maintainer's machine, never in CI, so a
# compromised CI can't sign. The key is RELEASE_SIGN_KEY (tools/paths.py). Its public half is
# packaging/allowed_signers, which players verify against.
#
# A published release is immutable. Check the draft's notes before you run this.
#
# Usage: tools/release/sign.sh vX.Y.Z   (gh logged in as an account with write access to nwncxee/cx)
# First time: ssh-keygen -t ed25519 -f ~/.ssh/cx-release -C cx-releases
#             echo "cx-releases $(cat ~/.ssh/cx-release.pub)" > packaging/allowed_signers   (and commit it)
set -euo pipefail
tag="${1:?usage: tools/release/sign.sh vX.Y.Z}"
repo=nwncxee/cx
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

ssh-keygen -Y sign -f "$key" -n file SHA256SUMS
ssh-keygen -Y verify -f "$signers" -I cx-releases -n file -s SHA256SUMS.sig < SHA256SUMS

gh release upload "$tag" -R "$repo" SHA256SUMS.sig --clobber
gh release edit "$tag" -R "$repo" --draft=false
echo "signed and published $tag"
