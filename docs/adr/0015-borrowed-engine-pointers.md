# ADR-0015: Any engine pointer held across frames is validated at the point of use, by type check or death notice.

Category: Architecture.

Decided 2026-08-26.

Because every renderer crash so far was a stale engine pointer, and re-fetching each frame was necessary but not sufficient.
