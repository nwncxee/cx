# ADR-0013: Every tuning knob is a chat command, a config file key or deleted, never an environment variable.

Category: Tooling.

Decided 2026-08-24.

Because 149 environment variables read at 78 sites by four duplicate parsers made knobs undiscoverable, and modules borrowed each other's helpers.
