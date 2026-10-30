# ADR-0006: The extender owns draw submission for static world geometry, merging same-buffer parts into group draws.

Category: Architecture.

Decided 2026-08-18.

Because the engine pays full per-draw state churn for about 1950 draws a frame, and letting it configure each group's state keeps output identical.

Superseded by ADR-0016.
