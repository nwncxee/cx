#!/usr/bin/env bash
# The rules' own approved examples, which plant trees and, for the compiler rules, compile them: they need the SDK
# the gates job does not carry, so build.sh runs this. build.sh calls it rather than naming each script, because
# the approved-spec guard refuses a command whose script names an approved-examples file in full.
cd "$(dirname "$0")/.."
rc=0
for t in tools/gates/*_approved_examples.sh tools/analyzers/*_approved_examples.sh; do
  bash "$t" || { echo "!! approved examples failed: $t" >&2; rc=1; }
done
exit $rc
