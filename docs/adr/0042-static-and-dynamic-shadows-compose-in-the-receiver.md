# ADR-0042: Static and dynamic shadow maps compose in the receiver by taking the minimum, never in the texture.

Category: Architecture.

Decided 2026-08-30.

Because depth is a min-of-blockers operation so it composes exactly, while a texture composite lost to its own copy stall.
