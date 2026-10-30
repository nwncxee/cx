# ADR-0031: A rule the compiler can check is a compiler error, and only the framework folder does pointer arithmetic.

Category: Tooling.

Decided 2026-10-15.

Because a review found a pointer plus a named offset read in place, and naming the offset leaves every reader needing the hidden layout.
