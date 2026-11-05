# ADR-0056: Screen-space occlusion is a lighting input that darkens ambient and direct light; AO maps stay on ambient.

Category: Architecture.

Decided 2026-11-05.

Because most surfaces are lit mainly by direct light, so ambient-only occlusion left low-poly models looking glued together, while an authored AO map must not darken a lit surface.
