#!/usr/bin/env bash
# Run one script from this folder over one program in the Ghidra project, read-only, with no re-analysis.
#
#   tools/ghidra/headless.sh nwmain-linux DecompOne.java out.c Scene::RenderBeams @571860
#   tools/ghidra/headless.sh nwmain.exe   FindSym.java ...
#
# nwmain.exe opens the nwn-win project and anything else nwn-linux; GHIDRA_PROJ overrides. GHIDRA and
# GHIDRA_PROJECT resolve through tools/paths.py. Runs queue on one lock, and the GUI must not have the
# project open.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
GHIDRA="$(python3 "$HERE/../paths.py" GHIDRA)"
PROJDIR="$(python3 "$HERE/../paths.py" GHIDRA_PROJECT)"

prog="${1:?program (nwmain-linux | nwmain.exe)}"
script="${2:?script in tools/ghidra, e.g. DecompOne.java}"
shift 2
case "$prog" in
  *.exe) PROJ="${GHIDRA_PROJ:-nwn-win}" ;;
  *)     PROJ="${GHIDRA_PROJ:-nwn-linux}" ;;
esac

export GHIDRA_HEADLESS_MAXMEM="${GHIDRA_HEADLESS_MAXMEM:-6G}"
exec flock "$PROJDIR/.cx-headless.lock" "$GHIDRA/support/analyzeHeadless" "$PROJDIR" "$PROJ" \
  -process "$prog" -noanalysis -readOnly -scriptPath "$HERE" -postScript "$script" "$@"
