# ADR-0023: Textures are cached across area changes, and a resident texture is handed back instead of rebuilt.

Category: Architecture.

Decided 2026-09-05.

Because rebuilding a texture on every sight wasted decode, upload and transcode, and second sight now costs nothing.
