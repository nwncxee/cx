#!/usr/bin/env bash
# The ADR gate holds the current decision-record format: a record in the current shape passes, and a record
# numbered past the last one written in an older shape must take the current one. Oracle: hand-written records.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
rc=0
fail() { echo "!! adr: $*" >&2; rc=1; }
tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT

current() { cat > "$1" <<'EOF'
# ADR-0061: Records keep one current shape

Category: Process.

## Status

Accepted 2026-11-26.

## Context

- F1: By 2026-11-26, records had drifted between two shapes.

## Decision

While F1 holds, a new record takes the current shape.

## Consequences

Readers meet one shape. No migration: older records keep theirs.

## Guard

The record gate.
EOF
}
older() { cat > "$1" <<'EOF'
# ADR-0061: Records keep one current shape

Category: Process.

Decided 2026-11-26.

## Brief

A new record takes the current shape.

## Rationale

Readers meet one shape.
EOF
}

mkdir "$tmp/a" "$tmp/b" "$tmp/c"
current "$tmp/a/0061-records-keep-one-current-shape.md"
bash bin/check-adr "$tmp/a/"*.md > "$tmp/out" 2>&1 || fail "a record in the current format is refused: $(cat "$tmp/out")"
bash tools/gates/adr.sh "$tmp/a" > "$tmp/out" 2>&1 || fail "the gate refuses a current-format record: $(cat "$tmp/out")"

older "$tmp/b/0061-records-keep-one-current-shape.md"
bash tools/gates/adr.sh "$tmp/b" > "$tmp/out" 2>&1 && fail "the gate passes a new record in an older shape"
grep -q 'decision-record' "$tmp/out" || fail "the refusal does not name the format to use: $(cat "$tmp/out")"

older "$tmp/c/0060-an-older-record.md"
sed -i 's/ADR-0061/ADR-0060/' "$tmp/c/0060-an-older-record.md"
bash tools/gates/adr.sh "$tmp/c" > "$tmp/out" 2>&1 || fail "the gate refuses a kept record in an older shape: $(cat "$tmp/out")"

mkdir "$tmp/d"
current "$tmp/d/0061-records-keep-one-current-shape.md"
# shellcheck disable=SC2016 # the backticks are the record's text
echo 'It names `code`.' >> "$tmp/d/0061-records-keep-one-current-shape.md"
bash tools/gates/adr.sh "$tmp/d" > "$tmp/out" 2>&1 && fail "the gate passes a record check-adr refuses"

mkdir "$tmp/e"
older "$tmp/e/0021-an-older-record.md"
sed -i 's/ADR-0061/ADR-0021/' "$tmp/e/0021-an-older-record.md"
echo 'Superseded by ADR-0056.' >> "$tmp/e/0021-an-older-record.md"
bash tools/gates/adr.sh "$tmp/e" > "$tmp/out" 2>&1 && fail "the gate passes a pointer to a record that is gone and retired by none"
current "$tmp/e/0061-records-keep-one-current-shape.md"
sed -i 's/older records keep theirs\.$/older records keep theirs. Retires ADR-0056: restated./' "$tmp/e/0061-records-keep-one-current-shape.md"
bash tools/gates/adr.sh "$tmp/e" > "$tmp/out" 2>&1 || fail "the gate refuses a pointer that a Retires line answers: $(cat "$tmp/out")"

older "$tmp/c/0021-an-older-record.md"
sed -i 's/ADR-0061/ADR-0021/' "$tmp/c/0021-an-older-record.md"
echo 'Superseded by ADR-0060.' >> "$tmp/c/0021-an-older-record.md"
bash tools/gates/adr.sh "$tmp/c" > "$tmp/out" 2>&1 || fail "the gate refuses a pointer to a record that exists: $(cat "$tmp/out")"

[ $rc = 0 ] && echo "adr regression: passes"
exit $rc
