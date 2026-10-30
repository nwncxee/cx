# ADR-0040: A caster with unknown bounds still casts, while one whose record is stale waits a frame.

Category: Architecture.

Decided 2026-08-30.

Because a wrongly dropped caster deletes a shadow, but baking through another part's matrix is plainly wrong.
