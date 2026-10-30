# ADR-0039: The shadow mover signal is a level compared against stamps, never a consumed edge.

Category: Architecture.

Decided 2026-08-30.

Because whichever pass asked first used to take the one-time signal, leaving the other pass blind.
