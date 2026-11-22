#!/usr/bin/env bash
# reaches: tools/* tools/gates/*
# An approved-examples script carries its examples' Given / When / Then lines, which the approved-test lock guards;
# one with none is a regression test and is named *_regression (CX-633, E10). Scope: every tracked or new
# *_approved_examples*.sh under tools/, the files tools/approved-examples.sh and the by-hand runs take.
cd "$(dirname "$0")/../.." || exit 2
rc=0
while IFS= read -r f; do
  # one Given, then a When, then a Then, in that order
  awk '/^[[:space:]]*(#|\/\/)[[:space:]]*Given /{s=1} s==1&&/^[[:space:]]*(#|\/\/)[[:space:]]*When /{s=2} s==2&&/^[[:space:]]*(#|\/\/)[[:space:]]*Then /{f=1; exit} END{exit !f}' "$f" && continue
  echo "!! approved-lines: $f carries no Given / When / Then lines: add them, or name it *_regression" >&2; rc=1
done < <(git ls-files --cached --others --exclude-standard -- 'tools/*_approved_examples*.sh' 'tools/**/*_approved_examples*.sh')
exit $rc
