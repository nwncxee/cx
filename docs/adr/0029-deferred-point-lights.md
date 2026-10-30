# ADR-0029: Point lights move to a G-buffer pass plus tiled compute, with everything else staying in the lit stage.

Category: Architecture.

Decided 2026-10-07.

Because the buffer stores what a surface does with one more unit of point light, so results match the forward loop exactly without re-deriving its nonlinear composition.
