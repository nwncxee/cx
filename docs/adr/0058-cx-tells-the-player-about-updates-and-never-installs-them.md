# ADR-0058: CX tells the player about a newer release and never installs one.

Category: Security.

Decided 2026-11-23.

## Brief

While releases are signed offline and players install by hand, CX reads only a notice signed by a key it ships with, and only after the player agrees. An unverified notice, or one naming an older version, shows nothing, and no address in it is followed.

## Rationale

An updater writing code to disk exposes every player to one verifier bug. A hint keeps the player as the installer, so a hijacked release source cannot push code or redirect anyone.
