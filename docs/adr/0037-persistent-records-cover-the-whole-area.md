# ADR-0037: Alloy keeps persistent records for every part in the area, not just the visible set.

Category: Architecture.

Decided 2026-08-29.

Because the engine's visible set churns with each camera turn, so change tracking built on it would be rebuilt once culling moves to the GPU.
