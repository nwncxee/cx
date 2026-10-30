# ADR-0014: Code is layered by what it does, with UI and rendering as peers and the framework naming no service.

Category: Architecture.

Decided 2026-08-24.

Because the folder tree had fossilised the order features were built in, and a framework calling named services inverts the dependency.
