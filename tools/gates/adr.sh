#!/usr/bin/env bash
# Every decision record keeps ADR-FORMAT.md's shape: bin/check-adr, which CI runs too, run with the other gates so
# a record over its word limit or naming code fails the moment it is written.
cd "$(dirname "$0")/../.."
bash bin/check-adr docs/adr/*.md
