#!/usr/bin/env bash
# A cache file is read and written through CacheFile, which checks its header and writes through a temporary name. The
# RULE is CX0009 (tools/analyzers/CacheIo.cs), which the compiler checks: it can tell File.ReadAllBytes from a
# FileStream opened to read. This gate does the one thing the compiler cannot -- refuse a name in that list that no
# file in the tree answers to, so a deleted or renamed file leaves no standing permission behind it.
cd "$(dirname "$0")/../.."
python3 -I tools/gates/confined.py cache-io-confined tools/analyzers/CacheIo.cs CX0009 "cache file I/O"
