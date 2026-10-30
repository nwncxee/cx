# ADR-0050: The game thread owns the scene database and answers its getters from the columns.

Category: Architecture.

Decided 2026-10-01.

Because the game reads animation and attach-point results synchronously, so those must run where the game calls.
