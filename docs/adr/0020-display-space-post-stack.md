# ADR-0020: Post-processing is an ordered stack of display-space stages after the resolve, and a failed stage drops out alone.

Category: Architecture.

Decided 2026-09-04.

Because the frame must be quantised to eight bits exactly once, and no stage may take the whole frame back to vanilla.
