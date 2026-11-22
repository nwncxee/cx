#!/usr/bin/env bash
# A mutant boot that prints without end is stopped once its output passes mutants.py's OUTPUT_CAP, before the runner's
# memory follows it. Oracle: a stand-in boot that streams "yes", and a 512 MB address-space limit the runner must stay
# under. Exit 0 PASS, 1 FAIL.
set -uo pipefail
cd "$(dirname "$0")/../.." || exit 2
tmp=$(mktemp -d "${TMPDIR:-/tmp}/cx-mutants-flood.XXXXXX"); trap 'rm -rf "$tmp"' EXIT
printf '#!/bin/sh\necho ">> booting the self-checks"\nexec yes xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\n' > "$tmp/boot"
chmod +x "$tmp/boot"
out=$( (ulimit -v 524288; timeout 60 python3 -I - "$tmp" <<'PY'
import sys
sys.path.insert(0, 'tools/selfcheck')
import mutants as M
M.RUN = sys.argv[1] + '/boot'
M.OUTPUT_CAP = 8 << 20
rc, output, took = M.run(sys.argv[1], sys.argv[1] + '/out', False, 0, 50)
print(f'rc={rc} kept={len(output)}')
PY
) 2>&1); e=$?
rc=0
[ "$e" = 0 ] || { echo "!! the runner died (exit $e) holding a flooding boot's output: $(tail -1 <<<"$out")"; rc=1; }
grep -q '^rc=124 ' <<<"$out" || { echo "!! a flooding boot was not stopped: $(tail -1 <<<"$out")"; rc=1; }
[ $rc = 0 ] && echo ">> mutants flood: PASS" || echo "!! mutants flood: FAILED (above)" >&2
exit $rc
