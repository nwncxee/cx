#!/usr/bin/env bash
# tools/gates/run.sh and tools/gates/approved-lines.sh in a scratch tree with planted gates and scripts: a failing
# gate fails the run and is named, --for keeps only the gates that reach a path, and an approved-examples script
# without an ordered Given, When and Then is refused. Oracle: the exit codes and the names each prints.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
rc=0
fail() { echo "!! gate runner: $*" >&2; rc=1; }
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT
mkdir -p "$t/tools/gates" && cp tools/gates/run.sh tools/gates/approved-lines.sh "$t/tools/gates/"
printf '#!/usr/bin/env bash\nexit 0\n' > "$t/tools/gates/ok.sh"
printf '#!/usr/bin/env bash\n# reaches: docs/*\nexit 0\n' > "$t/tools/gates/docs-only.sh"
printf '#!/usr/bin/env bash\necho planted failure; exit 1\n' > "$t/tools/gates/bad.sh"
(cd "$t" && bash tools/gates/run.sh) > "$t/out" 2>&1 && fail "a failing gate left the run green"
grep -q 'gate failed: tools/gates/bad.sh' "$t/out" || fail "the failing gate is not named"
grep -q 'planted failure' "$t/out" || fail "the failing gate's log is not shown"
rm "$t/tools/gates/bad.sh"
(cd "$t" && bash tools/gates/run.sh) > /dev/null 2>&1 || fail "passing gates failed the run"
mine=$(cd "$t" && bash tools/gates/run.sh --list --for managed/X.cs | tr '\n' ' ')
[ "$mine" = "tools/gates/approved-lines.sh tools/gates/ok.sh " ] || fail "--for managed/X.cs chose '$mine'"
mine=$(cd "$t" && bash tools/gates/run.sh --list --for docs/a.md | tr '\n' ' ')
[ "$mine" = "tools/gates/approved-lines.sh tools/gates/docs-only.sh tools/gates/ok.sh " ] || fail "--for docs/a.md chose '$mine'"

lines() { (cd "$t" && git init -q 2>/dev/null; printf '%s\n' "$@" > tools/x_approved_examples.sh; bash tools/gates/approved-lines.sh) > /dev/null 2>&1; }
lines '# Given a' '# When b' '# Then c' || fail "approved-lines refused an ordered Given, When and Then"
lines '# Given a' '# When b' && fail "approved-lines passed a script with no Then"
lines '# Then c' '# When b' '# Given a' && fail "approved-lines passed Given, When and Then out of order"
[ $rc = 0 ] && echo "gate runner regression: passed"
exit $rc
