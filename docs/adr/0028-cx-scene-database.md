# ADR-0028: CX owns the scene database as generation-checked struct-of-arrays records.

Category: Architecture.

Decided 2026-10-01.

Because column walks are prefetchable streams, and a freed slot's generation moves on so stale handles resolve to nothing without death notices.
