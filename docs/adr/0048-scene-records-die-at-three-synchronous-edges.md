# ADR-0048: Scene records die only at three synchronous edges: object destroy, scene destroy and model release.

Category: Architecture.

Decided 2026-10-01.

Because CX produces every edge, so the renderer learns of a death the same frame with no inference, sweep or address test.
