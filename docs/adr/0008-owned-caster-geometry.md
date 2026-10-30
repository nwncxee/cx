# ADR-0008: The extender submits shadow caster geometry itself, reusing the engine's buffers instead of asking it to draw.

Category: Architecture.

Decided 2026-08-19.

Because the engine's per-draw state churn, repeated for every cube face, cost tens of milliseconds of GPU time that CPU timers never saw.
