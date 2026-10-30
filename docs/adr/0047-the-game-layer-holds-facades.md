# ADR-0047: The game layer holds facades with extender-built vtables, never engine objects.

Category: Architecture.

Decided 2026-10-01.

Because the game layer is nearly vtable-only, so a minted block per record lets CX own lifetime while the game keeps calling as before.
