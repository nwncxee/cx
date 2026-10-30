# ADR-0016: The Alloy renderer draws the whole 3D world from its own resources and loop, while the engine keeps the 2D UI and the present.

Category: Architecture.

Decided 2026-08-26.

Because owning the present would mean owning three present paths for nothing the world renderer can use, and the goal is control, not performance.
