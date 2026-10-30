# ADR-0032: A world view opens one GL bracket and closes one, covering shadows and colour.

Category: Architecture.

Decided 2026-10-24.

Because the reasons for separate brackets lapsed when the engine's scene pass stopped running, and each switch cost a fence, a flush and a context change.

Superseded by ADR-0033.
