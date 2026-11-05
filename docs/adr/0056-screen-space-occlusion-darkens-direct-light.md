# ADR-0056: Screen-space occlusion is a lighting input that darkens ambient and direct light; AO maps stay on ambient.

Category: Architecture.

Decided 2026-11-05.

## Brief

Screen-space occlusion, computed before the colour pass, scales ambient and direct light, and its buffer carries a view-angle grey that dims open surfaces seen at an angle. AO maps scale ambient alone.

## Rationale

Most surfaces are lit mainly by direct light, so ambient-only occlusion left low-poly models looking glued together. An authored AO map must not darken a lit surface.
