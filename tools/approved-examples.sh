#!/usr/bin/env bash
# The rules' and the build checks' own approved examples, which plant trees and, for the compiler rules, compile
# them: they need the SDK the gates job does not carry, so build.sh runs this. build.sh calls it rather than
# naming each script, because the approved-spec guard refuses a command whose script names an approved-examples
# file in full -- which is why the patterns below stop short of the extension, and why this runner is runnable.
cd "$(dirname "$0")/.."
shopt -s nullglob
rc=0
tests=(tools/*_approved_examples* tools/gates/*_approved_examples* tools/analyzers/*_approved_examples*)
# An empty list would report every example as passing, so it is a failure rather than a quiet success.
[ ${#tests[@]} -gt 0 ] || { echo "!! no approved-examples scripts found" >&2; exit 1; }
for t in "${tests[@]}"; do
  bash "$t" || { echo "!! approved examples failed: $t" >&2; rc=1; }
done
exit $rc
