# ADR-0024: Content may ship shader overrides, while the extender still ships every default embedded.

Category: Interface.

Decided 2026-09-13.

Because customisers need to replace stages, and the embedded fallback, dump and guards make a stale override fail soft.
