# ADR-0021: Ambient occlusion is computed before the colour pass and feeds lighting, not a post stage.

Category: Architecture.

Decided 2026-09-04.

Because as a lighting input it scales only the ambient term beside the material's own map, and never darkens direct light.
