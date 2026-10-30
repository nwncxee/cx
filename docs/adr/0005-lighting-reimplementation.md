# ADR-0005: Shadow lights come from the engine's master light list, not its camera-prioritized selection.

Category: Architecture.

Decided 2026-08-18.

Because the engine's stencil-era prioritizer churns with player movement, so shadow slots were evicted and rebaked constantly.
