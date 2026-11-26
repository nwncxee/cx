# ADR-0064: CX windows, native panels and NUI draw and take input in one stack of bands

Category: Architecture.

## Status

Accepted 2026-11-26.

## Context

- F1: By 2026-11-18, players reported dropdowns and context menus sinking under server NUI windows, and clicks reaching the window underneath.
- F2: By 2026-11-23, interleaving CX windows one by one with the engine's panels needed a draw site per panel, while bands needed one per seam.

## Decision

While F1 and F2 hold, everything draws, takes clicks and answers Esc in one order of bands, bottom to top: world-anchored CX windows, native panels, CX windows below NUI, NUI, CX windows above NUI, popups, then the overlay and cursor. A window with a list open moves into the popup band.

## Consequences

A click goes to what is drawn on top, with the renderer on or off. A CX window cannot sit between two NUI windows. Interleaving was turned down for its cost per panel. Retires ADR-0059: this record restates its rule in the current shape.

## Guard

Review only.
