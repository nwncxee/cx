# ADR-0055: The frame draws straight into the window's image, top row first; the backend flips for it.

Category: Architecture.

Decided 2026-11-01.

Because a present blit cost a full-screen copy every frame, while one flip in the backend keeps every pass speaking GL's rows. Replaces ADR-0016's present.
