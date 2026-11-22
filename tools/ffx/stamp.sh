#!/usr/bin/env bash
# A step's stamp holds the sha256 of its inputs, so a restored cache or a fresh checkout's file times never rerun it.
# Sourced by prepare.sh and build-lib.sh. inputs_hash <file>...: one hash over the files' names and contents.
inputs_hash() { sha256sum -- "$@" | sha256sum | cut -d' ' -f1; }
# stale <stamp> <hash>: true when the stamp is missing or holds another hash.
stale() { [ "$(cat -- "$1" 2>/dev/null)" != "$2" ]; }
