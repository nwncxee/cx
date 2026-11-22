# ADR-0057: A setting that cannot apply shows why: grey when it can be enabled, red when the device cannot run it.

Category: Interface.

Decided 2026-11-22.

## Brief

The renderer is a panel switch heading the graphics category, which depends on it whole. A renderer-only row elsewhere depends on it alone. Grey or red rows take no change. Until the first frame decides, settings act as if the renderer runs.

## Rationale

An audit found about forty rows live while doing nothing, most because graphics never depended on the renderer it needs.
