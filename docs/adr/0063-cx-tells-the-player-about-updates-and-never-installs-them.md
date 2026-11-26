# ADR-0063: CX tells the player about a newer release and never installs one

Category: Security.

## Status

Accepted 2026-11-26.

## Context

- F1: By 2026-11-23, releases were signed with an offline key and players installed them by hand from the releases page.
- F2: By 2026-11-23, a check for updates alone sent the player's address to the release host.

## Decision

While F1 and F2 hold, CX reads only an update notice signed by a key it ships with, and only after the player agrees to checks. An unverified notice, or one naming a version no newer than the running one, shows nothing, and no address in a notice is followed.

## Consequences

The player stays the installer, so a hijacked release source cannot push code or redirect anyone. An updater writing code to disk was turned down: one verifier bug would expose every player. Updating stays a manual step. Retires ADR-0058: this record restates its rule in the current shape.

## Guard

Review only.
