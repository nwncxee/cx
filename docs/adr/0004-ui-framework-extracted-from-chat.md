# ADR-0004: A shared UI framework owns the frame, windows and input precedence, not the chat service.

Category: Architecture.

Decided 2026-08-18.

Because chat owned them only by being first, other services re-solved input arbitration by hand, and a toolkit-only extraction would leave that coupling.
