# ADR-0017: Per-frame CPU work must scale with what changed, not with what Alloy owns.

Category: Architecture.

Decided 2026-08-29.

Because the measured cost was rebuilding draw records for every part each frame, not submitting them, so cutting draw calls would optimise a non-problem.
