# ADR-0009: The engine's renderer is replaced wholesale instead of optimised, and legacy visual parity is a non-goal.

Category: Architecture.

Decided 2026-08-19.

Because its submission model makes cost proportional to draw count in every pass, so each incremental fix bought only one pass.
