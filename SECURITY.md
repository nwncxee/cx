# Security

## Reporting a vulnerability

Report it privately through [Report a vulnerability](https://github.com/nwncxee/cx/security/advisories/new).
Don't open a public issue for it, and don't post it in Discord.

Report anything that lets someone else run code or read files on a player's machine through CX. That
includes a packet a server can send, a file in a hak or NWSync, and a release zip that doesn't match
its tagged source. A bug in the game itself goes to Beamdog, and a bug in a server's own extender goes
to that server.

We fix the latest release only. Update before you report.

## How a release is made

Only an admin can push a `v*` tag, and the tag must point at a commit on `main`, which only changes
through a reviewed pull request. GitHub's hosted runners build both zips from the tag with no cache, and
the build attests where each zip came from. The release stays a draft until a maintainer checks that
attestation and every checksum on their own machine and signs `SHA256SUMS`. Releases are immutable once
published.

[Checking a download is genuine](README.md#checking-a-download-is-genuine) in the README shows how to
verify a zip yourself.
