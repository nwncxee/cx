# ADR-0001: Chat appearance is global, while tabs and layout belong to each window.

Category: Interface.

Decided 2026-08-15.

Because appearance runs through one shared glyph atlas and palette, and per-window copies would mean a real refactor of the draw path for little user value.
