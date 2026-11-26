# ADR-0061: Screen-space occlusion darkens ambient and direct light, and AO maps darken ambient alone

Category: Architecture.

## Status

Accepted 2026-11-26.

## Context

- F1: By 2026-11-05, most lit surfaces took their light mainly from direct terms, so occlusion on ambient alone left models looking glued to the ground.
- F2: By 2026-11-05, the materials spec promised that an authored AO map never darkens a lit surface.

## Decision

While F1 and F2 hold, screen-space occlusion is a lighting input computed before the colour pass: it scales ambient and direct light, and its buffer carries a view-angle grey that dims open surfaces seen at an angle. AO maps and height-map occlusion scale ambient alone.

## Consequences

Occlusion shows on lit corners, and so do its artifacts. A post stage over the finished frame was turned down: it would darken authored maps on lit surfaces. Retires ADR-0056: this record restates its rule in the current shape.

## Guard

The ambient-occlusion self-check fails when direct light stops taking the screen-space term.
