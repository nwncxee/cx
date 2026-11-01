# ADR-0053: The world's depth is reversed; casters, shadow cascades and the GUI keep GL's.

Category: Architecture.

Decided 2026-10-31.

Because float depth is most precise near zero, and reversing it stops far surfaces flickering, while shadow maps and the GUI gain nothing and share the engine's stages.
