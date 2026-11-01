# ADR-0053: The world's depth is reversed; casters, shadow cascades and the GUI keep GL's.

Category: Architecture.

Decided 2026-10-31.

Because a float depth buffer is most precise near 0, and reversed depth spends that precision on far surfaces, which stopped them flickering. Shadow maps and the GUI gain nothing from it and share stages with the engine's GL convention, so they keep GL's. The build refuses GL's convention on world depth: `tools/gates/world-depth.sh` for shaders, CX0006 for passes.
