# ADR-0007: The extender owns the frame schedule, using engine callbacks only as data taps and suppression points, never as triggers.

Category: Architecture.

Decided 2026-08-19.

Because passes riding an engine-chosen trigger ran on stale light data in some zones, which looked like random flicker.
