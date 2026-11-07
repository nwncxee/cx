#!/usr/bin/env bash
# A content stage fixture lives in both translations, the C# self-checks and tools/content_stage.py.
cd "$(dirname "$0")/../.."
exec python3 tools/gates/content-fixtures.py
