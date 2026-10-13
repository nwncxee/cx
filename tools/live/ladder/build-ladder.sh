#!/usr/bin/env bash
# Build cxladder.mod -- the live harness's scripted verification ladder (CX-120 rungs 3 and 4).
#
#   tools/live/ladder/build-ladder.sh <dest-modules-dir> [cxladder|cxnui] [script.nss]
#
# A third argument builds that script as the module's OnClientEnter, named by the second (the harness's
# --showcase passes tools/live/showcase/<name>.nss as cxshow); the inc_*.nss beside it are compiled in.
#
# The module is DERIVED from the installed stock "The Dark Ranger's Treasure.mod": that .mod is
# unpacked, this directory's cxladder.nss/.dlg.json are compiled/converted into it, module.ifo gets
# its OnClientEnter pointed at cxladder, and the lot is repacked. Nothing of Beamdog's is committed --
# only the .nss, the .dlg.json and this script are in the repo; the content comes from the user's own
# installed game at build time, into the harness's throwaway user directory.
#
# Needs on PATH: nwn_erf, nwn_gff, nwn_script_comp (neverwinter.nim). NWN_DIR overrides the game root.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
NWN_DIR="${NWN_DIR:-$(python3 "$HERE/../../../tools/paths.py" NWN_DIR)}"
# Spelled out of the brace default on purpose: an apostrophe inside ${x:-...} opens a quote in bash.
STOCK_MOD="The Dark Ranger's Treasure.mod"
BASE_MOD="${LADDER_BASE_MOD:-}"
[ -n "$BASE_MOD" ] || BASE_MOD="$NWN_DIR/data/mod/$STOCK_MOD"
NAME="${2:-cxladder}"
SRC="${3:-$HERE/$NAME.nss}"
[ -f "$SRC" ] || { echo "build-ladder: script not found: $SRC" >&2; exit 2; }

DEST="${1:-}"
[ -n "$DEST" ] || { echo "build-ladder: usage: build-ladder.sh <dest-modules-dir>" >&2; exit 2; }
for t in nwn_erf nwn_gff nwn_script_comp; do
    command -v "$t" >/dev/null || { echo "build-ladder: $t not on PATH (neverwinter.nim)" >&2; exit 2; }
done
[ -f "$BASE_MOD" ] || { echo "build-ladder: stock module not found: $BASE_MOD" >&2; exit 2; }
mkdir -p "$DEST"
DEST="$(cd "$DEST" && pwd)"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/cxladder.XXXXXX")"
trap 'rm -rf "$WORK"' EXIT

# 1. unpack the stock module (nwn_erf unpacks into the CWD, so we go there)
( cd "$WORK" && nwn_erf -x -f "$BASE_MOD" >/dev/null )

# 2. our two resources. The compiler needs the game's resman for nwscript.nss.
cp -f "$SRC" "$WORK/$NAME.nss"
cp -f "$(dirname "$SRC")"/inc_*.nss "$WORK/" 2>/dev/null || true
# LADDER_NUI_CLOSE=<seconds> closes the ladder's NUI window then; unset, it stays open.
[ -z "${LADDER_NUI_CLOSE:-}" ] || sed -i "s/^const float T_NUI_CLOSE = .*;/const float T_NUI_CLOSE = ${LADDER_NUI_CLOSE}.0;/" "$WORK/$NAME.nss"
nwn_script_comp --root "$NWN_DIR" --quiet -o "$WORK/$NAME.ncs" "$WORK/$NAME.nss"
[ ! -f "$HERE/$NAME.dlg.json" ] || nwn_gff -i "$HERE/$NAME.dlg.json" -l json -o "$WORK/$NAME.dlg" -k gff

# 3. module.ifo: our OnClientEnter, and a name that says what this is.
nwn_gff -i "$WORK/module.ifo" -k json -o "$WORK/module.ifo.json"
python3 - "$WORK/module.ifo.json" "$NAME" <<'PY'
import json, sys
path, name = sys.argv[1], sys.argv[2]
d = json.load(open(path))
d["Mod_OnClientEntr"] = {"type": "resref", "value": name}
d["Mod_Name"] = {"type": "cexolocstring", "value": {"0": "CX live-harness ladder"}}
json.dump(d, open(path, "w"))
PY
nwn_gff -i "$WORK/module.ifo.json" -l json -o "$WORK/module.ifo" -k gff
rm -f "$WORK/module.ifo.json" "$WORK/$NAME.nss" "$WORK"/inc_*.nss

# 4. repack. The .mod extension is what nwn_erf reads the ERF type off.
rm -f "$DEST/$NAME.mod"
( cd "$WORK" && nwn_erf -c -f "$DEST/$NAME.mod" . >/dev/null )
SIZE=$(stat -c %s "$DEST/$NAME.mod")
echo "build-ladder: $DEST/$NAME.mod -- $SIZE bytes, derived from ${BASE_MOD##*/}"
