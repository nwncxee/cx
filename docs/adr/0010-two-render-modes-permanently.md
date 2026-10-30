# ADR-0010: The engine's render path ships permanently as a supported mode, suppressed rather than deleted.

Category: Architecture.

Decided 2026-08-20.

Because macOS stops at OpenGL 4.1, chat and dialog features must work without the new renderer, and switching modes diagnoses rendering bugs.
