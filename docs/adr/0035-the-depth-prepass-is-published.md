# ADR-0035: An opaque depth prepass is built and published as a resource.

Category: Architecture.

Decided 2026-08-20.

Because water and ambient occlusion both want exactly that buffer, and building it privately would mean building it twice.
