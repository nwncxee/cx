# ADR-0062: A setting that cannot apply shows why, grey when it can be enabled and red when the device cannot run it

Category: Interface.

## Status

Accepted 2026-11-26.

## Context

- F1: By 2026-11-22, an audit found about forty settings rows live while doing nothing.
- F2: By 2026-11-22, some capabilities were known only once a session drew its first frame.

## Decision

While F1 and F2 hold, the renderer is a panel switch heading the graphics category, which depends on it whole, and a renderer-only row elsewhere depends on it alone. A row that cannot apply greys when the player can turn on what it needs and shows red when the device cannot run it, and a grey or red row takes no change. Until the first frame decides, settings act as if the renderer runs.

## Consequences

A player sees why a row does nothing. No migration: a saved value that cannot apply stays saved and shows red. Retires ADR-0057: this record restates its rule in the current shape.

## Guard

Review only.
