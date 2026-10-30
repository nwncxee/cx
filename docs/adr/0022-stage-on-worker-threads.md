# ADR-0022: Staging runs on worker threads under one rule: a worker touches only its own slot's data.

Category: Architecture.

Decided 2026-09-05.

Because the frame is CPU-bound and 120 frames per second needs the build under a millisecond, so shared effects defer to the main thread.
