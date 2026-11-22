#!/usr/bin/env bash
# Stryker.NET over the lines of the engine-free core (managed/Core/core.files.props) that changed since a revision,
# tested by managed/Core/Tests. Run it by hand inside nix develop; it is never a CI gate.
#
# Usage: tools/stryker.sh [--since REV] [--output DIR]
#   --since REV   mutate the core lines changed since REV (default: the merge-base with origin/main)
#   --output DIR  where Stryker writes reports/mutation-report.json (default: build/stryker)
#
# Prints one line per file, "<file>: <n> killed, <n> survived, <n> timed out", then "SURVIVED <file>:<line> <mutator>
# <the replacement>" for each survivor. Exit 0 when nothing survived, 1 when a mutant survived, 2 on bad arguments or
# a Stryker run that wrote no report. Linux-only, like the rest of the developer tooling.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2
since="" out="$PWD/build/stryker"
while [ $# -gt 0 ]; do
  case "$1" in
    --since) since=$2; shift 2 ;;
    --output) out=$(realpath -m "$2"); shift 2 ;;
    *) sed -n '2,12p' "$0" >&2; exit 2 ;;
  esac
done
[ -n "$since" ] || since=$(git merge-base HEAD origin/main) || exit 2
mapfile -t files < <(sed -n 's/.*Include="\([^"]*\)".*/managed\/\1/p' managed/Core/core.files.props)
mapfile -t spans < <(python3 -I - "$since" "${files[@]}" <<'PY'
import re, subprocess, sys
rev, files = sys.argv[1], sys.argv[2:]
diff = subprocess.run(['git', 'diff', '-U0', rev, '--', *files], capture_output=True, text=True, check=True).stdout
path = None
for line in diff.splitlines():
    if line.startswith('+++ '):
        path = None if line == '+++ /dev/null' else line[6:]
        continue
    m = re.match(r'@@ -\S+ \+(\d+)(?:,(\d+))? @@', line)
    if not (m and path):
        continue
    first, count = int(m.group(1)), int(m.group(2) or 1)
    if count == 0:
        continue
    text = open(path, encoding='utf-8').read()
    starts = [0] + [i + 1 for i, c in enumerate(text) if c == '\n']
    last = starts[first + count - 2]
    end = last + len(text[last:].split('\n', 1)[0])
    print(f'**/{path.split("managed/", 1)[1]}{{{starts[first - 1]}..{end}}}')
PY
) || exit 2
if [ ${#spans[@]} = 0 ]; then echo ">> no core line changed since $since: nothing to mutate"; exit 0; fi
args=()
for s in "${spans[@]}"; do args+=(--mutate "$s"); done
dotnet tool restore >/dev/null || exit 2
rm -rf "$out"
(cd managed/Core/Tests && dotnet stryker --project cx_core.csproj --output "$out" --reporter json --reporter progress \
  --concurrency "$(( ($(nproc) + 1) / 2 > 3 ? 3 : ($(nproc) + 1) / 2 ))" "${args[@]}") >&2
report=$out/reports/mutation-report.json
[ -f "$report" ] || { echo "!! Stryker wrote no $report" >&2; exit 2; }
python3 -I - "$report" <<'PY'
import json, sys
r = json.load(open(sys.argv[1]))
survived = []
for f, v in sorted(r['files'].items()):
    ms = [m for m in v['mutants'] if m['status'] not in ('Ignored', 'NoCoverage', 'CompileError')]
    if not ms:
        continue
    name = f.split('/')[-1]
    n = lambda s: sum(m['status'] == s for m in ms)
    print(f'{name}: {n("Killed")} killed, {n("Survived")} survived, {n("Timeout")} timed out')
    survived += [f"SURVIVED {name}:{m['location']['start']['line']} {m['mutatorName']} {m.get('replacement', '')}".rstrip()
                 for m in ms if m['status'] == 'Survived']
print('\n'.join(survived)) if survived else None
sys.exit(1 if survived else 0)
PY
