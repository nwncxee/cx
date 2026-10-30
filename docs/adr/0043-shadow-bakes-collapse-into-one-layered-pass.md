# ADR-0043: Shadow bakes collapse into one layered pass, kept behind a runtime capability check.

Category: Architecture.

Decided 2026-08-30.

Because it cuts per-view submission, but the needed layer extension is not core in GL 4.6, so the per-view loop remains the fallback.
