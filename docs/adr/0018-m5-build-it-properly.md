# ADR-0018: Alloy's frame is built only from its own state, never from observing the engine render.

Category: Architecture.

Decided 2026-08-29.

Because Alloy learned what exists by watching the engine draw, so it inherited the engine's cull, resources and pass order.
