# ADR-0036: The Alloy frame is linear end to end, with one resolve stage encoding to display space.

Category: Architecture.

Decided 2026-08-20.

Because blending in gamma space is wrong, and one encode per screen pixel is cheaper than one per fragment.
