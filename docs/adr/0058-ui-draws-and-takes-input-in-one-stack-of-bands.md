# ADR-0058: Amending ADR-0004, CX windows, native panels and NUI draw and take input in one stack of bands.

Category: Architecture.

Decided 2026-11-23.

## Brief

Bottom to top: world-anchored CX windows, native panels, CX windows below NUI, NUI, CX windows above NUI, popups, then the overlay and cursor. Each window draws, takes clicks and answers Esc by its band. A window with a list open moves into the popup band.

## Rationale

Interleaving windows one by one with the engine's panels needs a draw site per panel; bands need one per seam.
