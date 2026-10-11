#!/usr/bin/env bash
# Signs a published release: downloads its zips and the SHA256SUMS CI wrote, checks every zip against
# it, signs SHA256SUMS with the release key and uploads SHA256SUMS.sig. Runs on the maintainer's machine,
# never in CI, so a compromised CI cannot sign. The key is RELEASE_SIGN_KEY (tools/paths.py); its public
# half is packaging/allowed_signers, which players verify against.
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

work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
gh release download "$tag" -R "$repo" -D "$work" -p '*.zip' -p SHA256SUMS
cd "$work"
[[ -f SHA256SUMS ]] || { echo "$tag has no SHA256SUMS asset" >&2; exit 1; }
for z in *.zip; do
  grep -q "  $z\$" SHA256SUMS || { echo "$z is not listed in SHA256SUMS" >&2; exit 1; }
done
sha256sum -c SHA256SUMS

ssh-keygen -Y sign -f "$key" -n file SHA256SUMS
ssh-keygen -Y verify -f "$signers" -I cx-releases -n file -s SHA256SUMS.sig < SHA256SUMS

gh release upload "$tag" -R "$repo" SHA256SUMS.sig --clobber
echo "signed $tag"
